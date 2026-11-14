using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using RigolDPRemoteControl.Devices;
using RigolDPRemoteControl.Infrastructure;
using RigolDPRemoteControl.Scpi;
using RigolDPRemoteControl.Settings;
using RigolDPRemoteControl.Trend;

namespace RigolDPRemoteControl.ViewModels;

public enum ConnectionState { Disconnected, Connecting, Connected, Error }

/// <summary>
/// One power supply. Owns its own TCP connection and sampling task, so several instruments are sampled in parallel
/// and the UI thread is never blocked by network I/O.
/// </summary>
public sealed class DeviceViewModel : ObservableObject
{
    private Dp800Device? _device;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private volatile bool _forceStatus;
    private int _cycleCount;
    private string? _lastError;
    private readonly Stopwatch _rateWatch = new();
    /// <summary>True while the connection works; a single failure then triggers an immediate, quiet reconnect.</summary>
    private bool _healthy;
    private bool _quietReconnect;
    private int _hiccups;
    private const int SlowCycleMs = 1500;

    public DeviceViewModel(MainViewModel main, DeviceSettings settings)
    {
        Main = main;
        Settings = settings;
        if (settings.ChannelCount > 0) EnsureChannels(settings.ChannelCount, settings.Model.Length > 0 ? ModelCatalog.Resolve(settings.Model) : null);

        ConnectCommand = new RelayCommand(Connect, () => State is ConnectionState.Disconnected or ConnectionState.Error);
        DisconnectCommand = new RelayCommand(() => _ = DisconnectAsync(true), () => State is ConnectionState.Connected or ConnectionState.Connecting or ConnectionState.Error);
        AllOnCommand = new RelayCommand(() => Main.Switch(Channels, true), () => IsConnected);
        AllOffCommand = new RelayCommand(() => Main.Switch(Channels, false), () => IsConnected);
        RefreshCommand = new RelayCommand(() => _forceStatus = true, () => IsConnected);
    }

    public MainViewModel Main { get; }
    public DeviceSettings Settings { get; }
    public string Host => Settings.Host;
    public int Port => Settings.Port;
    public string Address => IsSerial ? $"{Host.ToUpperInvariant()} @ {Port} Bd" : Port == 5555 ? Host : $"{Host}:{Port}";
    public bool IsSimulated => Host.StartsWith("sim", StringComparison.OrdinalIgnoreCase);
    /// <summary>RS232 connection ("COM3"); <see cref="Port"/> is then the baud rate.</summary>
    public bool IsSerial => ScpiSerialTransport.IsSerialPortName(Host);

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();
    public DeviceIdentity? Identity { get; private set; }

    public string Model => Identity?.Model ?? (Settings.Model.Length > 0 ? Settings.Model : "DP");
    public string Serial => Identity?.SerialNumber ?? Settings.Serial;
    public string Title => $"{Model}   {Address}" + (IsSimulated ? "   (simulated)" : "");

