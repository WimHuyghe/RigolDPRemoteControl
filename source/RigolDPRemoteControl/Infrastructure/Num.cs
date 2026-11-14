using System.Globalization;

namespace RigolDPRemoteControl.Infrastructure;

/// <summary>
/// Culture-safe number helpers. SCPI always uses '.' as decimal separator, the UI accepts both '.' and ','.
/// </summary>
public static class Num
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Scpi(double v) => v.ToString("0.0####", Inv);

    public static bool TryParseUser(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace(',', '.');
        // strip trailing unit letters (e.g. "5V", "1.2 A", "500mA")
        double mult = 1;
        var lower = t.ToLowerInvariant();
        if (lower.EndsWith("ma") || lower.EndsWith("mv") || lower.EndsWith("mw") || lower.EndsWith("ms")) { mult = 0.001; t = t[..^2]; }
        else if (lower.EndsWith("v") || lower.EndsWith("a") || lower.EndsWith("w") || lower.EndsWith("s")) t = t[..^1];
        if (!double.TryParse(t.Trim(), NumberStyles.Float, Inv, out value)) return false;
        value *= mult;
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public static bool TryParseScpi(string? text, out double value)
    {
        value = double.NaN;
        if (text == null) return false;
        var t = text.Trim().Trim('"').Trim();
        return double.TryParse(t, NumberStyles.Float, Inv, out value);
    }

    public static string F(double v, int decimals) => double.IsNaN(v) ? "---" : v.ToString("F" + decimals, Inv);
}
