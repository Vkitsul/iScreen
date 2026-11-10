using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using IScreen.Host.Audio;
using IScreen.Host.Capture;
using IScreen.Host.Input;
using IScreen.Host.Net;

namespace IScreen.Host.Streaming;

/// <summary>Host settings, stored in %APPDATA%\iScreen\host.json.</summary>
public sealed class HostConfig
{
    public string Pin { get; set; } = "";
    /// <summary>Monitor to stream (friendly name like "G27Q", or \\.\DISPLAYn). Null = primary.</summary>
    public string? Monitor { get; set; }
    /// <summary>The tray app has already applied its "start with Windows" default once.</summary>
    public bool AutoStartConfigured { get; set; }
    /// <summary>Device whose resolution we changed for a client and haven't restored yet (crash safety).</summary>
    public string? ChangedDisplay { get; set; }

    /// <summary>%APPDATA%\iScreen; the tray points it at the user's own when its service runs it as SYSTEM.</summary>
    public static string Directory { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iScreen");
    public static string PathOf => System.IO.Path.Combine(Directory, "host.json");
    public static string LogPath => System.IO.Path.Combine(Directory, "host.log");

    public static HostConfig LoadOrCreate()
    {
        HostConfig? config = null;
        try
        {
            if (File.Exists(PathOf))
                config = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(PathOf));
        }
        catch (JsonException) { }
        config ??= new HostConfig();
        if (string.IsNullOrWhiteSpace(config.Pin))
        {
            config.Pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            config.Save();
        }
        return config;
    }

    readonly Lock _saveLock = new();

