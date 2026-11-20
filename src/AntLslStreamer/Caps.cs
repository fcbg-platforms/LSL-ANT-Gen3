namespace AntLslStreamer;

/// <summary>A waveguard cap: electrode label for each referential amplifier channel index (0-based).</summary>
/// <param name="Labels">Label of amplifier channel 0, 1, 2, ... (datasheet channel number 1, 2, 3, ...).</param>
/// <param name="Reference">Reference electrode position(s) if documented, else null.</param>
/// <param name="Ground">Ground electrode position(s) if documented, else null.</param>
/// <param name="Source">Where the layout comes from.</param>
internal sealed record CapLayout(string[] Labels, string? Reference, string? Ground, string Source);

internal static class Caps
{
    /// <summary>
    /// waveguard CA-208 (64 ch). Source: CBG lsl-eego plugin (mainwindow.cpp, electrodeMap_208);
    /// confirmed correct by the lab.
    /// </summary>
    private static readonly CapLayout CA208 = new(new[]
    {
        "Fp1", "Fpz", "Fp2", "F7",  "F3",  "Fz",  "F4",
        "F8",  "FC5", "FC1", "FC2", "FC6", "M1",  "T7",
        "C3",  "Cz",  "C4",  "T8",  "M2",  "CP5", "CP1",
        "CP2", "CP6", "P7",  "P3",  "Pz",  "P4",  "P8",
        "POz", "O1",  "O2",  "EOG", "AF7", "AF3", "AF4",
        "AF8", "F5",  "F1",  "F2",  "F6",  "FC3", "FCz",
        "FC4", "C5",  "C1",  "C2",  "C6",  "CP3", "CP4",
        "P5",  "P1",  "P2",  "P6",  "PO5", "PO3", "PO4",
        "PO6", "FT7", "FT8", "TP7", "TP8", "PO7", "PO8",
        "Oz",
    }, Reference: null, Ground: null, Source: "lsl-eego electrodeMap_208 (lab-confirmed)");

    /// <summary>
    /// waveguard NA-265 (64 ch, 10-10, for EE-2xx amplifiers).
    /// Source: ANT/eemagine datasheet UDO-SM-0987, DRN-15891 Rev. 5 (2026-01-16), "Pinning Specifications"
    /// (channel numbers 1-32 on connector 1, 33-64 on connector 2). REF at Cz and CPz, GND at AFz and Fpz.
    /// </summary>
    private static readonly CapLayout NA265 = new(new[]
    {
        // connector 1: channels 1-32
        "Fp1", "Fp2", "F9",  "F7",  "F3",  "Fz",  "F4",  "F8",
        "F10", "FC5", "FC1", "FC2", "FC6", "T9",  "T7",  "C3",
        "C4",  "T8",  "T10", "CP5", "CP1", "CP2", "CP6", "P9",
        "P7",  "P3",  "Pz",  "P4",  "P8",  "P10", "O1",  "O2",
        // connector 2: channels 33-64
        "AF7", "AF3", "AF4", "AF8", "F5",  "F1",  "F2",  "F6",
        "FC3", "FCz", "FC4", "C5",  "C1",  "C2",  "C6",  "CP3",
        "CP4", "P5",  "P1",  "P2",  "P6",  "PO5", "PO3", "PO4",
        "PO6", "FT7", "FT8", "TP7", "TP8", "PO7", "PO8", "POz",
    }, Reference: "Cz, CPz", Ground: "AFz, Fpz", Source: "ANT datasheet UDO-SM-0987 / DRN-15891 Rev. 5");

    public static readonly IReadOnlyDictionary<string, CapLayout> Layouts = new Dictionary<string, CapLayout>
    {
        ["CA-208"] = CA208,
        ["NA-265"] = NA265,
    };

    /// <summary>Accepts "CA-208", "ca208", "208", "NA_265", ... and returns the canonical name.</summary>
    public static string Normalize(string cap)
    {
        string key = new string(cap.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        foreach (var name in Layouts.Keys)
        {
            string canon = name.Replace("-", "");
            if (key == canon || key == canon[2..]) return name;
        }
        throw new ArgumentException($"Unknown cap '{cap}'. Available: {string.Join(", ", Layouts.Keys)}");
    }

    /// <summary>Electrode label for a referential channel, or null if the cap has no electrode there.</summary>
    public static string? Label(string cap, int referentialIndex)
    {
        var map = Layouts[cap].Labels;
        return referentialIndex >= 0 && referentialIndex < map.Length ? map[referentialIndex] : null;
    }
}
