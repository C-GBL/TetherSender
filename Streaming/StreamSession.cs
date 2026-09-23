using System.Diagnostics;
using System.Net.Sockets;
using TetherSender.Audio;
using TetherSender.Protocol;

namespace TetherSender.Streaming;

public sealed record SessionStats(double LastRttMs, int PhoneBufferedMs, int PhoneUnderruns, long PacketsSent, long BytesSent);

/// <summary>
/// One connection to the phone: HELLO handshake, audio forwarding, PING every second, and a reader
/// for PONG / CONTROL / BYE (§3.6, §11.2). <see cref="RunAsync"/> completes when the session ends.
/// </summary>
public sealed class StreamSession : IDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly AudioCapturePipeline _audio;
    private readonly bool _handleControl;
    private readonly object _sendLock = new();
    private readonly CancellationTokenSource _cts = new();
    private uint _sequence;
    private long _packetsSent;
    private long _bytesSent;
    private double _lastRttMs;
    private int _phoneBufferedMs;
    private int _phoneUnderruns;
    private volatile bool _failed;
    private Exception? _failure;
    private readonly byte[] _audioFrame;

    public event Action<SessionStats>? StatsUpdated;

    public StreamSession(Socket socket, AudioCapturePipeline audio, bool handleControl)
    {
        _socket = socket;
        _socket.NoDelay = true;
        _socket.SendBufferSize = 256 * 1024;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _audio = audio;
        _handleControl = handleControl;
        _audioFrame = new byte[TetherProtocol.HeaderSize + 8 + audio.PacketBytes];
    }

    public SessionStats Stats => new(_lastRttMs, _phoneBufferedMs, _phoneUnderruns, Interlocked.Read(ref _packetsSent), Interlocked.Read(ref _bytesSent));

    /// <summary>Runs until the phone closes, the cable is unplugged, or <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(string senderName, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;

        // HELLO / HELLO_ACK with a 3 s timeout.
        Send(TetherProtocol.Hello((uint)_audio.OutputSampleRate, AudioCapturePipeline.OutputChannels, (ushort)_audio.FramesPerPacket, senderName));
        var reader = new FrameReader(_stream);
        using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var (header, payload) = await reader.ReadFrameAsync(helloTimeout.Token);
            if (header.Type != TetherProtocol.TypeHelloAck) throw new InvalidDataException($"expected HELLO_ACK, got 0x{header.Type:X2}");
            var ack = FrameReader.ParseHelloAck(payload);
            if (!ack.Accepted) throw new RejectedException(ack.Reason);
        }

        _audio.PacketReady += OnPacket;
        try
        {
            var pingTask = PingLoopAsync(token);
            var readTask = ReadLoopAsync(reader, token);
            var finished = await Task.WhenAny(readTask, pingTask);
            linked.Cancel();
            try { await finished; } catch (OperationCanceledException) { }
            if (_failed && _failure != null) throw new IOException("send failed", _failure);
        }
        finally
        {
            _audio.PacketReady -= OnPacket;
            if (!_failed)
            {
                try { Send(TetherProtocol.Bye(0)); } catch { }
            }
        }
    }

    private void OnPacket(byte[] samples)
    {
        if (_failed) return;
        lock (_sendLock)
        {
            if (_failed) return;
            try
            {
                TetherProtocol.WriteAudio(_audioFrame, _sequence++, (ushort)_audio.FramesPerPacket, samples);
                _socket.Send(_audioFrame);
                Interlocked.Increment(ref _packetsSent);
                Interlocked.Add(ref _bytesSent, _audioFrame.Length);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }
    }

    private void Send(byte[] frame)
    {
        lock (_sendLock)
        {
            _socket.Send(frame);
        }
    }

    private void Fail(Exception ex)
    {
        if (_failed) return;
        _failed = true;
        _failure = ex;
        _cts.Cancel();
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            try { Send(TetherProtocol.Ping((ulong)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency)))); }
            catch (Exception ex) { Fail(ex); return; }
        }
    }

    private async Task ReadLoopAsync(FrameReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (header, payload) = await reader.ReadFrameAsync(ct);
            switch (header.Type)
            {
                case TetherProtocol.TypePong:
                    var pong = FrameReader.ParsePong(payload);
                    var nowNs = Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency);
                    _lastRttMs = (nowNs - pong.ClientTimeNs) / 1e6;
                    _phoneBufferedMs = pong.BufferedMs;
                    _phoneUnderruns = pong.UnderrunCount;
                    Trace.WriteLine($"PONG rtt {_lastRttMs:F1} ms, phone buffered {pong.BufferedMs} ms, underruns {pong.UnderrunCount}");
                    StatsUpdated?.Invoke(Stats);
                    break;
                case TetherProtocol.TypeControl:
                    if (_handleControl && payload.Length >= 1)
                    {
                        Trace.WriteLine($"CONTROL {payload[0]} → media play/pause key");
                        MediaKeys.PlayPause();   // play/pause/toggle all map to the single media key
                    }
                    break;
                case TetherProtocol.TypeBye:
                    var reason = payload.Length > 0 ? payload[0] : (byte)0;
                    throw new ByeException(reason);
                default:
                    break;   // unknown types are skipped (§3.2)
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        _stream.Dispose();
        _socket.Dispose();
        _cts.Dispose();
    }
}

public sealed class RejectedException(string reason) : Exception($"phone rejected HELLO: {reason}")
{
    public string Reason { get; } = reason;
}

public sealed class ByeException(byte reason) : Exception(reason switch
{
    1 => "phone replaced this connection with a newer sender",
    2 => "phone reported a protocol error",
    _ => "phone ended the session",
})
{
    public byte Reason { get; } = reason;
}
