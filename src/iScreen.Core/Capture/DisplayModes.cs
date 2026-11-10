using System.Runtime.InteropServices;

namespace IScreen.Host.Capture;

public sealed record DisplayMode(int Width, int Height, int Frequency, uint Orientation)
{
    public double Aspect => Width / (double)Height;
    public override string ToString() => $"{Width}x{Height}@{Frequency}";
}

/// <summary>
/// Temporary resolution changes so the streamed desktop has the client's aspect ratio (fills the Mac
/// screen). Changes use CDS_FULLSCREEN: never written to the registry, so the user's own setting is
/// what <see cref="Restore"/> goes back to.
/// </summary>
public static class DisplayModes
{
    public static DisplayMode? Current(string device) => Get(device, ENUM_CURRENT_SETTINGS);

    /// <summary>The mode saved in Windows settings (what Restore returns to).</summary>
    public static DisplayMode? Saved(string device) => Get(device, ENUM_REGISTRY_SETTINGS);

    public static (int Left, int Top, int Width, int Height)? CurrentRect(string device)
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettingsEx(device, ENUM_CURRENT_SETTINGS, ref dm, 0)) return null;
        return (dm.dmPositionX, dm.dmPositionY, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
    }

    /// <summary>Modes the monitor accepts in 32-bit colour, best refresh rate per size.</summary>
    public static List<DisplayMode> List(string device)
    {
        var all = new List<DisplayMode>();
        var dm = NewDevMode();
        for (int i = 0; EnumDisplaySettingsEx(device, i, ref dm, 0); i++)
        {
            if (dm.dmBitsPerPel == 32)
                all.Add(new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency, dm.dmDisplayOrientation));
            dm = NewDevMode();
        }
        return all
            .GroupBy(m => (m.Width, m.Height, m.Orientation))
            .Select(g => g.OrderByDescending(m => m.Frequency).First())
            .OrderByDescending(m => m.Width * m.Height)
            .ToList();
    }

    /// <summary>
    /// The mode whose aspect ratio is closest to the client's screen, as large as possible without
    /// exceeding it; null when the current mode is already as close as it gets.
    /// </summary>
    public static DisplayMode? BestFor(string device, int clientWidth, int clientHeight)
    {
        var current = Current(device);
        if (current is null || clientWidth <= 0 || clientHeight <= 0) return null;
        if (current.Width >= current.Height != clientWidth >= clientHeight) return null; // portrait monitor vs landscape Mac
        double target = clientWidth / (double)clientHeight;
        double Error(DisplayMode m) => Math.Abs(m.Aspect - target) / target;

        // Same orientation, a usable size, and not a slower refresh than the current one.
        var candidates = List(device)
            .Where(m => m.Orientation == current.Orientation && m.Height >= 720 &&
                        m.Frequency >= Math.Min(current.Frequency, 60))
            .ToList();
        if (candidates.Count == 0) return null;

        double bestError = candidates.Min(Error);
        if (bestError >= Error(current) - 0.005) return null; // nothing fits better than what we have

        var closest = candidates.Where(m => Error(m) <= bestError + 0.01).ToList();
        var fitting = closest.Where(m => m.Width <= clientWidth && m.Height <= clientHeight).ToList();
        var pick = fitting.Count > 0
            ? fitting.MaxBy(m => m.Width * m.Height)!
            : closest.MinBy(m => m.Width * m.Height)!;
        // Keep the refresh rate the user has, if this size supports it.
        return pick.Frequency >= current.Frequency ? pick with { Frequency = current.Frequency } : pick;
    }

    public static bool Apply(string device, DisplayMode mode)
    {
        var dm = NewDevMode();
        dm.dmPelsWidth = (uint)mode.Width;
        dm.dmPelsHeight = (uint)mode.Height;
        dm.dmDisplayFrequency = (uint)mode.Frequency;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero);
        if (r != DISP_CHANGE_SUCCESSFUL)
        {
            // Some drivers reject the exact refresh rate: let Windows choose one.
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
            r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero);
        }
        return r == DISP_CHANGE_SUCCESSFUL;
    }

    /// <summary>Back to the resolution saved in Windows settings.</summary>
    public static bool Restore(string device) =>
        ChangeDisplaySettingsEx(device, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL;

    static DisplayMode? Get(string device, int which)
    {
        var dm = NewDevMode();
        return EnumDisplaySettingsEx(device, which, ref dm, 0)
            ? new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency, dm.dmDisplayOrientation)
            : null;
    }

    static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    const int ENUM_CURRENT_SETTINGS = -1, ENUM_REGISTRY_SETTINGS = -2;
    const uint DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    const uint CDS_FULLSCREEN = 0x4;
    const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettingsEx(string device, int modeNum, ref DEVMODE devMode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string device, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr param);
}
