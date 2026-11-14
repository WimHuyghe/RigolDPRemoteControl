using System.Windows;
using System.Windows.Controls;

namespace RigolDPRemoteControl.Views;

/// <summary>Quick reference of the mouse / keyboard controls of the trend (opened from the trend's right-click menu).</summary>
public partial class TrendNavigationWindow : Window
{
    private static readonly (string keys, string action)[] Rows =
    {
        ("Left-click-drag", "Pan (scroll time and values)"),
        ("Right-click-drag", "Zoom (drag right/up = zoom in, left/down = zoom out)"),
        ("Middle-click-drag", "Zoom to the selected region"),
        ("ALT + left-click-drag", "Zoom to the selected region"),
        ("Scroll wheel", "Zoom at the mouse cursor"),
        ("Right-click", "Show the menu (export, layout, ruler, legend, …)"),
        ("Middle-click", "Auto-axis: show the complete trend with autoscaled axes (same as Fit all)"),
        ("Double-click", "Back to the live view with autoscaling (history window: the complete trend) – same as Reset zoom"),
        ("CTRL + left-click-drag", "Pan horizontally (time only)"),
        ("SHIFT + left-click-drag", "Pan vertically (values only)"),
        ("CTRL + right-click-drag", "Zoom horizontally (time only)"),
        ("SHIFT + right-click-drag", "Zoom vertically (values only)"),
        ("CTRL + SHIFT + right-click-drag", "Zoom evenly (time and values by the same factor, around the mouse position)"),
        ("Left-click (ruler on)", "Move the ruler to the clicked time; drag the ruler line to move it"),
    };

    private static TrendNavigationWindow? _open;

    /// <summary>Shows the reference (non-modal, one window at a time) so the trend can be tried out while reading.</summary>
    public static void ShowFor(Window? owner)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new TrendNavigationWindow { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public TrendNavigationWindow()
    {
        InitializeComponent();
        AddRow("Mouse / keys", "Action", header: true);
        foreach (var (keys, action) in Rows) AddRow(keys, action, header: false);
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void AddRow(string keys, string action, bool header)
    {
        int row = Table.RowDefinitions.Count;
        Table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var k = new TextBlock { Text = keys, Margin = new Thickness(6, 4, 24, 4), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Top };
        var a = new TextBlock { Text = action, Margin = new Thickness(0, 4, 6, 4), TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        if (header)
        {
            foreach (var t in new[] { k, a }) { t.FontWeight = FontWeights.Bold; t.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); }
            var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 2) };
            line.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            Grid.SetRow(line, row); Grid.SetColumnSpan(line, 2);
            Table.Children.Add(line);
        }
        else
        {
            k.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            a.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        }
        Grid.SetRow(k, row); Grid.SetRow(a, row); Grid.SetColumn(a, 1);
        Table.Children.Add(k); Table.Children.Add(a);
    }
}
