using System.Diagnostics;

namespace IScreen.Host;

public enum StreamMode : byte
{
    Yuv420 = 0,
    Yuv444Packed = 1,
}

/// <summary>Geometry of the encoded NV12 frame. See docs/ARCHITETTURA.md for the 4:4:4 packing.</summary>
public readonly record struct FrameLayout(int Width, int Height, int EncWidth, int EncHeight, int AuxOffset, StreamMode Mode)
{
    public static FrameLayout Create(int width, int height, StreamMode mode)
    {
        if ((width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException($"Source size must be even, got {width}x{height}");
        int encWidth = AlignUp(width, 16);
        if (mode == StreamMode.Yuv420)
            return new(width, height, encWidth, AlignUp(height, 16), 0, mode);
        int aux = AlignUp(height + 16, 64);
        return new(width, height, encWidth, AlignUp(aux + height, 16), aux, mode);
    }

    public static int AlignUp(int v, int a) => (v + a - 1) / a * a;
}

/// <summary>CIE 1931 xy chromaticities of a display.</summary>
public readonly record struct Primaries(float Rx, float Ry, float Gx, float Gy, float Bx, float By, float Wx, float Wy)
{
    public static readonly Primaries Srgb = new(0.640f, 0.330f, 0.300f, 0.600f, 0.150f, 0.060f, 0.3127f, 0.3290f);

    /// <summary>Gamut triangle area relative to sRGB.</summary>
    public double AreaVsSrgb => Area(this) / Area(Srgb);

    static double Area(Primaries p) =>
        Math.Abs(p.Rx * (p.Gy - p.By) + p.Gx * (p.By - p.Ry) + p.Bx * (p.Ry - p.Gy)) / 2;

    public override string ToString() =>
        $"R({Rx:0.000},{Ry:0.000}) G({Gx:0.000},{Gy:0.000}) B({Bx:0.000},{By:0.000}) W({Wx:0.0000},{Wy:0.0000})";
}

public static class HostProcess
{
    /// <summary>Process-wide setup every host executable needs before capturing.</summary>
    public static void Initialize(bool setDpiAwareness = true)
    {
        // Desktop Duplication (DuplicateOutput1) requires per-monitor-v2 DPI awareness.
        if (setDpiAwareness) Native.Win32.SetProcessDpiAwarenessContext(Native.Win32.DpiAwarenessPerMonitorV2);
        Native.Win32.DisablePowerThrottling();
        Native.Win32.timeBeginPeriod(1);
    }
}

public static class Clock
{
    static readonly long Freq = Stopwatch.Frequency;

    public static ulong NowUs => QpcToUs(Stopwatch.GetTimestamp());

    // DXGI LastPresentTime and Stopwatch both use QueryPerformanceCounter.
    public static ulong QpcToUs(long qpc) =>
        (ulong)(qpc / Freq * 1_000_000 + qpc % Freq * 1_000_000 / Freq);
}
