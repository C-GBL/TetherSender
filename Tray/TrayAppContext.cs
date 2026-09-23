using TetherSender.Audio;
using TetherSender.Streaming;
using TetherSender.Usbmux;

namespace TetherSender.Tray;

/// <summary>Tray icon with status, device picker, and reconnect (§11.1, M8).</summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _statusItem = new("Starting…") { Enabled = false };
    private readonly ToolStripMenuItem _devicesItem = new("iPhone");
    private readonly ToolStripMenuItem _sourcesItem = new("Audio source");
    private readonly ToolStripMenuItem _makeDefaultItem = new("Send all Windows audio here (set as default playback device)");
    private readonly ToolStripMenuItem _captureItem = new("") { Enabled = false };
    private readonly SenderService _service;
    private readonly SynchronizationContext _ui;

    public TrayAppContext(SenderOptions options)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _service = new SenderService(options);
        _service.StatusChanged += OnStatus;

        _menu.Items.Add(_statusItem);
        _menu.Items.Add(_captureItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_devicesItem);
        _menu.Items.Add(_sourcesItem);
        _menu.Items.Add(_makeDefaultItem);
        _makeDefaultItem.Click += (_, _) => MakeActiveSourceDefault();
        _menu.Items.Add(new ToolStripMenuItem("Open Windows sound settings", null, (_, _) => OpenSoundSettings()));
        _menu.Items.Add(new ToolStripMenuItem("Reconnect", null, (_, _) => _service.Reconnect()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem($"Port {options.Port}") { Enabled = false });
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));
        _menu.Opening += (_, _) => { RefreshDevices(); RefreshSources(); };
        _service.CaptureStatusChanged += text => _ui.Post(_ => _captureItem.Text = $"Capture: {text}", null);

        _icon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Tether Sender",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        try
        {
            _service.Start();
            _captureItem.Text = $"Capture: {_service.CaptureDescription}";
        }
        catch (Exception ex)
        {
            _statusItem.Text = $"Audio capture failed: {ex.Message}";
            _icon.ShowBalloonTip(5000, "Tether Sender", $"Audio capture failed: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void OnStatus(SenderStatus status)
    {
        _ui.Post(_ =>
        {
            _statusItem.Text = status.Text.Length > 120 ? status.Text[..120] + "…" : status.Text;
            var tip = status.State switch
            {
                SenderState.Streaming => "Tether Sender – streaming",
                SenderState.NoDevice => "Tether Sender – no iPhone",
                SenderState.PhoneNotListening => "Tether Sender – open Tether Audio on the phone",
                _ => "Tether Sender",
            };
            _icon.Text = tip.Length > 63 ? tip[..63] : tip;
        }, null);
    }

    private void RefreshDevices()
    {
        _devicesItem.DropDownItems.Clear();
        var devices = _service.Devices;
        if (devices.Count == 0)
        {
            _devicesItem.DropDownItems.Add(new ToolStripMenuItem("No iPhone over USB") { Enabled = false });
            return;
        }
        var auto = new ToolStripMenuItem("Automatic (first USB device)") { Checked = _service.SelectedSerial == null };
        auto.Click += (_, _) => _service.SelectedSerial = null;
        _devicesItem.DropDownItems.Add(auto);
        foreach (UsbDevice d in devices)
        {
            var item = new ToolStripMenuItem(d.ToString()) { Checked = _service.SelectedSerial == d.Serial };
            item.Click += (_, _) => _service.SelectedSerial = d.Serial;
            _devicesItem.DropDownItems.Add(item);
        }
    }

    private void RefreshSources()
    {
        _sourcesItem.DropDownItems.Clear();
        var selected = _service.SelectedSource;
        var auto = new ToolStripMenuItem("Default playback device (follows Windows setting)") { Checked = selected == null };
        auto.Click += (_, _) => _service.SelectAudioSource(null);
        _sourcesItem.DropDownItems.Add(auto);
        _sourcesItem.DropDownItems.Add(new ToolStripSeparator());
        foreach (AudioSource source in _service.ListAudioSources())
        {
            var item = new ToolStripMenuItem(source.ToString()) { Checked = selected?.Id == source.Id };
            var captured = source;
            item.Click += (_, _) => _service.SelectAudioSource(captured);
            _sourcesItem.DropDownItems.Add(item);
        }
        var active = _service.ActiveSource;
        _makeDefaultItem.Enabled = active is { IsLoopback: true, IsDefault: false };
        _makeDefaultItem.Text = active is { IsLoopback: true }
            ? $"Send all Windows audio to “{active.Name}” (set as default playback device)"
            : "Send all Windows audio here (needs a playback device as source)";
        _captureItem.Text = $"Capture: {_service.CaptureDescription} · {_service.CaptureStats}";
    }

    private void MakeActiveSourceDefault()
    {
        var active = _service.ActiveSource;
        if (active == null || !active.IsLoopback) return;
        if (DefaultDeviceSwitcher.TrySetDefaultPlayback(active.Id, out var error))
            _icon.ShowBalloonTip(3000, "Tether Sender", $"“{active.Name}” is now the default playback device. Everything Windows plays goes to the phone.", ToolTipIcon.Info);
        else
            _icon.ShowBalloonTip(5000, "Tether Sender", $"Couldn't change the default device ({error}). Set it in Windows sound settings instead.", ToolTipIcon.Warning);
    }

    /// <summary>Embedded multi-size .ico (Assets/tray.ico); falls back to the stock icon if missing.</summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            using var stream = typeof(TrayAppContext).Assembly.GetManifestResourceStream("TetherSender.tray.ico");
            if (stream != null) return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch { }
        return SystemIcons.Application;
    }

    private static void OpenSoundSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); } catch { }
    }

    private void Exit()
    {
        _icon.Visible = false;
        _service.Stop();
        _service.Dispose();
        ExitThread();
    }
}
