using IScreen.Host.Capture;
using IScreen.Host.Native;
using static IScreen.Host.Native.Win32;

namespace IScreen.Host.Input;

/// <summary>Injects client input with SendInput. Keys arrive as PC set-1 scancodes (0x100 = E0 prefix).</summary>
public sealed class InputInjector(string monitorDevice)
{
    readonly HashSet<ushort> _keysDown = new();
    readonly HashSet<byte> _buttonsDown = new();
    float _wheelX, _wheelY;
    readonly INPUT[] _one = new INPUT[1];
    (int Left, int Top, int Width, int Height) _rect;
    long _rectCheckedAt;

    /// <summary>The monitor's current position/size: it changes when iScreen adapts the resolution.</summary>
    (int Left, int Top, int Width, int Height) Rect()
    {
        long now = Environment.TickCount64;
        if (_rect.Width == 0 || now - _rectCheckedAt > 250)
        {
            if (DisplayModes.CurrentRect(monitorDevice) is { } r) _rect = r;
            _rectCheckedAt = now;
        }
        return _rect;
    }

    public void MouseMove(float nx, float ny)
    {
        var (left, top, width, height) = Rect();
        int px = left + (int)MathF.Round(Math.Clamp(nx, 0, 1) * (width - 1));
        int py = top + (int)MathF.Round(Math.Clamp(ny, 0, 1) * (height - 1));
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        Mouse(new MOUSEINPUT
        {
            dx = (int)((px - vx) * 65535L / Math.Max(1, vw - 1)),
            dy = (int)((py - vy) * 65535L / Math.Max(1, vh - 1)),
            dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
        });
    }

    public void MouseButton(byte button, bool down)
    {
        if (down) _buttonsDown.Add(button); else _buttonsDown.Remove(button);
        var (flags, data) = (button, down) switch
        {
            (0, true) => (MOUSEEVENTF_LEFTDOWN, 0),
            (0, false) => (MOUSEEVENTF_LEFTUP, 0),
            (1, true) => (MOUSEEVENTF_RIGHTDOWN, 0),
            (1, false) => (MOUSEEVENTF_RIGHTUP, 0),
            (2, true) => (MOUSEEVENTF_MIDDLEDOWN, 0),
            (2, false) => (MOUSEEVENTF_MIDDLEUP, 0),
            (3, _) => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 1),
            (4, _) => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 2),
            _ => (0u, 0),
        };
        if (flags != 0) Mouse(new MOUSEINPUT { dwFlags = flags, mouseData = data });
    }

    /// <summary>Deltas in notches (1 = 120 Windows units); fractions accumulate for smooth scrolling.</summary>
    public void Wheel(float dx, float dy)
    {
        _wheelY += dy * 120;
        _wheelX += dx * 120;
        int y = (int)_wheelY, x = (int)_wheelX;
        if (y != 0) { Mouse(new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = y }); _wheelY -= y; }
        if (x != 0) { Mouse(new MOUSEINPUT { dwFlags = MOUSEEVENTF_HWHEEL, mouseData = x }); _wheelX -= x; }
    }

    public void Key(ushort scancode, bool down)
    {
        if (down) _keysDown.Add(scancode); else _keysDown.Remove(scancode);
        uint flags = KEYEVENTF_SCANCODE;
        if ((scancode & 0x100) != 0) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;
        _one[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wScan = (ushort)(scancode & 0xFF), dwFlags = flags } },
        };
        Send();
    }

    /// <summary>Releases everything still held, so a dropped client doesn't leave stuck keys.</summary>
    public void ReleaseAll()
    {
        foreach (var k in _keysDown.ToArray()) Key(k, false);
        foreach (var b in _buttonsDown.ToArray()) MouseButton(b, false);
    }

    void Mouse(MOUSEINPUT mi)
    {
        _one[0] = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = mi } };
        Send();
    }

    void Send()
    {
        int size = System.Runtime.InteropServices.Marshal.SizeOf<INPUT>();
        if (SendInput(1, _one, size) == 1) return;
        // Refused because the input desktop changed (UAC prompt, lock screen): follow it if allowed, retry.
        if (InputDesktop.Sync()) SendInput(1, _one, size);
    }
}
