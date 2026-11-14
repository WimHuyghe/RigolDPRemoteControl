namespace RigolDPRemoteControl.Devices;

public sealed record ChannelSpec(string Name, double RatedVoltage, double RatedCurrent, double OvpMax, double OcpMax);

public sealed record ModelSpec(
    string Model,
    string Series,
    string Description,
    double TotalPowerW,
    bool SingleChannelWithRanges,
    IReadOnlyList<ChannelSpec> Channels,
    string? RangeInfo = null,
    bool HasLan = true,
    string Interfaces = "LAN, USB, RS232");

/// <summary>
/// Static knowledge about the supported Rigol DP families (DP700, DP800, DP900, DP2000), taken from the datasheets in /info.
/// All families use the same SCPI command set (DP700 = subset, single channel, RS232 only).
/// </summary>
public static class ModelCatalog
{
    private static readonly Dictionary<string, ModelSpec> Models = Build();

    public static IReadOnlyCollection<ModelSpec> All => Models.Values;

    /// <summary>Model numbers that the application accepts ("DP7xx", "DP8xx", "DP9xx", "DP2xxx").</summary>
    public static bool IsSupportedModel(string model)
    {
        model = (model ?? "").Trim().ToUpperInvariant();
        return model.Length >= 5 && model.StartsWith("DP") && char.IsDigit(model[2]) && "7892".Contains(model[2]);
    }

    public static string SeriesOf(string model)
    {
        model = (model ?? "").Trim().ToUpperInvariant();
        if (model.Length < 3 || !model.StartsWith("DP")) return "DP";
        return model[2] switch { '7' => "DP700", '8' => "DP800", '9' => "DP900", '2' => "DP2000", _ => "DP" };
    }

    private static Dictionary<string, ModelSpec> Build()
    {
        var list = new List<ModelSpec>();
        void Add(string model, string series, string desc, double power, bool ranges, string? rangeInfo, bool lan, string itf, params ChannelSpec[] ch)
            => list.Add(new ModelSpec(model, series, desc, power, ranges, ch, rangeInfo, lan, itf));

        // ---- DP700: single output, RS232 only ----
        Add("DP711", "DP700", "Programmable Linear DC Power Supply, 1 output 30 V / 5 A, 150 W", 150, false, null, false, "RS232",
            new ChannelSpec("CH1", 30, 5, 33, 5.5));
        Add("DP712", "DP700", "Programmable Linear DC Power Supply, 1 output 50 V / 3 A, 150 W", 150, false, null, false, "RS232",
            new ChannelSpec("CH1", 50, 3, 55, 3.3));

        // ---- DP800 ----
        foreach (var suffix in new[] { "A", "" })
        {
            Add("DP832" + suffix, "DP800", "Programmable Linear DC Power Supply, 3 outputs, 195 W", 195, false, null, true, "LAN, USB, RS232, Digital I/O",
                new("CH1", 30, 3, 33, 3.3), new("CH2", 30, 3, 33, 3.3), new("CH3", 5, 3, 5.5, 3.3));
            Add("DP831" + suffix, "DP800", "Programmable Linear DC Power Supply, 3 outputs (CH3 is a negative output)", 160, false, null, true, "LAN, USB, RS232, Digital I/O",
                new("CH1", 8, 5, 8.8, 5.5), new("CH2", 30, 2, 33, 2.2), new("CH3", -30, 2, -33, 2.2));
            Add("DP822" + suffix, "DP800", "Programmable Linear DC Power Supply, 2 outputs, remote sense on CH2", 180, false, null, true, "LAN, USB, RS232, Digital I/O",
                new("CH1", 20, 5, 22, 5.5), new("CH2", 5, 16, 5.5, 16.8));
            Add("DP821" + suffix, "DP800", "Programmable Linear DC Power Supply, 2 outputs, remote sense on CH2", 140, false, null, true, "LAN, USB, RS232, Digital I/O",
                new("CH1", 60, 1, 66, 1.1), new("CH2", 8, 10, 8.8, 11));
            Add("DP813" + suffix, "DP800", "Programmable Linear DC Power Supply, 1 output with 2 ranges, remote sense", 200, true,
                "Range 1: 8V/20A, Range 2: 20V/10A", true, "LAN, USB, RS232, Digital I/O",
                new ChannelSpec("CH1", 20, 20, 22, 22));
            Add("DP811" + suffix, "DP800", "Programmable Linear DC Power Supply, 1 output with 2 ranges, remote sense", 200, true,
                "Range 1: 20V/10A, Range 2: 40V/5A", true, "LAN, USB, RS232, Digital I/O",
                new ChannelSpec("CH1", 40, 10, 44, 11));
        }

        // ---- DP900: 3 isolated outputs, internal series/parallel of CH1+CH2 ----
        foreach (var (suffix, what) in new[] { ("A", "standard"), ("U", "university, safety sockets"), ("", "standard") })
            Add("DP932" + suffix, "DP900", $"Programmable Linear DC Power Supply, 3 isolated outputs, 210 W ({what})", 210, false,
                "CH1+CH2 internal series 64 V / parallel 6 A", true, "LAN, USB, Digital I/O",
                new("CH1", 32, 3, 35.2, 3.3), new("CH2", 32, 3, 35.2, 3.3), new("CH3", 6, 3, 6.6, 3.3));
        Add("DP932E", "DP900", "Programmable Linear DC Power Supply, 3 isolated outputs, 210 W (e-commerce)", 210, false,
            "CH1+CH2 internal series 64 V / parallel 6 A", true, "LAN, USB",
            new("CH1", 30, 3, 33, 3.3), new("CH2", 30, 3, 33, 3.3), new("CH3", 6, 3, 6.6, 3.3));

        // ---- DP2000: 3 isolated outputs, low current range on CH1/CH2 ----
        Add("DP2031", "DP2000", "Programmable Linear DC Power Supply, 3 isolated outputs, 222 W, front + rear terminals, remote sense", 222, false,
            "Range 1: 32V/3A, 32V/3A, 6V/5A – Range 2 (option): 32V/2A, 32V/2A, 6V/10A", true, "LAN, USB, RS232, Digital I/O",
            new("CH1", 32, 3, 35.2, 3.3), new("CH2", 32, 3, 35.2, 3.3), new("CH3", 6, 5, 6.6, 5.5));

        return list.ToDictionary(m => m.Model, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Find a model; unknown family members get a best guess (ratings are read from the instrument anyway).</summary>
    public static ModelSpec Resolve(string model)
    {
        model = (model ?? "").Trim();
        if (Models.TryGetValue(model, out var spec)) return spec;
        // e.g. "DP832B": try without the last letter
        if (model.Length > 5 && char.IsLetter(model[^1]) && Models.TryGetValue(model[..^1], out spec))
            return spec with { Model = model };

        var series = SeriesOf(model);
        int count = series switch
        {
            "DP700" => 1,
            "DP900" or "DP2000" => 3,
            // DP83x = 3ch, DP82x = 2ch, DP81x = 1ch
            _ => model.Length >= 4 && char.IsDigit(model[3]) ? model[3] switch { '1' => 1, '2' => 2, _ => 3 } : 3,
        };
        var ch = Enumerable.Range(1, count).Select(i => new ChannelSpec($"CH{i}", 30, 3, 33, 3.3)).ToArray();
        return new ModelSpec(model, series, $"{series} series power supply (model not in catalog, ratings read from instrument)", 0,
            series == "DP800" && count == 1, ch, null, series != "DP700", series == "DP700" ? "RS232" : "LAN");
    }
}
