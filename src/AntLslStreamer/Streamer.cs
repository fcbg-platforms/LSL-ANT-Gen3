using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using ES.DataModel.Amplifier;
using ES.Edi;
using ES.Internal.Edi;

namespace AntLslStreamer;

/// <summary>
/// Connects to an eego amplifier through EDI2, configures EEG mode and forwards every frame to LSL.
/// Automatically waits for / reconnects to the amplifier when it is unplugged.
/// </summary>
internal sealed class Streamer
{
    public const string SdkVersion = "2.0.2.1355";

    private readonly Options _opt;
    private readonly CancellationToken _ct;
    private LslOutlets? _outlets;
    private volatile bool _detached;
    private ImpedanceResult? _impedance; // measured once, before the first outlet is created

    // Trigger de-duplication: EDI2 reports the same time marker in several consecutive frames (sliding
    // window). A marker is considered a repeat if the identical (time, code) was seen in the last 10 s.
    private readonly Dictionary<(long, int), long> _seenMarkers = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private const long MarkerDedupWindowMs = 10_000;

    // statistics
    private long _samples, _markers, _sampleLossEvents;

    public Streamer(Options opt, CancellationToken ct)
    {
        _opt = opt;
        _ct = ct;
    }

    // ===================================================================================================
    // Entry points
    // ===================================================================================================

