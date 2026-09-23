using TetherSender.Tray;

namespace TetherSender;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var options = SenderOptions.Parse(args);
        Application.Run(new TrayAppContext(options));
    }
}

/// <summary>Command-line options. All optional; the tray UI can change the device at runtime.</summary>
public sealed class SenderOptions
{
    public ushort Port { get; init; } = 47474;
    public string? PreferredSerial { get; init; }
    public int FramesPerPacket { get; init; } = 480;
    public bool HandleControlFrames { get; init; } = true;
    /// <summary>Substring of an audio endpoint name to capture from (e.g. "CABLE Input"). Null = default playback device.</summary>
    public string? SourceName { get; init; }
    public int PrebufferMs { get; init; } = 50;

    public static SenderOptions Parse(string[] args)
    {
        ushort port = 47474;
        string? serial = null;
        int fpp = 480;
        bool control = true;
        string? source = null;
        int prebuffer = 50;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    port = ushort.Parse(args[++i]);
                    break;
                case "--device" when i + 1 < args.Length:
                    serial = args[++i];
                    break;
                case "--fpp" when i + 1 < args.Length:
                    fpp = int.Parse(args[++i]);
                    break;
                case "--no-control":
                    control = false;
                    break;
                case "--source" when i + 1 < args.Length:
                    source = args[++i];
                    break;
                case "--prebuffer" when i + 1 < args.Length:
                    prebuffer = int.Parse(args[++i]);
                    break;
            }
        }
        return new SenderOptions { Port = port, PreferredSerial = serial, FramesPerPacket = fpp, HandleControlFrames = control, SourceName = source, PrebufferMs = prebuffer };
    }
}
