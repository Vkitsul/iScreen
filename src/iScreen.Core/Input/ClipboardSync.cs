using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace IScreen.Host.Input;

/// <summary>
/// Text clipboard of this PC: reports changes (<see cref="Changed"/>) and applies text from the
/// client (<see cref="Set"/>). Runs on its own thread with a message-only window, because Windows
/// needs a window to own clipboard data and expects the owner to answer clipboard messages.
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    public const int MaxChars = 2 * 1024 * 1024;

    readonly ConcurrentQueue<string> _incoming = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    volatile bool _stop;
    IntPtr _window;
    uint _lastSequence;

    /// <summary>Raised on the clipboard thread when someone on the PC copies text.</summary>
    public event Action<string>? Changed;

    public ClipboardSync()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Puts text from the client on the PC clipboard (applied on the clipboard thread).</summary>
    public void Set(string text)
    {
        _incoming.Enqueue(text);
        _wake.Set();
    }

    void Run()
    {
        _window = CreateWindowExW(0, "STATIC", "iScreen clipboard", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, 0, 0);
        _lastSequence = GetClipboardSequenceNumber();
        IntPtr[] handles = [_wake.SafeWaitHandle.DangerousGetHandle()];
        while (!_stop)
        {
            MsgWaitForMultipleObjects(1, handles, false, 250, QS_ALLINPUT);
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            while (_incoming.TryDequeue(out var text)) Write(text);

            uint sequence = GetClipboardSequenceNumber();
            if (sequence == _lastSequence) continue;
            _lastSequence = sequence;
            if (Read() is { Length: > 0 and <= MaxChars } copied)
                Changed?.Invoke(copied.Replace("\r\n", "\n"));
        }
        DestroyWindow(_window);
    }

    void Write(string text)
    {
        // Windows apps expect CRLF line breaks.
        text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        if (!Open()) return;
        try
        {
            EmptyClipboard();
            IntPtr handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)((text.Length + 1) * 2));
            IntPtr p = GlobalLock(handle);
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(handle);
            if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero) GlobalFree(handle);
        }
        finally
        {
            CloseClipboard();
        }
        // Our own change: don't send it back to the client.
        _lastSequence = GetClipboardSequenceNumber();
    }

    string? Read()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT) || !Open()) return null;
        try
        {
            IntPtr handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            IntPtr p = GlobalLock(handle);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(handle); }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Another app may be holding the clipboard for a moment: retry briefly.</summary>
    bool Open()
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(_window)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(1000);
        _wake.Dispose();
    }

    static readonly IntPtr HWND_MESSAGE = new(-3);
    const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 2, QS_ALLINPUT = 0x04FF, PM_REMOVE = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr handle);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint ms, uint wakeMask);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr handle);
}