    private ConnectionState _state = ConnectionState.Disconnected;
    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(IsConnected));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public bool IsConnected => State == ConnectionState.Connected;

    private string _statusText = "Disconnected";
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

    private string _rateText = "";
    public string RateText { get => _rateText; private set => SetField(ref _rateText, value); }

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand AllOnCommand { get; }
    public ICommand AllOffCommand { get; }
    public ICommand RefreshCommand { get; }

    public int OnCount => Channels.Count(c => c.IsOutputOn);

    /// <summary>Minimized view: per channel only the setpoints (output off) or the actual values (output on).</summary>
    public bool IsCompact
    {
        get => Settings.Compact;
        set
        {
            if (Settings.Compact == value) return;
            Settings.Compact = value;
            OnPropertyChanged();
            Main.RequestSave();
        }
    }

    /// <summary>Undocked: the supply is shown in its own always-on-top window (the main list shows a placeholder).</summary>
    public bool IsFloating
    {
        get => Settings.Floating;
        set
        {
            if (Settings.Floating == value) return;
            Settings.Floating = value;
            OnPropertyChanged();
            Main.RequestSave();
        }
    }

    // ------------------------------------------------------------------------------------------ presets

    private List<PresetChannelState>? _pendingInstrument;
    private string _pendingModel = "";

    /// <summary>Instrument settings from a preset, written to the supply as soon as it is connected.</summary>
    public void SetPendingInstrumentSettings(List<PresetChannelState> states, string model)
    {
        _pendingInstrument = states.Count > 0 ? states : null;
        _pendingModel = model ?? "";
    }

    public bool HasPendingInstrumentSettings => _pendingInstrument != null;

    /// <summary>Reads setpoints and OVP/OCP of every channel (empty list when not connected).</summary>
    public async Task<List<PresetChannelState>> CaptureInstrumentAsync()
    {
        var list = new List<PresetChannelState>();
        if (!IsConnected)
        {
            if (_pendingInstrument != null) list.AddRange(_pendingInstrument); // loaded from a preset, never connected since
            return list;
        }
        foreach (var ch in Channels.ToArray())
        {
            var sp = await QueryAsync(d => d.ReadSetpointsAsync(ch.Index), $"{ch.ScpiName} read setpoints");
            var pr = await QueryAsync(d => d.ReadProtectionAsync(ch.Index), $"{ch.ScpiName} read OVP/OCP");
            list.Add(new PresetChannelState
            {
                Index = ch.Index,
                Voltage = PresetChannelState.Known(sp?.Voltage ?? ch.VoltageSet),
                Current = PresetChannelState.Known(sp?.Current ?? ch.CurrentSet),
                OvpValue = pr != null ? PresetChannelState.Known(pr.OvpValue) : null,
                OvpEnabled = pr?.OvpEnabled,
                OcpValue = pr != null ? PresetChannelState.Known(pr.OcpValue) : null,
                OcpEnabled = pr?.OcpEnabled,
            });
        }
        return list;
    }

    /// <summary>Switches every output off immediately (no delays) and waits for it. Returns false if this failed.</summary>
    public async Task<bool> SwitchAllOffNowAsync()
    {
        foreach (var c in Channels) c.CancelPending();
        var dev = _device;
        if (dev == null || !IsConnected) return true;
        try
        {
            await Task.Run(async () =>
            {
                foreach (var ch in Channels.ToArray())
                {
                    await dev.SetOutputAsync(ch.Index, false);
                    if (await dev.ReadOutputAsync(ch.Index))
                        throw new ScpiException($"{ch.ScpiName} did not switch off.");
                    ch.PushStatus(false, null, null);
                }
            });
            UpdateUi();
            return true;
        }
        catch (Exception ex)
        {
            Main.ReportError($"{Address}: switching off failed: {ex.Message}");
            return false;
        }
    }

    private async Task ApplyPendingInstrumentAsync(Dp800Device dev, CancellationToken ct)
    {
        var states = _pendingInstrument;
        if (states == null) return;
        var model = dev.Identity?.Model ?? "";
        if (_pendingModel.Length > 0 && !string.Equals(_pendingModel, model, StringComparison.OrdinalIgnoreCase))
        {
            _pendingInstrument = null;
            Main.ReportError($"{Address}: preset was saved for a {_pendingModel}, but a {model} is connected – setpoints and OVP/OCP were not written.");
            return;
        }
        int written = 0;
        foreach (var s in states)
        {
            if (s.Index < 1 || s.Index > dev.ChannelCount) continue;
            // output is off (switched off before loading); protection first, then the setpoints
            if (s.OvpValue is { } ovp && s.OcpValue is { } ocp && s.OvpEnabled is { } ovpOn && s.OcpEnabled is { } ocpOn)
                await dev.SetProtectionAsync(s.Index, new ProtectionSettings(ovp, ovpOn, ocp, ocpOn), ct);
            if (s.Voltage is { } v) await dev.SetVoltageAsync(s.Index, v, ct);
            if (s.Current is { } a) await dev.SetCurrentAsync(s.Index, a, ct);
            written++;
        }
        _pendingInstrument = null; // only after success: a connection error retries on the next connect
        if (written > 0) Main.ReportInfo($"{Address}: preset setpoints and OVP/OCP written to {written} channel(s).");
    }

    // ------------------------------------------------------------------------------------------

    private void EnsureChannels(int count, ModelSpec? spec)
    {
        while (Channels.Count > count) Channels.RemoveAt(Channels.Count - 1);
        for (int i = Channels.Count + 1; i <= count; i++)
        {
            var cs = Settings.Channels.FirstOrDefault(c => c.Index == i);
            if (cs == null)
            {
                cs = new ChannelSettings { Index = i };
                Settings.Channels.Add(cs);
            }
            if (string.IsNullOrEmpty(cs.Color)) cs.Color = Main.NextChannelColor();
            var ch = new ChannelViewModel(this, i, cs, spec != null && i <= spec.Channels.Count ? spec.Channels[i - 1] : null);
            Channels.Add(ch);
            Main.RegisterChannel(ch);
        }
    }

    public void Connect()
    {
        if (_pollTask != null && !_pollTask.IsCompleted) return;
        var timeout = TimeSpan.FromMilliseconds(Math.Max(500, Main.Settings.CommandTimeoutMs));
        IScpiTransport transport = IsSimulated
            ? new SimulatedDp800Transport(Host) { Timeout = timeout }
            : IsSerial
                ? new ScpiSerialTransport(Host, Port) { Timeout = timeout }
                : new ScpiTcpTransport(Host, Port) { Timeout = timeout };
        _device?.Dispose();
        _device = new Dp800Device(transport);
        _pollCts = new CancellationTokenSource();
        State = ConnectionState.Connecting;
        StatusText = "Connecting…";
        Settings.AutoConnect = true;
        var ct = _pollCts.Token;
        var dev = _device;
        _pollTask = Task.Run(() => PollLoopAsync(dev, ct));
    }

    public async Task DisconnectAsync(bool userRequest, bool goLocal = true)
    {
        if (userRequest) Settings.AutoConnect = false;
        foreach (var c in Channels) c.CancelPending();
        _pollCts?.Cancel();
        if (_pollTask != null) { try { await _pollTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
        if (_device != null)
        {
            if (goLocal && Main.Settings.ReturnToLocalOnExit) await _device.GoLocalAsync();
            _device.Disconnect();
        }
        foreach (var c in Channels) c.MarkDisconnected();
        State = ConnectionState.Disconnected;
        StatusText = "Disconnected";
        RateText = "";
        Main.RequestSave();
    }

    private void Ui(Action a) => Application.Current?.Dispatcher.Invoke(a);

    private async Task PollLoopAsync(Dp800Device dev, CancellationToken ct)
    {
        var lastStatus = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!dev.IsConnected)
                {
                    Ui(() => { State = ConnectionState.Connecting; StatusText = _quietReconnect ? "Reconnecting…" : "Connecting…"; });
                    await dev.ConnectAsync(ct);
                    await OnConnectedAsync(dev, ct);
                    _quietReconnect = false;
                    _healthy = true;
                    lastStatus = DateTime.Now;
                    _cycleCount = 0;
                    _rateWatch.Restart();
                }

                // ---- fast part: measurements + CV/CC mode of every channel ----
                var channels = Channels.ToArray();
                var now = DateTime.Now;
                long cycleStart = Stopwatch.GetTimestamp();
                foreach (var ch in channels)
                {
                    var m = await dev.MeasureAsync(ch.Index, ct);
                    var mode = await dev.ReadModeAsync(ch.Index, ct);
                    var t = DateTime.Now;
                    ch.PushMeasurement(m, mode);
                    Main.OnSample(ch, t, m, mode);
                }

                // ---- slow part: output state, setpoints and protection trips (front panel changes) ----
                if (_forceStatus || (now - lastStatus).TotalMilliseconds >= Main.Settings.StatusIntervalMs)
                {
                    _forceStatus = false;
                    lastStatus = now;
                    foreach (var ch in channels)
                    {
                        var output = await dev.ReadOutputAsync(ch.Index, ct);
                        var sp = await dev.ReadSetpointsAsync(ch.Index, ct);
                        var trip = await dev.ReadTripAsync(ch.Index, ct);
                        ch.PushStatus(output, sp, trip);
                    }
                }

                // a polling cycle normally takes ~0.1 s: report stalls, they explain gaps in the trend
                double cycleMs = Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
                if (cycleMs > SlowCycleMs)
                    Main.ReportInfo($"{Address}: slow response – polling cycle took {cycleMs / 1000:0.0} s (network or instrument pause)");

                _cycleCount++;
                if (_rateWatch.ElapsedMilliseconds >= 2000)
                {
                    double rate = _cycleCount * 1000.0 / _rateWatch.ElapsedMilliseconds;
                    _cycleCount = 0;
                    _rateWatch.Restart();
                    Ui(() => RateText = $"{rate:0.0} S/s");
                }

                int pause = Main.Settings.PollIntervalMs;
                if (pause > 0) await Task.Delay(pause, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                dev.Disconnect();
                var msg = ex is ScpiException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";

                // A single time-out on a working connection is usually a short network / instrument pause:
                // reconnect straight away (the socket must be renewed – a late answer would mix up the responses)
                // and only show an error when that does not work either.
                if (_healthy && ex is ScpiException)
                {
                    _healthy = false;
                    _quietReconnect = true;
                    int n = ++_hiccups;
                    Main.ReportInfo($"{Address}: {msg.TrimEnd('.')} – reconnected immediately ({n}× this session)");
                    continue;
                }
                _healthy = false;
                _quietReconnect = false;
                foreach (var c in Channels.ToArray()) c.MarkDisconnected();
                Ui(() =>
                {
                    State = ConnectionState.Error;
                    StatusText = "Error – retrying in 3 s: " + msg;
                    RateText = "";
                    if (msg != _lastError) Main.ReportError($"{Address}: {msg}"); // do not flood the log while unreachable
                    _lastError = msg;
                });
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Read identity and the settings that are configured in the power supply (setpoints, outputs, protection).</summary>
    private async Task OnConnectedAsync(Dp800Device dev, CancellationToken ct)
    {
        var id = dev.Identity!;
        var spec = dev.Spec!;
        Ui(() =>
        {
            Identity = id;
            Settings.Model = id.Model;
            Settings.Serial = id.SerialNumber;
            Settings.ChannelCount = spec.Channels.Count;
            EnsureChannels(spec.Channels.Count, spec);
            OnPropertyChanged(nameof(Model));
            OnPropertyChanged(nameof(Serial));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Identity));
            foreach (var c in Channels) c.RefreshLabels();
            Main.RequestSave();
        });

        await ApplyPendingInstrumentAsync(dev, ct);

        foreach (var ch in Channels.ToArray())
        {
            var output = await dev.ReadOutputAsync(ch.Index, ct);
            var sp = await dev.ReadSetpointsAsync(ch.Index, ct);
            var trip = await dev.ReadTripAsync(ch.Index, ct);
            ch.PushStatus(output, sp, trip);
        }

        Ui(() =>
        {
            _lastError = null;
            State = ConnectionState.Connected;
            StatusText = $"Connected  –  S/N {id.SerialNumber}  –  FW {id.FirmwareVersion}";
            if (!_quietReconnect) Main.ReportInfo($"Connected to {id.Model} at {Address}");
        });
    }

    /// <summary>Run a user command on the instrument (off the UI thread) and report errors in the status bar.</summary>
    public async Task RunAsync(Func<Dp800Device, Task> action, string description)
    {
        var dev = _device;
        if (dev == null || !IsConnected)
        {
            Main.ReportError($"{Address} is not connected.");
            return;
        }
        try
        {
            await Task.Run(() => action(dev));
            _forceStatus = true;
        }
        catch (Exception ex)
        {
            Main.ReportError($"{Address} {description} failed: {ex.Message}");
        }
    }

    public async Task<T?> QueryAsync<T>(Func<Dp800Device, Task<T>> action, string description)
    {
        var dev = _device;
        if (dev == null || !IsConnected) return default;
        try { return await Task.Run(() => action(dev)); }
        catch (Exception ex)
        {
            Main.ReportError($"{Address} {description} failed: {ex.Message}");
            return default;
        }
    }

    internal void UpdateUi()
    {
        foreach (var c in Channels) c.UpdateUi();
    }
}
