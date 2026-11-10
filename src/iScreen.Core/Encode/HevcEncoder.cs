using System.Buffers;
using FFmpeg.AutoGen;

namespace IScreen.Host.Encode;

public sealed record EncoderSettings
{
    public int Fps { get; init; } = 60;
    public int BitrateKbps { get; init; } = 80_000;
    /// <summary>AMF quality preset: speed | balanced | quality.</summary>
    public string Quality { get; init; } = "speed";
    /// <summary>AMF rate control: cbr | vbr_latency | vbr_peak | cqp | qvbr.</summary>
    public string RateControl { get; init; } = "vbr_latency";
    /// <summary>"amf" = AMD AMF directly (default, lowest latency); "hevc_amf" = through FFmpeg.</summary>
    public string Codec { get; init; } = "amf";
    /// <summary>How long to poll for the packet of the frame just submitted.</summary>
    public int MaxWaitMs { get; init; } = 30;
    public bool Verbose { get; init; }
    /// <summary>Extra private encoder options ("name=value"), applied last.</summary>
    public string[] Extra { get; init; } = [];
}

public readonly struct EncodedPacket
{
    public required byte[] Buffer { get; init; } // rented from ArrayPool<byte>.Shared
    public required int Length { get; init; }
    public required bool Key { get; init; }
    public required long Pts { get; init; }
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);
    public void Return() => ArrayPool<byte>.Shared.Return(Buffer);
}

/// <summary>Hardware HEVC encoder fed with D3D11 NV12 textures (zero copy).</summary>
public sealed unsafe class HevcEncoder : IFrameEncoder
{
    const uint D3D11_BIND_RENDER_TARGET = 0x20;

    static readonly object InitLock = new();
    static bool _ffmpegLoaded;

    AVBufferRef* _device;
    AVBufferRef* _frames;
    AVCodecContext* _ctx;
    AVPacket* _pkt;
    long _pts;
    readonly EncoderSettings _settings;

    public FrameLayout Layout { get; }
    public List<string> AppliedOptions { get; } = new();
    public bool SupportsForcedIdr => false; // FFmpeg 7.1's AMF wrapper ignores forced picture types
    AVFrame* _pending;

    public IntPtr NextTexture()
    {
        _pending = AcquireFrame(out IntPtr texture, out _);
        return texture;
    }

    public void Encode(bool forceIdr, List<EncodedPacket> output)
    {
        var frame = _pending;
        _pending = null;
        Encode(frame, output);
    }

    public static void LoadFFmpeg()
    {
        lock (InitLock)
        {
            if (_ffmpegLoaded) return;
            ffmpeg.RootPath = AppContext.BaseDirectory;
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
            _ffmpegLoaded = true;
        }
    }

    public HevcEncoder(IntPtr d3dDevice, FrameLayout layout, EncoderSettings settings)
    {
        LoadFFmpeg();
        Layout = layout;
        _settings = settings;

        _device = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        var deviceCtx = (AVHWDeviceContext*)_device->data;
        var d3d = (AVD3D11VADeviceContext*)deviceCtx->hwctx;
        System.Runtime.InteropServices.Marshal.AddRef(d3dDevice); // released by FFmpeg on uninit
        d3d->device = (ID3D11Device*)d3dDevice;
        Check(ffmpeg.av_hwdevice_ctx_init(_device), "av_hwdevice_ctx_init");

        _frames = ffmpeg.av_hwframe_ctx_alloc(_device);
        var framesCtx = (AVHWFramesContext*)_frames->data;
        framesCtx->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        framesCtx->width = layout.EncWidth;
        framesCtx->height = layout.EncHeight;
        // With RENDER_TARGET binding FFmpeg allocates textures one by one, so this is not a hard cap.
        // amfenc derives its queue limit from it (pool - 1): with 1 surface in flight it polls
        // QueryOutput until the packet is ready instead of handing it out one frame late.
        framesCtx->initial_pool_size = 2;
        ((AVD3D11VAFramesContext*)framesCtx->hwctx)->BindFlags = D3D11_BIND_RENDER_TARGET;
        Check(ffmpeg.av_hwframe_ctx_init(_frames), "av_hwframe_ctx_init");

        var codec = ffmpeg.avcodec_find_encoder_by_name(settings.Codec);
        if (codec == null) throw new InvalidOperationException($"Encoder {settings.Codec} not available");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->width = layout.EncWidth;
        _ctx->height = layout.EncHeight;
        _ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
        _ctx->sw_pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        _ctx->hw_frames_ctx = ffmpeg.av_buffer_ref(_frames);
        _ctx->time_base = new AVRational { num = 1, den = settings.Fps };
        _ctx->framerate = new AVRational { num = settings.Fps, den = 1 };
        _ctx->max_b_frames = 0;
        _ctx->gop_size = int.MaxValue; // IDR only on request
        long bitrate = settings.BitrateKbps * 1000L;
        _ctx->bit_rate = bitrate;
        // Small VBV: caps how big one frame can get, i.e. the burst Wi-Fi has to swallow at once.
        // Quality dips briefly on big changes, then the idle re-encodes refine it.
        _ctx->rc_max_rate = bitrate * 3 / 2;
        _ctx->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate / settings.Fps * 2);
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        // We do our own RGB<->YCbCr on both ends; tag it honestly anyway.
        _ctx->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        _ctx->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        _ctx->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        _ctx->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;