    public void Save()
    {
        lock (_saveLock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

/// <summary>Accepts one client at a time on the TCP control port and runs its stream session.</summary>
public sealed class ControlServer : IDisposable
{
    readonly HostConfig _config;
    readonly SessionOptions _defaults;
    readonly bool _allowInput;
    readonly IPAddress _bindAddress;
    readonly VideoSender _sender;
    readonly ClipboardSync _clipboard = new();
    readonly Lock _lock = new();
    Connection? _current;
    volatile int _monitorIndex;

    public ControlServer(HostConfig config, int monitorIndex, SessionOptions defaults, bool allowInput, IPAddress bindAddress)
    {
        _config = config;
        _monitorIndex = monitorIndex;
        _defaults = defaults;
        _allowInput = allowInput;
        _bindAddress = bindAddress;
        _sender = new VideoSender(bindAddress, Protocol.VideoPort);
        _clipboard.Changed += text => _current?.SendClipboard(text); // copied on the PC -> client

        // A previous run ended (crash, power cut) while a monitor was at a client's resolution.
        if (config.ChangedDisplay is { } device)
        {
            Console.WriteLine($"restoring the resolution of {device} left over from the last run");
            DisplayModes.Restore(device);
            config.ChangedDisplay = null;
            config.Save();
        }
    }

    public VideoSender Sender => _sender;
    public StreamSession? Session => _current?.Session;
    public string? ClientAddress => _current?.Address;
    public int MonitorIndex => _monitorIndex;

    /// <summary>Raised on a background thread: client address on connect, null on disconnect.</summary>
    public event Action<string?>? ClientChanged;

    /// <summary>
    /// Raised on a background thread when the GPU stopped working for this process (the stream keeps
    /// failing, or DXGI shows no monitor): the client has been dropped and only a new process recovers.
    /// </summary>
    public event Action<string>? GpuLost;

    /// <summary>Ends the current session (e.g. before the PC sleeps: nothing should survive a suspend).</summary>
    public void DisconnectClient(string reason)
    {
        Connection? current;
        lock (_lock) current = _current;
        if (current is null) return;
        Console.WriteLine($"{current.Address}: disconnecting ({reason})");
        current.Kick();
    }

    /// <summary>Streams another monitor; a connected client switches live.</summary>
    public void SetMonitor(int index)
    {
        _monitorIndex = index;
        Connection? current;
        lock (_lock) current = _current;
        current?.Restart(index);
    }

    public void Run(CancellationToken ct)
    {
        _sender.ClientRegistered += ep =>
        {
            Console.WriteLine($"video -> {ep}");
            _current?.Session?.RequestKeyframe();
        };

        var listener = new TcpListener(_bindAddress, Protocol.ControlPort);
        listener.Start();
        ct.Register(listener.Stop);
        Console.WriteLine($"Listening on {_bindAddress} TCP {Protocol.ControlPort} / UDP {_sender.Port}");
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            new Thread(() => Handle(client)) { IsBackground = true, Name = "control" }.Start();
        }
    }

    void Handle(TcpClient tcp)
    {
        var ip = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address;
        string address = ip is null ? "?" : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
        ControlChannel.ConfigureSocket(tcp.Client);
        tcp.ReceiveTimeout = 10_000;
        using var channel = new ControlChannel(tcp.GetStream());
        Connection? conn = null;
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(16);
            channel.Send(new MessageWriter(Msg.Challenge).U16(Protocol.Version).Bytes(nonce));

            var hello = channel.Receive();
            if (hello is not { Type: Msg.Hello }) return;
            var (mode, bitrateKbps, fps, mac, clientDisplay, audio, clipboard) = ParseHello(hello.Value.Payload);
            if (!CryptographicOperations.FixedTimeEquals(mac, Protocol.PinMac(_config.Pin, nonce)))
            {
                Console.WriteLine($"{address}: wrong PIN");
                channel.Send(new MessageWriter(Msg.Reject).U8(1));
                return;
            }

            var encoder = _defaults.Encoder with
            {
                BitrateKbps = bitrateKbps > 0 ? (int)bitrateKbps : _defaults.Encoder.BitrateKbps,
                Fps = fps > 0 ? fps : _defaults.Encoder.Fps,
            };
            var options = _defaults with { Mode = mode ?? _defaults.Mode, Encoder = encoder, ClientDisplay = clientDisplay, Audio = audio, Clipboard = clipboard };
            if (clientDisplay is var (cw, ch)) Console.WriteLine($"{address}: client screen {cw}x{ch}, match resolution on");
            conn = new Connection(this, tcp, channel, options, address,
                (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue));

            lock (_lock)
            {
                // A new client replaces the current one; stop the old stream before starting ours.
                _current?.Kick();
                _current = conn;
                _sender.ResetClient();
                _sender.SessionId = conn.SessionId;
            }
            conn.Start(_monitorIndex);
            if (options.Audio) conn.StartAudio();
            ClientChanged?.Invoke(address);

            tcp.ReceiveTimeout = 15_000; // the client pings every second
            while (channel.Receive() is { } msg)
                conn.Dispatch(msg.Type, msg.Payload);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
        }
        catch (Exception e)
        {
            Console.WriteLine($"{address}: session failed: {e.Message}");
            if (IsGpuLost(e)) GpuLost?.Invoke(e.Message);
        }
        finally
        {
            conn?.Dispose();
            bool wasCurrent = false;
            lock (_lock)
            {
                if (conn is not null && _current == conn)
                {
                    _current = null;
                    _sender.ResetClient();
                    _sender.SessionId = 0;
                    wasCurrent = true;
                }
            }
            if (wasCurrent) ClientChanged?.Invoke(null);
            Console.WriteLine($"{address}: disconnected");
        }
    }

    /// <summary>
    /// No monitor visible, or DXGI_ERROR_DEVICE_REMOVED/_HUNG/_RESET. In a test on 30/10 a stalled AMF
    /// encoder was followed 50 s later by DEVICE_REMOVED ("device instance has been suspended"), and from
    /// then on the process saw the Radeon with 0 outputs.
    /// </summary>
    static bool IsGpuLost(Exception e) =>
        e is GpuLostException || (uint)e.HResult is 0x887A0005 or 0x887A0006 or 0x887A0007;

    static (StreamMode? Mode, uint BitrateKbps, int Fps, byte[] Mac, (int, int)? ClientDisplay, bool Audio, bool Clipboard) ParseHello(byte[] payload)
    {
        var r = new MessageReader(payload);
        ushort version = r.U16();
        if (version != Protocol.Version) throw new InvalidDataException($"Client protocol {version}");
        r.U16(); // client UDP port (informational; the real endpoint comes from Register)
        byte mode = r.U8();
        uint bitrate = r.U32();
        int fps = r.U16();
        byte[] mac = r.Bytes(32).ToArray();
        // Optional (newer clients): screen size in pixels + flags (match its aspect ratio, send audio).
        (int, int)? display = null;
        bool audio = false, clipboard = false;
        if (r.Remaining >= 5)
        {
            int w = r.U16(), h = r.U16();
            byte flags = r.U8();
            if ((flags & Protocol.HelloMatchResolution) != 0 && w > 0 && h > 0) display = (w, h);
            audio = (flags & Protocol.HelloAudio) != 0;
            clipboard = (flags & Protocol.HelloClipboard) != 0;
        }
        return (mode switch { 0 => StreamMode.Yuv420, 1 => StreamMode.Yuv444Packed, _ => null }, bitrate, fps, mac, display, audio, clipboard);
    }

    public void Dispose()
    {
        Connection? current;
        lock (_lock)
        {
            current = _current;
            _current = null;
        }
        current?.Kick();
        _sender.Dispose();
        _clipboard.Dispose();
    }

    /// <summary>One authenticated client: its stream session and input injector.</summary>
    sealed class Connection(ControlServer server, TcpClient tcp, ControlChannel channel, SessionOptions options,
        string address, uint sessionId) : IDisposable
    {
        readonly Lock _lock = new();
        StreamSession? _session;
        InputInjector? _input;
        string? _changedDisplay; // monitor whose resolution we changed for this client
        AudioStreamer? _audio;
        bool _disposed;
        int _recoveries;
        long _lastRecoveryMs;

        public string Address => address;
        public uint SessionId => sessionId;
        public StreamSession? Session => _session;

        /// <summary>(Re)starts streaming the given monitor; the client gets a fresh SessionInfo.</summary>
        public void Start(int monitorIndex)
        {
            lock (_lock)
            {
                if (_disposed) return;
                StopStream();

                var all = Monitors.ListOrThrow();
                var target = all[Math.Clamp(monitorIndex, 0, all.Count - 1)];
                if (_changedDisplay is not null && _changedDisplay != target.DeviceName) RestoreResolution();
                if (!options.Pattern && options.ClientDisplay is { } client && _changedDisplay is null)
                    MatchResolution(target, client.Width, client.Height);

                var monitors = Monitors.EnumerateOrThrow(); // after the mode change: fresh size and position
                StreamSession session;
                try
                {
                    var (info, adapter, output) = monitors[Math.Clamp(monitorIndex, 0, monitors.Count - 1)];
                    session = new StreamSession(info, adapter, output, options, server._sender, m => channel.TrySend(m));
                }
                finally { Monitors.DisposeAll(monitors); }

                var m = session.Monitor;
                if (server._allowInput && !options.Pattern) _input = new InputInjector(m.DeviceName);
                session.LayoutChanged += () => channel.TrySend(session.BuildSessionInfo(sessionId));
                session.Failed += e => OnStreamFailed(session, e);
                channel.TrySend(session.BuildSessionInfo(sessionId));
                session.Start();
                _session = session;

                var l = session.Layout;
                Console.WriteLine($"{address}: streaming {m.FriendlyName} {l.Width}x{l.Height} {l.Mode} as {l.EncWidth}x{l.EncHeight} " +
                                  $"{options.Encoder.Codec} {options.Encoder.BitrateKbps / 1000} Mbps @{options.Encoder.Fps}" +
                                  (_input is null ? " (input disabled)" : ""));
            }
        }

        /// <summary>PC audio for the whole connection (independent of which monitor is streamed).</summary>
        public void StartAudio()
        {
            lock (_lock)
            {
                if (_disposed || _audio is not null) return;
                try
                {
                    var audio = new AudioStreamer(packet => server._sender.SendAudio(packet), address);
                    audio.Start();
                    _audio = audio;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{address}: audio unavailable: {e.Message}");
                }
            }
        }

        /// <summary>
        /// The stream stopped on an error (GPU encoder stalled, device lost on wake): start it again on a
        /// fresh device; the client just gets a new SessionInfo and keyframe. If that keeps failing, drop the
        /// connection rather than leave a frozen image on the client, and report the GPU as lost: on 30/10
        /// the stalled AMF encoder never finished terminating and this process then saw no monitor at all.
        /// </summary>
        void OnStreamFailed(StreamSession session, Exception e)
        {
            Console.WriteLine($"{address}: stream failed ({e.GetType().Name}: {e.Message}), restarting it");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                long now = Environment.TickCount64;
                if (now - _lastRecoveryMs > 60_000) _recoveries = 0;
                _lastRecoveryMs = now;
                if (++_recoveries > 3)
                {
                    Console.WriteLine($"{address}: the stream keeps failing, closing the connection");
                    Kick();
                    server.GpuLost?.Invoke($"the stream keeps failing ({e.Message})");
                    return;
                }
                Thread.Sleep(300 * _recoveries); // e.g. the GPU still waking up
                if (_session != session) return; // already replaced (monitor switch, disconnect)
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Start(server._monitorIndex);
                    Console.WriteLine($"{address}: stream restarted in {sw.ElapsedMilliseconds} ms");
                }
                catch (Exception again)
                {
                    Console.WriteLine($"{address}: could not restart the stream: {again.Message}");
                    Kick();
                    if (IsGpuLost(again)) server.GpuLost?.Invoke(again.Message);
                }
            });
        }

        public void Restart(int monitorIndex)
        {
            try { Start(monitorIndex); }
            catch (Exception e) { Console.WriteLine($"{address}: monitor switch failed: {e.Message}"); }
        }

        public void Dispatch(Msg type, byte[] payload)
        {
            var r = new MessageReader(payload);
            if (type == Msg.Ping)
            {
                channel.TrySend(new MessageWriter(Msg.Pong).U64(r.U64()).U64(IScreen.Host.Clock.NowUs));
                return;
            }
            if (type == Msg.Clipboard)
            {
                // Copied on the client: u8 format (1 = UTF-8 text), u32 length, bytes.
                if (options.Clipboard && r.U8() == 1 && r.U32() is var length && length <= r.Remaining)
                {
                    string text = System.Text.Encoding.UTF8.GetString(r.Bytes((int)length));
                    if (text.Length <= ClipboardSync.MaxChars) server._clipboard.Set(text);
                }
                return;
            }
            lock (_lock)
            {
                switch (type)
                {
                    case Msg.KeyframeRequest: _session?.RequestKeyframe(); break;
                    case Msg.MouseMove: _input?.MouseMove(r.F32(), r.F32()); break;
                    case Msg.MouseButton: _input?.MouseButton(r.U8(), r.U8() != 0); break;
                    case Msg.MouseWheel: _input?.Wheel(r.F32(), r.F32()); break;
                    case Msg.Key: _input?.Key(r.U16(), r.U8() != 0); break;
                }
            }
        }

        /// <summary>Copied on the PC: hand it to the client, if it asked for clipboard sharing.</summary>
        public void SendClipboard(string text)
        {
            if (!options.Clipboard) return;
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
            channel.TrySend(new MessageWriter(Msg.Clipboard).U8(1).U32((uint)utf8.Length).Bytes(utf8));
        }

        void StopStream()
        {
            _input?.ReleaseAll();
            _session?.Dispose();
            _session = null;
            _input = null;
        }

        /// <summary>Switches the monitor to the mode closest to the client's aspect ratio (fills its screen).</summary>
        void MatchResolution(MonitorInfo monitor, int clientWidth, int clientHeight)
        {
            if (monitor.IsRotated) return;
            var best = DisplayModes.BestFor(monitor.DeviceName, clientWidth, clientHeight);
            if (best is null)
            {
                Console.WriteLine($"{address}: {monitor.FriendlyName} already matches a {clientWidth}x{clientHeight} screen");
                return;
            }
            // Record it before changing, so a crash in between still gets restored on the next start.
            server._config.ChangedDisplay = monitor.DeviceName;
            server._config.Save();
            if (DisplayModes.Apply(monitor.DeviceName, best))
            {
                _changedDisplay = monitor.DeviceName;
                Thread.Sleep(300); // let the desktop settle before Desktop Duplication attaches
                Console.WriteLine($"{address}: {monitor.FriendlyName} switched to {best} to fit a {clientWidth}x{clientHeight} screen");
            }
            else
            {
                Console.WriteLine($"{address}: {monitor.FriendlyName} refused {best}");
                server._config.ChangedDisplay = null;
                server._config.Save();
            }
        }

        void RestoreResolution()
        {
            if (_changedDisplay is null) return;
            bool ok = DisplayModes.Restore(_changedDisplay);
            Console.WriteLine($"{address}: resolution of {_changedDisplay} {(ok ? "restored" : "could not be restored")}");
            _changedDisplay = null;
            server._config.ChangedDisplay = null;
            server._config.Save();
        }

        /// <summary>Replaced by a newer client: stop streaming now and drop the connection.</summary>
        public void Kick()
        {
            Dispose();
            try { tcp.Close(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _audio?.Dispose();
                _audio = null;
                StopStream();
                RestoreResolution();
            }
        }
    }
}
