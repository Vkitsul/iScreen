using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace IScreen.Host.Capture;

public static class GpuDevice
{
    public static (ID3D11Device Device, ID3D11DeviceContext Context) Create(IDXGIAdapter1 adapter)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            levels, out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();

        // FFmpeg/AMF touch the immediate context from their own threads.
        using (var mt = device!.QueryInterface<ID3D11Multithread>())
            mt.SetMultithreadProtected(true);

        // Best effort: needs SeIncreaseBasePriorityPrivilege for values above 0.
        using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
            dxgiDevice.SetGPUThreadPriority(7);

        return (device, context!);
    }
}

public sealed class CursorShape
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int HotX { get; init; }
    public required int HotY { get; init; }
    /// <summary>Straight (non-premultiplied) RGBA8, row-major, no padding.</summary>
    public required byte[] Rgba { get; init; }

    public static CursorShape FromDxgi(OutduplPointerShapeInfo info, ReadOnlySpan<byte> src)
    {
        int w = (int)info.Width;
        int pitch = (int)info.Pitch;
        bool mono = info.Type == 1; // DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME
        int h = mono ? (int)info.Height / 2 : (int)info.Height;
        var rgba = new byte[w * h * 4];

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            byte r, g, b, a;
            if (mono)
            {
                int bit = 0x80 >> (x & 7);
                bool and = (src[y * pitch + x / 8] & bit) != 0;
                bool xor = (src[(y + h) * pitch + x / 8] & bit) != 0;
                // AND=1,XOR=1 means "invert the screen": approximated as opaque black.
                (r, g, b, a) = (and, xor) switch
                {
                    (true, false) => ((byte)0, (byte)0, (byte)0, (byte)0),
                    (false, false) => ((byte)0, (byte)0, (byte)0, (byte)255),
                    (false, true) => ((byte)255, (byte)255, (byte)255, (byte)255),
                    _ => ((byte)0, (byte)0, (byte)0, (byte)255),
                };
            }
            else
            {
                int s = y * pitch + x * 4;
                b = src[s]; g = src[s + 1]; r = src[s + 2]; a = src[s + 3];
                if (info.Type == 4) // DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR
                {
                    // Alpha is a mask: 0 = opaque colour, 0xFF = XOR with screen (black XOR = no-op).
                    a = a == 0 ? (byte)255 : (r | g | b) == 0 ? (byte)0 : (byte)255;
                }
            }
            rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
        }

        return new CursorShape { Width = w, Height = h, HotX = info.HotSpot.X, HotY = info.HotSpot.Y, Rgba = rgba };
    }
}

/// <summary>DXGI Desktop Duplication of one output into a GPU texture we own.</summary>
public sealed class DesktopCapture : IDisposable
{
    public enum Status { Timeout, NewFrame, CursorOnly, Lost }

    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _context;
    readonly IDXGIOutput5 _output;
    IDXGIOutputDuplication? _dup;
    ID3D11Texture2D? _frame;
    byte[] _shapeBuffer = new byte[256 * 256 * 4];

    public DesktopCapture(ID3D11Device device, ID3D11DeviceContext context, IDXGIOutput output)
    {
        _device = device;
        _context = context;
        _output = output.QueryInterface<IDXGIOutput5>();
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public ModeRotation Rotation { get; private set; }
    public ID3D11Texture2D? FrameTexture => _frame;
    public ID3D11ShaderResourceView? FrameView { get; private set; }
    public bool HasFrame { get; private set; }

    public bool CursorVisible { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorMoved { get; set; }
    public CursorShape? PendingShape { get; set; }

    /// <summary>Fires when the duplicated mode changes size (the caller must rebuild the encoder).</summary>
    public event Action<int, int>? SizeChanged;

    public Status Acquire(int timeoutMs, out long presentQpc)
    {
        presentQpc = 0;
        if (_dup is null && !TryCreate())
        {
            Thread.Sleep(100);
            return Status.Lost;
        }

        var hr = _dup!.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo info, out IDXGIResource? resource);
        if (hr == Vortice.DXGI.ResultCode.WaitTimeout)
            return Status.Timeout;
        if (hr.Failure)
        {
            ResetDuplication();
            return Status.Lost;
        }

        var status = Status.CursorOnly;
        try
        {
            if (info.LastMouseUpdateTime != 0)
            {
                CursorVisible = info.PointerPosition.Visible;
                CursorX = info.PointerPosition.Position.X;
                CursorY = info.PointerPosition.Position.Y;
                CursorMoved = true;
            }
            if (info.PointerShapeBufferSize > 0)
                ReadPointerShape(info.PointerShapeBufferSize);

            if (info.LastPresentTime != 0 && resource is not null)
            {
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                _context.CopyResource(_frame!, texture);
                presentQpc = info.LastPresentTime;
                HasFrame = true;
                status = Status.NewFrame;
            }
        }
        finally
        {
            resource?.Dispose();
            _dup.ReleaseFrame();
        }
        return status;
    }

    unsafe void ReadPointerShape(uint size)
    {
        if (_shapeBuffer.Length < size) _shapeBuffer = new byte[size];
        OutduplPointerShapeInfo shapeInfo;
        fixed (byte* p = _shapeBuffer)
        {
            if (_dup!.GetFramePointerShape(size, (IntPtr)p, out _, out shapeInfo).Failure)
                return;
        }
        PendingShape = CursorShape.FromDxgi(shapeInfo, _shapeBuffer.AsSpan(0, (int)size));
    }

    bool TryCreate()
    {
        // Duplication follows this thread's desktop: switch to the UAC prompt / lock screen and back.
        IScreen.Host.Native.InputDesktop.Sync();
        try
        {
            _dup = _output.DuplicateOutput1(_device, [Format.B8G8R8A8_UNorm]);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Secure desktop (UAC / lock screen) when not running as SYSTEM, mode switch in progress, etc.
            _dup = null;
            return false;
        }

        var desc = _dup.Description;
        Rotation = desc.Rotation;
        int w = (int)desc.ModeDescription.Width, h = (int)desc.ModeDescription.Height;
        if (_frame is null || w != Width || h != Height)
        {
            FrameView?.Dispose();
            _frame?.Dispose();
            _frame = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            });
            FrameView = _device.CreateShaderResourceView(_frame);
            bool changed = Width != 0;
            Width = w;
            Height = h;
            HasFrame = false;
            if (changed) SizeChanged?.Invoke(w, h);
        }
        return true;
    }

    void ResetDuplication()
    {
        _dup?.Dispose();
        _dup = null;
    }

    public void Dispose()
    {
        ResetDuplication();
        FrameView?.Dispose();
        _frame?.Dispose();
        _output.Dispose();
    }
}