        SetOption("usage", "ultralowlatency");
        SetOption("quality", settings.Quality);
        SetOption("rc", settings.RateControl);
        SetOption("header_insertion_mode", "idr");
        SetOption("gops_per_idr", "1"); // with an endless GOP: one IDR at the start, and it is an IDR (0 gives a CRA)
        SetOption("preanalysis", "0");
        SetOption("vbaq", "0");
        SetOption("enforce_hrd", "0");
        SetOption("filler_data", "0");
        SetOption("frame_skipping", "0");
        SetOption("latency", "1");
        SetOption("query_timeout", "50");
        SetOption("async_depth", "1");
        foreach (var kv in settings.Extra)
        {
            var parts = kv.Split('=', 2);
            if (parts.Length == 2) SetOption(parts[0], parts[1]);
        }

        Check(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2");
        _pkt = ffmpeg.av_packet_alloc();
    }

    void SetOption(string name, string value)
    {
        if (ffmpeg.av_opt_find(_ctx->priv_data, name, null, 0, 0) == null)
        {
            if (_settings.Verbose) Console.WriteLine($"  encoder option '{name}' not supported");
            return;
        }
        int r = ffmpeg.av_opt_set(_ctx->priv_data, name, value, 0);
        if (r < 0) Console.WriteLine($"  encoder option {name}={value} rejected: {Error(r)}");
        else AppliedOptions.Add($"{name}={value}");
    }

    /// <summary>Gets a pooled D3D11 NV12 texture to render the next frame into.</summary>
    public AVFrame* AcquireFrame(out IntPtr texture, out uint slice)
    {
        var frame = ffmpeg.av_frame_alloc();
        Check(ffmpeg.av_hwframe_get_buffer(_frames, frame, 0), "av_hwframe_get_buffer");
        texture = (IntPtr)frame->data[0];
        slice = (uint)(nint)frame->data[1];
        return frame;
    }

    /// <summary>
    /// Submits a frame (ownership transferred) and collects the packets that come out.
    /// FFmpeg 7.1's AMF wrapper ignores forced picture types: for an IDR, create a new encoder.
    /// </summary>
    public void Encode(AVFrame* frame, List<EncodedPacket> output)
    {
        frame->pts = _pts++;
        int r = ffmpeg.avcodec_send_frame(_ctx, frame);
        ffmpeg.av_frame_free(&frame);
        Check(r, "avcodec_send_frame");
        Receive(output, _pts - 1);
    }

    /// <summary>Flushes the encoder at shutdown.</summary>
    public void Drain(List<EncodedPacket> output)
    {
        ffmpeg.avcodec_send_frame(_ctx, null);
        Receive(output, long.MaxValue);
    }

    void Receive(List<EncodedPacket> output, long waitForPts)
    {
        long deadline = Environment.TickCount64 + _settings.MaxWaitMs;
        while (true)
        {
            int r = ffmpeg.avcodec_receive_packet(_ctx, _pkt);
            if (r == 0)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(_pkt->size);
                new ReadOnlySpan<byte>(_pkt->data, _pkt->size).CopyTo(buffer);
                long pts = _pkt->pts;
                output.Add(new EncodedPacket
                {
                    Buffer = buffer,
                    Length = _pkt->size,
                    Key = (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0 || ContainsIrap(buffer.AsSpan(0, _pkt->size)),
                    Pts = pts,
                });
                ffmpeg.av_packet_unref(_pkt);
                if (pts >= waitForPts && waitForPts != long.MaxValue) return;
                continue;
            }
            if (r == ffmpeg.AVERROR_EOF) return;
            if (r != ffmpeg.AVERROR(ffmpeg.EAGAIN)) Check(r, "avcodec_receive_packet");
            if (waitForPts == long.MaxValue || Environment.TickCount64 > deadline) return;
            Thread.Sleep(0);
        }
    }

    /// <summary>NAL unit types in an Annex-B access unit (diagnostics).</summary>
    public static List<int> NalTypes(ReadOnlySpan<byte> au)
    {
        var types = new List<int>();
        for (int i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] != 0 || au[i + 1] != 0 || au[i + 2] != 1) continue;
            types.Add((au[i + 3] >> 1) & 0x3F);
            i += 3;
        }
        return types;
    }

    /// <summary>True if the Annex-B access unit holds an IRAP picture (IDR/CRA/BLA, NAL types 16..21).</summary>
    public static bool ContainsIrap(ReadOnlySpan<byte> au)
    {
        for (int i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] != 0 || au[i + 1] != 0 || au[i + 2] != 1) continue;
            int type = (au[i + 3] >> 1) & 0x3F;
            if (type is >= 16 and <= 21) return true;
            if (type < 32) return false; // first VCL NAL decides
            i += 3;
        }
        return false;
    }

    public static string Error(int code)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(code, buf, 256);
        return new string((sbyte*)buf);
    }

    static void Check(int r, string what)
    {
        if (r < 0) throw new InvalidOperationException($"{what} failed: {Error(r)} ({r})");
    }

    public void Dispose()
    {
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
        if (_frames != null) { var f = _frames; ffmpeg.av_buffer_unref(&f); _frames = null; }
        if (_device != null) { var d = _device; ffmpeg.av_buffer_unref(&d); _device = null; }
    }
}
