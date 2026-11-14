using System.Windows.Input;
using System.Windows.Media;
using RigolDPRemoteControl.Devices;
using RigolDPRemoteControl.Infrastructure;
using RigolDPRemoteControl.Settings;
using RigolDPRemoteControl.Trend;

namespace RigolDPRemoteControl.ViewModels;

public sealed class ChannelViewModel : ObservableObject
{
    private readonly object _snapLock = new();
    // ---- snapshot written by the sampling thread ----
    private double _sV = double.NaN, _sI = double.NaN, _sP = double.NaN;
    private string _sMode = "";
    private bool _sOut;
    private bool _sOutKnown;
    private double _sVSet = double.NaN, _sISet = double.NaN, _sRatedV = double.NaN, _sRatedA = double.NaN;
    private bool _sOvpTrip, _sOcpTrip;
    private long _snapVersion, _uiVersion = -1;
    private bool _outKnownUi; // the UI has received at least one real output state

    private CancellationTokenSource? _pendingCts;
    private DateTime _pendingDue;
    private bool _pendingOn;

    public ChannelViewModel(DeviceViewModel device, int index, ChannelSettings settings, ChannelSpec? spec)
    {
        Device = device;
        Index = index;
        Settings = settings;
        if (spec != null) { _ratedV = spec.RatedVoltage; _ratedA = spec.RatedCurrent; }

        ToggleOutputCommand = new RelayCommand(() => RequestOutput(!IsOutputOn), () => Device.IsConnected);
        OnCommand = new RelayCommand(() => RequestOutput(true), () => Device.IsConnected);
        OffCommand = new RelayCommand(() => RequestOutput(false), () => Device.IsConnected);
        ApplyVoltageCommand = new RelayCommand(ApplyVoltage, () => Device.IsConnected);
        ApplyCurrentCommand = new RelayCommand(ApplyCurrent, () => Device.IsConnected);
        RevertVoltageCommand = new RelayCommand(() => { VoltageEditText = Num.F(VoltageSet, 3); IsEditingVoltage = false; });
        RevertCurrentCommand = new RelayCommand(() => { CurrentEditText = Num.F(CurrentSet, 3); IsEditingCurrent = false; });
        CancelPendingCommand = new RelayCommand(CancelPending);
        ClearTripCommand = new RelayCommand(async () => await Device.RunAsync(d => d.ClearTripAsync(Index), $"Clear protection {ScpiName}"));
        ResetEnergyCommand = new RelayCommand(ResetEnergy);
    }

    public ICommand ResetEnergyCommand { get; }

    public DeviceViewModel Device { get; }
    public ChannelSettings Settings { get; }
    public int Index { get; }
    public string ScpiName => $"CH{Index}";
    public string Key => $"{Device.Host}:{Device.Port}/CH{Index}";

