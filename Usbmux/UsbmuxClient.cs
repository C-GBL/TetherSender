using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace TetherSender.Usbmux;

public sealed record UsbDevice(long DeviceId, string Serial, string ConnectionType)
{
    public bool IsUsb => string.Equals(ConnectionType, "USB", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => $"{Serial} (#{DeviceId}, {ConnectionType})";
}

/// <summary>
/// usbmuxd client (§11.3). On Windows the Apple Mobile Device Service listens on TCP 127.0.0.1:27015;
/// on macOS/Linux it is the Unix socket /var/run/usbmuxd. Packet: 16-byte LE header + XML plist.
/// </summary>
public static class UsbmuxClient
{
    private const string ProgName = "tether-sender";
    private static int _tag = 1;

    public static Socket OpenSocket()
    {
        if (OperatingSystem.IsWindows())
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            s.Connect(new IPEndPoint(IPAddress.Loopback, 27015));
            return s;
        }
        var u = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        u.Connect(new UnixDomainSocketEndPoint("/var/run/usbmuxd"));
        return u;
    }

    private static Dictionary<string, object> BaseMessage(string type) => new()
    {
        ["MessageType"] = type,
        ["ClientVersionString"] = ProgName,
        ["ProgName"] = ProgName,
    };

    public static void Send(Socket s, IReadOnlyDictionary<string, object> message)
    {
        var body = Plist.Serialize(message);
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), (uint)(16 + body.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);   // version: plist
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8);   // message: plist
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)Interlocked.Increment(ref _tag));
        s.Send(header);
        s.Send(body);
    }

    public static Dictionary<string, object> Receive(Socket s)
    {
        var header = ReadExact(s, 16);
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length < 16 || length > 1 << 20) throw new InvalidDataException($"usbmuxd packet length {length}");
        var body = ReadExact(s, length - 16);
        return Plist.Parse(body);
    }

    /// <summary>Lists attached devices, keeping only USB connections (the sender-side USB-only guarantee).</summary>
    public static List<UsbDevice> ListUsbDevices()
    {
        using var s = OpenSocket();
        Send(s, BaseMessage("ListDevices"));
        var reply = Receive(s);
        var result = new List<UsbDevice>();
        if (reply.TryGetValue("DeviceList", out var listObj) && listObj is List<object> list)
        {
            foreach (var item in list.OfType<Dictionary<string, object>>())
            {
                var device = ParseDevice(item);
                if (device is { IsUsb: true }) result.Add(device);
            }
        }
        return result;
    }

    public static UsbDevice? ParseDevice(Dictionary<string, object> item)
    {
        if (!item.TryGetValue("DeviceID", out var idObj)) return null;
        var props = item.TryGetValue("Properties", out var p) ? p as Dictionary<string, object> : null;
        var serial = props?.GetValueOrDefault("SerialNumber") as string ?? "";
        var connType = props?.GetValueOrDefault("ConnectionType") as string ?? "";
        return new UsbDevice(Convert.ToInt64(idObj), serial, connType);
    }

    /// <summary>
    /// Opens a tunnel to <paramref name="port"/> on the device. On success the returned socket carries
    /// the Tether Protocol from now on; usbmuxd is no longer spoken on it.
    /// </summary>
    public static Socket Connect(UsbDevice device, ushort port)
    {
        var s = OpenSocket();
        try
        {
            var msg = BaseMessage("Connect");
            msg["DeviceID"] = device.DeviceId;
            msg["PortNumber"] = (int)(ushort)IPAddress.HostToNetworkOrder((short)port);   // htons(port) as an integer
            Send(s, msg);
            var reply = Receive(s);
            var number = reply.TryGetValue("Number", out var n) ? Convert.ToInt64(n) : -1;
            if (number != 0)
            {
                throw new UsbmuxConnectException(number, number switch
                {
                    3 => "connection refused: is Tether Audio open on the phone?",
                    2 => "device not found",
                    _ => $"usbmuxd Connect failed with code {number}",
                });
            }
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Subscribes to Attached/Detached events on a long-lived socket. Calls <paramref name="onAttached"/>
    /// for USB devices only. Blocks until cancelled or the socket fails.
    /// </summary>
    public static void Listen(Action<UsbDevice> onAttached, Action<long> onDetached, CancellationToken ct)
    {
        using var s = OpenSocket();
        Send(s, BaseMessage("Listen"));
        var ack = Receive(s);
        using var reg = ct.Register(() => { try { s.Shutdown(SocketShutdown.Both); s.Close(); } catch { } });
        while (!ct.IsCancellationRequested)
        {
            var msg = Receive(s);
            var type = msg.GetValueOrDefault("MessageType") as string;
            if (type == "Attached")
            {
                var device = ParseDevice(msg);
                if (device is { IsUsb: true }) onAttached(device);
            }
            else if (type == "Detached" && msg.TryGetValue("DeviceID", out var id))
            {
                onDetached(Convert.ToInt64(id));
            }
        }
    }

    private static byte[] ReadExact(Socket s, int count)
    {
        var buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = s.Receive(buf, offset, count - offset, SocketFlags.None);
            if (n == 0) throw new EndOfStreamException("usbmuxd closed the connection");
            offset += n;
        }
        return buf;
    }
}

public sealed class UsbmuxConnectException(long code, string message) : Exception(message)
{
    public long Code { get; } = code;
    public bool IsRefused => Code == 3;
}
