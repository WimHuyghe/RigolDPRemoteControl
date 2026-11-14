using System.Windows;

namespace RigolDPRemoteControl.Infrastructure;

/// <summary>
/// Switches between dark and light mode: the WPF Fluent theme (Application.ThemeMode) styles the standard controls,
/// the Palette.*.xaml dictionaries hold the application specific colours.
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;
    public static event Action? ThemeChanged;

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var app = Application.Current;
        if (app == null) return;

        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;

        var dicts = app.Resources.MergedDictionaries;
        var uri = new Uri($"pack://application:,,,/RigolDPRemoteControl;component/Themes/Palette.{(dark ? "Dark" : "Light")}.xaml");
        var palette = new ResourceDictionary { Source = uri };
        var old = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("Palette.", StringComparison.OrdinalIgnoreCase) == true);
        if (old != null) dicts[dicts.IndexOf(old)] = palette;
        else dicts.Add(palette);

        ThemeChanged?.Invoke();
    }
}
