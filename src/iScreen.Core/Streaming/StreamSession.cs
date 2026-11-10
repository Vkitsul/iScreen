using System.Diagnostics;
using IScreen.Host.Capture;
using IScreen.Host.Encode;
using IScreen.Host.Gpu;
using IScreen.Host.Native;
using IScreen.Host.Net;
using IScreen.Host.Tools;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace IScreen.Host.Streaming;

/// <param name="ClientDisplay">The client screen in pixels, when it asked us to match its aspect ratio.</param>
public sealed record SessionOptions(StreamMode Mode, EncoderSettings Encoder, bool Pattern,
    (int Width, int Height)? ClientDisplay = null, bool Audio = false, bool Clipboard = false);

/// <summary>Capture -> convert -> encode -> UDP, on one dedicated thread.</summary>
public sealed class StreamSession : IDisposable
{
    // After the screen stops changing, re-encode the last image a few times: each pass spends bits
    // on the remaining residual, so static text converges to near-lossless.
    const int RefinementFrames = 8;
    const int RefinementSpacingMs = 33;

    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _context;
    readonly DesktopCapture? _capture;
    readonly ID3D11Texture2D? _patternTexture;
    readonly ID3D11ShaderResourceView? _patternView;
    readonly FrameConverter _converter;
    readonly VideoSender _sender;
    readonly Action<MessageWriter> _sendControl;
    readonly SessionOptions _options;
    IFrameEncoder _encoder;
    bool _encoderFresh = true; // a new encoder's first frame is always an IDR
    long _lastKeyframeUs = -1_000_000;
    Thread? _thread;
    volatile bool _stop;
    volatile bool _forceIdr = true;
    volatile bool _rebuild;
    volatile bool _simulateFailure;
    uint _frameId = 1;
    int _scroll;

    public MonitorInfo Monitor { get; }
    public FrameLayout Layout { get; private set; }
    public event Action? LayoutChanged;
    /// <summary>The stream thread stopped on an error (raised on that thread, which then exits).</summary>
    public event Action<Exception>? Failed;

    // Stats, read by the console reporter.
    public long FramesSent;
    public long EncodeUsTotal;
    public long HostDelayUsTotal;
    public long Keyframes;

