using System.Diagnostics;
using System.Globalization;
using ES.DataModel.Amplifier;
using ES.Edi;

namespace AntLslStreamer;

/// <summary>One impedance value as written to the stream header.</summary>
internal sealed record ImpedanceValue(string Label, int? AmplifierIndex, double Ohms, string State);

/// <summary>Impedances measured before streaming (GitHub issue #1).</summary>
internal sealed record ImpedanceResult(
    DateTimeOffset MeasuredAt, int Frames,
    IReadOnlyList<ImpedanceValue> Channels,
    IReadOnlyList<ImpedanceValue> Reference,
    IReadOnlyList<ImpedanceValue> Ground)
{
    public string Summary()
    {
        if (Channels.Count == 0) return "no channel values";
        var v = Channels.Select(c => c.Ohms / 1000).OrderBy(x => x).ToList();
        string f(double x) => x.ToString("F1", CultureInfo.InvariantCulture);
        return $"{Channels.Count} electrodes, median {f(v[v.Count / 2])} kOhm (min {f(v[0])}, max {f(v[^1])}), " +
               $"{v.Count(x => x > 20)} above 20 kOhm; " +
               $"REF {string.Join("/", Reference.Select(r => f(r.Ohms / 1000)))} kOhm, " +
               $"GND {string.Join("/", Ground.Select(g => f(g.Ohms / 1000)))} kOhm";
    }
}

internal static class Impedance
{
    /// <summary>
    /// Puts the amplifier in impedance mode for about <paramref name="seconds"/> and returns the median of
    /// each electrode's values. The first frame is dropped: right after the mode switch it holds a
    /// settling transient. EDI2 reports values in ohms. Its connection state was "Connected" even with no
    /// cap attached, so it is stored but should not be relied on. Leaves the amplifier in Idle mode.
    /// </summary>
    /// <param name="referentialLabel">Label for the i-th referential electrode (i = position in the impedance list).</param>
    public static ImpedanceResult? Measure(IAmplifier amp, double seconds, Func<int, (string Label, int? Index)> referentialLabel,
        CancellationToken ct)
    {
        if (!amp.GetModesAvailable().Contains(AmplifierMode.Impedance))
        {
            Log.Warn("Amplifier has no impedance mode; impedances are not recorded.");
            return null;
        }

        var rates = amp.GetSamplingRatesAvailable();
        double rate = rates.Contains(500) ? 500 : rates.Min(); // EDI2 ignores the rate in impedance mode
        var sp = new StreamParams
        {
            SamplingRate = rate,
            ActiveChannels = amp.GetChannelsAvailable()
                .Where(c => c.ChannelPolarity != ChannelPolarity.Disabled).Select(c => c.ChannelIndex).ToList(),
            // Impedance mode needs a range for every polarity (KeyNotFoundException otherwise).
            Ranges = amp.GetRangesAvailable().Where(kv => kv.Value is { Count: > 0 }).ToDictionary(kv => kv.Key, kv => kv.Value.Max()),
            BufferSize = (int)rate,
            DataReadyPercentage = 10,
        };

        Log.Info($"Measuring impedances for {seconds.ToString(CultureInfo.InvariantCulture)} s (use --impedance 0 to skip)...");
        var frames = new List<Impedances>();
        var sw = Stopwatch.StartNew();
        amp.SetMode(AmplifierMode.Impedance, sp);
        try
        {
            // Wait the requested time, but at least until 2 frames are in (1 is dropped); give up after +10 s.
            while (!ct.IsCancellationRequested &&
                   (sw.Elapsed.TotalSeconds < seconds || frames.Count < 2) && sw.Elapsed.TotalSeconds < seconds + 10)
            {
                var got = amp.GetFrame();
                bool any = false;
                if (got != null)
                    foreach (var f in got)
                        if (f.Impedance?.Channels is { Count: > 0 }) { frames.Add(f.Impedance); any = true; }
                if (!any) ct.WaitHandle.WaitOne(50);
            }
        }
        finally
        {
            try { amp.SetMode(AmplifierMode.Idle, null); } catch { /* best effort */ }
        }

        if (frames.Count == 0)
        {
            Log.Warn("No impedance values received; impedances are not recorded.");
            return null;
        }
        var use = frames.Count > 1 ? frames.Skip(1).ToList() : frames;
        if (frames.Count == 1) Log.Warn("Only one impedance frame received; it may contain a settling transient.");

        static List<ImpedanceValue> Combine(List<Impedances> fr, Func<Impedances, List<(uint, ChannelConnectionState)>?> sel,
            Func<int, (string, int?)> label)
        {
            int n = fr.Max(f => sel(f)?.Count ?? 0);
            var result = new List<ImpedanceValue>(n);
            for (int i = 0; i < n; i++)
            {
                var vals = fr.Select(sel).Where(l => l != null && i < l.Count).Select(l => (double)l![i].Item1).OrderBy(x => x).ToList();
                double median = vals.Count % 2 == 1 ? vals[vals.Count / 2] : (vals[vals.Count / 2 - 1] + vals[vals.Count / 2]) / 2;
                var state = fr.Select(sel).Last(l => l != null && i < l.Count)![i].Item2.ToString();
                var (lab, idx) = label(i);
                result.Add(new ImpedanceValue(lab, idx, median, state));
            }
            return result;
        }

        var res = new ImpedanceResult(
            DateTimeOffset.Now, use.Count,
            Combine(use, f => f.Channels, referentialLabel),
            Combine(use, f => f.Reference, i => (i == 0 ? "REF" : $"REF{i + 1}", null)),
            Combine(use, f => f.Ground, i => (i == 0 ? "GND" : $"GND{i + 1}", null)));
        Log.Info($"Impedances ({res.Frames} frames): {res.Summary()}");
        return res;
    }
}
