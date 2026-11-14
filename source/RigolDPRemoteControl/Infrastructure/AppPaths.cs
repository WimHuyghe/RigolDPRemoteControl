using System.IO;

namespace RigolDPRemoteControl.Infrastructure;

/// <summary>All application folders live next to the executable (portable application).</summary>
public static class AppPaths
{
    public static string BaseDir => AppContext.BaseDirectory;

    public static string SettingsDir => Ensure(Path.Combine(BaseDir, "settings"));
    public static string SettingsFile => Path.Combine(SettingsDir, "settings.json");
    /// <summary>Saved configuration presets (*.dppreset, JSON).</summary>
    public static string PresetDir => Ensure(Path.Combine(SettingsDir, "presets"));

    public static string TrendDir => Ensure(Path.Combine(BaseDir, "trend"));
    public static string DefaultTrendFile => Path.Combine(TrendDir, "LiveTrend.csv");

    public static string HelpFile => Path.Combine(BaseDir, "Help", "Help.html");

    private static string Ensure(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* reported when the file is used */ }
        return dir;
    }
}
