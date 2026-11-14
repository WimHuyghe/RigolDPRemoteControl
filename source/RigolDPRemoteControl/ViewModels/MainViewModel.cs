using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using RigolDPRemoteControl.Devices;
using RigolDPRemoteControl.Infrastructure;
using RigolDPRemoteControl.Settings;
using RigolDPRemoteControl.Trend;

namespace RigolDPRemoteControl.ViewModels;

/// <summary>Operations of the live trend that the trend view can trigger.</summary>
public interface ILiveTrendController : INotifyPropertyChanged
{
    bool IsRecording { get; }
    string RecordingFileName { get; }
    void StartRecording();
    void StopRecording();
    void ClearTrend();
    void SaveTrendAs(string path);
}

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Text { get; init; } = "";
    public bool IsError { get; init; }
    public override string ToString() => $"{Time:HH:mm:ss}  {Text}";
}

public sealed class MainViewModel : ObservableObject, ILiveTrendController
{
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly TrendRecorder _recorder = new();

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        foreach (var h in settings.Devices.Select(d => d.Host).Concat(settings.RecentHosts).Distinct(StringComparer.OrdinalIgnoreCase))
            RecentHosts.Add(h);
        _newPort = settings.DefaultPort.ToString();

        foreach (var ds in settings.Devices) Devices.Add(new DeviceViewModel(this, ds));
        foreach (var gs in settings.Groups) Groups.Add(new GroupViewModel(this, gs));

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => UpdateUi();
        _uiTimer.Start();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
    }

    public AppSettings Settings { get; }
    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<GroupViewModel> Groups { get; } = new();
    public ObservableCollection<ChannelViewModel> AllChannels { get; } = new();
    public ObservableCollection<string> RecentHosts { get; } = new();
    public ObservableCollection<LogEntry> Log { get; } = new();
    public TrendDataSet TrendData { get; } = new(isLive: true);

    private string _newHost = "";
    public string NewHost { get => _newHost; set => SetField(ref _newHost, value); }
    private string _newPort;
    public string NewPort { get => _newPort; set => SetField(ref _newPort, value); }

    private string _status = "Ready";
    public string StatusMessage { get => _status; private set => SetField(ref _status, value); }
    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => SetField(ref _statusIsError, value); }

    public bool IsDarkMode
    {
        get => Settings.DarkMode;
        set
        {
            if (Settings.DarkMode == value) return;
            Settings.DarkMode = value;
            ThemeManager.Apply(value);
            OnPropertyChanged();
            RequestSave();
        }
    }

    public bool HasNoDevices => Devices.Count == 0;
    public bool HasNoGroups => Groups.Count == 0;

    /// <summary>The "Channel groups" panel is undocked in its own always-on-top window.</summary>
    public bool IsGroupsFloating
    {
        get => Settings.GroupsFloating;
        set
        {
            if (Settings.GroupsFloating == value) return;
            Settings.GroupsFloating = value;
            OnPropertyChanged();
            RequestSave();
        }
    }

    // ---------------------------------------------------------------- startup / shutdown

    public void Start()
    {
        foreach (var d in Devices.Where(d => d.Settings.AutoConnect)) d.Connect();
        if (Settings.AutoStartTrend) StartRecording();
        ReportInfo(Devices.Count == 0
            ? "Welcome – enter the IP address (or COM port) of a Rigol DP power supply, or 'sim' for a simulated one, and press Add."
            : $"Connecting to {Devices.Count(d => d.Settings.AutoConnect)} power suppl{(Devices.Count == 1 ? "y" : "ies")}…");
    }

    public async Task ShutdownAsync()
    {
        _uiTimer.Stop();
        _recorder.Stop();
        await Task.WhenAll(Devices.Select(d => d.DisconnectAsync(false)));
        SaveSettings();
    }

    // ---------------------------------------------------------------- devices

    public void AddDevice()
    {
        var host = (NewHost ?? "").Trim();
        if (host.Length == 0) { ReportError("Enter an IP address or host name first."); return; }
        int port = Settings.DefaultPort;
        // allow "192.168.1.10:5555" and "COM3:9600"
        int colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out var p2)) { port = p2; host = host[..colon]; }
        else if (int.TryParse(NewPort, out var p)) port = p;

        if (Scpi.ScpiSerialTransport.IsSerialPortName(host))
        {
            // RS232 (DP700): the port field holds the baud rate
            host = host.ToUpperInvariant();
            if (port == Settings.DefaultPort || port < 300) port = 9600;
        }

        if (Devices.Any(d => d.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && d.Port == port))
        {
            ReportError($"{host}:{port} is already in the list.");
            return;
        }
        var ds = new DeviceSettings { Host = host, Port = port, AutoConnect = true };
        Settings.Devices.Add(ds);
        var vm = new DeviceViewModel(this, ds);
        Devices.Add(vm);
        if (!RecentHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) RecentHosts.Insert(0, host);
        if (!Settings.RecentHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) Settings.RecentHosts.Insert(0, host);
        while (Settings.RecentHosts.Count > 20) Settings.RecentHosts.RemoveAt(Settings.RecentHosts.Count - 1);
        NewHost = "";
        OnPropertyChanged(nameof(HasNoDevices));
        vm.Connect();
        RequestSave();
    }

    public async Task RemoveDeviceAsync(DeviceViewModel d)
    {
        await d.DisconnectAsync(true);
        DropDevice(d, removeTrend: true);
        var prefix = $"{d.Host}:{d.Port}/";
        foreach (var g in Groups) g.Settings.Members.RemoveAll(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        Settings.Devices.Remove(d.Settings);
        OnPropertyChanged(nameof(HasNoDevices));
        RequestSave();
    }

    /// <summary>Removes the device and its channels from the lists (and its series from the live trend).</summary>
    private void DropDevice(DeviceViewModel d, bool removeTrend)
    {
        foreach (var c in d.Channels)
        {
            AllChannels.Remove(c);
            if (removeTrend) TrendData.Remove(c.Key); // the trend series selection disappears with the supply
            c.TrendSeries = null;
        }
        Devices.Remove(d);
    }

    public void MoveDevice(DeviceViewModel d, int delta)
    {
        int i = Devices.IndexOf(d), j = i + delta;
        if (i < 0 || j < 0 || j >= Devices.Count) return;
        Devices.Move(i, j);
        RequestSave();
    }

    internal void RegisterChannel(ChannelViewModel ch)
    {
        if (!AllChannels.Contains(ch)) AllChannels.Add(ch);
        var s = TrendData.GetOrAdd(ch.Key, ch.LegendLabel, ch.ColorHex);
        s.Label = ch.LegendLabel;
        s.ColorHex = ch.ColorHex;
        s.ShowVoltage = ch.Settings.TrendVoltage;
        s.ShowCurrent = ch.Settings.TrendCurrent;
        s.ShowPower = ch.Settings.TrendPower;
        s.VisibilityChanged += () =>
        {
            if (!ReferenceEquals(ch.TrendSeries, s)) return; // channel was replaced (preset loaded / supply removed)
            ch.Settings.TrendVoltage = s.ShowVoltage;
            ch.Settings.TrendCurrent = s.ShowCurrent;
            ch.Settings.TrendPower = s.ShowPower;
            RequestSave();
        };
        ch.TrendSeries = s;
    }

    public ChannelViewModel? FindChannel(string key)
        => AllChannels.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    internal string NextChannelColor()
        => TrendColors.Next(Settings.Devices.SelectMany(d => d.Channels).Select(c => c.Color).Where(c => !string.IsNullOrEmpty(c)));

    /// <summary>Switch a set of channels, honouring each channel's on/off delay.</summary>
    public void Switch(IEnumerable<ChannelViewModel> channels, bool on, bool useDelays = true)
    {
        var list = channels.Where(c => c.Device.IsConnected).ToList();
        foreach (var ch in list)
            ch.ScheduleSwitch(on, useDelays ? (on ? ch.OnDelayMs : ch.OffDelayMs) : 0);
        if (list.Count > 0) ReportInfo($"Switching {list.Count} channel(s) {(on ? "ON" : "OFF")}" +
            (useDelays && list.Any(c => (on ? c.OnDelayMs : c.OffDelayMs) > 0) ? " (with delays)" : ""));
    }

    public void AllOff()
    {
        foreach (var d in Devices) Switch(d.Channels, false, useDelays: false);
    }

    // ---------------------------------------------------------------- groups

    public GroupViewModel AddGroup(string name, IEnumerable<string> members)
    {
        var gs = new GroupSettings { Name = name, Members = members.ToList() };
        Settings.Groups.Add(gs);
        var g = new GroupViewModel(this, gs);
        Groups.Add(g);
        OnPropertyChanged(nameof(HasNoGroups));
        RequestSave();
        return g;
    }

    public void RemoveGroup(GroupViewModel g)
    {
        Groups.Remove(g);
        Settings.Groups.Remove(g.Settings);
        OnPropertyChanged(nameof(HasNoGroups));
        RequestSave();
    }

    // ---------------------------------------------------------------- presets (complete configuration)

    /// <summary>Raised after a preset replaced the trend settings (the trend view re-reads them).</summary>
    public event Action? TrendSettingsReplaced;

    /// <summary>Lets the trend view copy its UI state into <see cref="AppSettings.Trend"/> before a preset is saved.</summary>
    public Action? StoreTrendSettings { get; set; }

    public int ChannelsOnCount => Devices.Sum(d => d.Channels.Count(c => c.IsOutputOn || c.HasPending));

    /// <summary>Supplies that are not connected: their instrument settings cannot be read for a preset.</summary>
    public IEnumerable<DeviceViewModel> DevicesWithoutInstrumentData
        => Devices.Where(d => !d.IsConnected && !d.HasPendingInstrumentSettings);

    /// <summary>Lets an open preset-info window copy its page into <see cref="AppSettings.PresetInfo"/>.</summary>
    public Action? StorePresetInfo { get; set; }

    /// <summary>Raised after a preset replaced the preset info page (an open info window reloads it).</summary>
    public event Action? PresetInfoReplaced;

    public string CurrentPresetName
    {
        get => Settings.CurrentPresetName;
        private set
        {
            if (Settings.CurrentPresetName == value) return;
            Settings.CurrentPresetName = value;
            OnPropertyChanged();
            RequestSave();
        }
    }

    public async Task<PresetFile> CapturePresetAsync()
    {
        StoreTrendSettings?.Invoke();
        StorePresetInfo?.Invoke();
        var p = new PresetFile
        {
            Saved = DateTime.Now,
            Groups = PresetService.Clone(Groups.Select(g => g.Settings).ToList()),
            Trend = PresetService.Clone(Settings.Trend),
            Info = PresetService.Clone(Settings.PresetInfo),
        };
        foreach (var d in Devices.ToArray())
        {
            p.Devices.Add(new PresetDevice
            {
                Device = PresetService.Clone(d.Settings),
                Instrument = await d.CaptureInstrumentAsync(),
            });
        }
        return p;
    }

    public async Task<bool> SavePresetAsync(string path)
    {
        try
        {
            var p = await CapturePresetAsync();
            PresetService.Save(path, p);
            CurrentPresetName = Path.GetFileNameWithoutExtension(path);
            int missing = p.Devices.Count(d => d.Instrument.Count == 0);
            ReportInfo($"Preset saved: {Path.GetFileName(path)}" +
                       (missing > 0 ? $"  ({missing} supply/supplies not connected – their setpoints and OVP/OCP are not in the preset)" : ""));
            return true;
        }
        catch (Exception ex)
        {
            ReportError("Saving preset failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Loads a complete configuration. All outputs must already be off (<see cref="SwitchEverythingOffAsync"/>);
    /// the supplies are re-created from the preset and the instrument settings are written as soon as each one is connected.
    /// </summary>
    public async Task LoadPresetAsync(PresetFile p, string name)
    {
        // 1. disconnect and drop the current supplies (outputs are off; keep remote control, we reconnect right away)
        var newKeys = new HashSet<string>(p.Devices.Select(d => $"{d.Device.Host}:{d.Device.Port}"), StringComparer.OrdinalIgnoreCase);
        foreach (var d in Devices.ToArray())
        {
            bool stays = newKeys.Contains($"{d.Host}:{d.Port}");
            await d.DisconnectAsync(false, goLocal: !stays);
            DropDevice(d, removeTrend: !stays); // trend history of supplies that are also in the preset is kept
        }
        AllChannels.Clear();

        // 2. supplies
        Settings.Devices = new List<DeviceSettings>();
        foreach (var pd in p.Devices)
        {
            var ds = PresetService.Clone(pd.Device);
            if (Devices.Any(x => x.Host.Equals(ds.Host, StringComparison.OrdinalIgnoreCase) && x.Port == ds.Port)) continue;
            Settings.Devices.Add(ds);
            var vm = new DeviceViewModel(this, ds);
            vm.SetPendingInstrumentSettings(pd.Instrument, ds.Model);
            Devices.Add(vm);
            if (!RecentHosts.Contains(ds.Host, StringComparer.OrdinalIgnoreCase)) RecentHosts.Insert(0, ds.Host);
            if (!Settings.RecentHosts.Contains(ds.Host, StringComparer.OrdinalIgnoreCase)) Settings.RecentHosts.Insert(0, ds.Host);
        }

        // 3. groups
        Groups.Clear();
        Settings.Groups = PresetService.Clone(p.Groups);
        foreach (var gs in Settings.Groups) Groups.Add(new GroupViewModel(this, gs));

        // 4. trend / plot settings (copied into the existing object: the trend view holds a reference to it)
        var t = Settings.Trend;
        var src = p.Trend;
        t.WindowSeconds = src.WindowSeconds;
        t.Smoothing = src.Smoothing;
        t.SmoothingSamples = src.SmoothingSamples;
        t.ShowLegend = src.ShowLegend;
        t.SeparatePlots = src.SeparatePlots;
        t.Voltage = PresetService.Clone(src.Voltage);
        t.Current = PresetService.Clone(src.Current);
        t.Power = PresetService.Clone(src.Power);
        TrendSettingsReplaced?.Invoke();

        // 5. preset info page
        Settings.PresetInfo = PresetService.Clone(p.Info ?? new PresetInfo());
        CurrentPresetName = name;
        PresetInfoReplaced?.Invoke();

        OnPropertyChanged(nameof(HasNoDevices));
        OnPropertyChanged(nameof(HasNoGroups));

        // 6. connect: the preset's setpoints / OVP / OCP are written right after each connection
        foreach (var d in Devices.Where(d => d.Settings.AutoConnect)) d.Connect();
        ReportInfo($"Preset '{name}' loaded: {Devices.Count} supply/supplies, {Groups.Count} group(s). All outputs are off.");
        SaveSettings();
    }

    /// <summary>Switch every output of every connected supply off immediately and wait for it.</summary>
    public async Task<bool> SwitchEverythingOffAsync()
    {
        var results = await Task.WhenAll(Devices.Select(d => d.SwitchAllOffNowAsync()));
        return results.All(r => r);
    }

    // ---------------------------------------------------------------- sampling / trend

    /// <summary>Called on the sampling thread of a device for every new measurement.</summary>
    internal void OnSample(ChannelViewModel ch, DateTime t, Measurement m, string mode)
    {
        if (!_isRecording) return;
        var s = ch.TrendSeries;
        if (s == null) return;
        s.Add(t.ToOADate(), m.Voltage, m.Current, m.Power, TrendSeries.ModeCode(mode));
        _recorder.Record(s, t, m.Voltage, m.Current, m.Power, mode);
    }

    private volatile bool _isRecording;
    public bool IsRecording => _isRecording;
    public string RecordingFileName => _recorder.CurrentFile != null ? Path.GetFileName(_recorder.CurrentFile) : "";

    public void StartRecording()
    {
        // restarting the trend overwrites the default trend file
        _isRecording = false;
        TrendData.ClearData();
        try
        {
            _recorder.Start(AppPaths.DefaultTrendFile, TrendData.Series);
            _isRecording = true;
            ReportInfo($"Trend recording started → {AppPaths.DefaultTrendFile}");
        }
        catch (Exception ex)
        {
            ReportError("Cannot write trend file: " + ex.Message);
        }
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordingFileName));
    }

    public void StopRecording()
    {
        _isRecording = false;
        _recorder.Stop();
        ReportInfo("Trend recording stopped. The data stays visible until the trend is cleared or restarted.");
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordingFileName));
    }

    public void ClearTrend()
    {
        if (_isRecording) StartRecording();
        else TrendData.ClearData();
    }

    public void SaveTrendAs(string path)
    {
        try
        {
            if (_isRecording)
            {
                _recorder.SaveAsAndContinue(path);
                ReportInfo($"Trend saved as {Path.GetFileName(path)} – recording continues into this file.");
            }
            else
            {
                TrendFile.Export(path, TrendData.Series);
                ReportInfo($"Trend saved as {Path.GetFileName(path)}");
            }
        }
        catch (Exception ex)
        {
            ReportError("Saving trend failed: " + ex.Message);
        }
        OnPropertyChanged(nameof(RecordingFileName));
    }

    // ---------------------------------------------------------------- UI updates, status, settings

    private void UpdateUi()
    {
        foreach (var d in Devices) d.UpdateUi();
        foreach (var g in Groups) g.UpdateUi();
        if (_recorder.LastError is { } err && !StatusIsError) ReportError("Trend file: " + err);
    }

    public void ReportError(string msg) => Report(msg, true);
    public void ReportInfo(string msg) => Report(msg, false);

    private void Report(string msg, bool error)
    {
        void Do()
        {
            StatusMessage = msg;
            StatusIsError = error;
            Log.Insert(0, new LogEntry { Text = msg, IsError = error });
            while (Log.Count > 300) Log.RemoveAt(Log.Count - 1);
        }
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return;
        if (disp.CheckAccess()) Do(); else disp.BeginInvoke(Do);
    }

    public void RequestSave()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return;
        if (!disp.CheckAccess()) { disp.BeginInvoke(RequestSave); return; }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveSettings()
    {
        try
        {
            Settings.Devices = Devices.Select(d => d.Settings).ToList();
            Settings.Groups = Groups.Select(g => g.Settings).ToList();
            SettingsService.Save(Settings);
        }
        catch (Exception ex)
        {
            ReportError("Saving settings failed: " + ex.Message);
        }
    }

    /// <summary>Watt-hour counters visible on the channel cards (application setting).</summary>
    public bool ShowEnergy => Settings.ShowEnergy;

    public void SettingsChanged()
    {
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(ShowEnergy));
        RequestSave();
    }
}
