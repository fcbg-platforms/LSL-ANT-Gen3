using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AntLslStreamer;

/// <summary>
/// A small LSL inlet used to verify that the stream can be resolved and read by any LSL client.
/// </summary>
internal static class InletTest
{
    public static int Run(Options opt, CancellationToken ct)
    {
        double duration = opt.Duration ?? 10;
        string prop = opt.InletName != null ? "name" : "type";
        string value = opt.InletName ?? opt.InletType;

        Log.Info($"Resolving LSL stream with {prop}='{value}' (up to 15 s)...");
        var found = new IntPtr[16];
        int n = Lsl.lsl_resolve_byprop(found, (uint)found.Length, prop, value, 1, 15.0);
        if (n <= 0)
        {
            Log.Error("No stream found.");
            return 1;
        }
        for (int i = 0; i < n; i++)
            Log.Info($"  found '{Lsl.PtrToString(Lsl.lsl_get_name(found[i]))}' type={Lsl.PtrToString(Lsl.lsl_get_type(found[i]))} " +
                     $"ch={Lsl.lsl_get_channel_count(found[i])} srate={Lsl.lsl_get_nominal_srate(found[i])} " +
                     $"source_id={Lsl.PtrToString(Lsl.lsl_get_source_id(found[i]))}");

        IntPtr info = found[0];
        string sourceId = Lsl.PtrToString(Lsl.lsl_get_source_id(info));
        int nch = Lsl.lsl_get_channel_count(info);
        double srate = Lsl.lsl_get_nominal_srate(info);

        IntPtr inlet = Lsl.lsl_create_inlet(info, 360, 0, 1);
        int stimCh = -1;
        var chLabels = new List<string>();
        var chTypes = new List<string>();
        IntPtr fullInfo = Lsl.lsl_get_fullinfo(inlet, 5.0, out int ec);
        if (ec == 0 && fullInfo != IntPtr.Zero)
        {
            string xml = Lsl.TakeString(Lsl.lsl_get_xml(fullInfo));
            var labels = Regex.Matches(xml, "<label>(.*?)</label>").Select(m => m.Groups[1].Value).ToList();
            chLabels = labels;
            chTypes = Regex.Matches(xml, "<channel>.*?<type>(.*?)</type>", RegexOptions.Singleline)
                .Select(m => m.Groups[1].Value).ToList();
            stimCh = labels.IndexOf("STIM");
            var unit = Regex.Match(xml, "<unit>(.*?)</unit>").Groups[1].Value;
            var model = Regex.Match(xml, "<model>(.*?)</model>").Groups[1].Value;
            var cap = Regex.Match(xml, "<cap>.*?<name>(.*?)</name>", RegexOptions.Singleline).Groups[1].Value;
            var types = Regex.Matches(xml, "<channel>.*?<type>(.*?)</type>", RegexOptions.Singleline).Select(m => m.Groups[1].Value)
                .GroupBy(t => t).Select(g => $"{g.Key}x{g.Count()}");
            Log.Info($"Metadata: model={model}, cap={(cap == "" ? "-" : cap)}, unit={unit}, types {string.Join(" ", types)}");
            Log.Info($"{labels.Count} labels: {string.Join(",", labels)}");
            Lsl.lsl_destroy_streaminfo(fullInfo);
        }

        // Try to also attach to the matching marker stream.
        IntPtr markerInlet = IntPtr.Zero;
        var mfound = new IntPtr[1];
        if (Lsl.lsl_resolve_byprop(mfound, 1, "source_id", sourceId + "-markers", 1, 2.0) > 0)
        {
            markerInlet = Lsl.lsl_create_inlet(mfound[0], 360, 0, 1);
            Lsl.lsl_open_stream(markerInlet, 5.0, out _);
            Log.Info($"Also reading marker stream '{Lsl.PtrToString(Lsl.lsl_get_name(mfound[0]))}'.");
        }

        Lsl.lsl_open_stream(inlet, 10.0, out ec);
        if (ec != 0) { Log.Error($"lsl_open_stream failed ({ec})"); return 1; }
        double tc = Lsl.lsl_time_correction(inlet, 5.0, out _);

        int maxSamples = 4096;
        var data = new float[maxSamples * nch];
        var ts = new double[maxSamples];
        long total = 0, markers = 0, stimEvents = 0;
        double firstTs = double.NaN, lastTs = double.NaN, maxGap = 0;
        int showCh = nch;
        var sum = new double[showCh];
        var sumSq = new double[showCh];
        var min = Enumerable.Repeat(double.PositiveInfinity, showCh).ToArray();
        var max = Enumerable.Repeat(double.NegativeInfinity, showCh).ToArray();
        var nans = new long[showCh];
        var sw = Stopwatch.StartNew();
        var report = Stopwatch.StartNew();
        var mbuf = new IntPtr[1];

        while (sw.Elapsed.TotalSeconds < duration && !ct.IsCancellationRequested)
        {
            uint got = Lsl.lsl_pull_chunk_f(inlet, data, ts, (uint)data.Length, (uint)ts.Length, 0.2, out ec);
            if (ec != 0) { Log.Error($"lsl_pull_chunk failed ({ec})"); break; }
            int ns = (int)(got / (uint)nch);
            for (int s = 0; s < ns; s++)
            {
                if (double.IsNaN(firstTs)) firstTs = ts[s];
                else maxGap = Math.Max(maxGap, ts[s] - lastTs);
                lastTs = ts[s];
                if (stimCh >= 0 && data[s * nch + stimCh] != 0)
                {
                    stimEvents++;
                    Log.Info($"  STIM {data[s * nch + stimCh]} at {ts[s]:F4}");
                }
                for (int c = 0; c < showCh; c++)
                {
                    double v = data[s * nch + c];
                    if (double.IsNaN(v)) { nans[c]++; continue; }
                    sum[c] += v;
                    sumSq[c] += v * v;
                    if (v < min[c]) min[c] = v;
                    if (v > max[c]) max[c] = v;
                }
            }
            total += ns;

            while (markerInlet != IntPtr.Zero)
            {
                double mts = Lsl.lsl_pull_sample_str(markerInlet, mbuf, 1, 0.0, out int mec);
                if (mts == 0 || mec != 0) break;
                markers++;
                Log.Info($"  marker '{Lsl.TakeString(mbuf[0])}' at {mts:F4}");
            }

            if (report.Elapsed.TotalSeconds >= 2)
            {
                Log.Info($"  received {total} samples, last timestamp {lastTs:F4}");
                report.Restart();
            }
        }

        double span = lastTs - firstTs;
        Log.Info("---- inlet summary ----");
        Log.Info($"samples: {total} in {sw.Elapsed.TotalSeconds:F1} s wall clock; markers: {markers}" +
                 (stimCh >= 0 ? $"; STIM events: {stimEvents}" : "; no STIM channel"));
        if (total > 1)
        {
            Log.Info($"timestamp span {span:F3} s -> measured rate {(total - 1) / span:F2} Hz (nominal {srate} Hz), " +
                     $"max timestamp gap {maxGap * 1000:F1} ms, time correction {tc * 1000:F2} ms");
            var sds = new double[showCh];
            if (opt.Stats)
                Log.Info($"  {"#",3} {"label",-8} {"type",-5} {"mean",12} {"sd",12} {"min",12} {"max",12} {"NaN",6}");
            for (int c = 0; c < showCh; c++)
            {
                long valid = total - nans[c];
                double mean = valid > 0 ? sum[c] / valid : double.NaN;
                sds[c] = valid > 0 ? Math.Sqrt(Math.Max(0, sumSq[c] / valid - mean * mean)) : 0;
                string label = c < chLabels.Count ? chLabels[c] : $"ch{c}";
                string type = c < chTypes.Count ? chTypes[c] : "";
                if (opt.Stats)
                    Log.Info($"  {c,3} {label,-8} {type,-5} {mean,12:G5} {sds[c],12:G5} {min[c],12:G5} {max[c],12:G5} {nans[c],6}");
                else if (c < 4)
                    Log.Info($"  ch{c}: mean {mean:G5}, sd {sds[c]:G5}");
            }
            int distinct = sds.Select(v => Math.Round(v, 6)).Distinct().Count();
            Log.Info($"  sd across all {showCh} channels: min {sds.Min():G4}, max {sds.Max():G4} " +
                     $"({distinct} distinct values); flat channels: {sds.Count(v => v == 0)}");
        }

        if (markerInlet != IntPtr.Zero) Lsl.lsl_destroy_inlet(markerInlet);
        Lsl.lsl_destroy_inlet(inlet);
        return total > 0 ? 0 : 1;
    }
}
