using System.Globalization;

namespace AntLslStreamer;

internal enum Command { Stream, List, Inlet, Help }

/// <summary>Command line options.</summary>
internal sealed class Options
{
    public Command Command { get; private set; } = Command.Stream;

    // ---- acquisition ----
    public double? SamplingRate { get; private set; }
    public double? ReferentialRange { get; private set; }
    public double? BipolarRange { get; private set; }
    public List<int>? Channels { get; private set; }
    public bool ReferentialOnly { get; private set; }
    public string? Serial { get; private set; }
    public string? LabelsFile { get; private set; }
    public string? Cap { get; private set; }

    // ---- LSL ----
    public string? StreamName { get; private set; }
    public string? SourceId { get; private set; }
    public bool Markers { get; private set; } = true;
    public bool KeepVolts { get; private set; }

    // ---- simulator ----
    public string? SimFile { get; private set; }
    public string SimType { get; private set; } = "EE_225";

    // ---- misc ----
    public double? Duration { get; private set; }
    public string LogDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntLslStreamer", "logs");

    // ---- inlet test ----
    public string InletType { get; private set; } = "EEG";
    public string? InletName { get; private set; }

    public const string Usage = """
        AntLslStreamer - stream an ANT Neuro eego amplifier (EDI2 SDK) to Lab Streaming Layer

        Usage:
          AntLslStreamer [stream] [options]   Connect to the amplifier and stream EEG + triggers to LSL (default)
          AntLslStreamer list                 List connected amplifiers, channels, sampling rates and ranges
          AntLslStreamer inlet [options]      Built-in test inlet: resolve a stream and print what arrives
          AntLslStreamer help

        Stream options:
          -r, --rate <Hz>            Sampling rate (default: 500 if available, else the lowest available)
          --ref-range <V>            Referential (EEG) input range in volts (default: largest available, e.g. 1)
          --bip-range <V>            Bipolar input range in volts (default: largest available)
          --channels <list>          Amplifier channel indices to stream, e.g. "0-63,64,65" (default: all)
          --referential-only         Only stream referential (EEG) channels
          -c, --cap <cap>            EEG cap connected to the amplifier: CA-208 or NA-265 (64-ch waveguard).
                                     Sets the electrode labels of the referential channels.
          --labels <file>            Text file with one channel label per line (or comma separated), in stream order
          --serial <serial>          Use the amplifier with this serial if several are connected
          -n, --name <name>          LSL stream name (default: "eego <model> <serial>")
          --source-id <id>           LSL source id (default: "ANT-eego-<serial>")
          --no-markers               Do not create the trigger marker stream
          --volts                    Stream raw volts instead of microvolts
          --sim <file.cnt>           Use the EDI2 file simulator with this .cnt file instead of USB
          --sim-type <EE_xxx>        Amplifier type reported by the simulator (default: EE_225)
          -d, --duration <s>         Stop automatically after this many seconds
          --log-dir <dir>            EDI2 log directory (default: %LOCALAPPDATA%\AntLslStreamer\logs)

        Inlet options:
          --type <type>              Stream type to resolve (default: EEG)
          -n, --name <name>          Stream name to resolve (overrides --type)
          -d, --duration <s>         How long to read (default: 10)
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        int i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            o.Command = args[0].ToLowerInvariant() switch
            {
                "stream" => Command.Stream,
                "list" => Command.List,
                "inlet" => Command.Inlet,
                "help" => Command.Help,
                _ => throw new ArgumentException($"Unknown command '{args[0]}'"),
            };
            i = 1;
        }

        for (; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}");
            double NextDouble() => double.Parse(Next(), CultureInfo.InvariantCulture);

            switch (a)
            {
                case "-h": case "--help": o.Command = Command.Help; break;
                case "-r": case "--rate": o.SamplingRate = NextDouble(); break;
                case "--ref-range": o.ReferentialRange = NextDouble(); break;
                case "--bip-range": o.BipolarRange = NextDouble(); break;
                case "--channels": o.Channels = ParseIndexList(Next()); break;
                case "--referential-only": o.ReferentialOnly = true; break;
                case "--labels": o.LabelsFile = Next(); break;
                case "-c": case "--cap": o.Cap = Caps.Normalize(Next()); break;
                case "--serial": o.Serial = Next(); break;
                case "-n": case "--name": o.StreamName = Next(); o.InletName = o.StreamName; break;
                case "--source-id": o.SourceId = Next(); break;
                case "--no-markers": o.Markers = false; break;
                case "--volts": o.KeepVolts = true; break;
                case "--sim": o.SimFile = Path.GetFullPath(Next()); break;
                case "--sim-type": o.SimType = Next(); break;
                case "-d": case "--duration": o.Duration = NextDouble(); break;
                case "--log-dir": o.LogDir = Next(); break;
                case "--type": o.InletType = Next(); break;
                default: throw new ArgumentException($"Unknown option '{a}'");
            }
        }
        return o;
    }

    private static List<int> ParseIndexList(string s)
    {
        var result = new List<int>();
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-');
            if (range.Length == 2)
            {
                int a = int.Parse(range[0]), b = int.Parse(range[1]);
                for (int k = a; k <= b; k++) result.Add(k);
            }
            else result.Add(int.Parse(part));
        }
        return result.Distinct().ToList();
    }
}
