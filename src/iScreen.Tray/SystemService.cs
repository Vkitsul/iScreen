using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace IScreen.Tray;

/// <summary>
/// The "iScreen" Windows service (LocalSystem, session 0). It has no window and streams nothing: it keeps
/// iScreen running as SYSTEM on the desktop of whoever is logged on at the PC, the way Parsec does.
/// Running as SYSTEM is what lets iScreen show and click the UAC prompt and the lock screen (the secure
/// desktop) and control windows of programs run as administrator, which Windows shields from the input
/// of a normal user's programs (UIPI). Installed from the tray menu, see <see cref="AdminMode"/>.
/// </summary>
static class SystemService
{
    public const string Name = "iScreen";
    public const string RunFlag = "--service";
    /// <summary>Marks the iScreen this service starts (SYSTEM, on the user's desktop).</summary>
    public const string AgentFlag = "--from-service";
    /// <summary>Custom controls: start iScreen (a user opened it) / start a fresh one (it lost the GPU).</summary>
    public const int ControlStartAgent = 128, ControlReplaceAgent = 129;

    /// <summary>Set by the service to ask its iScreen to close (it is stopping, or the PC changed user).</summary>
    public static string QuitEventName(int pid) => $@"Global\iScreen.Quit.{pid}";

    /// <summary>
    /// %ProgramData%\iScreen: settings and logs of the iScreen run as SYSTEM. Not the user's AppData: a
    /// SYSTEM process writing where the user can plant links can be steered into overwriting system files.
    /// The installer locks it down (users may only read). See <see cref="AdminMode"/>.
    /// </summary>
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "iScreen");

    public static string LogPath => Path.Combine(DataDir, "service.log");

    static readonly Scm.ServiceMainProc MainProc = ServiceMain; // the SCM calls these: keep them alive
    static readonly Scm.HandlerProc Handler = OnControl;
    static readonly ManualResetEvent Stopping = new(false);
    static readonly Supervisor Agents = new();
    static IntPtr _status;
    static volatile bool _shutdown;

    /// <summary>Entry point when Windows starts the service; returns when it stops.</summary>
    public static int Run()
    {
        var table = new[]
        {
            new Scm.SERVICE_TABLE_ENTRY { Name = Name, Proc = Marshal.GetFunctionPointerForDelegate(MainProc) },
            default,
        };
        // Fails unless Windows started us as the service (then nothing is logged: DataDir may not exist yet).
        return Scm.StartServiceCtrlDispatcher(table) ? 0 : 1;
    }

    static void ServiceMain(int argc, IntPtr argv)
    {
        _status = Scm.RegisterServiceCtrlHandlerEx(Name, Handler, IntPtr.Zero);
        SetState(Scm.SERVICE_RUNNING);
        Log("--- service started ---");
        Agents.Run(Stopping, stopAgent: () => !_shutdown);
        Log("--- service stopped ---");
        SetState(Scm.SERVICE_STOPPED);
    }

    static int OnControl(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case Scm.SERVICE_CONTROL_STOP:
            case Scm.SERVICE_CONTROL_SHUTDOWN:
                // At shutdown Windows closes iScreen itself; nothing to restore.
                _shutdown = control == Scm.SERVICE_CONTROL_SHUTDOWN;
                SetState(Scm.SERVICE_STOP_PENDING, waitHintMs: 15_000);
                Stopping.Set();
                return 0;
            case Scm.SERVICE_CONTROL_SESSIONCHANGE:
                // WTSSESSION_NOTIFICATION { DWORD cbSize; DWORD dwSessionId; }
                Agents.OnSessionChange(eventType, (uint)Marshal.ReadInt32(eventData, 4));
                return 0;
            case ControlStartAgent:
                Agents.StartRequested();
                return 0;
            case ControlReplaceAgent:
                Agents.ReplaceRequested();
                return 0;
            case Scm.SERVICE_CONTROL_INTERROGATE:
                return 0;
        }
        return Scm.ERROR_CALL_NOT_IMPLEMENTED;
    }

    static int _checkPoint;

    static void SetState(int state, int waitHintMs = 0)
    {
        var status = new Scm.SERVICE_STATUS
        {
            dwServiceType = Scm.SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = state == Scm.SERVICE_RUNNING
                ? Scm.SERVICE_ACCEPT_STOP | Scm.SERVICE_ACCEPT_SHUTDOWN | Scm.SERVICE_ACCEPT_SESSIONCHANGE
                : 0,
            dwCheckPoint = state is Scm.SERVICE_RUNNING or Scm.SERVICE_STOPPED ? 0 : ++_checkPoint,
            dwWaitHint = waitHintMs,
        };
        Scm.SetServiceStatus(_status, ref status);
    }

    static readonly object LogLock = new();

    /// <summary>service.log in <see cref="DataDir"/>, shared with the installer.</summary>
    public static void Log(string message)
    {
        lock (LogLock)
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000) File.Delete(LogPath);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\r\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Keeps one iScreen running as SYSTEM in the console session while a user is logged on.</summary>
    sealed class Supervisor
    {
        sealed record Agent(SafeProcessHandle Process, int Pid, uint Session, long StartedMs);

        readonly object _lock = new();
        readonly AutoResetEvent _wake = new(false);
        Agent? _agent;
        uint _closedIn = Sessions.None; // the user closed iScreen in this session: leave it closed
        bool _replace;
        int _failures;
        long _notBeforeMs;
        long _userSinceMs = -1; // when the console session was first seen with a user but no taskbar yet

        public void Run(WaitHandle stopping, Func<bool> stopAgent)
        {
            while (WaitHandle.WaitAny([stopping, _wake], 1000) != 0)
            {
                lock (_lock)
                {
                    try { Check(); }
                    catch (Exception e) { Log($"supervisor: {e}"); }
                }
            }
            lock (_lock)
                if (stopAgent()) Close("the service is stopping");
        }

        public void OnSessionChange(int type, uint session)
        {
            Log($"session {session}: {type switch { 1 => "console connect", 2 => "console disconnect", 5 => "logon", 6 => "logoff", 7 => "lock", 8 => "unlock", _ => $"event {type}" }}");
            if (type == WTS_SESSION_LOGON)
                lock (_lock) _closedIn = Sessions.None;
            _wake.Set();
        }

        public void StartRequested()
        {
            lock (_lock)
            {
                _closedIn = Sessions.None;
                _notBeforeMs = 0;
            }
            _wake.Set();
        }

        public void ReplaceRequested()
        {
            lock (_lock) _replace = true;
            _wake.Set();
        }

        void Check()
        {
            long now = Environment.TickCount64;
            uint console = Sessions.Console;
            if (console == Sessions.None) return; // the screen is switching sessions: look again shortly
            bool hasUser = Sessions.HasUser(console);

            if (_replace)
            {
                // Asked by iScreen itself before it exits (GPU lost): don't take that exit for "closed".
                _replace = false;
                int old = _agent?.Pid ?? 0;
                Forget();
                Log($"iScreen (pid {old}) asked for a fresh process");
                if (hasUser) Launch(console, old > 0 ? $"--gpu-restart {old}" : null);
                return;
            }

            if (_agent is { } a && Sessions.WaitForExit(a.Process, 0))
            {
                uint code = Sessions.ExitCode(a.Process);
                Forget();
                if (code == 0)
                {
                    Log($"iScreen (pid {a.Pid}) was closed");
                    _closedIn = a.Session;
                }
                else
                {
                    _failures = now - a.StartedMs > 60_000 ? 1 : _failures + 1;
                    int delayMs = Math.Min(60_000, 1000 << Math.Min(_failures, 6));
                    _notBeforeMs = now + delayMs;
                    Log($"iScreen (pid {a.Pid}) ended with code 0x{code:X}: starting it again in {delayMs / 1000} s");
                }
            }

            if (_agent is { } current && (current.Session != console || !hasUser))
                Close($"session {current.Session} is no longer the one on the screen");

            if (!hasUser || _agent is not null || console == _closedIn || now < _notBeforeMs)
            {
                if (!hasUser) _userSinceMs = -1;
                return;
            }
            // Right after logon give Explorer a moment, so the tray icon has a taskbar to go to.
            if (_userSinceMs < 0) _userSinceMs = now;
            if (!Sessions.HasShell(console) && now - _userSinceMs < 30_000) return;
            Launch(console, null);
        }

        void Launch(uint session, string? extraArgs)
        {
            string command = $"\"{Environment.ProcessPath}\" {AgentFlag}" + (extraArgs is null ? "" : " " + extraArgs);
            try
            {
                var process = Sessions.StartAsSystem(session, command, out int pid);
                _agent = new Agent(process, pid, session, Environment.TickCount64);
                Log($"started iScreen in session {session} (pid {pid})");
            }
            catch (Exception e)
            {
                Log($"could not start iScreen in session {session}: {e.Message}");
                _notBeforeMs = Environment.TickCount64 + 10_000;
            }
        }

        /// <summary>Asks iScreen to close (it restores the resolution); ends it if it doesn't.</summary>
        void Close(string reason)
        {
            if (_agent is not { } a) return;
            Log($"closing iScreen (pid {a.Pid}): {reason}");
            if (EventWaitHandle.TryOpenExisting(QuitEventName(a.Pid), out var quit))
                using (quit) quit.Set();
            if (!Sessions.WaitForExit(a.Process, 8000))
            {
                Log($"iScreen (pid {a.Pid}) did not close: ending it");
                Sessions.Kill(a.Process);
            }
            Forget();
        }

        void Forget()
        {
            _agent?.Process.Dispose();
            _agent = null;
        }

        const int WTS_SESSION_LOGON = 5;
    }
}
