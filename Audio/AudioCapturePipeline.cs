using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TetherSender.Audio;

/// <summary>An audio endpoint the sender can capture from.</summary>
/// <param name="Id">MMDevice ID (stable across sessions).</param>
/// <param name="IsLoopback">True for playback endpoints (captured via WASAPI loopback); false for recording endpoints.</param>
public sealed record AudioSource(string Id, string Name, bool IsLoopback, bool IsDefault)
{
    public override string ToString() => $"{Name}{(IsLoopback ? " (playback, loopback)" : " (recording)")}{(IsDefault ? " · default" : "")}";
    public bool LooksLikeVirtualCable => Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
                                      || Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Captures audio from a playback endpoint (WASAPI loopback) or a recording endpoint, converts it to
/// s16le stereo at 44.1/48 kHz, and emits exactly one packet every packet period on a wall-clock
/// paced thread (§11.2).
///
/// Pacing is the key design point. WASAPI loopback delivers audio in irregular chunks (NAudio polls
/// every few tens of ms) and delivers nothing at all while the system is silent. An earlier version
/// injected silence whenever no callback arrived for 20 ms, which also fired *between* normal chunks
/// and produced ~130 packets/s instead of 100: the phone then overflowed its jitter buffer and hard-
/// resynced continuously. Now captured audio goes into a queue and a pacer thread sends one packet
/// per period from the queue, or a zero-filled packet if the queue is empty, so the wire rate is
/// always exactly real time regardless of callback timing.
/// </summary>
public sealed class AudioCapturePipeline : IDisposable
{
    public const int OutputChannels = 2;
    private const int BytesPerFrame = OutputChannels * 2;

    private readonly object _lock = new();
    private readonly int _framesPerPacket;
    private readonly int _prebufferMs;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private MMDeviceEnumerator? _enumerator;
    private DeviceNotifications? _notifications;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _chain;
    private float[] _floatScratch = new float[8192];
    private byte[] _convertScratch = new byte[8192 * 2];

    // Queue of converted s16 stereo bytes (circular). Producer: capture callback. Consumer: pacer.
    private byte[] _queue = Array.Empty<byte>();
    private int _queueHead;
    private int _queueCount;
    private bool _draining;

    private Thread? _pacer;
    private volatile bool _running;
    private volatile bool _restartRequested;

    public int OutputSampleRate { get; private set; } = 48_000;
    public int FramesPerPacket => _framesPerPacket;
    public int PacketBytes => _framesPerPacket * BytesPerFrame;
    public TimeSpan PacketDuration => TimeSpan.FromSeconds((double)_framesPerPacket / OutputSampleRate);
    public string CaptureFormatDescription { get; private set; } = "not started";
    public AudioSource? ActiveSource { get; private set; }

    /// <summary>Null = the default playback device (loopback).</summary>
    public AudioSource? SelectedSource { get; private set; }

    public long CapturedPackets;
    public long SilencePackets;
    public long DroppedPackets;
    public int QueueMs => (int)(_queueCount / (double)BytesPerFrame * 1000 / OutputSampleRate);

    /// <summary>Raised on the pacer thread with exactly one packet of s16le stereo samples, once per packet period.</summary>
    public event Action<byte[]>? PacketReady;
    /// <summary>Raised when the capture device changes or fails (text for the UI).</summary>
    public event Action<string>? CaptureChanged;

    /// <param name="prebufferMs">Audio queued before real packets start flowing after silence. Must cover the capture callback interval; 40–60 ms is typical.</param>
    public AudioCapturePipeline(int framesPerPacket, int prebufferMs = 50)
    {
        if (framesPerPacket is < 64 or > 4800) throw new ArgumentOutOfRangeException(nameof(framesPerPacket));
        _framesPerPacket = framesPerPacket;
        _prebufferMs = Math.Clamp(prebufferMs, 10, 500);
    }

    // MARK: Sources

    public static IReadOnlyList<AudioSource> ListSources()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultRender = null, defaultCapture = null;
        try { defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
        try { defaultCapture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID; } catch { }
        var list = new List<AudioSource>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
        {
            bool loopback = d.DataFlow == DataFlow.Render;
            list.Add(new AudioSource(d.ID, d.FriendlyName, loopback, d.ID == (loopback ? defaultRender : defaultCapture)));
        }
        return list.OrderByDescending(s => s.IsLoopback).ThenByDescending(s => s.IsDefault).ThenBy(s => s.Name).ToList();
    }

    /// <summary>Selects a source (null = default playback device). Restarts capture if running.</summary>
    public void SelectSource(AudioSource? source)
    {
        lock (_lock)
        {
            SelectedSource = source;
            if (_running) _restartRequested = true;
        }
    }

