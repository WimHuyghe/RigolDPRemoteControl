using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RigolDPRemoteControl.Infrastructure;

namespace RigolDPRemoteControl.Settings;

public sealed class AppSettings
{
    public bool DarkMode { get; set; } = true;

    /// <summary>Extra pause between two polling cycles (0 = sample as fast as the instrument answers).</summary>
    public int PollIntervalMs { get; set; } = 0;
    /// <summary>
    /// Maximum wait for a response. DP800 instruments occasionally pause their LAN answers for about 2 s
    /// (TCP retransmission after a lost packet), so the default leaves room for that.
    /// </summary>
    public int CommandTimeoutMs { get; set; } = 5000;

    /// <summary>Format version of this file (used to migrate old defaults).</summary>
    public int SettingsVersion { get; set; }
    public const int CurrentSettingsVersion = 2;
    public int DefaultPort { get; set; } = 5555;
    /// <summary>How often output state / setpoints / protection trips are re-read (front panel changes).</summary>
    public int StatusIntervalMs { get; set; } = 1000;

    public bool AutoStartTrend { get; set; } = true;
    public bool ApplyDelaysToSingleChannel { get; set; } = false;
    public bool ReturnToLocalOnExit { get; set; } = true;
    /// <summary>Show the watt-hour counter (energy delivered since the output was switched on) on each channel card.</summary>
    public bool ShowEnergy { get; set; } = false;

    public List<DeviceSettings> Devices { get; set; } = new();
    /// <summary>IP addresses / host names used in previous sessions (also those of removed devices).</summary>
    public List<string> RecentHosts { get; set; } = new();
    public List<GroupSettings> Groups { get; set; } = new();
    public TrendSettings Trend { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    /// <summary>The "Channel groups" panel undocked in its own always-on-top window.</summary>
    public bool GroupsFloating { get; set; }
    public double GroupsFloatLeft { get; set; } = double.NaN;
    public double GroupsFloatTop { get; set; } = double.NaN;

    /// <summary>Notes page of the current configuration (stored in a preset file when the preset is saved).</summary>
    public PresetInfo PresetInfo { get; set; } = new();
    /// <summary>Name of the last loaded / saved preset ("" = not saved as preset yet).</summary>
    public string CurrentPresetName { get; set; } = "";
}

/// <summary>
/// "Preset info" notes page: formatted text with images (WPF XamlPackage) and freehand drawing (ink, ISF),
/// both base64-encoded so they live inside the single JSON preset file.
/// </summary>
public sealed class PresetInfo
{
    public string NotesXamlPackage { get; set; } = "";
    public string InkStrokes { get; set; } = "";
    public double PageHeight { get; set; } = 1400;
    public DateTime Modified { get; set; }
}

public sealed class DeviceSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5555;
    public bool AutoConnect { get; set; } = true;
    /// <summary>Minimized view: one compact line per channel.</summary>
    public bool Compact { get; set; }
    /// <summary>Undocked: shown in its own always-on-top window at FloatLeft / FloatTop (DIPs).</summary>
    public bool Floating { get; set; }
    public double FloatLeft { get; set; } = double.NaN;
    public double FloatTop { get; set; } = double.NaN;
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public int ChannelCount { get; set; }
    public List<ChannelSettings> Channels { get; set; } = new();
}

public sealed class ChannelSettings
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int OnDelayMs { get; set; }
    public int OffDelayMs { get; set; }
    public string Color { get; set; } = "";
    public bool TrendVoltage { get; set; } = true;
    public bool TrendCurrent { get; set; } = true;
    public bool TrendPower { get; set; } = true;

    /// <summary>
    /// Delayed protection: OVP/OCP are switched off together with the output and only enabled again
    /// <see cref="ProtectionDelayMs"/> after the output was switched on (avoids trips by inrush current).
    /// </summary>
    public bool ProtectionDelayEnabled { get; set; }
    public int ProtectionDelayMs { get; set; } = 1000;
    /// <summary>OVP / OCP state wanted once the delay has passed.</summary>
    public bool DelayedOvpOn { get; set; } = true;
    public bool DelayedOcpOn { get; set; } = true;
}

public sealed class GroupSettings
{
    public string Name { get; set; } = "";
    /// <summary>Channel keys: "host:port/CHn".</summary>
    public List<string> Members { get; set; } = new();
}

public sealed class TrendSettings
{
    public double WindowSeconds { get; set; } = 120;
    public bool Smoothing { get; set; }
    public int SmoothingSamples { get; set; } = 10;
    public bool ShowLegend { get; set; } = true;
    /// <summary>false = one plot with 3 y-axes, true = 3 stacked plots (V, A, W) with a shared time axis.</summary>
    public bool SeparatePlots { get; set; }
    public AxisSettings Voltage { get; set; } = new();
    public AxisSettings Current { get; set; } = new();
    public AxisSettings Power { get; set; } = new();
}

public sealed class AxisSettings
{
    public bool Auto { get; set; } = true;
    public double Min { get; set; } = 0;
    public double Max { get; set; } = 10;
}

public sealed class WindowSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1680;
    public double Height { get; set; } = 950;
    public bool Maximized { get; set; }
    public double LeftPaneWidth { get; set; } = 860;
}

/// <summary>Loads/saves settings/settings.json next to the executable.</summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppSettings Load()
    {
        try
        {
            var file = AppPaths.SettingsFile;
            if (File.Exists(file))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Options) ?? new AppSettings();
                if (s.SettingsVersion < 2 && s.CommandTimeoutMs == 2000) s.CommandTimeoutMs = 5000; // old default was too tight
                s.SettingsVersion = AppSettings.CurrentSettingsVersion;
                return s;
            }
        }
        catch (Exception ex)
        {
            try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bad", true); } catch { }
            System.Diagnostics.Debug.WriteLine("Settings load failed: " + ex.Message);
        }
        return new AppSettings { SettingsVersion = AppSettings.CurrentSettingsVersion };
    }

    public static void Save(AppSettings settings)
    {
        var file = AppPaths.SettingsFile;
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, file, true);
    }
}