    public string Name
    {
        get => Settings.Name;
        set
        {
            if (Settings.Name == value) return;
            Settings.Name = value?.Trim() ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(LegendLabel));
            if (TrendSeries != null) TrendSeries.Label = LegendLabel;
            Device.Main.RequestSave();
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? ScpiName : Name;

    /// <summary>Legend text: "Name (CH1 192.168.1.10)" or "CH1 192.168.1.10" when no name is set.</summary>
    public string LegendLabel => string.IsNullOrWhiteSpace(Name)
        ? $"{ScpiName} {Device.Host}"
        : $"{Name} ({ScpiName} {Device.Host})";

    /// <summary>Text used in group editors / lists.</summary>
    public string FullLabel => $"{Device.Host}  {ScpiName}" + (string.IsNullOrWhiteSpace(Name) ? "" : $"  –  {Name}");

    public int OnDelayMs { get => Settings.OnDelayMs; set { Settings.OnDelayMs = Math.Max(0, value); OnPropertyChanged(); Device.Main.RequestSave(); } }
    public int OffDelayMs { get => Settings.OffDelayMs; set { Settings.OffDelayMs = Math.Max(0, value); OnPropertyChanged(); Device.Main.RequestSave(); } }

    // ---- delayed OVP / OCP enable (prevents protection trips by inrush current) ----

    public bool ProtectionDelayEnabled => Settings.ProtectionDelayEnabled;
    public int ProtectionDelayMs => Settings.ProtectionDelayMs;

    /// <summary>Changes the delayed-protection settings (from the channel settings dialog).</summary>
    public void SetProtectionDelay(bool enabled, int delayMs, bool ovpOn, bool ocpOn)
    {
        Settings.ProtectionDelayEnabled = enabled;
        Settings.ProtectionDelayMs = Math.Clamp(delayMs, 0, 3_600_000);
        Settings.DelayedOvpOn = ovpOn;
        Settings.DelayedOcpOn = ocpOn;
        if (!enabled) CancelProtectionTimer();
        _protLastOut = null; // re-evaluate the current output state
        OnPropertyChanged(nameof(ProtectionDelayEnabled));
        OnPropertyChanged(nameof(ProtectionDelayMs));
       
        Device.Main.RequestSave();
    }

    /// <summary>True while OVP/OCP are deliberately held off (output on, delay running).</summary>
    public bool IsProtectionDelayRunning => _protCts != null;

    private string _protPendingText = "";
    public string ProtectionPendingText
    {
        get => _protPendingText;
        private set { if (SetField(ref _protPendingText, value)) OnPropertyChanged(nameof(HasProtectionPending)); }
    }
    public bool HasProtectionPending => _protPendingText.Length > 0;

    private CancellationTokenSource? _protCts;
    private DateTime _protDue;
    private bool? _protLastOut;

    /// <summary>
    /// Follows the output state (also front-panel switching and protection trips): output off → OVP/OCP off,
    /// output on → OVP/OCP enabled after the delay. Called from the UI timer.
    /// </summary>
    private void HandleProtectionDelay()
    {
        if (!Settings.ProtectionDelayEnabled || !Device.IsConnected)
        {
            if (!Device.IsConnected) { CancelProtectionTimer(); _protLastOut = null; }
            return;
        }
        if (_protLastOut == IsOutputOn) return;
        _protLastOut = IsOutputOn;
        if (IsOutputOn) StartProtectionTimer();
        else
        {
            CancelProtectionTimer();
            _ = Device.RunAsync(d => d.SetProtectionStateAsync(Index, false, false), $"{ScpiName} OVP/OCP off");
        }
    }

    private void StartProtectionTimer()
    {
        CancelProtectionTimer();
        var cts = new CancellationTokenSource();
        _protCts = cts;
        int delay = Settings.ProtectionDelayMs;
        _protDue = DateTime.Now.AddMilliseconds(delay);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
                await Device.RunAsync(async d =>
                {
                    // only when the output is still on (it may have been switched off meanwhile)
                    if (cts.IsCancellationRequested || !await d.ReadOutputAsync(Index)) return;
                    await d.SetProtectionStateAsync(Index, Settings.DelayedOvpOn, Settings.DelayedOcpOn);
                }, $"{ScpiName} enable OVP/OCP");
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_protCts, cts)) _protCts = null;
                cts.Dispose();
            }
        });
    }

    private void CancelProtectionTimer()
    {
        var c = _protCts;
        _protCts = null;
        try { c?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public string ColorHex => Settings.Color;
    public Brush ColorBrush
    {
        get
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Settings.Color)); }
            catch { return Brushes.Gray; }
        }
    }

    public TrendSeries? TrendSeries { get; set; }

    // ---- values shown in the UI (updated by the UI timer from the snapshot) ----
    private double _measV = double.NaN, _measI = double.NaN, _measP = double.NaN;
    public double MeasuredVoltage { get => _measV; private set { if (SetField(ref _measV, value)) OnPropertyChanged(nameof(MeasuredVoltageText)); } }
    public double MeasuredCurrent { get => _measI; private set { if (SetField(ref _measI, value)) OnPropertyChanged(nameof(MeasuredCurrentText)); } }
    public double MeasuredPower { get => _measP; private set { if (SetField(ref _measP, value)) OnPropertyChanged(nameof(MeasuredPowerText)); } }
    public string MeasuredVoltageText => Num.F(MeasuredVoltage, 3);
    public string MeasuredCurrentText => Num.F(MeasuredCurrent, 4);
    public string MeasuredPowerText => Num.F(MeasuredPower, 3);

    private double _energyWh;
    /// <summary>Energy delivered since the output was switched on (or since the last reset), in Wh.</summary>
    public double EnergyWh
    {
        get => _energyWh;
        private set { if (SetField(ref _energyWh, value)) { OnPropertyChanged(nameof(EnergyText)); OnPropertyChanged(nameof(EnergyUnit)); } }
    }
    public string EnergyText => EnergyWh >= 1000 ? Num.F(EnergyWh / 1000, 3) : Num.F(EnergyWh, EnergyWh >= 100 ? 2 : 3);
    public string EnergyUnit => EnergyWh >= 1000 ? "kWh" : "Wh";

    public void ResetEnergy()
    {
        lock (_snapLock)
        {
            _sEnergyWh = 0;
            _sEnergyMarkWh = 0;
            _snapVersion++;
        }
        EnergyWh = 0;
    }

    private string _mode = "";
    /// <summary>"CV", "CC", "UR" or "" (unknown / output off).</summary>
    public string Mode { get => _mode; private set { if (SetField(ref _mode, value)) OnPropertyChanged(nameof(ModeText)); } }
    public string ModeText => !IsOutputOn ? "OFF" : string.IsNullOrEmpty(Mode) ? "--" : Mode;

    private bool _isOutputOn;
    public bool IsOutputOn
    {
        get => _isOutputOn;
        private set { if (SetField(ref _isOutputOn, value)) { OnPropertyChanged(nameof(ModeText)); OnPropertyChanged(nameof(OutputText)); } }
    }
    public string OutputText => IsOutputOn ? "ON" : "OFF";

    private double _vSet = double.NaN, _iSet = double.NaN, _ratedV = double.NaN, _ratedA = double.NaN;
    public double VoltageSet { get => _vSet; private set { if (SetField(ref _vSet, value)) { OnPropertyChanged(nameof(VoltageSetText)); if (!IsEditingVoltage) VoltageEditText = Num.F(value, 3); } } }
    public double CurrentSet { get => _iSet; private set { if (SetField(ref _iSet, value)) { OnPropertyChanged(nameof(CurrentSetText)); if (!IsEditingCurrent) CurrentEditText = Num.F(value, 3); } } }
    public string VoltageSetText => Num.F(VoltageSet, 3);
    public string CurrentSetText => Num.F(CurrentSet, 3);
    public double RatedVoltage { get => _ratedV; private set { if (SetField(ref _ratedV, value)) { OnPropertyChanged(nameof(RatingText)); OnPropertyChanged(nameof(RatingBracket)); } } }
    public double RatedCurrent { get => _ratedA; private set { if (SetField(ref _ratedA, value)) { OnPropertyChanged(nameof(RatingText)); OnPropertyChanged(nameof(RatingBracket)); } } }
    public string RatingText => double.IsNaN(RatedVoltage) ? "" : $"{RatedVoltage.ToString("0.##", Num.Inv)}V / {RatedCurrent.ToString("0.##", Num.Inv)}A";
    /// <summary>"(30V / 3A)" – shown next to the channel number.</summary>
    public string RatingBracket => RatingText.Length == 0 ? "" : $"({RatingText})";

    private string _vEdit = "", _iEdit = "";
    public string VoltageEditText { get => _vEdit; set => SetField(ref _vEdit, value); }
    public string CurrentEditText { get => _iEdit; set => SetField(ref _iEdit, value); }
    private bool _editV, _editI;
    public bool IsEditingVoltage { get => _editV; set => SetField(ref _editV, value); }
    public bool IsEditingCurrent { get => _editI; set => SetField(ref _editI, value); }

    private bool _ovpTrip, _ocpTrip;
    public bool OvpTripped { get => _ovpTrip; private set { if (SetField(ref _ovpTrip, value)) OnPropertyChanged(nameof(IsTripped)); } }
    public bool OcpTripped { get => _ocpTrip; private set { if (SetField(ref _ocpTrip, value)) OnPropertyChanged(nameof(IsTripped)); } }
    public bool IsTripped => OvpTripped || OcpTripped;
    public string TripText => OvpTripped && OcpTripped ? "OVP+OCP" : OvpTripped ? "OVP" : "OCP";

    private string _pendingText = "";
    public string PendingText { get => _pendingText; private set { if (SetField(ref _pendingText, value)) OnPropertyChanged(nameof(HasPending)); } }
    public bool HasPending => PendingText.Length > 0;

    public ICommand ToggleOutputCommand { get; }
    public ICommand OnCommand { get; }
    public ICommand OffCommand { get; }
    public ICommand ApplyVoltageCommand { get; }
    public ICommand ApplyCurrentCommand { get; }
    public ICommand RevertVoltageCommand { get; }
    public ICommand RevertCurrentCommand { get; }
    public ICommand CancelPendingCommand { get; }
    public ICommand ClearTripCommand { get; }

    // ---- sampling thread side ----

    // ---- energy (watt-hour counter), integrated on the sampling thread ----
    private double _sEnergyWh;        // energy since the output was switched on (or since the last reset)
    private double _sEnergyMarkWh;    // counter value at the last status read with the output OFF
    private double _lastP = double.NaN;
    private long _lastPTicks;
    /// <summary>Gaps longer than this (connection lost) are not bridged.</summary>
    private const double MaxIntegrationGapS = 30;

    internal void PushMeasurement(Measurement m, string? mode)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_snapLock)
        {
            _sV = m.Voltage; _sI = m.Current; _sP = m.Power;
            if (mode != null) _sMode = mode;

            // trapezoidal integration of the measured power (it is 0 W while the output is off)
            double p = Math.Abs(m.Power);
            if (!double.IsNaN(p) && !double.IsNaN(_lastP))
            {
                double dt = (now - _lastPTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (dt > 0 && dt <= MaxIntegrationGapS) _sEnergyWh += (p + _lastP) / 2 * dt / 3600.0;
            }
            _lastP = p;
            _lastPTicks = now;
            _snapVersion++;
        }
    }

    internal void PushStatus(bool? output, Setpoints? sp, (bool ovp, bool ocp)? trip)
    {
        lock (_snapLock)
        {
            if (output.HasValue)
            {
                // off -> on: the counter restarts. The output state is only re-read every second, so keep
                // what was counted since the last "off" reading (the first part of the on-period).
                if (output.Value && _sOutKnown && !_sOut) _sEnergyWh = Math.Max(0, _sEnergyWh - _sEnergyMarkWh);
                if (!output.Value) _sEnergyMarkWh = _sEnergyWh;
            }
            if (output.HasValue) { _sOut = output.Value; _sOutKnown = true; }
            if (sp != null) { _sVSet = sp.Voltage; _sISet = sp.Current; _sRatedV = sp.RatedVoltage; _sRatedA = sp.RatedCurrent; }
            if (trip.HasValue) { _sOvpTrip = trip.Value.ovp; _sOcpTrip = trip.Value.ocp; }
            _snapVersion++;
        }
    }

    internal void MarkDisconnected()
    {
        lock (_snapLock)
        {
            _sV = _sI = _sP = double.NaN;
            _sMode = "";
            _lastP = double.NaN; // do not integrate across a lost connection
            _snapVersion++;
        }
    }

    // ---- UI thread side ----

    /// <summary>Copies the latest snapshot into the bound properties (called by the UI timer).</summary>
    internal void UpdateUi()
    {
        lock (_snapLock)
        {
            if (_snapVersion != _uiVersion)
            {
                _uiVersion = _snapVersion;
                MeasuredVoltage = _sV; MeasuredCurrent = _sI; MeasuredPower = _sP;
                EnergyWh = _sEnergyWh;
                if (_sOutKnown) { IsOutputOn = _sOut; _outKnownUi = true; }
                Mode = _sMode;
                if (!double.IsNaN(_sRatedV)) { RatedVoltage = _sRatedV; RatedCurrent = _sRatedA; }
                VoltageSet = _sVSet; CurrentSet = _sISet;
                bool tripChanged = OvpTripped != _sOvpTrip || OcpTripped != _sOcpTrip;
                OvpTripped = _sOvpTrip; OcpTripped = _sOcpTrip;
                if (tripChanged) OnPropertyChanged(nameof(TripText));
            }
        }
        if (_pendingCts != null)
        {
            var left = _pendingDue - DateTime.Now;
            PendingText = $"{(_pendingOn ? "ON" : "OFF")} in {Math.Max(0, left.TotalSeconds):0.0} s";
        }
        else PendingText = "";

        if (_outKnownUi) HandleProtectionDelay();
        ProtectionPendingText = _protCts != null
            ? $"OVP/OCP enabled in {Math.Max(0, (_protDue - DateTime.Now).TotalSeconds):0.0} s"
            : "";
    }

    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(LegendLabel));
        OnPropertyChanged(nameof(FullLabel));
        OnPropertyChanged(nameof(ColorBrush));
        if (TrendSeries != null) TrendSeries.Label = LegendLabel;
    }

    // ---- commands ----

    private void RequestOutput(bool on)
    {
        int delay = Device.Main.Settings.ApplyDelaysToSingleChannel ? (on ? OnDelayMs : OffDelayMs) : 0;
        ScheduleSwitch(on, delay);
    }

    /// <summary>Switch the output after <paramref name="delayMs"/>. A new request cancels a pending one.</summary>
    public void ScheduleSwitch(bool on, int delayMs)
    {
        CancelPending();
        if (delayMs <= 0)
        {
            _ = SetOutputNowAsync(on);
            return;
        }
        var cts = new CancellationTokenSource();
        _pendingCts = cts;
        _pendingDue = DateTime.Now.AddMilliseconds(delayMs);
        _pendingOn = on;
        UpdateUi();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token);
                await SetOutputNowAsync(on);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_pendingCts, cts)) _pendingCts = null;
                cts.Dispose();
            }
        });
    }

    public void CancelPending()
    {
        var c = _pendingCts;
        _pendingCts = null;
        try { c?.Cancel(); } catch (ObjectDisposedException) { }
        PendingText = "";
    }

    private async Task SetOutputNowAsync(bool on)
    {
        bool holdProtection = on && Settings.ProtectionDelayEnabled;
        await Device.RunAsync(async d =>
        {
            // delayed protection: make sure OVP/OCP are off before the output (and the inrush current) starts
            if (holdProtection) await d.SetProtectionStateAsync(Index, false, false);
            await d.SetOutputAsync(Index, on);
            var state = await d.ReadOutputAsync(Index);
            PushStatus(state, null, null);
        }, $"{ScpiName} output {(on ? "ON" : "OFF")}");
    }

    private bool ValidateSet(string text, double rated, string unit, out double value)
    {
        if (!Num.TryParseUser(text, out value))
        {
            Device.Main.ReportError($"'{text}' is not a valid number.");
            return false;
        }
        if (!double.IsNaN(rated))
        {
            // allow the small over-range the DP800 supports (about +5..7 %)
            double lim = Math.Abs(rated) * 1.07;
            if (rated < 0 && value > 0) value = -value; // negative channel (DP831 CH3): accept "5" as -5 V
            if (Math.Abs(value) > lim || (rated > 0 && value < 0))
            {
                Device.Main.ReportError($"{value} {unit} is outside the range of {ScpiName} ({rated} {unit}).");
                return false;
            }
        }
        return true;
    }

    private async void ApplyVoltage()
    {
        IsEditingVoltage = false;
        if (!ValidateSet(VoltageEditText, RatedVoltage, "V", out var v)) { VoltageEditText = Num.F(VoltageSet, 3); return; }
        await Device.RunAsync(async d =>
        {
            await d.SetVoltageAsync(Index, v);
            PushStatus(null, await d.ReadSetpointsAsync(Index), null);
        }, $"{ScpiName} set {v} V");
    }

    private async void ApplyCurrent()
    {
        IsEditingCurrent = false;
        if (!ValidateSet(CurrentEditText, Math.Abs(RatedCurrent), "A", out var a)) { CurrentEditText = Num.F(CurrentSet, 3); return; }
        await Device.RunAsync(async d =>
        {
            await d.SetCurrentAsync(Index, a);
            PushStatus(null, await d.ReadSetpointsAsync(Index), null);
        }, $"{ScpiName} set {a} A");
    }
}