    public int Run()
    {
        using var ctl = CreateController(_opt);
        try
        {
            while (!_ct.IsCancellationRequested)
            {
                IAmplifier? amp = WaitForAmplifier(ctl);
                if (amp == null) break; // cancelled

                try
                {
                    StreamFrom(amp);
                }
                catch (Exception ex) when (!_ct.IsCancellationRequested)
                {
                    Log.Error($"Acquisition stopped: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    TryIdle(amp);
                }

                if (_opt.SimFile != null && !_ct.IsCancellationRequested)
                {
                    Log.Warn("Simulator stopped delivering data; restarting it.");
                }
                if (!_ct.IsCancellationRequested)
                {
                    Log.Info("Will try to reconnect to the amplifier...");
                    _ct.WaitHandle.WaitOne(1000);
                }
            }
        }
        finally
        {
            _outlets?.Dispose();
            _outlets = null;
        }
        Log.Info($"Stopped. Streamed {_samples} samples and {_markers} markers in total.");
        return 0;
    }

    public static int List(Options opt)
    {
        using var ctl = CreateController(opt);
        var devices = FindDevices(ctl, opt, out _);
        if (devices.Count == 0)
        {
            Log.Warn("No amplifier found.");
            PrintNoDeviceHint();
            return 1;
        }
        foreach (var d in devices)
            Console.WriteLine($"Amplifier: {d.AmplifierType}  serial={d.Serial}  key={d.Key}");

        if (devices.Count > 1)
            Console.WriteLine($"Details below are for {devices[0].AmplifierType} {devices[0].Serial} " +
                              "(the one streamed by default). Use --serial to see another.");
        ctl.CreateDevice(new List<DeviceInfo> { devices[0] }, null);
        var amp = ctl.Amplifier ?? throw new InvalidOperationException("EDI2 did not create the amplifier.");
        try
        {
            Console.WriteLine($"Modes:           {string.Join(", ", amp.GetModesAvailable())}");
            Console.WriteLine($"Sampling rates:  {string.Join(", ", amp.GetSamplingRatesAvailable())}");
            foreach (var kv in amp.GetRangesAvailable())
                Console.WriteLine($"Ranges {kv.Key,-12}: {string.Join(", ", kv.Value ?? new List<double>())} V");
            foreach (var p in amp.GetDeviceParameters())
                Console.WriteLine($"Device:          battery={p.HasBattery} trigger-in bits={p.TriggerInputBitCount} trigger-out bits={p.TriggerOutputBitCount}");
            try
            {
                foreach (var p in amp.GetPower())
                    Console.WriteLine($"Power:           on={p.IsPowerOn} battery={p.BatteryLevel}% charging={p.IsBatteryCharging}");
            }
            catch (Exception ex) { Log.Debug($"GetPower: {ex.Message}"); }

            var chans = amp.GetChannelsAvailable();
            Console.WriteLine($"Channels ({chans.Count}):");
            foreach (var g in chans.GroupBy(c => c.ChannelPolarity))
            {
                var idx = g.Select(c => c.ChannelIndex).ToList();
                Console.WriteLine($"  {g.Key,-12} {idx.Count,3} channels, indices {FormatRanges(idx)}, unit {g.First().UnitType}");
            }
            var named = chans.Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
            if (named.Count > 0)
                Console.WriteLine("  Names: " + string.Join(", ", named.Select(c => $"{c.ChannelIndex}={c.Name}")));
        }
        finally
        {
            (amp as IDisposable)?.Dispose();
        }
        return 0;
    }

    // ===================================================================================================
    // Connection
    // ===================================================================================================

    private static EdiController CreateController(Options opt)
    {
        var ctl = new EdiController();
        try
        {
            Directory.CreateDirectory(opt.LogDir);
            ctl.InitLog(opt.LogDir.EndsWith('\\') ? opt.LogDir : opt.LogDir + "\\");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not initialise EDI2 log in '{opt.LogDir}': {ex.Message}");
        }
        return ctl;
    }

    private static IList<DeviceInfo> FindDevices(EdiController ctl, Options opt, out IDeviceManager manager)
    {
        IList<DeviceInfo> devices;
        if (opt.SimFile != null)
        {
            manager = ctl.GetDeviceManager(DeviceConnection.File);
            string dir = Path.GetDirectoryName(opt.SimFile)!;
            devices = manager.GetDevices(new object[] { dir })
                .Where(d => string.Equals(Path.GetFullPath(d.Serial), opt.SimFile, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var type = Enum.Parse<EegDeviceType>(opt.SimType, ignoreCase: true);
            foreach (var d in devices) d.AmplifierType = type;
            if (devices.Count == 0) throw new FileNotFoundException("Simulator file not found or not readable", opt.SimFile);
        }
        else
        {
            manager = ctl.GetDeviceManager(DeviceConnection.Usb);
            devices = manager.GetDevices(Array.Empty<object>()) ?? new List<DeviceInfo>();
        }

        if (opt.Serial != null)
            devices = devices.Where(d => string.Equals(d.Serial, opt.Serial, StringComparison.OrdinalIgnoreCase)).ToList();

        // An eego hub (ES-356/ES-357) appears as separate modules. Put the M04111 (hub port 1) first: it is
        // the module with the bipolar channels, while the M04112 modules (ports 2-4) have only referential ones.
        return devices.OrderBy(d => d.AmplifierType == EegDeviceType.M04111 ? 0 : 1).ToList();
    }

    /// <summary>Blocks until an amplifier is available (or cancellation) and returns it.</summary>
    private IAmplifier? WaitForAmplifier(EdiController ctl)
    {
        bool announced = false;
        while (!_ct.IsCancellationRequested)
        {
            IList<DeviceInfo> devices;
            IDeviceManager manager;
            try
            {
                devices = FindDevices(ctl, _opt, out manager);
            }
            catch (FileNotFoundException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Device discovery failed: {ex.Message}");
                devices = new List<DeviceInfo>();
                manager = null!;
            }

            if (devices.Count > 0)
            {
                if (devices.Count > 1)
                    Log.Warn($"{devices.Count} amplifiers found; using {devices[0].Serial}. Use --serial to choose.");
                var dev = devices[0];
                Log.Info($"Connecting to {dev.AmplifierType} (serial {dev.Serial})...");
                try
                {
                    ctl.CreateDevice(new List<DeviceInfo> { dev }, null);
                    var amp = ctl.Amplifier;
                    if (amp != null)
                    {
                        _detached = false;
                        if (_opt.SimFile == null)
                        {
                            manager.DeviceDetach -= OnDetach;
                            manager.DeviceDetach += OnDetach;
                        }
                        return amp;
                    }
                    Log.Warn("EDI2 returned no amplifier object.");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not open amplifier: {ex.GetType().Name}: {ex.Message}");
                }
            }
            else if (!announced)
            {
                Log.Info("Waiting for an eego amplifier to be connected (Ctrl+C to quit)...");
                PrintNoDeviceHint();
                announced = true;
            }
            _ct.WaitHandle.WaitOne(1000);
        }
        return null;
    }

    private void OnDetach(object? sender, DeviceAttachEventArgs e)
    {
        Log.Warn($"Amplifier detached: {e.DeviceInfo?.AmplifierType} {e.DeviceInfo?.Serial}");
        _detached = true;
    }

    private static void PrintNoDeviceHint()
    {
        Log.Info("  Hint: EDI2 accesses the amplifier through libusb, so Windows must have the WinUSB driver bound " +
                 "to it (Device Manager > 'eego amplifier' must not show a warning sign). See README.md.");
    }

    private static void TryIdle(IAmplifier amp)
    {
        try { amp.SetMode(AmplifierMode.Idle, null); } catch { /* amplifier may be gone */ }
    }

    // ===================================================================================================
    // Configuration
    // ===================================================================================================

    private (StreamParams Params, List<StreamChannel> Channels, DeviceDescription Device) Configure(IAmplifier amp)
    {
        var info = amp.GetDeviceInformation()[0];
        var available = amp.GetChannelsAvailable();

        // ---- channel selection ----
        var selected = available
            .Where(c => c.ChannelPolarity != ChannelPolarity.Disabled)
            .Where(c => _opt.Channels == null || _opt.Channels.Contains(c.ChannelIndex))
            .Where(c => !_opt.ReferentialOnly || c.ChannelPolarity == ChannelPolarity.Referential)
            .ToList();
        if (_opt.Channels != null)
        {
            var missing = _opt.Channels.Except(selected.Select(c => c.ChannelIndex)).ToList();
            if (missing.Count > 0) Log.Warn($"Requested channels not available and ignored: {FormatRanges(missing)}");
        }
        if (selected.Count == 0) throw new InvalidOperationException("No channels selected.");

        // ---- sampling rate ----
        var rates = amp.GetSamplingRatesAvailable();
        double rate = _opt.SamplingRate ?? (rates.Contains(500) ? 500 : rates.Min());
        if (!rates.Contains(rate))
            throw new ArgumentException($"Sampling rate {rate} Hz not supported. Available: {string.Join(", ", rates)}");

        // ---- input ranges ----
        var rangesAvailable = amp.GetRangesAvailable();
        var ranges = new Dictionary<ChannelPolarity, double>();
        foreach (var pol in selected.Select(c => c.ChannelPolarity).Distinct())
        {
            if (!rangesAvailable.TryGetValue(pol, out var opts) || opts == null || opts.Count == 0) continue;
            double? wanted = pol switch
            {
                ChannelPolarity.Referential => _opt.ReferentialRange,
                ChannelPolarity.Bipolar => _opt.BipolarRange,
                _ => null,
            };
            double r = wanted ?? opts.Max();
            if (!opts.Contains(r))
                throw new ArgumentException($"Range {r} V not available for {pol} channels. Available: {string.Join(", ", opts)}");
            ranges[pol] = r;
        }

        // ---- labels ----
        List<string>? labels = null;
        if (_opt.LabelsFile != null)
        {
            labels = File.ReadAllText(_opt.LabelsFile)
                .Split(new[] { '\r', '\n', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (labels.Count != selected.Count)
                Log.Warn($"Labels file has {labels.Count} labels but {selected.Count} channels are streamed; " +
                         "missing labels fall back to defaults.");
        }

        var referential = available.Where(c => c.ChannelPolarity == ChannelPolarity.Referential)
            .Select(c => c.ChannelIndex).ToList();
        if (_opt.Cap != null && referential.Count != Caps.Layouts[_opt.Cap].Labels.Length)
            Log.Warn($"Cap {_opt.Cap} has {Caps.Layouts[_opt.Cap].Labels.Length} electrodes but the amplifier has " +
                     $"{referential.Count} referential channels; unmatched channels keep default labels.");

        var counters = new Dictionary<ChannelPolarity, int>();
        var channels = new List<StreamChannel>();
        for (int i = 0; i < selected.Count; i++)
        {
            var c = selected[i];
            counters[c.ChannelPolarity] = counters.GetValueOrDefault(c.ChannelPolarity) + 1;
            int n = counters[c.ChannelPolarity];
            // Cap labels are looked up by the channel's position among the amplifier's referential
            // channels, so they stay correct when only a subset of channels is streamed.
            string? capLabel = _opt.Cap != null && c.ChannelPolarity == ChannelPolarity.Referential
                ? Caps.Label(_opt.Cap, referential.IndexOf(c.ChannelIndex))
                : null;
            string label = labels != null && i < labels.Count ? labels[i]
                : capLabel != null ? capLabel
                : !string.IsNullOrWhiteSpace(c.Name) ? c.Name
                : c.ChannelPolarity switch
                {
                    ChannelPolarity.Referential => $"EEG{n}",
                    ChannelPolarity.Bipolar => $"BIP{n}",
                    ChannelPolarity.Auxilary => $"AUX{n}",
                    _ => $"{c.ChannelPolarity}{n}",
                };
            string type = c.ChannelPolarity switch
            {
                ChannelPolarity.Referential when label.StartsWith("EOG", StringComparison.OrdinalIgnoreCase) => "EOG",
                ChannelPolarity.Referential => "EEG",
                ChannelPolarity.Bipolar => "BIP",
                ChannelPolarity.Auxilary => "AUX",
                _ => c.ChannelPolarity.ToString().ToUpperInvariant(),
            };
            (string unit, double scale) = c.UnitType switch
            {
                UnitType.Volt when !_opt.KeepVolts => ("microvolts", 1e6),
                UnitType.Volt => ("volts", 1.0),
                UnitType.Microvolt => ("microvolts", 1.0),
                _ => (c.UnitType.ToString().ToLowerInvariant(), 1.0),
            };
            channels.Add(new StreamChannel(label, type, unit, c.ChannelIndex, scale));
        }
        // Trigger codes as an extra last channel: the code at the trigger's sample, 0 elsewhere.
        if (_opt.Stim) channels.Add(StreamChannel.Stim());

        var sp = new StreamParams
        {
            SamplingRate = rate,
            ActiveChannels = selected.Select(c => c.ChannelIndex).ToList(),
            Ranges = ranges,
            // Amplifier-side buffer; data is handed over each time DataReadyPercentage of it is filled, so
            // the chunk duration (and minimum latency) is about BufferSeconds * ReadyPercent / 100.
            BufferSize = (int)Math.Round(rate * _opt.BufferSeconds),
            DataReadyPercentage = _opt.ReadyPercent,
        };
        Log.Info($"Amplifier buffer {_opt.BufferSeconds.ToString(CultureInfo.InvariantCulture)} s ({sp.BufferSize} samples), " +
                 $"data ready at {_opt.ReadyPercent}% -> ~{_opt.BufferSeconds * _opt.ReadyPercent * 10:F0} ms chunks.");

        var device = new DeviceDescription(info.AmplifierType.ToString(), info.Serial ?? "", rate,
            ranges.ToDictionary(k => k.Key.ToString(), k => k.Value), SdkVersion, _opt.Cap);
        return (sp, channels, device);
    }

    // ===================================================================================================
    // Acquisition loop
    // ===================================================================================================

    private void StreamFrom(IAmplifier amp)
    {
        var (sp, channels, device) = Configure(amp);
        string serial = device.Serial;
        if (_opt.SimFile != null) serial = "SIM-" + Path.GetFileNameWithoutExtension(_opt.SimFile);
        string name = _opt.StreamName ?? $"eego {device.Model} {serial}".Trim();
        string sourceId = _opt.SourceId ?? $"ANT-eego-{serial}";

        Log.Info($"Amplifier {device.Model} serial {device.Serial}: {channels.Count} channels @ {sp.SamplingRate} Hz, " +
                 $"ranges {string.Join(", ", sp.Ranges.Select(kv => $"{kv.Key}={kv.Value.ToString(CultureInfo.InvariantCulture)} V"))}");

        // Impedances go into the stream header, which cannot change later: measure only before the first
        // outlet exists (not after a reconnect that keeps the outlet), and not with the file simulator.
        if (_opt.ImpedanceSeconds > 0 && _outlets == null && _impedance == null && _opt.SimFile == null)
        {
            var referential = amp.GetChannelsAvailable().Where(c => c.ChannelPolarity == ChannelPolarity.Referential)
                .Select(c => c.ChannelIndex).ToList();
            (string, int?) Label(int i)
            {
                int? idx = i < referential.Count ? referential[i] : null;
                string? label = channels.FirstOrDefault(c => !c.IsStim && c.AmplifierIndex == idx)?.Label
                                ?? (_opt.Cap != null ? Caps.Label(_opt.Cap, i) : null);
                return (label ?? $"EEG{i + 1}", idx);
            }
            try
            {
                _impedance = Impedance.Measure(amp, _opt.ImpedanceSeconds, Label, _ct);
            }
            catch (Exception ex) when (!_ct.IsCancellationRequested)
            {
                Log.Warn($"Impedance measurement failed ({ex.GetType().Name}: {ex.Message}); streaming without impedances.");
                TryIdle(amp);
            }
            if (_ct.IsCancellationRequested) return;
        }

        amp.SetMode(AmplifierMode.Eeg, sp);
        Log.Info("EEG mode started.");

        float[] buffer = new float[channels.Count * (int)Math.Max(sp.SamplingRate, 1000)];
        double rate = sp.SamplingRate;
        var lastData = Stopwatch.StartNew();
        var statTimer = new Stopwatch(); // started at the first data, so the first rate report is not biased low
        long statSamples = 0;
        var clock = new SampleClock(rate);
        int consecutiveErrors = 0;
        bool warnedRowMismatch = false;

        while (!_ct.IsCancellationRequested)
        {
            if (_detached) throw new InvalidOperationException("amplifier was disconnected");

            ReadOnlyCollection<AmplifierFrame>? frames;
            try
            {
                frames = amp.GetFrame();
                consecutiveErrors = 0;
            }
            catch (SampleLossException ex)
            {
                _sampleLossEvents++;
                Log.Warn($"Sample loss reported by amplifier: {ex.Message}. Timestamps will be re-anchored.");
                clock.Reset(); // the sample count no longer matches elapsed time
                continue;
            }
            catch (Exception ex) when (ex is not WrongAmplifierStateException && ++consecutiveErrors < 5)
            {
                Log.Warn($"GetFrame failed ({consecutiveErrors}): {ex.Message}");
                _ct.WaitHandle.WaitOne(100);
                continue;
            }

            var eegFrames = frames?.Where(f => f.FrameType == AmplifierFrameType.EEG && f.Values != null &&
                                               f.Values.GetLength(1) > 0).ToList();
            if (eegFrames == null || eegFrames.Count == 0)
            {
                if (lastData.Elapsed.TotalSeconds > 5)
                    throw new TimeoutException("no data received from the amplifier for 5 s");
                _ct.WaitHandle.WaitOne(5);
                continue;
            }
            double now = Lsl.lsl_local_clock();
            lastData.Restart();
            bool firstChunk = !statTimer.IsRunning;
            if (firstChunk) statTimer.Start();

            int nch = channels.Count;
            int stimIdx = channels.FindIndex(c => c.IsStim);       // -1 if there is no STIM channel
            int dataCh = stimIdx >= 0 ? nch - 1 : nch;              // channels coming from the amplifier
            int total = eegFrames.Sum(f => f.Values.GetLength(1));

            // Create (or re-use after a reconnect) the outlet once we know the real frame layout.
            int rows = eegFrames[0].Values.GetLength(0);
            if (rows != dataCh && !warnedRowMismatch)
            {
                Log.Warn($"Amplifier delivers {rows} rows but {dataCh} channels were requested; streaming the first {Math.Min(rows, dataCh)}.");
                warnedRowMismatch = true;
            }
            EnsureOutlets(name, sourceId, channels, device);

            if (buffer.Length < total * nch) buffer = new float[total * nch * 2];

            // Multiplex all frames of this read into one chunk.
            int s0 = 0;
            foreach (var f in eegFrames)
            {
                var m = f.Values;
                int ns = m.GetLength(1), nr = Math.Min(m.GetLength(0), dataCh);
                for (int s = 0; s < ns; s++)
                {
                    int o = (s0 + s) * nch;
                    for (int r = 0; r < nr; r++) buffer[o + r] = (float)(m.At(r, s) * channels[r].Scale);
                    for (int r = nr; r < dataCh; r++) buffer[o + r] = float.NaN;
                    if (stimIdx >= 0) buffer[o + stimIdx] = 0f;
                }
                s0 += ns;
            }

            // Trigger markers: written into the STIM channel of this chunk (before it is pushed) and
            // sent on the marker stream. Sample times come from the sample counter (see SampleClock), not
            // from the arrival time, so they are evenly spaced and never step backwards between chunks.
            double firstSampleLsl = clock.Stamp(total, now);
            double lastSampleLsl = firstSampleLsl + (total - 1) / rate;
            s0 = 0;
            foreach (var f in eegFrames)
            {
                int ns = f.Values.GetLength(1);
                double frameStartLsl = firstSampleLsl + s0 / rate;
                double frameDuration = ns / rate;
                if (f.TimeMarkers != null)
                    foreach (var tm in f.TimeMarkers)
                    {
                        var marker = HandleMarker(tm, f, frameStartLsl, frameDuration, lastSampleLsl);
                        if (marker is not { } mk || stimIdx < 0) continue;
                        int idx = (int)Math.Round((mk.Timestamp - firstSampleLsl) * rate);
                        if (idx < 0)
                        {
                            Log.Warn($"Trigger {mk.Code} belongs to an earlier chunk ({-idx} samples before this one); " +
                                     "it is on the marker stream but not in the STIM channel.");
                            continue;
                        }
                        idx = Math.Min(idx, total - 1);
                        // Two triggers on the same sample: move the later one to the next free sample so
                        // that neither is lost (the marker stream keeps the exact time).
                        int free = idx;
                        while (free < total && buffer[free * nch + stimIdx] != 0) free++;
                        if (free == total)
                        {
                            free = idx; // at the end of the chunk: look earlier instead
                            while (free >= 0 && buffer[free * nch + stimIdx] != 0) free--;
                        }
                        if (free < 0)
                        {
                            Log.Warn($"Trigger {mk.Code}: no free STIM sample in this chunk; it is only on the marker stream.");
                            continue;
                        }
                        if (free != idx)
                            Log.Warn($"Trigger {mk.Code} shares a sample with another trigger; written {free - idx:+0;-0} sample(s) away in STIM.");
                        buffer[free * nch + stimIdx] = mk.Code;
                    }
                s0 += ns;
            }

            // LSL back-computes the earlier stamps of the chunk from the nominal rate.
            _outlets!.PushChunk(buffer, total, lastSampleLsl);

            _samples += total;
            if (!firstChunk) statSamples += total; // the first chunk is backlog from before the timer started
            if (statTimer.Elapsed.TotalSeconds >= 10)
            {
                double eff = statSamples / statTimer.Elapsed.TotalSeconds;
                Log.Info($"Streaming: {_samples} samples ({eff:F1} Hz effective), {_markers} markers, " +
                         $"consumers={(_outlets.HaveConsumers ? "yes" : "no")}, " +
                         $"max arrival jitter {clock.TakeMaxAbsError() * 1000:F1} ms" +
                         (clock.Resyncs > 0 ? $", clock re-anchors={clock.Resyncs}" : "") +
                         (_sampleLossEvents > 0 ? $", sample-loss events={_sampleLossEvents}" : ""));
                if (Math.Abs(eff - rate) > 0.05 * rate)
                    Log.Warn($"Effective rate {eff:F1} Hz differs from nominal {rate} Hz.");
                statSamples = 0;
                statTimer.Restart();
            }
        }
    }

    private void EnsureOutlets(string name, string sourceId, List<StreamChannel> channels, DeviceDescription device)
    {
        if (_outlets != null && _outlets.IsCompatible(channels, device.SamplingRate)) return;
        if (_outlets != null)
        {
            Log.Warn("Channel layout changed; re-creating the LSL outlet.");
            _outlets.Dispose();
        }
        _outlets = new LslOutlets(name, sourceId, channels, device, _opt.Markers, _impedance);
        Log.Info($"LSL outlet '{name}' (type EEG, {channels.Count} ch, {device.SamplingRate} Hz, float32, " +
                 $"{channels[0].Unit}, source_id '{sourceId}') is live." +
                 (_outlets.HasMarkers ? $" Triggers on '{name} Markers' (type Markers)." : ""));
        Log.Info("Channels: " + string.Join(", ", channels.Select(c => c.Label)));
    }

    /// <summary>Publishes a new trigger on the marker stream. Returns its code and LSL timestamp, or null for a repeat.</summary>
    private (int Code, double Timestamp)? HandleMarker(TimeMarker tm, AmplifierFrame frame, double frameStartLsl,
        double frameDuration, double now)
    {
        var key = (tm.Start.UtcTicks + tm.Offset.Ticks, tm.TimeMarkerCode);
        long nowMs = _clock.ElapsedMilliseconds;
        if (_seenMarkers.TryGetValue(key, out long seenAt) && nowMs - seenAt < MarkerDedupWindowMs)
        {
            _seenMarkers[key] = nowMs; // still being repeated; keep the entry alive
            return null;
        }
        _seenMarkers[key] = nowMs;
        if (_seenMarkers.Count > 1024)
            foreach (var old in _seenMarkers.Where(kv => nowMs - kv.Value >= MarkerDedupWindowMs).Select(kv => kv.Key).ToList())
                _seenMarkers.Remove(old);

        // Marker time relative to the start of the frame (both in amplifier time). If the two clocks are
        // obviously unrelated (e.g. file simulator), fall back to the receive time.
        double rel = (tm.Start + tm.Offset - frame.Start).TotalSeconds;
        double ts = rel > -10 && rel < frameDuration + 10 ? frameStartLsl + rel : now;

        string text = tm.TimeMarkerCode.ToString(CultureInfo.InvariantCulture);
        var label = tm.Information?.FirstOrDefault(i => i.Type == TimeMarkerEnum.Text || i.Type == TimeMarkerEnum.ConditionLabel);
        if (label?.Value is string s && !string.IsNullOrWhiteSpace(s) && s != text) text += " " + s;

        _outlets!.PushMarker(text, ts);
        _markers++;
        Log.Debug($"Trigger {text} at {ts:F4} (marker start {tm.Start:HH:mm:ss.ffffff}, offset {tm.Offset.TotalMilliseconds} ms, " +
                  $"frame start {frame.Start:HH:mm:ss.ffffff}, rel {rel * 1000:F1} ms, frame {frameDuration * 1000:F0} ms, " +
                  $"{(ts == now ? "FALLBACK arrival time" : "amplifier time")})");
        return (tm.TimeMarkerCode, ts);
    }

    private static string FormatRanges(IEnumerable<int> values)
    {
        var v = values.OrderBy(x => x).ToList();
        var parts = new List<string>();
        for (int i = 0; i < v.Count;)
        {
            int j = i;
            while (j + 1 < v.Count && v[j + 1] == v[j] + 1) j++;
            parts.Add(i == j ? $"{v[i]}" : $"{v[i]}-{v[j]}");
            i = j + 1;
        }
        return string.Join(",", parts);
    }
}
