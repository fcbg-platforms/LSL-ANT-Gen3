using System.Globalization;
using System.Runtime.InteropServices;

namespace AntLslStreamer;

/// <summary>Description of one channel in the outgoing LSL stream.</summary>
internal sealed record StreamChannel(string Label, string Type, string Unit, int AmplifierIndex, double Scale);

/// <summary>Device / acquisition information written into the LSL stream metadata.</summary>
internal sealed record DeviceDescription(string Model, string Serial, double SamplingRate,
    IReadOnlyDictionary<string, double> Ranges, string SdkVersion, string? Cap);

/// <summary>
/// Owns the EEG outlet (float32, multiplexed chunks) and the optional string marker outlet.
/// </summary>
internal sealed class LslOutlets : IDisposable
{
    private IntPtr _eegInfo, _eegOutlet, _markerInfo, _markerOutlet;
    private readonly IntPtr[] _markerSample = new IntPtr[1];

    public IReadOnlyList<StreamChannel> Channels { get; }
    public double SamplingRate { get; }
    public string Name { get; }
    public string SourceId { get; }
    public bool HasMarkers => _markerOutlet != IntPtr.Zero;

    public LslOutlets(string name, string sourceId, IReadOnlyList<StreamChannel> channels,
        DeviceDescription device, bool withMarkers)
    {
        Name = name;
        SourceId = sourceId;
        Channels = channels;
        SamplingRate = device.SamplingRate;

        _eegInfo = Lsl.lsl_create_streaminfo(name, "EEG", channels.Count, device.SamplingRate, Lsl.ChannelFormat.Float32, sourceId);
        if (_eegInfo == IntPtr.Zero) throw new InvalidOperationException("lsl_create_streaminfo failed");

        IntPtr desc = Lsl.lsl_get_desc(_eegInfo);
        IntPtr chns = Lsl.lsl_append_child(desc, "channels");
        foreach (var c in channels)
        {
            IntPtr ch = Lsl.lsl_append_child(chns, "channel");
            Lsl.lsl_append_child_value(ch, "label", c.Label);
            Lsl.lsl_append_child_value(ch, "unit", c.Unit);
            Lsl.lsl_append_child_value(ch, "type", c.Type);
            Lsl.lsl_append_child_value(ch, "amplifier_index", c.AmplifierIndex.ToString(CultureInfo.InvariantCulture));
        }

        if (device.Cap != null)
        {
            IntPtr cap = Lsl.lsl_append_child(desc, "cap");
            Lsl.lsl_append_child_value(cap, "manufacturer", "ANT Neuro");
            Lsl.lsl_append_child_value(cap, "name", "waveguard " + device.Cap);
            Lsl.lsl_append_child_value(cap, "labelscheme", "10-10");
            var layout = Caps.Layouts[device.Cap];
            Lsl.lsl_append_child_value(cap, "layout_source", layout.Source);
            if (layout.Reference != null) Lsl.lsl_append_child_value(cap, "reference", layout.Reference);
            if (layout.Ground != null) Lsl.lsl_append_child_value(cap, "ground", layout.Ground);
        }

        IntPtr acq = Lsl.lsl_append_child(desc, "acquisition");
        Lsl.lsl_append_child_value(acq, "manufacturer", "ANT Neuro");
        Lsl.lsl_append_child_value(acq, "model", device.Model);
        Lsl.lsl_append_child_value(acq, "serial_number", device.Serial);
        Lsl.lsl_append_child_value(acq, "sdk", "EDI2 " + device.SdkVersion);
        Lsl.lsl_append_child_value(acq, "application", "AntLslStreamer " + typeof(LslOutlets).Assembly.GetName().Version);
        IntPtr ranges = Lsl.lsl_append_child(acq, "input_ranges_volts");
        foreach (var kv in device.Ranges)
            Lsl.lsl_append_child_value(ranges, kv.Key.ToLowerInvariant(), kv.Value.ToString(CultureInfo.InvariantCulture));

        // chunk_size 0 = sender decides; buffer up to 360 s for slow consumers
        _eegOutlet = Lsl.lsl_create_outlet(_eegInfo, 0, 360);
        if (_eegOutlet == IntPtr.Zero) throw new InvalidOperationException("lsl_create_outlet failed");

        if (withMarkers)
        {
            _markerInfo = Lsl.lsl_create_streaminfo(name + " Markers", "Markers", 1, Lsl.IrregularRate,
                Lsl.ChannelFormat.String, sourceId + "-markers");
            IntPtr mdesc = Lsl.lsl_get_desc(_markerInfo);
            IntPtr mch = Lsl.lsl_append_child(Lsl.lsl_append_child(mdesc, "channels"), "channel");
            Lsl.lsl_append_child_value(mch, "label", "TriggerCode");
            Lsl.lsl_append_child_value(mch, "type", "Marker");
            Lsl.lsl_append_child_value(Lsl.lsl_append_child(mdesc, "acquisition"), "eeg_source_id", sourceId);
            _markerOutlet = Lsl.lsl_create_outlet(_markerInfo, 0, 360);
        }
    }

    /// <summary>Push a multiplexed chunk; <paramref name="lastSampleTime"/> is the LSL time of the last sample.</summary>
    public void PushChunk(float[] data, int sampleCount, double lastSampleTime)
    {
        int ec = Lsl.lsl_push_chunk_ftp(_eegOutlet, data, (uint)(sampleCount * Channels.Count), lastSampleTime, 1);
        if (ec < 0) throw new InvalidOperationException($"lsl_push_chunk failed (error {ec})");
    }

    public void PushMarker(string marker, double timestamp)
    {
        if (_markerOutlet == IntPtr.Zero) return;
        IntPtr p = Lsl.StringToUtf8(marker);
        try
        {
            _markerSample[0] = p;
            Lsl.lsl_push_sample_strtp(_markerOutlet, _markerSample, timestamp, 1);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    public bool HaveConsumers => _eegOutlet != IntPtr.Zero && Lsl.lsl_have_consumers(_eegOutlet) != 0;

    /// <summary>True if this outlet can keep serving data with the given layout (so inlets survive a reconnect).</summary>
    public bool IsCompatible(IReadOnlyList<StreamChannel> channels, double rate) =>
        rate == SamplingRate && channels.Count == Channels.Count &&
        channels.Zip(Channels).All(p => p.First.Label == p.Second.Label && p.First.Unit == p.Second.Unit);

    public void Dispose()
    {
        if (_eegOutlet != IntPtr.Zero) { Lsl.lsl_destroy_outlet(_eegOutlet); _eegOutlet = IntPtr.Zero; }
        if (_eegInfo != IntPtr.Zero) { Lsl.lsl_destroy_streaminfo(_eegInfo); _eegInfo = IntPtr.Zero; }
        if (_markerOutlet != IntPtr.Zero) { Lsl.lsl_destroy_outlet(_markerOutlet); _markerOutlet = IntPtr.Zero; }
        if (_markerInfo != IntPtr.Zero) { Lsl.lsl_destroy_streaminfo(_markerInfo); _markerInfo = IntPtr.Zero; }
    }
}
