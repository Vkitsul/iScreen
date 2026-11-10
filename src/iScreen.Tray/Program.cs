using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using IScreen.Host;
using IScreen.Host.Streaming;

namespace IScreen.Tray;

static class Program
{
    /// <summary>--gpu-restart &lt;pid&gt;: started by an iScreen that lost the GPU, to replace it.</summary>
    const string GpuRestartFlag = "--gpu-restart";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains(SystemService.RunFlag)) return SystemService.Run();
        if (args.Contains(AdminMode.InstallFlag)) return AdminMode.Install(PidAfter(args, AdminMode.InstallFlag));
        if (args.Contains(AdminMode.UninstallFlag)) return AdminMode.Uninstall();

        // Started by the service: SYSTEM, on the desktop of the user at the PC. Settings and log in the
        // service's folder, where the installer copied the user's settings (PIN included).
        bool fromService = args.Contains(SystemService.AgentFlag) && Sessions.IsSystem;
        if (fromService) HostConfig.Directory = SystemService.DataDir;

        using var mutex = CreateMutex(out bool firstInstance);
        int replacing = PidAfter(args, GpuRestartFlag);
        if (replacing > 0)
        {
            if (!firstInstance && (mutex is null || !TakeOver(mutex, replacing))) return 1;
            EndPrevious(replacing);
        }
        else if (!firstInstance)
        {
            MessageBox.Show("iScreen è già attivo: trovi la sua icona vicino all'orologio.", "iScreen",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        else if (!fromService && AdminMode.IsInstalled)
        {
            // The service runs iScreen (the user had closed it): ask it to start it again, and let go of
            // the mutex first or that iScreen finds us. If the service doesn't answer, run as before.
            mutex!.ReleaseMutex();
            try
            {
                AdminMode.StartViaService();
                return 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                if (!mutex.WaitOne(0)) return 0;
            }
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); // also what Desktop Duplication needs
        HostProcess.Initialize(setDpiAwareness: false);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // No console in a tray app: everything the host prints goes to host.log in HostConfig.Directory
        // (%APPDATA%\iScreen, or %ProgramData%\iScreen when the service runs us).
        var log = OpenLog();
        Console.SetOut(log);
        Console.SetError(log);
        Console.WriteLine(fromService ? "--- iScreen started (by the service, as SYSTEM) ---" : "--- iScreen started ---");
        Application.ThreadException += (_, e) => Console.WriteLine($"UI error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.WriteLine($"Fatal: {e.ExceptionObject}");

        // --bind is for local testing only (127.0.0.1 avoids the firewall prompt).
        int bindAt = Array.IndexOf(args, "--bind");
        var bind = bindAt >= 0 && bindAt + 1 < args.Length ? IPAddress.Parse(args[bindAt + 1]) : IPAddress.Any;

        // --no-autostart: don't apply the first-run "start with Windows" default (testing).
        var context = new TrayContext(bind, applyAutoStartDefault: !args.Contains("--no-autostart"),
            afterGpuRestart: replacing > 0, fromService);
        Application.Run(context);
        if (context.RestartRequested)
        {
            // The server is already closed (port free, resolution restored). Hand over now rather than at
            // exit: a stalled AMF teardown thread may keep this process from ending, and the new one kills it.
            // When the service runs us, it starts the next one itself, to keep track of it.
            mutex!.ReleaseMutex();
            if (!fromService || !AdminMode.RequestReplacement())
                Process.Start(Environment.ProcessPath!, RestartArgs(args));
        }
        return 0;
    }

    /// <summary>The single-instance mutex; null (not first) when the service's iScreen, as SYSTEM, holds it.</summary>
    static Mutex? CreateMutex(out bool firstInstance)
    {
        try
        {
            return new Mutex(true, @"Local\iScreen.Host", out firstInstance);
        }
        catch (UnauthorizedAccessException)
        {
            firstInstance = false;
            return null;
        }
    }

    static int PidAfter(string[] args, string flag) =>
        Array.IndexOf(args, flag) is var at && at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int pid)
            ? pid
            : 0;

    /// <summary>Waits for the iScreen we replace to release the mutex; if it hangs, ends it.</summary>
    static bool TakeOver(Mutex mutex, int pid)
    {
        try
        {
            if (mutex.WaitOne(TimeSpan.FromSeconds(10))) return true;
            EndPrevious(pid, waitMs: 0);
            return mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            return true; // it exited without releasing: the mutex is ours
        }
    }

    /// <summary>The replaced iScreen should exit right after handing over; kill it if it hangs.</summary>
    static void EndPrevious(int pid, int waitMs = 5000)
    {
        try
        {
            using var old = Process.GetProcessById(pid);
            if (old.ProcessName != Process.GetCurrentProcess().ProcessName) return; // pid already reused
            if (!old.WaitForExit(waitMs)) old.Kill();
        }
        catch (ArgumentException) { } // already gone
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Same arguments, with --gpu-restart pointing at this process.</summary>
    static List<string> RestartArgs(string[] args)
    {
        var kept = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == GpuRestartFlag) { i++; continue; }
            kept.Add(args[i]);
        }
        kept.Add(GpuRestartFlag);
        kept.Add(Environment.ProcessId.ToString());
        return kept;
    }

    static TextWriter OpenLog()
    {
        Directory.CreateDirectory(HostConfig.Directory);
        string path = HostConfig.LogPath;
        // Start over once the log passes 1 MB.
        var mode = File.Exists(path) && new FileInfo(path).Length > 1_000_000 ? FileMode.Create : FileMode.Append;
        var stream = new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite);
        return TextWriter.Synchronized(new TimestampedWriter(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true }));
    }

    sealed class TimestampedWriter(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;
        public override void Write(char value) => inner.Write(value);
        public override void Write(string? value) => inner.Write(value);
        public override void WriteLine(string? value) => inner.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {value}");
    }
}
