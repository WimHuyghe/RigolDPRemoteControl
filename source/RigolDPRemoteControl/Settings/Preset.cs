using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigolDPRemoteControl.Settings;

/// <summary>
/// A complete configuration: the power supplies (address, channel names, delays, colours, trend selection, minimized view),
/// the settings inside the instruments (setpoints, OVP/OCP), the channel groups and the trend/plot settings.
/// Stored as JSON (*.dppreset) in settings/presets.
/// </summary>
public sealed class PresetFile
{
    public string Application { get; set; } = "Rigol DP Remote Control";
    public int FormatVersion { get; set; } = 1;
    public DateTime Saved { get; set; } = DateTime.Now;
    public string Description { get; set; } = "";
    public List<PresetDevice> Devices { get; set; } = new();
    public List<GroupSettings> Groups { get; set; } = new();
    public TrendSettings Trend { get; set; } = new();
    /// <summary>Notes page that belongs to this preset (setup description, wiring sketch, goal of the test, …).</summary>
    public PresetInfo Info { get; set; } = new();
}

public sealed class PresetDevice
{
    /// <summary>Application side settings of the supply (host, port, model, channel names, delays, …).</summary>
    public DeviceSettings Device { get; set; } = new();
    /// <summary>Settings inside the instrument, per channel. Empty when the supply was not connected while saving.</summary>
    public List<PresetChannelState> Instrument { get; set; } = new();
}

/// <summary>Instrument settings of one channel (null = unknown, not written when loading).</summary>
public sealed class PresetChannelState
{
    public int Index { get; set; }
    public double? Voltage { get; set; }
    public double? Current { get; set; }
    public double? OvpValue { get; set; }
    public bool? OvpEnabled { get; set; }
    public double? OcpValue { get; set; }
    public bool? OcpEnabled { get; set; }

    public static double? Known(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;
}

public static class PresetService
{
    public const string Extension = ".dppreset";
    public const string DialogFilter = "Rigol DP preset (*.dppreset)|*.dppreset|All files|*.*";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void Save(string path, PresetFile preset)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(preset, Options));
        File.Move(tmp, path, true);
    }

    public static PresetFile Load(string path)
    {
        var p = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("The file is empty.");
        if (p.Devices.Count == 0 && p.Groups.Count == 0 && p.Application.Length == 0)
            throw new InvalidDataException("This is not a Rigol DP Remote Control preset.");
        p.Devices.RemoveAll(d => d.Device == null || string.IsNullOrWhiteSpace(d.Device.Host));
        p.Trend ??= new TrendSettings();
        p.Groups ??= new List<GroupSettings>();
        p.Info ??= new PresetInfo();
        return p;
    }

    /// <summary>Deep copy through JSON, so the preset never shares objects with the live settings.</summary>
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
