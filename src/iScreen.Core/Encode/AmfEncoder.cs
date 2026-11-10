using System.Buffers;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace IScreen.Host.Encode;

/// <summary>Hardware encoder fed with NV12 D3D11 textures the converter renders into.</summary>
public interface IFrameEncoder : IDisposable
{
    FrameLayout Layout { get; }
    List<string> AppliedOptions { get; }
    /// <summary>False when <see cref="Encode"/> can't honour forceIdr (then recreate the encoder).</summary>
    bool SupportsForcedIdr { get; }
    /// <summary>The NV12 texture to render the next frame into.</summary>
    IntPtr NextTexture();
    /// <summary>Encodes the texture returned by the last <see cref="NextTexture"/>.</summary>
    void Encode(bool forceIdr, List<EncodedPacket> output);
}

public static class FrameEncoders
{
    /// <summary>"amf" = AMD AMF called directly (default); "hevc_amf" = through FFmpeg.</summary>
    public static IFrameEncoder Create(ID3D11Device device, FrameLayout layout, EncoderSettings settings) =>
        settings.Codec == "amf"
            ? new AmfEncoder(device, layout, settings)
            : new HevcEncoder(device.NativePointer, layout, settings);
}

/// <summary>
/// HEVC on the Radeon through AMD's AMF runtime (amfrt64.dll, part of the driver) using its C
/// interface. Compared with FFmpeg 7.1's wrapper: QueryOutput blocks until the frame is ready
/// (HevcQueryTimeout) instead of being polled with 1 ms sleeps, and IDRs can be forced on demand.
/// Vtable slots follow amf/public/include (core/Context.h, components/Component.h, core/Buffer.h).
/// </summary>
public sealed unsafe class AmfEncoder : IFrameEncoder
{
    const int AMF_OK = 0, AMF_EOF = 23, AMF_REPEAT = 24, AMF_INPUT_FULL = 25;
    const int AMF_SURFACE_NV12 = 1, AMF_DX11_1 = 111;
    const int VARIANT_BOOL = 1, VARIANT_INT64 = 2, VARIANT_SIZE = 5, VARIANT_RATE = 7;
    static readonly Guid IID_AMFBuffer = new(0xb04b7248, 0xb6f0, 0x4321, 0xb6, 0x91, 0xba, 0xa4, 0x74, 0x0f, 0x9f, 0xcb);

