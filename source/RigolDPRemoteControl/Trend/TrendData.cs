using System.Collections.ObjectModel;
using RigolDPRemoteControl.Infrastructure;

namespace RigolDPRemoteControl.Trend;

public enum Quantity { Voltage = 0, Current = 1, Power = 2 }

/// <summary>
/// Samples of one power-supply channel. Appended from sampling threads, read from the UI thread under <see cref="Sync"/>.
/// Time stamps are stored as OLE automation dates (ScottPlot DateTime axis units).
/// </summary>
public sealed class TrendSeries : ObservableObject
{
    public const int MaxPointsInMemory = 3_000_000;

    public TrendSeries(string key, string label, string colorHex)
    {
        Key = key;
        _label = label;
        _colorHex = colorHex;
    }

    public object Sync { get; } = new();
    public string Key { get; }

    public readonly List<double> T = new();
    public readonly List<double> V = new();
    public readonly List<double> I = new();
    public readonly List<double> P = new();
    public readonly List<byte> M = new(); // 0 = unknown, 1 = CV, 2 = CC, 3 = UR

    private string _label;
    public string Label { get => _label; set => SetField(ref _label, value); }

    private string _colorHex;
    public string ColorHex { get => _colorHex; set => SetField(ref _colorHex, value); }

    private bool _showV = true, _showI = true, _showP = true;
    public bool ShowVoltage { get => _showV; set { if (SetField(ref _showV, value)) VisibilityChanged?.Invoke(); } }
    public bool ShowCurrent { get => _showI; set { if (SetField(ref _showI, value)) VisibilityChanged?.Invoke(); } }
    public bool ShowPower { get => _showP; set { if (SetField(ref _showP, value)) VisibilityChanged?.Invoke(); } }
    public event Action? VisibilityChanged;

    private string _rulerText = "";
    /// <summary>Values at the trend ruler position (display only, empty when the ruler is off).</summary>
    public string RulerText
    {
        get => _rulerText;
        set { if (SetField(ref _rulerText, value)) OnPropertyChanged(nameof(HasRulerText)); }
    }
    public bool HasRulerText => _rulerText.Length > 0;

    /// <summary>Incremented on every change, lets the view detect new data cheaply.</summary>
    public long Version;

    public int Count { get { lock (Sync) return T.Count; } }

    public void Add(double t, double v, double i, double p, byte mode)
    {
        lock (Sync)
        {
            // keep time strictly ordered (required by the binary search in the decimator)
            if (T.Count > 0 && t < T[^1]) t = T[^1];
            T.Add(t); V.Add(v); I.Add(i); P.Add(p); M.Add(mode);
            if (T.Count > MaxPointsInMemory)
            {
                int drop = MaxPointsInMemory / 3;
                T.RemoveRange(0, drop); V.RemoveRange(0, drop); I.RemoveRange(0, drop);
                P.RemoveRange(0, drop); M.RemoveRange(0, drop);
            }
            Version++;
        }
    }

    public void ClearData()
    {
        lock (Sync)
        {
            T.Clear(); V.Clear(); I.Clear(); P.Clear(); M.Clear();
            Version++;
        }
    }

    public List<double> Values(Quantity q) => q switch { Quantity.Voltage => V, Quantity.Current => I, _ => P };

    public bool IsShown(Quantity q) => q switch { Quantity.Voltage => ShowVoltage, Quantity.Current => ShowCurrent, _ => ShowPower };

    public static byte ModeCode(string? mode) => mode switch { "CV" => 1, "CC" => 2, "UR" => 3, _ => 0 };
    public static string ModeText(byte code) => code switch { 1 => "CV", 2 => "CC", 3 => "UR", _ => "" };
}

/// <summary>A set of trend series (the live trend, or a trend loaded from file).</summary>
public sealed class TrendDataSet
{
    public TrendDataSet(bool isLive) { IsLive = isLive; }

    public bool IsLive { get; }
    public string? SourceFile { get; set; }

    /// <summary>Modified on the UI thread only.</summary>
    public ObservableCollection<TrendSeries> Series { get; } = new();

    public TrendSeries? Find(string key) => Series.FirstOrDefault(s => s.Key == key);

    public TrendSeries GetOrAdd(string key, string label, string color)
    {
        var s = Find(key);
        if (s != null) return s;
        s = new TrendSeries(key, label, color);
        Series.Add(s);
        return s;
    }

    public void ClearData()
    {
        foreach (var s in Series) s.ClearData();
    }

    /// <summary>Removes a series (e.g. its power supply was removed). UI thread only.</summary>
    public bool Remove(string key)
    {
        var s = Find(key);
        return s != null && Series.Remove(s);
    }

    public bool HasData => Series.Any(s => s.Count > 0);
}

public static class TrendColors
{
    // Distinct, colour-blind friendly-ish palette that works on dark and light backgrounds
    public static readonly string[] Palette =
    {
        "#1F77B4", "#FF7F0E", "#2CA02C", "#D62728", "#9467BD", "#8C564B",
        "#E377C2", "#17BECF", "#BCBD22", "#7F7F7F", "#3366FF", "#FF3399",
        "#00A88F", "#C49A00", "#6A5ACD", "#FF6347",
    };

    public static string Next(IEnumerable<string> used)
    {
        var u = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
        foreach (var c in Palette) if (!u.Contains(c)) return c;
        return Palette[u.Count % Palette.Length];
    }
}
