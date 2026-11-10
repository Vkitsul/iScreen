using System.Buffers.Binary;
using IScreen.Host.Net;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace IScreen.Host.Audio;

/// <summary>
/// Captures what Windows is playing (WASAPI loopback on the default output device) and sends it as
/// 5 ms packets of 16-bit stereo PCM. Uncompressed on purpose: ~1.5 Mbps is nothing on a LAN and
/// it avoids codec latency. Audio is never retransmitted: a late packet is useless.
/// </summary>
public sealed class AudioStreamer : IDisposable
{
    readonly Action<ReadOnlySpan<byte>> _send;
    readonly string _logPrefix;
    readonly MMDeviceEnumerator _devices = new();
    readonly DeviceWatcher _watcher;
    readonly Lock _lock = new();
    WasapiLoopbackCapture? _capture;
    bool _disposed;
    byte[] _packet = [];
    int _framesPerPacket, _pendingFrames, _channels, _sampleRate, _bytesPerSample;
    bool _float;
    uint _seq;

    public long PacketsSent;

    public AudioStreamer(Action<ReadOnlySpan<byte>> send, string logPrefix)
    {
        _send = send;
        _logPrefix = logPrefix;
        // Switching headphones changes the default output: follow it instead of capturing silence.
        _watcher = new DeviceWatcher(() => Restart("default output changed"));
    }

    public void Start()
    {
        lock (_lock) StartCapture();
        _devices.RegisterEndpointNotificationCallback(_watcher);
    }

    void StartCapture()
    {
        var device = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _capture = new WasapiLoopbackCapture(device);
        var f = _capture.WaveFormat;
        _sampleRate = f.SampleRate;
        _channels = f.Channels;
        _bytesPerSample = f.BitsPerSample / 8;
        // The shared-mode mix format is 32-bit float in practice; 16/24-bit integer just in case.
        _float = f.BitsPerSample == 32 && f.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible;
        _framesPerPacket = _sampleRate / 200;
        _packet = new byte[Protocol.AudioHeaderSize + _framesPerPacket * 4];
        _pendingFrames = 0;
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += OnStopped;
        _capture.StartRecording();
        Console.WriteLine($"{_logPrefix}: audio from {device.FriendlyName}: {_sampleRate} Hz, {_channels} ch, " +
                          $"{f.BitsPerSample}-bit{(_float ? " float" : "")}");
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        Console.WriteLine($"{_logPrefix}: audio capture stopped: {e.Exception.Message}");
        Restart("device lost"); // e.g. headphones unplugged
    }

    /// <summary>Off the notification/capture thread: those must not block, and stopping waits for them.</summary>
    void Restart(string reason)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(300); // let Windows finish switching devices
            lock (_lock)
            {
                if (_disposed) return;
                Console.WriteLine($"{_logPrefix}: restarting audio ({reason})");
                StopCapture();
                try { StartCapture(); }
                catch (Exception e) { Console.WriteLine($"{_logPrefix}: audio unavailable: {e.Message}"); }
            }
        });
    }

    void StopCapture()
    {
        if (_capture is null) return;
        _capture.DataAvailable -= OnData;
        _capture.RecordingStopped -= OnStopped;
        try { _capture.StopRecording(); } catch (InvalidOperationException) { } catch (COMException) { }
        _capture.Dispose();
        _capture = null;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int frameBytes = _channels * _bytesPerSample;
        var data = e.Buffer.AsSpan(0, e.BytesRecorded - e.BytesRecorded % frameBytes);
        for (int offset = 0; offset < data.Length; offset += frameBytes)
        {
            var frame = data.Slice(offset, frameBytes);
            float left = Sample(frame, 0), right = _channels > 1 ? Sample(frame, 1) : left;
            // 5.1 / 7.1 (FL FR FC LFE BL BR [SL SR]): fold centre, back and side channels into stereo.
            if (_channels >= 3)
            {
                float centre = Sample(frame, 2) * 0.707f;
                left += centre;
                right += centre;
            }
            if (_channels >= 6)
            {
                left += Sample(frame, 4) * 0.5f;
                right += Sample(frame, 5) * 0.5f;
            }
            if (_channels >= 8)
            {
                left += Sample(frame, 6) * 0.5f;
                right += Sample(frame, 7) * 0.5f;
            }
            int o = Protocol.AudioHeaderSize + _pendingFrames * 4;
            BinaryPrimitives.WriteInt16LittleEndian(_packet.AsSpan(o), ToS16(left));
            BinaryPrimitives.WriteInt16LittleEndian(_packet.AsSpan(o + 2), ToS16(right));
            if (++_pendingFrames == _framesPerPacket) Flush();
        }
    }

    float Sample(ReadOnlySpan<byte> frame, int channel)
    {
        var s = frame.Slice(channel * _bytesPerSample, _bytesPerSample);
        return _bytesPerSample switch
        {
            4 when _float => BinaryPrimitives.ReadSingleLittleEndian(s),
            4 => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
            3 => (s[0] << 8 | s[1] << 16 | s[2] << 24) / 2147483648f,
            2 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
            _ => 0,
        };
    }

    static short ToS16(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue, short.MaxValue);

    void Flush()
    {
        var h = _packet.AsSpan();
        h[0] = Protocol.UdpAudio;
        h[1] = 2; // channels
        BinaryPrimitives.WriteUInt16LittleEndian(h[2..], (ushort)_framesPerPacket);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], _seq++);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], (uint)_sampleRate);
        BinaryPrimitives.WriteUInt64LittleEndian(h[12..], Clock.NowUs);
        _send(_packet);
        PacketsSent++;
        _pendingFrames = 0;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            StopCapture();
        }
        try { _devices.UnregisterEndpointNotificationCallback(_watcher); } catch (COMException) { }
        _devices.Dispose();
    }

    sealed class DeviceWatcher(Action defaultOutputChanged) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) defaultOutputChanged();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
