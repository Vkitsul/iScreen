using System.Runtime.InteropServices;

namespace IScreen.Host.Native;

/// <summary>
/// Follows the desktop that receives input. The UAC prompt, the lock screen and Ctrl+Alt+Del run on the
/// secure "Winlogon" desktop: Desktop Duplication and SendInput only reach it from a thread attached to
/// it, and only a process running as SYSTEM may attach (iScreen does when its Windows service starts it).
/// For anyone else opening that desktop fails and nothing changes.
/// </summary>
public static unsafe partial class InputDesktop
{
    [ThreadStatic] static IntPtr _attached; // the desktop handle this thread opened and switched to
    [ThreadStatic] static string? _refused; // the last desktop this thread could not switch to (logged once)

    /// <summary>Moves the calling thread to the input desktop if it is on another one; true if it moved.</summary>
    public static bool Sync()
    {
        IntPtr input = OpenInputDesktop(0, false, GENERIC_ALL);
        if (input == IntPtr.Zero) return false;
        string? name = Name(input);
        if (name == Name(GetThreadDesktop(GetCurrentThreadId())))
        {
            CloseDesktop(input);
            return false;
        }
        if (!SetThreadDesktop(input))
        {
            // E.g. the thread owns a window: it must stay where it is.
            if (_refused != name)
                Console.WriteLine($"{Thread.CurrentThread.Name ?? "thread"}: cannot move to the {name} desktop (error {Marshal.GetLastPInvokeError()})");
            _refused = name;
            CloseDesktop(input);
            return false;
        }
        if (_attached != IntPtr.Zero) CloseDesktop(_attached);
        _attached = input;
        _refused = null;
        Console.WriteLine($"{Thread.CurrentThread.Name ?? "thread"}: now on the {name} desktop");
        return true;
    }

    static string? Name(IntPtr desktop)
    {
        char* buffer = stackalloc char[64];
        return GetUserObjectInformation(desktop, UOI_NAME, buffer, 64 * sizeof(char), out _)
            ? new string(buffer)
            : null;
    }

    const uint GENERIC_ALL = 0x10000000;
    const int UOI_NAME = 2;

    [LibraryImport("user32.dll")]
    private static partial IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetThreadDesktop(uint threadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadDesktop(IntPtr desktop);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(IntPtr desktop);

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformation(IntPtr obj, int index, char* info, uint length, out uint needed);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