    // AMFInterface: 0 Acquire, 1 Release, 2 QueryInterface. AMFPropertyStorage: 3 SetProperty, 4 GetProperty.
    const int Release_ = 1, QueryInterface_ = 2, SetProperty_ = 3, GetProperty_ = 4;
    // AMFFactory (no base interface).
    const int Factory_CreateContext = 0, Factory_CreateComponent = 1;
    // AMFContext.
    const int Context_Terminate = 13, Context_InitDX11 = 18, Context_CreateSurfaceFromDX11Native = 49;
    // AMFComponent.
    const int Component_Init = 17, Component_Terminate = 19, Component_SubmitInput = 22, Component_QueryOutput = 23;
    // AMFBuffer.
    const int Buffer_GetSize = 24, Buffer_GetNative = 25;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct Variant
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public long Int64;
        [FieldOffset(8)] public byte Bool;
        [FieldOffset(8)] public int A; // AMFSize.width / AMFRate.num
        [FieldOffset(12)] public int B; // AMFSize.height / AMFRate.den
    }

    static readonly object InitLock = new();
    static IntPtr _factory; // process-wide singleton, never released (AMFFactory has no Release)

    IntPtr _context, _encoder;
    readonly ID3D11Texture2D[] _textures;
    int _nextTexture;
    IntPtr _current;
    long _pts;
    readonly EncoderSettings _settings;

    public FrameLayout Layout { get; }
    public List<string> AppliedOptions { get; } = new();
    public bool SupportsForcedIdr => true;

    public AmfEncoder(ID3D11Device device, FrameLayout layout, EncoderSettings settings)
    {
        Layout = layout;
        _settings = settings;
        IntPtr factory = Factory();

        IntPtr context, encoder;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)FactoryVtbl(factory)[Factory_CreateContext])(factory, &context), "CreateContext");
        _context = context;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)Vtbl(_context)[Context_InitDX11])(_context, device.NativePointer, AMF_DX11_1), "InitDX11");
        fixed (char* id = "AMFVideoEncoderHW_HEVC")
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, char*, IntPtr*, int>)FactoryVtbl(factory)[Factory_CreateComponent])(factory, _context, id, &encoder), "CreateComponent(HEVC)");
        _encoder = encoder;

        long bitrate = settings.BitrateKbps * 1000L;
        // Usage first: it resets the other parameters to that usage's defaults.
        Set("HevcUsage", Int(1)); // ULTRA_LOW_LATENCY
        Set("HevcQualityPreset", Int(settings.Quality switch { "quality" => 0, "balanced" => 5, _ => 10 }));
        Set("HevcFrameSize", new Variant { Type = VARIANT_SIZE, A = layout.EncWidth, B = layout.EncHeight });
        Set("HevcFrameRate", new Variant { Type = VARIANT_RATE, A = settings.Fps, B = 1 });
        Set("HevcRateControlMethod", Int(settings.RateControl switch
        {
            "cqp" => 0, "vbr_peak" => 2, "cbr" => 3, "qvbr" => 4, _ => 1, // 1 = latency-constrained VBR
        }));
        Set("HevcTargetBitrate", Int(bitrate));
        Set("HevcPeakBitrate", Int(bitrate * 3 / 2));
        // Small VBV: caps how big one frame can get, i.e. the burst Wi-Fi has to swallow at once.
        Set("HevcVBVBufferSize", Int(bitrate / settings.Fps * 2));
        Set("HevcEnforceHRD", Bool(false));
        Set("HevcFillerDataEnable", Bool(false));
        Set("HevcEnableVBAQ", Bool(false));
        Set("HevcRateControlPreAnalysisEnable", Bool(false));
        Set("LowLatencyInternal", Bool(true));
        // Endless GOP: one IDR at the start, then only when the client asks for one.
        Set("HevcGOPSize", Int(int.MaxValue));
        Set("HevcGOPSPerIDR", Int(1));
        Set("HevcHeaderInsertionMode", Int(2)); // IDR aligned
        Set("HevcNominalRange", Int(1)); // full range, as our shaders produce
        // The point of calling AMF directly: QueryOutput waits for the frame instead of polling.
        Set("HevcQueryTimeout", Int(50));

        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, int, int>)Vtbl(_encoder)[Component_Init])(_encoder, AMF_SURFACE_NV12, layout.EncWidth, layout.EncHeight), "Init");

        // Our own NV12 targets; two so the converter never writes one the encoder may still read.
        var desc = new Texture2DDescription
        {
            Width = (uint)layout.EncWidth, Height = (uint)layout.EncHeight, MipLevels = 1, ArraySize = 1,
            Format = Format.NV12, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        _textures = [device.CreateTexture2D(desc), device.CreateTexture2D(desc)];
    }

    static IntPtr Factory()
    {
        lock (InitLock)
        {
            if (_factory != IntPtr.Zero) return _factory;
            IntPtr lib = NativeLibrary.Load("amfrt64.dll");
            var queryVersion = (delegate* unmanaged[Cdecl]<ulong*, int>)NativeLibrary.GetExport(lib, "AMFQueryVersion");
            var init = (delegate* unmanaged[Cdecl]<ulong, IntPtr*, int>)NativeLibrary.GetExport(lib, "AMFInit");
            ulong version;
            Check(queryVersion(&version), "AMFQueryVersion");
            IntPtr factory;
            Check(init(version, &factory), "AMFInit"); // ask for the runtime's own version
            _factory = factory;
            return factory;
        }
    }

    public IntPtr NextTexture()
    {
        _current = _textures[_nextTexture].NativePointer;
        _nextTexture = (_nextTexture + 1) % _textures.Length;
        return _current;
    }

    public void Encode(bool forceIdr, List<EncodedPacket> output)
    {
        IntPtr surface;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, IntPtr, int>)Vtbl(_context)[Context_CreateSurfaceFromDX11Native])(_context, _current, &surface, IntPtr.Zero), "CreateSurfaceFromDX11Native");
        try
        {
            if (forceIdr)
            {
                SetOn(surface, "HevcForcePictureType", Int(2)); // IDR
                SetOn(surface, "HevcInsertHeader", Bool(true));
            }
            int r = Submit(surface);
            if (r == AMF_INPUT_FULL)
            {
                Collect(output, waitForOne: true); // shouldn't happen with one frame in flight
                r = Submit(surface);
            }
            Check(r, "SubmitInput");
        }
        finally
        {
            Release(surface); // the encoder holds its own reference
        }
        Collect(output, waitForOne: true);
    }

    int Submit(IntPtr surface) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vtbl(_encoder)[Component_SubmitInput])(_encoder, surface);

    void Collect(List<EncodedPacket> output, bool waitForOne)
    {
        long deadline = Environment.TickCount64 + 500;
        while (true)
        {
            IntPtr data;
            int r = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(_encoder)[Component_QueryOutput])(_encoder, &data);
            if (r == AMF_OK && data != IntPtr.Zero)
            {
                output.Add(ReadPacket(data));
                return;
            }
            if (r == AMF_EOF || !waitForOne) return;
            if (r != AMF_REPEAT && r != AMF_OK) Check(r, "QueryOutput");
            // Only reached if the driver ignores HevcQueryTimeout: spin briefly rather than Sleep(1).
            if (Environment.TickCount64 > deadline) throw new TimeoutException("AMF produced no output");
            Thread.SpinWait(200);
        }
    }

    EncodedPacket ReadPacket(IntPtr data)
    {
        try
        {
            IntPtr buffer;
            Guid iid = IID_AMFBuffer;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(data)[QueryInterface_])(data, &iid, &buffer), "QueryInterface(AMFBuffer)");
            try
            {
                int size = (int)((delegate* unmanaged[Stdcall]<IntPtr, nuint>)Vtbl(buffer)[Buffer_GetSize])(buffer);
                byte* native = (byte*)((delegate* unmanaged[Stdcall]<IntPtr, void*>)Vtbl(buffer)[Buffer_GetNative])(buffer);
                var bytes = ArrayPool<byte>.Shared.Rent(size);
                new ReadOnlySpan<byte>(native, size).CopyTo(bytes);

                Variant type;
                bool key;
                fixed (char* n = "HevcOutputDataType")
                    key = ((delegate* unmanaged[Stdcall]<IntPtr, char*, Variant*, int>)Vtbl(buffer)[GetProperty_])(buffer, n, &type) == AMF_OK && type.Int64 == 0;
                key |= HevcEncoder.ContainsIrap(bytes.AsSpan(0, size));
                return new EncodedPacket { Buffer = bytes, Length = size, Key = key, Pts = _pts++ };
            }
            finally
            {
                Release(buffer);
            }
        }
        finally
        {
            Release(data);
        }
    }

    void Set(string name, Variant value)
    {
        int r = SetOn(_encoder, name, value);
        if (r == AMF_OK) AppliedOptions.Add(name);
        else Console.WriteLine($"  AMF property {name} rejected ({r})");
    }

    static int SetOn(IntPtr obj, string name, Variant value)
    {
        fixed (char* n = name)
            return ((delegate* unmanaged[Stdcall]<IntPtr, char*, Variant, int>)Vtbl(obj)[SetProperty_])(obj, n, value);
    }

    static Variant Int(long v) => new() { Type = VARIANT_INT64, Int64 = v };
    static Variant Bool(bool v) => new() { Type = VARIANT_BOOL, Bool = v ? (byte)1 : (byte)0 };

    static void** Vtbl(IntPtr obj) => *(void***)obj;
    static void** FactoryVtbl(IntPtr factory) => *(void***)factory;

    static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(obj)[Release_])(obj);
    }

    static void Check(int r, string what)
    {
        if (r != AMF_OK) throw new InvalidOperationException($"AMF {what} failed ({r})");
    }

    public void Dispose()
    {
        if (_encoder != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(_encoder)[Component_Terminate])(_encoder);
            Release(_encoder);
            _encoder = IntPtr.Zero;
        }
        if (_context != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(_context)[Context_Terminate])(_context);
            Release(_context);
            _context = IntPtr.Zero;
        }
        foreach (var t in _textures) t.Dispose();
    }
}
