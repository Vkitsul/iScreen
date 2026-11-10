using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using IScreen.Host.Net;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace IScreen.Tray;

/// <summary>
/// "Admin mode": iScreen installed as a Windows service (<see cref="SystemService"/>) that runs it as SYSTEM,
/// so the Mac can use windows of programs run as administrator and answer UAC prompts. Installing takes
/// administrator rights once. The files go to Program Files: the service runs them as SYSTEM, and from a
/// folder the user can write to any program could swap them and gain SYSTEM rights.
/// </summary>
static class AdminMode
{
    public const string InstallFlag = "--install-service";
    public const string UninstallFlag = "--uninstall-service";

    static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "iScreen");
    const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + SystemService.Name;
    const string ParametersKey = ServiceKey + @"\Parameters";

    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
            return key is not null;
        }
    }

    /// <summary>"Avvia con Windows" for the iScreen the service runs: the service's start type.</summary>
    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
            return key?.GetValue("Start") is Scm.SERVICE_AUTO_START;
        }
        set
        {
            using var service = Scm.Open(Scm.SERVICE_CHANGE_CONFIG);
            Scm.SetStartType(service, value ? Scm.SERVICE_AUTO_START : Scm.SERVICE_DEMAND_START);
        }
    }

    /// <summary>A user opened iScreen while the service runs it: have the service start it (again).</summary>
    public static void StartViaService()
    {
        using var service = Scm.Open(Scm.SERVICE_QUERY_STATUS | Scm.SERVICE_START | Scm.SERVICE_USER_DEFINED_CONTROL);
        if (Scm.State(service) == Scm.SERVICE_RUNNING) Scm.Control(service, SystemService.ControlStartAgent);
        else Scm.Start(service);
    }

    /// <summary>The service's iScreen lost the GPU: the service starts the next one, so it can keep track of it.</summary>
    public static bool RequestReplacement()
    {
        try
        {
            using var service = Scm.Open(Scm.SERVICE_USER_DEFINED_CONTROL);
            Scm.Control(service, SystemService.ControlReplaceAgent);
            return true;
        }
        catch (Win32Exception e)
        {
            Console.WriteLine($"the service did not take the restart ({e.Message}): restarting by myself");
            return false;
        }
    }

    /// <summary>
    /// Runs elevated (UAC), started from the menu of the per-user iScreen, which closes to free the ports:
    /// copy to Program Files, register and start the service, check that its iScreen listens. If it
    /// doesn't, everything goes back to the per-user iScreen.
    /// </summary>
    public static int Install(int trayPid)
    {
        string userApp = Environment.ProcessPath!;
        bool fresh = !IsInstalled;
        WaitForExit(trayPid);
        try
        {
            PrepareDataDir();
            Log($"--- installing from {AppContext.BaseDirectory} ---");
            if (!fresh) StopService();
            EndOtherInstances();
            CopyFiles(AppContext.BaseDirectory, InstallDir);

            string exe = Path.Combine(InstallDir, "iScreen.exe");
            AllowThroughFirewall(exe);
            string binary = $"\"{exe}\" {SystemService.RunFlag}";
            if (fresh) Scm.Create(binary);
            else Scm.Update(binary);
            if (!InInstallDir(userApp))
                using (var parameters = Registry.LocalMachine.CreateSubKey(ParametersKey))
                    parameters.SetValue("UserApp", userApp); // where to go back to (Uninstall)
            AutoStart.Enabled = false; // the service starts iScreen now; we run as the same user, elevated

            using (var service = Scm.Open(Scm.SERVICE_START | Scm.SERVICE_QUERY_STATUS))
                Scm.Start(service);
            if (!WaitForListener(TimeSpan.FromSeconds(40)))
                throw new TimeoutException("il servizio è partito, ma iScreen non si è messo in ascolto entro 40 secondi");
            Log("installed, iScreen is listening");
            MessageBox.Show(
                "Fatto: iScreen ora gira come servizio. Dal Mac puoi usare anche le finestre aperte come " +
                "amministratore e rispondere alle richieste di Controllo account utente.\n\n" +
                "Ricollegati dall'app iScreen sul Mac.",
                "iScreen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception e)
        {
            Log($"install failed: {e}");
            if (fresh)
            {
                TryRemoveService();
                Netsh($"advfirewall firewall delete rule name=\"{FirewallRule}\"");
                AutoStart.Enabled = true;
                StartUnelevated(userApp);
            }
            else
            {
                TryStartService(); // an update that failed: the service may still run what it had
            }
            MessageBox.Show(
                $"Non sono riuscito ad attivare il controllo delle app da amministratore:\n{e.Message}\n\n" +
                (fresh ? "iScreen continua a funzionare come prima. " : "") + $"Dettagli in {SystemService.LogPath}",
                "iScreen", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>
    /// Back to the per-user iScreen. Runs as SYSTEM (started from the menu of the service's iScreen) or
    /// elevated from a command line.
    /// </summary>
    public static int Uninstall()
    {
        try
        {
            Log("--- uninstalling ---");
            string userApp;
            using (var parameters = Registry.LocalMachine.OpenSubKey(ParametersKey))
                userApp = parameters?.GetValue("UserApp") as string ?? "";
            if (!File.Exists(userApp)) userApp = Path.Combine(InstallDir, "iScreen.exe");

            StopService();
            EndOtherInstances();
            using (var service = Scm.Open(Scm.DELETE))
                Scm.Delete(service);
            Netsh($"advfirewall firewall delete rule name=\"{FirewallRule}\"");

            uint session = Sessions.Console;
            if (Sessions.IsSystem)
            {
                // HKCU here would be SYSTEM's: write to the hive of the user at the PC.
                if (Sessions.UserSid(session) is { } sid)
                    using (var run = Registry.Users.CreateSubKey($@"{sid}\{AutoStart.RunKey}"))
                        run.SetValue(AutoStart.ValueName, $"\"{userApp}\"");
                Sessions.StartAsUser(session, $"\"{userApp}\"");
            }
            else
            {
                using (var run = Registry.CurrentUser.CreateSubKey(AutoStart.RunKey))
                    run.SetValue(AutoStart.ValueName, $"\"{userApp}\"");
                StartUnelevated(userApp);
            }
            Log($"uninstalled, back to {userApp}");
            MessageBox.Show(
                "Fatto: iScreen è tornato a girare come una normale app.", "iScreen",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception e)
        {
            Log($"uninstall failed: {e}");
            MessageBox.Show($"Non sono riuscito a togliere il servizio iScreen:\n{e.Message}", "iScreen",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static void Log(string message) => SystemService.Log($"setup: {message}");

    const string FirewallRule = "iScreen (servizio)";

    /// <summary>
    /// Firewall rules follow the program's path, and the copy in Program Files is a new one: let the Mac
    /// reach its two ports, on private networks only (the home network is one).
    /// </summary>
    static void AllowThroughFirewall(string exe)
    {
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRule}\""); // an update: no duplicates
        foreach (var (protocol, port) in new[] { ("TCP", Protocol.ControlPort), ("UDP", Protocol.VideoPort) })
        {
            if (Netsh($"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow " +
                      $"program=\"{exe}\" protocol={protocol} localport={port} profile=private") != 0)
                throw new InvalidOperationException("non riesco ad aprire le porte di iScreen nel firewall di Windows");
        }
        Log($"firewall: allowed TCP {Protocol.ControlPort} and UDP {Protocol.VideoPort} on private networks");
    }

    static int Netsh(string arguments)
    {
        using var netsh = Process.Start(new ProcessStartInfo("netsh.exe", arguments) { CreateNoWindow = true, UseShellExecute = false })!;
        netsh.WaitForExit();
        return netsh.ExitCode;
    }

    /// <summary>
    /// The service's folder (<see cref="SystemService.DataDir"/>): only SYSTEM and administrators may write
    /// there, users may read (the log). The user's settings and PIN come along on the first install.
    /// </summary>
    static void PrepareDataDir()
    {
        var dir = Directory.CreateDirectory(SystemService.DataDir);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        void Allow(WellKnownSidType who, FileSystemRights rights) => security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(who, null), rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
        dir.SetAccessControl(security);

        string config = Path.Combine(dir.FullName, "host.json");
        string userConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iScreen", "host.json");
        if (!File.Exists(config) && File.Exists(userConfig)) File.Copy(userConfig, config);
    }

    static bool InInstallDir(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), InstallDir, StringComparison.OrdinalIgnoreCase);

    static void WaitForExit(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var tray = Process.GetProcessById(pid);
            if (!tray.WaitForExit(15_000)) tray.Kill();
        }
        catch (ArgumentException) { } // already gone
    }

    /// <summary>Stops the service, which first closes the iScreen it runs.</summary>
    static void StopService()
    {
        using var service = Scm.Open(Scm.SERVICE_STOP | Scm.SERVICE_QUERY_STATUS);
        if (Scm.State(service) != Scm.SERVICE_STOPPED) Scm.Control(service, Scm.SERVICE_CONTROL_STOP);
        var sw = Stopwatch.StartNew();
        while (Scm.State(service) != Scm.SERVICE_STOPPED)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("il servizio iScreen non si ferma");
            Thread.Sleep(250);
        }
    }

    /// <summary>Any other iScreen still around holds the ports or the files: end it.</summary>
    static void EndOtherInstances()
    {
        try { Process.EnterDebugMode(); } catch (Exception) { } // to end one running as SYSTEM
        foreach (var p in Process.GetProcessesByName("iScreen"))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                    Log($"ended iScreen pid {p.Id}");
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            }
        }
    }

    static void CopyFiles(string from, string to)
    {
        if (string.Equals(Path.GetFullPath(from).TrimEnd('\\'), to, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(file, target, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(500); // a process that just ended may still hold it for a moment
                }
            }
        }
        Log($"copied {from} to {to}");
    }

    /// <summary>
    /// Connects to the control port. Not the TCP table (GetActiveTcpListeners, netstat): on this PC's
    /// Windows build (26300) it leaves out iScreen's listener, and the first install was undone for that.
    /// </summary>
    static bool WaitForListener(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            using var probe = new TcpClient();
            try
            {
                probe.Connect(IPAddress.Loopback, Protocol.ControlPort);
                return true;
            }
            catch (SocketException)
            {
                Thread.Sleep(500);
            }
        }
        return false;
    }

    static void TryStartService()
    {
        try
        {
            using var service = Scm.Open(Scm.SERVICE_START);
            Scm.Start(service);
        }
        catch (Win32Exception e)
        {
            Log($"could not start the service again: {e.Message}");
        }
    }

    static void TryRemoveService()
    {
        try
        {
            StopService();
            using var service = Scm.Open(Scm.DELETE);
            Scm.Delete(service);
        }
        catch (Exception e)
        {
            Log($"could not remove the service: {e.Message}");
        }
    }

    /// <summary>From an elevated process: Explorer starts it, as the user and not elevated.</summary>
    static void StartUnelevated(string path)
    {
        try { Process.Start("explorer.exe", $"\"{path}\""); }
        catch (Win32Exception e) { Log($"could not start {path}: {e.Message}"); }
    }
}

