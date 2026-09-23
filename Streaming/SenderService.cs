using System.Diagnostics;
using TetherSender.Audio;
using TetherSender.Usbmux;

namespace TetherSender.Streaming;

public enum SenderState { NoUsbmuxd, NoDevice, Connecting, PhoneNotListening, Streaming, Disconnected, Stopped }

public sealed record SenderStatus(SenderState State, string Text, UsbDevice? Device = null, SessionStats? Stats = null);

/// <summary>
/// Reconnect loop (§11.2 step 7): lists USB devices, connects through usbmuxd, runs a session, and
/// retries every second forever. A usbmuxd Listen socket wakes the loop immediately on Attached.
/// </summary>
public sealed class SenderService : IDisposable
{
    private readonly SenderOptions _options;
    private readonly AudioCapturePipeline _audio;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private Task? _loop;
    private Task? _listen;
    private string? _selectedSerial;
    private StreamSession? _session;
    private readonly Settings _settings;

    public event Action<SenderStatus>? StatusChanged;
    public SenderStatus Status { get; private set; } = new(SenderState.Stopped, "Stopped");
    public IReadOnlyList<UsbDevice> Devices { get; private set; } = Array.Empty<UsbDevice>();

    public SenderService(SenderOptions options)
    {
        _options = options;
        _settings = Settings.Load();
        _selectedSerial = options.PreferredSerial ?? _settings.DeviceSerial;
        _audio = new AudioCapturePipeline(options.FramesPerPacket, _settings.PrebufferMs ?? options.PrebufferMs);
        _audio.CaptureChanged += text => CaptureStatusChanged?.Invoke(text);

        // Audio source: --source substring, else the persisted choice, else the default playback device.
        AudioSource? source = null;
        try
        {
            var sources = AudioCapturePipeline.ListSources();
            if (options.SourceName != null)
                source = sources.FirstOrDefault(s => s.Name.Contains(options.SourceName, StringComparison.OrdinalIgnoreCase));
            else if (_settings.AudioSourceId != null)
                source = sources.FirstOrDefault(s => s.Id == _settings.AudioSourceId);
        }
        catch (Exception ex) { Trace.WriteLine($"listing audio sources failed: {ex.Message}"); }
        _audio.SelectSource(source);
    }

    public event Action<string>? CaptureStatusChanged;

    public AudioSource? SelectedSource => _audio.SelectedSource;
    public AudioSource? ActiveSource => _audio.ActiveSource;

    public IReadOnlyList<AudioSource> ListAudioSources()
    {
        try { return AudioCapturePipeline.ListSources(); }
        catch { return Array.Empty<AudioSource>(); }
    }

    /// <summary>Switches the capture source (null = default playback device) and remembers it.</summary>
    public void SelectAudioSource(AudioSource? source)
    {
        _audio.SelectSource(source);
        _settings.AudioSourceId = source?.Id;
        _settings.Save();
    }

    public string CaptureStats => $"queue {_audio.QueueMs} ms · real {Interlocked.Read(ref _audio.CapturedPackets)} · silence {Interlocked.Read(ref _audio.SilencePackets)} · dropped {Interlocked.Read(ref _audio.DroppedPackets)}";

    public string? SelectedSerial
    {
        get => _selectedSerial;
        set
        {
            if (_selectedSerial == value) return;
            _selectedSerial = value;
            _settings.DeviceSerial = value;
            _settings.Save();
            Reconnect();
        }
    }

    public string CaptureDescription => _audio.CaptureFormatDescription;

    public void Start()
    {
        _audio.Start();
        _listen = Task.Run(ListenLoopAsync);
        _loop = Task.Run(MainLoopAsync);
    }

    /// <summary>Drops the current session (if any) so the loop reconnects.</summary>
    public void Reconnect()
    {
        _session?.Dispose();
        _wake.Release();
    }

    public void Stop()
    {
        _cts.Cancel();
        _session?.Dispose();
        _wake.Release();
        try { _loop?.Wait(2000); } catch { }
        _audio.Stop();
        Publish(new SenderStatus(SenderState.Stopped, "Stopped"));
    }

    public void Dispose() => Stop();

    private async Task MainLoopAsync()
    {
        var ct = _cts.Token;
        string name = Environment.MachineName;
        while (!ct.IsCancellationRequested)
        {
            List<UsbDevice> devices;
            try
            {
                devices = UsbmuxClient.ListUsbDevices();
                Devices = devices;
            }
            catch (Exception ex)
            {
                Devices = Array.Empty<UsbDevice>();
                Publish(new SenderStatus(SenderState.NoUsbmuxd, "usbmuxd not reachable. Install the Apple Devices app or iTunes and make sure the Apple Mobile Device Service is running."));
                Trace.WriteLine($"ListDevices failed: {ex.Message}");
                await WaitAsync(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            var device = devices.FirstOrDefault(d => _selectedSerial == null || d.Serial == _selectedSerial) ?? devices.FirstOrDefault();
            if (device == null)
            {
                Publish(new SenderStatus(SenderState.NoDevice, "No iPhone found over USB. Plug it in and tap Trust."));
                await WaitAsync(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            Publish(new SenderStatus(SenderState.Connecting, $"Connecting to {device.Serial}…", device));
            try
            {
                using var socket = UsbmuxClient.Connect(device, _options.Port);
                using var session = new StreamSession(socket, _audio, _options.HandleControlFrames);
                _session = session;
                session.StatsUpdated += stats => Publish(new SenderStatus(SenderState.Streaming,
                    $"Streaming to {device.Serial} · {_audio.OutputSampleRate / 1000.0:0.#} kHz stereo · RTT {stats.LastRttMs:F1} ms · phone buffer {stats.PhoneBufferedMs} ms · underruns {stats.PhoneUnderruns}",
                    device, stats));
                Publish(new SenderStatus(SenderState.Streaming, $"Streaming to {device.Serial} ({_audio.CaptureFormatDescription})", device));
                await session.RunAsync(name, ct);
                Publish(new SenderStatus(SenderState.Disconnected, "Phone ended the session. Reconnecting…", device));
            }
            catch (UsbmuxConnectException ex) when (ex.IsRefused)
            {
                Publish(new SenderStatus(SenderState.PhoneNotListening, "Phone found, but Tether Audio isn't listening. Open the app on the phone.", device));
            }
            catch (RejectedException ex)
            {
                Publish(new SenderStatus(SenderState.Disconnected, $"Phone rejected the stream: {ex.Reason}", device));
                await WaitAsync(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Publish(new SenderStatus(SenderState.Disconnected, $"Disconnected: {ex.Message}. Reconnecting…", device));
                Trace.WriteLine(ex);
            }
            finally
            {
                _session = null;
            }
            await WaitAsync(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task ListenLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                UsbmuxClient.Listen(
                    onAttached: d => { Trace.WriteLine($"attached {d}"); _wake.Release(); },
                    onDetached: id => Trace.WriteLine($"detached #{id}"),
                    ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Trace.WriteLine($"usbmuxd listen failed: {ex.Message}");
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { }
            }
        }
    }

    /// <summary>Waits for the timeout or an early wake-up (device attached / reconnect requested).</summary>
    private async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _wake.WaitAsync(timeout, ct); } catch (OperationCanceledException) { }
    }

    private void Publish(SenderStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }
}