    // MARK: Lifecycle

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _enumerator = new MMDeviceEnumerator();
            _notifications = new DeviceNotifications(this);
            _enumerator.RegisterEndpointNotificationCallback(_notifications);
            OpenCapture();
            TimePeriod.Begin(1);
            _pacer = new Thread(PacerLoop) { IsBackground = true, Name = "tether-pacer", Priority = ThreadPriority.Highest };
            _pacer.Start();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
            CloseCapture();
            if (_enumerator != null && _notifications != null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); } catch { }
            }
            _enumerator?.Dispose();
            _enumerator = null;
            TimePeriod.End(1);
        }
        _pacer?.Join(500);
    }

    public void Dispose() => Stop();

    /// <summary>Caller holds the lock.</summary>
    private void OpenCapture()
    {
        CloseCapture();
        var enumerator = _enumerator ?? throw new InvalidOperationException();
        MMDevice device;
        bool loopback;
        if (SelectedSource != null)
        {
            device = enumerator.GetDevice(SelectedSource.Id);
            loopback = SelectedSource.IsLoopback;
        }
        else
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            loopback = true;
        }
        ActiveSource = new AudioSource(device.ID, device.FriendlyName, loopback, SelectedSource == null);

        var capture = loopback ? new LoopbackCapture(device, 30) : new WasapiCapture(device, false, 30);
        var fmt = capture.WaveFormat;
        OutputSampleRate = fmt.SampleRate is 44_100 or 48_000 ? fmt.SampleRate : 48_000;
        CaptureFormatDescription = $"{device.FriendlyName}: {fmt.SampleRate} Hz {fmt.Channels} ch {fmt.Encoding} → {OutputSampleRate} Hz stereo s16";

        _buffered = new BufferedWaveProvider(fmt) { ReadFully = false, DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(2) };
        ISampleProvider chain = _buffered.ToSampleProvider();
        if (fmt.Channels == 1) chain = new MonoToStereoSampleProvider(chain);
        else if (fmt.Channels > 2) chain = new FirstTwoChannelsSampleProvider(chain);
        if (fmt.SampleRate != OutputSampleRate) chain = new WdlResamplingSampleProvider(chain, OutputSampleRate);
        _chain = chain;

        // Queue holds prebuffer + 400 ms; beyond that the oldest audio is dropped (clock drift or stalls).
        int capacityFrames = (int)((_prebufferMs + 400) * (long)OutputSampleRate / 1000);
        _queue = new byte[capacityFrames * BytesPerFrame];
        _queueHead = 0;
        _queueCount = 0;
        _draining = false;

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null && _running)
            {
                Trace.WriteLine($"capture stopped: {e.Exception.Message}; restarting");
                _restartRequested = true;
            }
        };
        capture.StartRecording();
        _capture = capture;
        CaptureChanged?.Invoke(CaptureFormatDescription);
    }

    /// <summary>Caller holds the lock.</summary>
    private void CloseCapture()
    {
        if (_capture == null) return;
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
        _capture = null;
        _chain = null;
        _buffered = null;
    }

    private void RestartCapture()
    {
        lock (_lock)
        {
            if (!_running) return;
            _restartRequested = false;
            try { OpenCapture(); }
            catch (Exception ex)
            {
                Trace.WriteLine($"capture open failed: {ex.Message}");
                CaptureFormatDescription = $"capture failed: {ex.Message}";
                CaptureChanged?.Invoke(CaptureFormatDescription);
                // Try again on the next pacer tick after a short delay; the pacer keeps sending silence meanwhile.
                Task.Delay(1000).ContinueWith(_ => { if (_running) _restartRequested = true; });
            }
        }
    }

    // MARK: Capture → queue

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        lock (_lock)
        {
            if (!_running || _buffered == null || _chain == null) return;
            _buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
            while (true)
            {
                int n = _chain.Read(_floatScratch, 0, _floatScratch.Length);
                if (n <= 0) break;
                Enqueue(_floatScratch.AsSpan(0, n));
                if (n < _floatScratch.Length) break;
            }
        }
    }

    /// <summary>Converts floats to s16 and appends to the queue, dropping the oldest audio if full. Caller holds the lock.</summary>
    private void Enqueue(ReadOnlySpan<float> samples)
    {
        int bytes = samples.Length * 2;
        if (_convertScratch.Length < bytes) _convertScratch = new byte[bytes];
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
            short s = (short)MathF.Round(v * 32767f);
            _convertScratch[2 * i] = (byte)s;
            _convertScratch[2 * i + 1] = (byte)(s >> 8);
        }
        if (bytes > _queue.Length) return;
        int overflow = _queueCount + bytes - _queue.Length;
        if (overflow > 0)
        {
            // Drop whole packets from the head so packet boundaries stay aligned to real audio.
            int drop = (overflow + PacketBytes - 1) / PacketBytes * PacketBytes;
            drop = Math.Min(drop, _queueCount);
            _queueHead = (_queueHead + drop) % _queue.Length;
            _queueCount -= drop;
            Interlocked.Add(ref DroppedPackets, drop / PacketBytes);
        }
        int tail = (_queueHead + _queueCount) % _queue.Length;
        int first = Math.Min(bytes, _queue.Length - tail);
        Buffer.BlockCopy(_convertScratch, 0, _queue, tail, first);
        if (bytes > first) Buffer.BlockCopy(_convertScratch, first, _queue, 0, bytes - first);
        _queueCount += bytes;
    }

    /// <summary>Removes one packet from the queue into <paramref name="dst"/>. Caller holds the lock and checked the count.</summary>
    private void Dequeue(byte[] dst)
    {
        int first = Math.Min(PacketBytes, _queue.Length - _queueHead);
        Buffer.BlockCopy(_queue, _queueHead, dst, 0, first);
        if (PacketBytes > first) Buffer.BlockCopy(_queue, 0, dst, first, PacketBytes - first);
        _queueHead = (_queueHead + PacketBytes) % _queue.Length;
        _queueCount -= PacketBytes;
    }

    // MARK: Pacer

    private void PacerLoop()
    {
        long packetTicks = (long)(PacketDuration.TotalSeconds * Stopwatch.Frequency);
        long maxCatchUpTicks = Stopwatch.Frequency / 5;   // after a stall > 200 ms, resynchronise instead of bursting
        long nextDue = _clock.ElapsedTicks;
        int prebufferBytes = (int)((long)_prebufferMs * OutputSampleRate / 1000) * BytesPerFrame;

        while (_running)
        {
            long now = _clock.ElapsedTicks;
            long wait = nextDue - now;
            if (wait > 0)
            {
                double waitMs = wait * 1000.0 / Stopwatch.Frequency;
                if (waitMs > 2) Thread.Sleep((int)(waitMs - 1));
                else Thread.SpinWait(20);
                continue;
            }
            if (_restartRequested) RestartCapture();

            var packet = new byte[PacketBytes];
            bool real = false;
            lock (_lock)
            {
                if (_running)
                {
                    if (!_draining && _queueCount >= prebufferBytes) _draining = true;
                    if (_draining)
                    {
                        if (_queueCount >= PacketBytes) { Dequeue(packet); real = true; }
                        else _draining = false;   // queue ran dry: system went silent, go back to waiting for prebuffer
                    }
                }
            }
            if (real) Interlocked.Increment(ref CapturedPackets); else Interlocked.Increment(ref SilencePackets);
            PacketReady?.Invoke(packet);

            nextDue += packetTicks;
            if (now - nextDue > maxCatchUpTicks) nextDue = now;
        }
    }

    // MARK: Helpers

    /// <summary>WASAPI loopback capture with a configurable buffer (NAudio's WasapiLoopbackCapture is fixed at 100 ms).</summary>
    private sealed class LoopbackCapture : WasapiCapture
    {
        public LoopbackCapture(MMDevice device, int bufferMs) : base(device, false, bufferMs) { }
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
    }

    /// <summary>Restarts capture when the default playback device changes or the active device disappears.</summary>
    private sealed class DeviceNotifications(AudioCapturePipeline owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (owner.ActiveSource?.Id == deviceId && newState != DeviceState.Active) owner._restartRequested = true;
        }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId)
        {
            if (owner.ActiveSource?.Id == deviceId) owner._restartRequested = true;
        }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia && owner.SelectedSource == null) owner._restartRequested = true;
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    /// <summary>Keeps the first two channels of a multichannel source.</summary>
    private sealed class FirstTwoChannelsSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _sourceChannels;
        private float[] _scratch = new float[8192];

        public FirstTwoChannelsSampleProvider(ISampleProvider source)
        {
            _source = source;
            _sourceChannels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int needed = frames * _sourceChannels;
            if (_scratch.Length < needed) _scratch = new float[needed];
            int got = _source.Read(_scratch, 0, needed) / _sourceChannels;
            for (int f = 0; f < got; f++)
            {
                buffer[offset + 2 * f] = _scratch[f * _sourceChannels];
                buffer[offset + 2 * f + 1] = _scratch[f * _sourceChannels + 1];
            }
            return got * 2;
        }
    }

    private static class TimePeriod
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint ms);
        public static void Begin(uint ms) { if (OperatingSystem.IsWindows()) TimeBeginPeriod(ms); }
        public static void End(uint ms) { if (OperatingSystem.IsWindows()) TimeEndPeriod(ms); }
    }
}
