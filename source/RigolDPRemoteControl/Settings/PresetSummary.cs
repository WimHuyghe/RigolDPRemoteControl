using System.Text;
using RigolDPRemoteControl.Infrastructure;

namespace RigolDPRemoteControl.Settings;

/// <summary>Readable text summary of a configuration (shown on the preset info page).</summary>
public static class PresetSummary
{
    public static string Build(PresetFile p, string presetName)
    {
        var sb = new StringBuilder();
        sb.AppendLine(presetName.Length > 0 ? $"PRESET  {presetName}" : "CURRENT CONFIGURATION (not saved as preset yet)");
        sb.AppendLine($"Summary created {DateTime.Now:dd-MM-yyyy HH:mm}");
        sb.AppendLine();

        // channel key -> readable name, for the group members
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        sb.AppendLine($"POWER SUPPLIES ({p.Devices.Count})");
        int n = 1;
        foreach (var pd in p.Devices)
        {
            var d = pd.Device;
            bool serial = d.Host.StartsWith("COM", StringComparison.OrdinalIgnoreCase);
            string addr = serial ? $"{d.Host.ToUpperInvariant()} @ {d.Port} baud" : $"{d.Host}:{d.Port}";
            sb.AppendLine($"{n++}. {(d.Model.Length > 0 ? d.Model : "DP")}  {addr}");
            var info = new List<string>();
            if (d.Serial.Length > 0) info.Add($"S/N {d.Serial}");
            if (!d.AutoConnect) info.Add("not connected");
            if (d.Compact) info.Add("minimized view");
            if (info.Count > 0) sb.AppendLine("   " + string.Join(", ", info));
            if (pd.Instrument.Count == 0) sb.AppendLine("   (setpoints / OVP / OCP unknown – supply was not connected)");

            foreach (var cs in d.Channels.OrderBy(c => c.Index))
            {
                var inst = pd.Instrument.FirstOrDefault(i => i.Index == cs.Index);
                string label = $"CH{cs.Index}" + (cs.Name.Length > 0 ? $" \"{cs.Name}\"" : "");
                names[$"{d.Host}:{d.Port}/CH{cs.Index}"] = $"{label} @{d.Host}";

                sb.AppendLine($"   {label}");
                if (inst != null)
                {
                    bool ovpOn = cs.ProtectionDelayEnabled ? cs.DelayedOvpOn : inst.OvpEnabled == true;
                    bool ocpOn = cs.ProtectionDelayEnabled ? cs.DelayedOcpOn : inst.OcpEnabled == true;
                    sb.AppendLine($"      set {V(inst.Voltage, 3)} V / {V(inst.Current, 3)} A");
                    sb.AppendLine($"      OVP {V(inst.OvpValue, 3)} V {(ovpOn ? "on" : "off")}, OCP {V(inst.OcpValue, 3)} A {(ocpOn ? "on" : "off")}");
                }
                if (cs.OnDelayMs > 0 || cs.OffDelayMs > 0) sb.AppendLine($"      on-delay {S(cs.OnDelayMs)}, off-delay {S(cs.OffDelayMs)}");
                if (cs.ProtectionDelayEnabled) sb.AppendLine($"      OVP/OCP enabled {S(cs.ProtectionDelayMs)} after switch-on");
                var trend = string.Join("/", new[] { cs.TrendVoltage ? "V" : null, cs.TrendCurrent ? "A" : null, cs.TrendPower ? "W" : null }.Where(x => x != null));
                if (trend != "V/A/W") sb.AppendLine(trend.Length > 0 ? $"      trend {trend}" : "      not trended");
            }
        }
        sb.AppendLine();

        sb.AppendLine($"GROUPS ({p.Groups.Count})");
        if (p.Groups.Count == 0) sb.AppendLine("   none");
        foreach (var g in p.Groups)
        {
            var members = g.Members.Select(k => names.TryGetValue(k, out var nm) ? nm : k);
            sb.AppendLine($"   \"{g.Name}\": {string.Join(", ", members)}");
        }
        sb.AppendLine();

        var t = p.Trend;
        sb.AppendLine("TREND");
        sb.AppendLine($"   Window {(t.WindowSeconds <= 0 ? "all" : TimeText(t.WindowSeconds))}, {(t.SeparatePlots ? "separate V/A/W plots" : "combined plot")}");
        sb.AppendLine($"   Smoothing {(t.Smoothing ? $"on ({t.SmoothingSamples} samples)" : "off")}, legend {(t.ShowLegend ? "on" : "off")}");
        sb.AppendLine($"   Axes: V {Axis(t.Voltage)}, A {Axis(t.Current)}, W {Axis(t.Power)}");
        return sb.ToString();
    }

    private static string V(double? v, int decimals) => v is { } x ? Num.F(x, decimals) : "?";
    private static string S(int ms) => (ms / 1000.0).ToString("0.###", Num.Inv) + " s";
    private static string Axis(AxisSettings a) => a.Auto ? "auto" : $"{a.Min.ToString("0.####", Num.Inv)} … {a.Max.ToString("0.####", Num.Inv)}";
    private static string TimeText(double s) => s >= 3600 ? $"{s / 3600:0.#} h" : s >= 60 ? $"{s / 60:0.#} min" : $"{s:0} s";
}
