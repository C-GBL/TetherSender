using System.Buffers.Binary;
using System.Text;

namespace TetherSender.Protocol;

/// <summary>Tether Protocol v1 constants and frame builders (design doc §3). All integers little-endian.</summary>
public static class TetherProtocol
{
    public const ushort Magic = 0x5541;
    public const ushort Version = 1;
    public const ushort DefaultPort = 47474;
    public const int HeaderSize = 8;
    public const int MaxPayload = 65_536;

    public const byte TypeHello = 0x01;
    public const byte TypeHelloAck = 0x02;
    public const byte TypeAudio = 0x10;
    public const byte TypePing = 0x20;
    public const byte TypePong = 0x21;
    public const byte TypeControl = 0x30;
    public const byte TypeBye = 0x7F;

    public const byte SampleFormatS16le = 1;

    public static byte[] Frame(byte type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload) throw new ArgumentException("payload too large");
        var buf = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), Magic);
        buf[2] = type;
        buf[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static byte[] Hello(uint sampleRate, byte channels, ushort framesPerPacket, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 64) nameBytes = nameBytes[..64];
        var payload = new byte[11 + nameBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), sampleRate);
        payload[6] = channels;
        payload[7] = SampleFormatS16le;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), framesPerPacket);
        payload[10] = (byte)nameBytes.Length;
        nameBytes.CopyTo(payload, 11);
        return Frame(TypeHello, payload);
    }

    /// <summary>Writes an AUDIO frame into <paramref name="dst"/> (must be HeaderSize + 8 + samples.Length bytes).</summary>
    public static void WriteAudio(Span<byte> dst, uint sequence, ushort frameCount, ReadOnlySpan<byte> samples)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dst, Magic);
        dst[2] = TypeAudio;
        dst[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], (uint)(8 + samples.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[12..], frameCount);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[14..], 0);
        samples.CopyTo(dst[16..]);
    }

    public static byte[] Ping(ulong clientTimeNs)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, clientTimeNs);
        return Frame(TypePing, payload);
    }

    public static byte[] Bye(byte reason, string message = "")
    {
        var text = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + text.Length];
        payload[0] = reason;
        text.CopyTo(payload, 1);
        return Frame(TypeBye, payload);
    }
}

public readonly record struct FrameHeader(byte Type, int Length);

public sealed record HelloAck(bool Accepted, ushort ServerVersion, string Reason);

public sealed record Pong(ulong ClientTimeNs, ushort BufferedMs, ushort UnderrunCount);

/// <summary>Reads phone → PC frames from a stream (HELLO_ACK, PONG, CONTROL, BYE).</summary>
public sealed class FrameReader
{
    private readonly Stream _stream;

    public FrameReader(Stream stream) => _stream = stream;

    public async Task<(FrameHeader Header, byte[] Payload)> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[TetherProtocol.HeaderSize];
        await ReadExactAsync(header, ct);
        if (BinaryPrimitives.ReadUInt16LittleEndian(header) != TetherProtocol.Magic)
            throw new InvalidDataException("bad magic from phone");
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (length > TetherProtocol.MaxPayload) throw new InvalidDataException("payload too large from phone");
        var payload = new byte[length];
        await ReadExactAsync(payload, ct);
        return (new FrameHeader(header[2], length), payload);
    }

    public static HelloAck ParseHelloAck(ReadOnlySpan<byte> p)
    {
        if (p.Length < 4) throw new InvalidDataException("short HELLO_ACK");
        var reasonLength = p[3];
        var reason = p.Length >= 4 + reasonLength ? Encoding.UTF8.GetString(p.Slice(4, reasonLength)) : "";
        return new HelloAck(p[0] == 1, BinaryPrimitives.ReadUInt16LittleEndian(p[1..]), reason);
    }

    public static Pong ParsePong(ReadOnlySpan<byte> p)
    {
        if (p.Length < 12) throw new InvalidDataException("short PONG");
        return new Pong(BinaryPrimitives.ReadUInt64LittleEndian(p),
                        BinaryPrimitives.ReadUInt16LittleEndian(p[8..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(p[10..]));
    }

    private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (n == 0) throw new EndOfStreamException("phone closed the connection");
            offset += n;
        }
    }
}
