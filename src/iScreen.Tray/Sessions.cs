using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IScreen.Tray;

/// <summary>
/// Sessions, tokens and processes across accounts: for the service (SYSTEM, session 0) starting iScreen
/// on the user's desktop, and for that iScreen (SYSTEM, in the user's session) acting for the user.
/// </summary>
static class Sessions
{
    public const uint None = 0xFFFFFFFF;

    public static bool IsSystem
    {
        get
        {
            using var me = WindowsIdentity.GetCurrent();
            return me.IsSystem;
        }
    }

    public static uint Own => (uint)Process.GetCurrentProcess().SessionId;

    /// <summary>The session on the PC's own screen (whoever sits there); None while it changes.</summary>
    public static uint Console => WTSGetActiveConsoleSessionId();

    /// <summary>A user is logged on to the session (not just the logon screen). Needs SYSTEM.</summary>
    public static bool HasUser(uint session)
    {
        if (!WTSQueryUserToken(session, out var token)) return false;
        token.Dispose();
        return true;
    }

    /// <summary>Explorer runs in the session: the taskbar is there for the tray icon.</summary>
    public static bool HasShell(uint session)
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
            using (p)
                if (p.SessionId == session) return true;
        return false;
    }

    /// <summary>The SID of the session's user (the name of their registry hive), seen from SYSTEM.</summary>
    public static string? UserSid(uint session)
    {
        if (!WTSQueryUserToken(session, out var token)) return null;
        using (token)
        using (var user = new WindowsIdentity(token.DangerousGetHandle()))
            return user.User?.Value;
    }

    /// <summary>
    /// Starts a program as the user logged on to the session. iScreen running as SYSTEM opens everything
    /// for the user this way: a SYSTEM Notepad would hand them a file dialog with SYSTEM rights.
    /// </summary>
    public static void StartAsUser(uint session, string commandLine)
    {
        if (!WTSQueryUserToken(session, out var token)) throw new Win32Exception();
        using (token)
        {
            if (!CreateEnvironmentBlock(out IntPtr environment, token, false)) environment = IntPtr.Zero;
            try
            {
                Start(token, commandLine, environment, out _).Dispose();
            }
            finally
            {
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            }
        }
    }

    /// <summary>Starts a program as SYSTEM, like this process, on the user's desktop of a session.</summary>
    public static SafeProcessHandle StartAsSystem(uint session, string commandLine, out int pid)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out var own)) throw new Win32Exception();
        using (own)
        {
            if (!DuplicateTokenEx(own, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var token))
                throw new Win32Exception();
            using (token)
            {
                if (!SetTokenInformation(token, TokenSessionId, ref session, sizeof(uint))) throw new Win32Exception();
                return Start(token, commandLine, IntPtr.Zero, out pid);
            }
        }
    }

    static SafeProcessHandle Start(SafeAccessTokenHandle token, string commandLine, IntPtr environment, out int pid)
    {
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
        if (!CreateProcessAsUser(token, null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT, environment, AppContext.BaseDirectory, ref startup, out var info))
            throw new Win32Exception();
        CloseHandle(info.hThread);
        pid = info.dwProcessId;
        return new SafeProcessHandle(info.hProcess, ownsHandle: true);
    }

    /// <summary>Waits for a process started here; true once it has ended.</summary>
    public static bool WaitForExit(SafeProcessHandle process, int timeoutMs) => WaitForSingleObject(process, (uint)timeoutMs) == 0;

    public static uint ExitCode(SafeProcessHandle process) => GetExitCodeProcess(process, out uint code) ? code : 0xFFFFFFFF;

    public static void Kill(SafeProcessHandle process) => TerminateProcess(process, 1);

    const uint TOKEN_ALL_ACCESS = 0xF01FF, MAXIMUM_ALLOWED = 0x02000000, CREATE_UNICODE_ENVIRONMENT = 0x400;
    const int SecurityImpersonation = 2, TokenPrimary = 1, TokenSessionId = 12;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);

    [DllImport("userenv.dll")]
    static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level,
        int type, out SafeAccessTokenHandle duplicate);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetTokenInformation(SafeAccessTokenHandle token, int infoClass, ref uint info, int length);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string? application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment,
        string? directory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint code);

    [DllImport("kernel32.dll")]
    static extern bool TerminateProcess(SafeProcessHandle handle, uint code);
}
