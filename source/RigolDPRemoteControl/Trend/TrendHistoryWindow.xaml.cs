using System.IO;
using System.Windows;
using RigolDPRemoteControl.Settings;

namespace RigolDPRemoteControl.Trend;

/// <summary>Reviews a saved trend file with the same plot features as the live trend (runs next to live trending).</summary>
public partial class TrendHistoryWindow : Window
{
    public TrendHistoryWindow(TrendDataSet data, TrendSettings settings)
    {
        InitializeComponent();
        var name = data.SourceFile != null ? Path.GetFileName(data.SourceFile) : "Trend";
        int samples = data.Series.Sum(s => s.Count);
        Title = $"Trend history – {name}  ({data.Series.Count} channel(s), {samples:N0} samples)";
        View.Attach(data, settings, null);
    }
}