    public StreamSession(MonitorInfo monitor, IDXGIAdapter1 adapter, IDXGIOutput output, SessionOptions options,
        VideoSender sender, Action<MessageWriter> sendControl)
    {
        Monitor = monitor;
        _options = options;
        _sender = sender;
        _sendControl = sendControl;
        (_device, _context) = GpuDevice.Create(adapter);
        _converter = new FrameConverter(_device, _context);

        int width, height;
        if (options.Pattern)
        {
            width = monitor.Width & ~1;
            height = monitor.Height & ~1;
            var (bgra, _) = TestPattern.Create(width, height);
            _patternTexture = Upload(bgra, width, height);
            _patternView = _device.CreateShaderResourceView(_patternTexture);
        }
        else
        {
            _capture = new DesktopCapture(_device, _context, output);
            _capture.SizeChanged += (_, _) => _rebuild = true;
            var sw = Stopwatch.StartNew();
            while (!_capture.HasFrame)
            {
                _capture.Acquire(100, out _);
                if (sw.ElapsedMilliseconds > 5000) throw new TimeoutException("Desktop Duplication produced no frame");
            }
            if (monitor.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                Console.WriteLine($"warning: {monitor.DeviceName} is rotated ({monitor.Rotation}); rotation is not handled yet");
            width = _capture.Width;
            height = _capture.Height;
        }

        Layout = FrameLayout.Create(width, height, options.Mode);
        _encoder = FrameEncoders.Create(_device, Layout, options.Encoder);
    }

    public void Start()
    {
        _thread = new Thread(Run) { Name = "stream", Priority = ThreadPriority.Highest, IsBackground = true };
        _thread.Start();
    }

    public void RequestKeyframe()
    {
        // One just went out: the client is probably asking again before receiving it.
        if ((long)Clock.NowUs - Interlocked.Read(ref _lastKeyframeUs) < 300_000) return;
        _forceIdr = true;
    }

    public MessageWriter BuildSessionInfo(uint sessionId)
    {
        var p = Monitor.Primaries;
        var l = Layout;
        return new MessageWriter(Msg.SessionInfo)
            .U16(Protocol.Version).U32(sessionId)
            .U16((ushort)l.Width).U16((ushort)l.Height)
            .U16((ushort)l.EncWidth).U16((ushort)l.EncHeight).U16((ushort)l.AuxOffset)
            .U8((byte)l.Mode).U8(1 /* HEVC */).U8(8).U16((ushort)_options.Encoder.Fps)
            .F32(p.Rx).F32(p.Ry).F32(p.Gx).F32(p.Gy).F32(p.Bx).F32(p.By).F32(p.Wx).F32(p.Wy)
            .F32(2.2f)
            .U16((ushort)_sender.Port);
    }

    /// <summary>Testing: make the stream thread fail as if the encoder had stalled.</summary>
    public void SimulateFailure() => _simulateFailure = true;

    void Run()
    {
        // An unhandled exception here would take the whole host down (it did when AMF stalled):
        // stop this session and let the owner start a fresh one.
        try { Loop(); }
        catch (Exception e) when (!_stop)
        {
            Failed?.Invoke(e);
        }
    }

    void Loop()
    {
        uint taskIndex = 0;
        Win32.AvSetMmThreadCharacteristics("Capture", ref taskIndex);

        var packets = new List<EncodedPacket>();
        long freq = Stopwatch.Frequency;
        long interval = freq / Math.Max(1, _options.Encoder.Fps);
        long nextDue = 0, lastEncode = 0;
        bool pending = false;
        ulong pendingCaptureUs = 0;
        int refinements = RefinementFrames;

        while (!_stop)
        {
            if (_rebuild) Rebuild();
            if (_simulateFailure) throw new TimeoutException("simulated encoder stall");

            long now = Stopwatch.GetTimestamp();
            int timeoutMs = pending
                ? (int)Math.Max(0, (nextDue - now) * 1000 / freq)
                : refinements < RefinementFrames
                    ? (int)Math.Max(0, RefinementSpacingMs - (now - lastEncode) * 1000 / freq)
                    : 250;

            if (_capture is not null)
            {
                var status = _capture.Acquire(timeoutMs, out long qpc);
                if (status == DesktopCapture.Status.NewFrame)
                {
                    pending = true;
                    pendingCaptureUs = Clock.QpcToUs(qpc);
                    refinements = 0;
                }
                SendCursorUpdates();
            }
            else
            {
                // Test pattern: a new frame every interval, scrolling like a web page.
                if (timeoutMs > 0) Thread.Sleep(Math.Min(timeoutMs, Math.Max(1, (int)((nextDue - now) * 1000 / freq))));
                if (Stopwatch.GetTimestamp() >= nextDue)
                {
                    pending = true;
                    pendingCaptureUs = Clock.NowUs;
                    _scroll += 6;
                    refinements = 0;
                }
            }

            now = Stopwatch.GetTimestamp();
            if (pending && now >= nextDue)
            {
                EncodeAndSend(pendingCaptureUs, packets);
                pending = false;
                // Keep a steady cadence (no drift), but never schedule a burst after a stall.
                nextDue = Math.Max(nextDue + interval, now + interval / 2);
                lastEncode = now;
            }
            else if (!pending && refinements < RefinementFrames &&
                     (now - lastEncode) * 1000 / freq >= RefinementSpacingMs)
            {
                EncodeAndSend(Clock.NowUs, packets);
                refinements++;
                lastEncode = now;
            }
        }
    }

    void EncodeAndSend(ulong captureUs, List<EncodedPacket> packets)
    {
        if (!_sender.HasClient) return;
        var view = _capture?.FrameView ?? _patternView!;

        // The client needs to resynchronise: AMF can force an IDR; through FFmpeg 7.1 it can't, but a
        // new encoder always starts with one.
        bool idr = _forceIdr && !_encoderFresh;
        _forceIdr = false;
        if (idr && !_encoder.SupportsForcedIdr)
        {
            RecreateEncoder();
            idr = false;
        }

        long t0 = Stopwatch.GetTimestamp();
        IntPtr texture = _encoder.NextTexture();
        _converter.Convert(view, texture, 0, Layout, _capture is null ? _scroll : 0);
        _encoder.Encode(idr, packets);
        _encoderFresh = false;
        long encodeUs = (long)Stopwatch.GetElapsedTime(t0).TotalMicroseconds;

        foreach (var p in packets)
        {
            ulong delayUs = Clock.NowUs - captureUs;
            _sender.SendFrame(_frameId++, p.Span, p.Key, captureUs, (ushort)Math.Min(ushort.MaxValue, delayUs / 100));
            if (p.Key)
            {
                Interlocked.Increment(ref Keyframes);
                Interlocked.Exchange(ref _lastKeyframeUs, (long)Clock.NowUs);
            }
            Interlocked.Increment(ref FramesSent);
            Interlocked.Add(ref HostDelayUsTotal, (long)delayUs);
            p.Return();
        }
        Interlocked.Add(ref EncodeUsTotal, encodeUs);
        packets.Clear();
    }

    void SendCursorUpdates()
    {
        var c = _capture!;
        if (c.PendingShape is { } shape)
        {
            c.PendingShape = null;
            _sendControl(new MessageWriter(Msg.CursorShape)
                .U16((ushort)shape.Width).U16((ushort)shape.Height)
                .U16((ushort)shape.HotX).U16((ushort)shape.HotY)
                .Bytes(shape.Rgba));
        }
        if (c.CursorMoved)
        {
            c.CursorMoved = false;
            _sendControl(new MessageWriter(Msg.CursorState)
                .U8(c.CursorVisible ? (byte)1 : (byte)0)
                .F32(c.CursorX / (float)Math.Max(1, c.Width))
                .F32(c.CursorY / (float)Math.Max(1, c.Height)));
        }
    }

    void Rebuild()
    {
        _rebuild = false;
        Layout = FrameLayout.Create(_capture!.Width, _capture.Height, _options.Mode);
        RecreateEncoder();
        LayoutChanged?.Invoke();
    }

    void RecreateEncoder()
    {
        Retire(_encoder);
        _converter.ClearTargets(); // render-target views of the old encoder's texture pool
        _encoder = FrameEncoders.Create(_device, Layout, _options.Encoder);
        _encoderFresh = true;
    }

    /// <summary>
    /// AMF's Terminate takes 5 s on this driver (Drain doesn't help: EOF also comes after 5 s). Nothing
    /// should wait for that - not the stream, a reconnect, nor a PC about to sleep - so it happens on its
    /// own thread, followed by whatever needs the device to outlive the encoder.
    /// </summary>
    static void Retire(IFrameEncoder encoder, Action? then = null) =>
        new Thread(() =>
        {
            encoder.Dispose();
            then?.Invoke();
        }) { IsBackground = true, Name = "encoder-teardown" }.Start();

    unsafe ID3D11Texture2D Upload(byte[] bgra, int width, int height)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        };
        fixed (byte* p = bgra)
            return _device.CreateTexture2D(desc, [new SubresourceData((IntPtr)p, (uint)width * 4)]);
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(2000);
        _capture?.Dispose(); // now: the next session duplicates the same output
        Retire(_encoder, then: () =>
        {
            _converter.Dispose();
            _patternView?.Dispose();
            _patternTexture?.Dispose();
            _context.ClearState();
            _context.Dispose();
            _device.Dispose();
        });
    }
}