/// <summary>Service Control Manager calls, for the service itself and for installing and driving it.</summary>
static class Scm
{
    public sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Handle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    /// <summary>
    /// Interactive users (IU) may also start the service (RP) and send it its own controls (CR), so a
    /// per-user iScreen can ask it to start iScreen again after the user closed it. The rest is the default.
    /// </summary>
    const string Sddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)" +
                        "(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    public static Handle Open(int access)
    {
        using var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm.IsInvalid) throw new Win32Exception();
        var service = OpenService(scm, SystemService.Name, access);
        if (service.IsInvalid) throw new Win32Exception();
        return service;
    }

    public static void Create(string binary)
    {
        using var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
        if (scm.IsInvalid) throw new Win32Exception();
        using var service = CreateService(scm, SystemService.Name, "iScreen", SERVICE_ALL_ACCESS,
            SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, binary, null, IntPtr.Zero, null, null, null);
        if (service.IsInvalid) throw new Win32Exception();
        Configure(service);
    }

    public static void Update(string binary)
    {
        using var service = Open(SERVICE_ALL_ACCESS);
        if (!ChangeServiceConfig(service, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE, binary,
                null, IntPtr.Zero, null, null, null, null))
            throw new Win32Exception();
        Configure(service);
    }

    static void Configure(Handle service)
    {
        var description = new SERVICE_DESCRIPTION
        {
            Text = "Fa girare iScreen come SYSTEM sul desktop dell'utente, per controllare dal Mac anche le " +
                   "app da amministratore e le richieste di Controllo account utente.",
        };
        if (!ChangeServiceConfig2(service, SERVICE_CONFIG_DESCRIPTION, ref description)) throw new Win32Exception();

        // If the service itself crashes, Windows restarts it after 5 s (the counter resets after a day).
        IntPtr actions = Marshal.AllocHGlobal(3 * 8);
        try
        {
            for (int i = 0; i < 3; i++)
            {
                Marshal.WriteInt32(actions, i * 8, SC_ACTION_RESTART);
                Marshal.WriteInt32(actions, i * 8 + 4, 5000);
            }
            var failure = new SERVICE_FAILURE_ACTIONS { dwResetPeriod = 86_400, cActions = 3, lpsaActions = actions };
            if (!ChangeServiceConfig2(service, SERVICE_CONFIG_FAILURE_ACTIONS, ref failure)) throw new Win32Exception();
        }
        finally
        {
            Marshal.FreeHGlobal(actions);
        }

        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(Sddl, 1, out IntPtr sd, IntPtr.Zero))
            throw new Win32Exception();
        try
        {
            if (!SetServiceObjectSecurity(service, DACL_SECURITY_INFORMATION, sd)) throw new Win32Exception();
        }
        finally
        {
            LocalFree(sd);
        }
    }

    public static void SetStartType(Handle service, int startType)
    {
        if (!ChangeServiceConfig(service, SERVICE_NO_CHANGE, startType, SERVICE_NO_CHANGE, null, null, IntPtr.Zero,
                null, null, null, null))
            throw new Win32Exception();
    }

    public static int State(Handle service) =>
        QueryServiceStatus(service, out var status) ? status.dwCurrentState : throw new Win32Exception();

    public static void Start(Handle service)
    {
        if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != ERROR_SERVICE_ALREADY_RUNNING)
            throw new Win32Exception();
    }

    public static void Control(Handle service, int control)
    {
        if (!ControlService(service, control, out _) && Marshal.GetLastWin32Error() != ERROR_SERVICE_NOT_ACTIVE)
            throw new Win32Exception();
    }

    public static void Delete(Handle service)
    {
        if (!DeleteService(service) && Marshal.GetLastWin32Error() != ERROR_SERVICE_MARKED_FOR_DELETE)
            throw new Win32Exception();
    }

    public const int SC_MANAGER_CONNECT = 0x1, SC_MANAGER_CREATE_SERVICE = 0x2;
    public const int SERVICE_QUERY_STATUS = 0x4, SERVICE_CHANGE_CONFIG = 0x2, SERVICE_START = 0x10, SERVICE_STOP = 0x20,
        SERVICE_USER_DEFINED_CONTROL = 0x100, DELETE = 0x10000, SERVICE_ALL_ACCESS = 0xF01FF;
    public const int SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_AUTO_START = 2, SERVICE_DEMAND_START = 3,
        SERVICE_ERROR_NORMAL = 1, SERVICE_NO_CHANGE = -1;
    public const int SERVICE_STOPPED = 1, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4;
    public const int SERVICE_ACCEPT_STOP = 0x1, SERVICE_ACCEPT_SHUTDOWN = 0x4, SERVICE_ACCEPT_SESSIONCHANGE = 0x80;
    public const int SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_INTERROGATE = 4, SERVICE_CONTROL_SHUTDOWN = 5,
        SERVICE_CONTROL_SESSIONCHANGE = 0xE;
    public const int ERROR_CALL_NOT_IMPLEMENTED = 120, ERROR_SERVICE_ALREADY_RUNNING = 1056,
        ERROR_SERVICE_NOT_ACTIVE = 1062, ERROR_SERVICE_MARKED_FOR_DELETE = 1072;
    const int SERVICE_CONFIG_DESCRIPTION = 1, SERVICE_CONFIG_FAILURE_ACTIONS = 2, SC_ACTION_RESTART = 1;
    const int DACL_SECURITY_INFORMATION = 4;

    public delegate void ServiceMainProc(int argc, IntPtr argv);
    public delegate int HandlerProc(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_TABLE_ENTRY
    {
        public string? Name;
        public IntPtr Proc;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode,
            dwCheckPoint, dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SERVICE_DESCRIPTION
    {
        public string? Text;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SERVICE_FAILURE_ACTIONS
    {
        public int dwResetPeriod;
        public string? lpRebootMsg, lpCommand;
        public int cActions;
        public IntPtr lpsaActions;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool StartServiceCtrlDispatcher(SERVICE_TABLE_ENTRY[] table);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerProc handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    static extern Handle OpenSCManager(string? machine, string? database, int access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    static extern Handle OpenService(Handle scm, string name, int access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateServiceW")]
    static extern Handle CreateService(Handle scm, string name, string displayName, int access, int type, int start,
        int errorControl, string binary, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account,
        string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfigW")]
    static extern bool ChangeServiceConfig(Handle service, int type, int start, int errorControl, string? binary,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfig2W")]
    static extern bool ChangeServiceConfig2(Handle service, int level, ref SERVICE_DESCRIPTION info);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfig2W")]
    static extern bool ChangeServiceConfig2(Handle service, int level, ref SERVICE_FAILURE_ACTIONS info);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
        EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, int revision, out IntPtr sd, IntPtr size);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetServiceObjectSecurity(Handle service, int info, IntPtr sd);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatus(Handle service, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "StartServiceW")]
    static extern bool StartService(Handle service, int argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ControlService(Handle service, int control, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DeleteService(Handle service);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseServiceHandle(IntPtr handle);
}
