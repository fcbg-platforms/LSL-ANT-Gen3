using AntLslStreamer;

Options opt;
try
{
    opt = Options.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

if (opt.Command == Command.Help)
{
    Console.WriteLine(Options.Usage);
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Log.Info("Stopping...");
    cts.Cancel();
};
if (opt.Duration is double d && opt.Command == Command.Stream)
    cts.CancelAfter(TimeSpan.FromSeconds(d));

// Use the bundled liblsl config (quiet logging) unless the user provides their own.
string bundledCfg = Path.Combine(AppContext.BaseDirectory, "lsl_api.cfg");
if (Environment.GetEnvironmentVariable("LSLAPICFG") == null && File.Exists(bundledCfg)
    && !File.Exists("lsl_api.cfg"))
{
    // liblsl reads it with getenv(), i.e. from the C runtime's copy of the environment.
    Environment.SetEnvironmentVariable("LSLAPICFG", bundledCfg);
    try { CRuntime._putenv_s("LSLAPICFG", bundledCfg); } catch { /* cosmetic only */ }
}

try
{
    int v = Lsl.lsl_library_version();
    Log.Info($"liblsl {v / 100}.{v % 100}");
}
catch (DllNotFoundException)
{
    Log.Error("lsl.dll not found next to the executable.");
    return 3;
}

try
{
    return opt.Command switch
    {
        Command.List => Streamer.List(opt),
        Command.Inlet => InletTest.Run(opt, cts.Token),
        _ => new Streamer(opt, cts.Token).Run(),
    };
}
catch (Exception ex)
{
    Log.Error($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

namespace AntLslStreamer
{
    internal static class CRuntime
    {
        [System.Runtime.InteropServices.DllImport("ucrtbase.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        public static extern int _putenv_s(string name, string value);
    }

    internal static class Log
    {
        public static bool Verbose { get; set; } = Environment.GetEnvironmentVariable("ANTLSL_DEBUG") == "1";
        private static readonly object Gate = new();

        public static void Info(string m) => Write("INFO ", m, ConsoleColor.Gray);
        public static void Warn(string m) => Write("WARN ", m, ConsoleColor.Yellow);
        public static void Error(string m) => Write("ERROR", m, ConsoleColor.Red);
        public static void Debug(string m) { if (Verbose) Write("DEBUG", m, ConsoleColor.DarkGray); }

        private static void Write(string level, string m, ConsoleColor color)
        {
            lock (Gate)
            {
                var old = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} {m}");
                Console.ForegroundColor = old;
            }
        }
    }
}
