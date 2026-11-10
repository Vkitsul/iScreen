using Vortice.DXGI;

namespace IScreen.Host.Capture;

public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    string FriendlyName,
    string AdapterName,
    int Left, int Top, int Width, int Height,
    ModeRotation Rotation,
    bool HdrActive,
    uint BitsPerColor,
    Primaries Primaries,
    float MaxLuminance)
{
    public bool IsPrimary => Left == 0 && Top == 0;
    public bool IsRotated => Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified);

    public override string ToString() =>
        $"[{Index}] {FriendlyName} ({DeviceName}) {Width}x{Height} @({Left},{Top}) on {AdapterName}" +
        $"{(Rotation is ModeRotation.Identity or ModeRotation.Unspecified ? "" : $" rot={Rotation}")}" +
        $" {(HdrActive ? "HDR" : "SDR")} {BitsPerColor}bpc gamut={Primaries.AreaVsSrgb:P0} of sRGB {Primaries}";
}

/// <summary>The GPU stopped working for this process; only a new process gets it back.</summary>
public sealed class GpuLostException(string message) : Exception(message);

public static class Monitors
{
    /// <summary>All desktop outputs. Caller owns the returned adapters/outputs.</summary>
    public static List<(MonitorInfo Info, IDXGIAdapter1 Adapter, IDXGIOutput Output)> Enumerate()
    {
        var result = new List<(MonitorInfo, IDXGIAdapter1, IDXGIOutput)>();
        var names = DisplayNames.Query();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            bool used = false;
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
            {
                using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                if (output6 is null) { output.Dispose(); continue; }
                var d = output6.Description1;
                if (!d.AttachedToDesktop) { output.Dispose(); continue; }

                var r = d.DesktopCoordinates;
                var prim = new Primaries(
                    d.RedPrimary[0], d.RedPrimary[1], d.GreenPrimary[0], d.GreenPrimary[1],
                    d.BluePrimary[0], d.BluePrimary[1], d.WhitePoint[0], d.WhitePoint[1]);
                if (prim.Rx <= 0 || prim.Wy <= 0) prim = Primaries.Srgb;

                var info = new MonitorInfo(
                    result.Count, d.DeviceName, names.GetValueOrDefault(d.DeviceName, d.DeviceName),
                    adapter.Description1.Description,
                    r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, d.Rotation,
                    d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020,
                    d.BitsPerColor, prim, d.MaxLuminance);
                result.Add((info, adapter, output));
                used = true;
            }
            if (!used) adapter.Dispose();
        }
        return result;
    }

    /// <summary>Just the descriptions (COM objects released).</summary>
    public static List<MonitorInfo> List()
    {
        var all = Enumerate();
        var infos = all.Select(m => m.Info).ToList();
        DisposeAll(all);
        return infos;
    }

    /// <summary>Like <see cref="List"/>, but never empty (see <see cref="WaitForAny"/>).</summary>
    public static List<MonitorInfo> ListOrThrow() => WaitForAny(List);

    /// <summary>Like <see cref="Enumerate"/>, but never empty (see <see cref="WaitForAny"/>).</summary>
    public static List<(MonitorInfo Info, IDXGIAdapter1 Adapter, IDXGIOutput Output)> EnumerateOrThrow() =>
        WaitForAny(Enumerate);

    /// <summary>
    /// Outputs can drop out for a moment around a mode change, so wait up to 3 s for one. If DXGI still
    /// shows none, this process has lost the GPU: on 30/10, after AMF stalled, the host saw no monitor
    /// for minutes while a freshly started process saw all three.
    /// </summary>
    static List<T> WaitForAny<T>(Func<List<T>> enumerate)
    {
        var list = enumerate();
        for (int i = 0; list.Count == 0 && i < 10; i++)
        {
            Thread.Sleep(300);
            list = enumerate();
        }
        if (list.Count == 0) throw new GpuLostException($"DXGI shows no monitor ({Diagnose()})");
        return list;
    }

    /// <summary>For the log when no monitor shows up: each adapter, its outputs, and any DXGI error.</summary>
    public static string Diagnose()
    {
        var parts = new List<string>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; ; a++)
            {
                var hr = factory.EnumAdapters1(a, out IDXGIAdapter1? adapter);
                if (hr.Failure)
                {
                    if (hr != Vortice.DXGI.ResultCode.NotFound) parts.Add($"EnumAdapters1({a}) failed 0x{hr.Code:X8}");
                    break;
                }
                using (adapter)
                {
                    int outputs = 0, attached = 0;
                    for (uint o = 0; ; o++)
                    {
                        hr = adapter!.EnumOutputs(o, out IDXGIOutput? output);
                        if (hr.Failure) break;
                        using (output)
                        {
                            outputs++;
                            if (output!.Description.AttachedToDesktop) attached++;
                        }
                    }
                    string error = hr == Vortice.DXGI.ResultCode.NotFound ? "" : $", EnumOutputs failed 0x{hr.Code:X8}";
                    parts.Add($"{adapter.Description1.Description}: {outputs} outputs, {attached} on the desktop{error}");
                }
            }
        }
        catch (Exception e)
        {
            parts.Add($"DXGI error: {e.Message}");
        }
        return parts.Count == 0 ? "no adapters" : string.Join("; ", parts);
    }

    /// <summary>Index of the monitor with this name (friendly or device), else the primary one.</summary>
    public static int Find(IReadOnlyList<MonitorInfo> monitors, string? name)
    {
        if (name is not null)
        {
            int byName = monitors.ToList().FindIndex(m =>
                m.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                m.DeviceName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (byName >= 0) return byName;
        }
        int primary = monitors.ToList().FindIndex(m => m.IsPrimary);
        return Math.Max(0, primary);
    }

    public static void DisposeAll(IEnumerable<(MonitorInfo Info, IDXGIAdapter1 Adapter, IDXGIOutput Output)> list)
    {
        var adapters = new HashSet<IDXGIAdapter1>();
        foreach (var (_, adapter, output) in list)
        {
            output.Dispose();
            adapters.Add(adapter);
        }
        foreach (var a in adapters) a.Dispose();
    }
}
