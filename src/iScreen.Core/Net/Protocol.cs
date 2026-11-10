using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace IScreen.Host.Net;

/// <summary>Wire protocol v1. Keep in sync with docs/ARCHITETTURA.md and the Mac client.</summary>
public static class Protocol
{
    public const ushort Version = 2; // 2: FEC fields in the video header
    public const int ControlPort = 52700;
    public const int VideoPort = 52701;

    public const int VideoHeaderSize = 28;
    public const int MaxPayload = 1372; // shard size: 28 + 1372 + 28 (IP/UDP) = 1428 < 1500 MTU
    public const int FecBlockData = 32; // data shards per FEC block (at most)

    public const byte UdpVideo = 1;
    public const byte UdpAudio = 2;
    public const int AudioHeaderSize = 20;

    /// <summary>Hello flags (optional trailing byte).</summary>
    public const byte HelloMatchResolution = 1, HelloAudio = 2, HelloClipboard = 4;
    public const byte UdpRegister = 0x80;
    public const byte UdpNack = 0x81;

    public const byte FlagKeyframe = 1;

    public static byte[] PinMac(string pin, ReadOnlySpan<byte> nonce) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(pin), nonce);
}

public enum Msg : byte
{
    Challenge = 3,
    Hello = 1,
    SessionInfo = 2,
    Reject = 4,
    MouseMove = 10,
    MouseButton = 11,
    MouseWheel = 12,
    Key = 13,
    KeyframeRequest = 20,
    Ping = 30,
    Pong = 31,
    CursorShape = 60,
    CursorState = 61,
    Clipboard = 70,
}

/// <summary>Builds one length-prefixed control message: u32 len, u8 type, payload.</summary>
public sealed class MessageWriter
{
    byte[] _buf = new byte[256];
    int _pos;

    public MessageWriter(Msg type)
    {
        _pos = 4;
        U8((byte)type);
    }

    void Ensure(int n)
    {
        if (_pos + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _pos + n));
    }

    public MessageWriter U8(byte v) { Ensure(1); _buf[_pos++] = v; return this; }
    public MessageWriter U16(ushort v) { Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_pos), v); _pos += 2; return this; }
    public MessageWriter U32(uint v) { Ensure(4); BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_pos), v); _pos += 4; return this; }
    public MessageWriter U64(ulong v) { Ensure(8); BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(_pos), v); _pos += 8; return this; }
    public MessageWriter F32(float v) { Ensure(4); BinaryPrimitives.WriteSingleLittleEndian(_buf.AsSpan(_pos), v); _pos += 4; return this; }
    public MessageWriter Bytes(ReadOnlySpan<byte> v) { Ensure(v.Length); v.CopyTo(_buf.AsSpan(_pos)); _pos += v.Length; return this; }

    public ReadOnlyMemory<byte> Finish()
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buf, (uint)(_pos - 4));
        return _buf.AsMemory(0, _pos);
    }
}

public ref struct MessageReader(ReadOnlySpan<byte> data)
{
    readonly ReadOnlySpan<byte> _data = data;
    int _pos;

    public readonly int Remaining => _data.Length - _pos;
    public byte U8() => _data[_pos++];
    public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_data[_pos..]); _pos += 2; return v; }
    public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]); _pos += 4; return v; }
    public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(_data[_pos..]); _pos += 8; return v; }
    public float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(_data[_pos..]); _pos += 4; return v; }
    public ReadOnlySpan<byte> Bytes(int n) { var v = _data.Slice(_pos, n); _pos += n; return v; }
}

/// <summary>Reads/writes framed control messages on a TCP stream.</summary>
public sealed class ControlChannel(Stream stream) : IDisposable
{
    readonly Lock _writeLock = new();
    readonly byte[] _header = new byte[4];

    public void Send(MessageWriter message)
    {
        var data = message.Finish().Span;
        lock (_writeLock) stream.Write(data);
    }

    /// <summary>Send from threads that must not die when the peer goes away.</summary>
    public bool TrySend(MessageWriter message)
    {
        try
        {
            Send(message);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            return false;
        }
    }

    /// <summary>Returns (type, payload) or null at end of stream.</summary>
    public (Msg Type, byte[] Payload)? Receive()
    {
        if (!ReadExactly(_header)) return null;
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(_header);
        if (len < 1 || len > 16 * 1024 * 1024) throw new InvalidDataException($"Bad message length {len}");
        var body = new byte[len];
        if (!ReadExactly(body)) return null;
        return ((Msg)body[0], body[1..]);
    }

    bool ReadExactly(byte[] buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
            return true;
        }
        catch (EndOfStreamException) { return false; }
        catch (IOException) { return false; }
    }

    public void Dispose() => stream.Dispose();

    public static void ConfigureSocket(Socket s)
    {
        s.NoDelay = true;
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }
}
