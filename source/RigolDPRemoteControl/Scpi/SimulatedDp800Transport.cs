using System.Text.RegularExpressions;
using RigolDPRemoteControl.Devices;
using RigolDPRemoteControl.Infrastructure;

namespace RigolDPRemoteControl.Scpi;

/// <summary>
/// In-process simulator of a DP800 instrument, used when the host name starts with "sim" (e.g. "sim", "sim-dp831", "sim-dp811-2").
/// Lets you explore the application without hardware. Each channel drives a slowly varying resistive load.
/// </summary>
public sealed partial class SimulatedDp800Transport : IScpiTransport
{
    private sealed class SimChannel
    {
        public double VSet, ISet, Ovp, Ocp, LoadOhm, Phase;
        public bool Out, OvpOn = true, OcpOn, OvpTrip, OcpTrip;
        public double RatedV, RatedA;
    }

    private readonly object _lock = new();
    private readonly string _host;
    private readonly ModelSpec _spec;
    private readonly SimChannel[] _ch;
    private readonly string _serial;
    private readonly Random _rnd;
    private readonly DateTime _start = DateTime.UtcNow;
    private bool _connected;

    public SimulatedDp800Transport(string host)
    {
        _host = host;
        var m = ModelRegex().Match(host);
        var model = m.Success ? m.Value.ToUpperInvariant() : "DP832";
        if (!char.IsLetter(model[^1]) && ModelCatalog.All.Any(x => x.Model == model + "A")) model += "A";
        _spec = ModelCatalog.Resolve(model);
        int seed = host.Aggregate(17, (h, c) => h * 31 + c);
        _rnd = new Random(seed);
        _serial = "DPSIM" + Math.Abs(seed % 100000000).ToString("D8");
        _ch = _spec.Channels.Select((c, i) => new SimChannel
        {
            RatedV = _spec.SingleChannelWithRanges ? (model.StartsWith("DP811") ? 20 : 8) : c.RatedVoltage,
            RatedA = _spec.SingleChannelWithRanges ? (model.StartsWith("DP811") ? 10 : 20) : c.RatedCurrent,
            VSet = Math.Round(Math.Abs(c.RatedVoltage) * (0.1 + 0.15 * i), 2) * Math.Sign(c.RatedVoltage),
            ISet = Math.Round(c.RatedCurrent * 0.5, 3),
            Ovp = c.OvpMax,
            Ocp = c.OcpMax,
            LoadOhm = 5 + 10 * i + _rnd.NextDouble() * 5,
            Phase = _rnd.NextDouble() * 6,
        }).ToArray();
    }

    [GeneratedRegex(@"dp(2\d\d\d|[789]\d\d[aue]?)", RegexOptions.IgnoreCase)]
    private static partial Regex ModelRegex();

    public string Address => _host + " (simulated)";
    public bool IsConnected => _connected;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    public async Task ConnectAsync(CancellationToken ct)
    {
        await Task.Delay(150, ct);
        _connected = true;
    }

    public async Task WriteAsync(string command, CancellationToken ct = default)
    {
        if (!_connected) throw new ScpiException("Not connected (simulator).");
        await Task.Delay(1, ct);
        lock (_lock) Process(command);
    }

    public async Task<string> QueryAsync(string command, CancellationToken ct = default)
    {
        if (!_connected) throw new ScpiException("Not connected (simulator).");
        await Task.Delay(4, ct);
        await SimulateStallAsync(ct);
        lock (_lock) return Process(command) ?? throw new ScpiException($"Simulator: no response to '{command}'");
    }

    // Test hook: RIGOLDP_SIM_STALL="probability,milliseconds" makes random queries answer late, like a DP800 on a lossy network.
    private static readonly (double p, int ms) Stall = ParseStall(Environment.GetEnvironmentVariable("RIGOLDP_SIM_STALL"));
    private static (double, int) ParseStall(string? s)
    {
        var f = (s ?? "").Split(',');
        return f.Length == 2 && double.TryParse(f[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)
               && int.TryParse(f[1], out var ms) ? (p, ms) : (0, 0);
    }

    private async Task SimulateStallAsync(CancellationToken ct)
    {
        if (Stall.p <= 0 || Random.Shared.NextDouble() >= Stall.p) return;
        if (Stall.ms < Timeout.TotalMilliseconds) { await Task.Delay(Stall.ms, ct); return; }
        await Task.Delay(Timeout, ct);
        _connected = false; // the real transport drops the socket after a time-out
        throw new ScpiException($"Timeout waiting for response from {Address}.");
    }

    public void Close() => _connected = false;
    public void Dispose() => Close();

    // ------------------------------------------------------------------------------------------

    private SimChannel Ch(string? arg)
    {
        if (arg != null)
        {
            var m = Regex.Match(arg, @"CH(\d)");
            if (m.Success) return _ch[Math.Clamp(int.Parse(m.Groups[1].Value) - 1, 0, _ch.Length - 1)];
        }
        return _ch[0];
    }

    private (SimChannel ch, string value) ChVal(string args)
    {
        // "CH2,ON" or "ON"
        var parts = args.Split(',', 2);
        return parts.Length == 2 ? (Ch(parts[0]), parts[1].Trim()) : (_ch[0], parts[0].Trim());
    }

    private (double v, double i, string mode) Output(SimChannel c, int idx)
    {
        if (!c.Out) return (0, 0, "CV");
        double t = (DateTime.UtcNow - _start).TotalSeconds;
        double r = c.LoadOhm * (1 + 0.35 * Math.Sin(t / (8 + idx * 3) + c.Phase));
        double vset = Math.Abs(c.VSet);
        double v, i; string mode;
        if (vset / r > c.ISet) { i = c.ISet; v = i * r; mode = "CC"; }
        else { v = vset; i = v / r; mode = "CV"; }
        v *= 1 + (_rnd.NextDouble() - 0.5) * 0.0004;
        i *= 1 + (_rnd.NextDouble() - 0.5) * 0.001;
        if (c.OcpOn && i > c.Ocp) { c.Out = false; c.OcpTrip = true; return (0, 0, "CV"); }
        return (v * Math.Sign(c.VSet == 0 ? 1 : c.VSet), i, mode);
    }

    private string? Process(string raw)
    {
        var cmd = raw.Trim().ToUpperInvariant();
        string head = cmd, args = "";
        int sp = cmd.IndexOf(' ');
        if (sp > 0) { head = cmd[..sp]; args = cmd[(sp + 1)..].Trim(); }
        head = head.TrimStart(':');

        var srcMatch = Regex.Match(head, @"^SOUR(\d)?:");
        SimChannel? src = null;
        if (srcMatch.Success)
        {
            src = srcMatch.Groups[1].Success ? _ch[Math.Clamp(int.Parse(srcMatch.Groups[1].Value) - 1, 0, _ch.Length - 1)] : _ch[0];
            head = head[srcMatch.Length..];
        }

        switch (head)
        {
            case "*IDN?": return $"RIGOL TECHNOLOGIES,{_spec.Model},{_serial},00.01.16";
            case "SYST:COMM:LAN:MAC?":
                if (!_spec.HasLan) return null; // DP700: unknown command, no answer
                return $"00-19-AF-{_serial[^6..^4]}-{_serial[^4..^2]}-{_serial[^2..]}";
            case "OUTP:RANG?" when !_spec.SingleChannelWithRanges: return null; // only DP811/DP813 know this
            case "SYST:VERS?": return "1999.0";
            case "SYST:ERR?": return "0,\"No error\"";
            case "SYST:LOC": case "SYST:REM": case "*CLS": return null;
            case "MEAS:ALL?":
                {
                    var c = Ch(args); var (v, i, _) = Output(c, Array.IndexOf(_ch, c));
                    return $"{v.ToString("0.0000", Num.Inv)},{i.ToString("0.0000", Num.Inv)},{(Math.Abs(v) * i).ToString("0.000", Num.Inv)}";
                }
            case "OUTP:MODE?": case "OUTP:CVCC?":
                {
                    var c = Ch(args); return Output(c, Array.IndexOf(_ch, c)).mode;
                }
            case "OUTP?": case "OUTP:STAT?": return Ch(args).Out ? "ON" : "OFF";
            case "OUTP": case "OUTP:STAT":
                {
                    var (c, val) = ChVal(args);
                    bool on = val is "ON" or "1";
                    if (on && (c.OvpTrip || c.OcpTrip)) return null;
                    if (on && c.OvpOn && Math.Abs(c.VSet) > Math.Abs(c.Ovp)) { c.OvpTrip = true; c.Out = false; return null; }
                    c.Out = on; return null;
                }
            case "APPL?":
                {
                    if (_ch.Length == 1) return $"{Num.Scpi(_ch[0].VSet)},{_ch[0].ISet.ToString("0.0000", Num.Inv)}";
                    var c = Ch(args); int n = Array.IndexOf(_ch, c) + 1;
                    return $"CH{n}:{c.RatedV:0}V/{c.RatedA:0}A,{c.VSet.ToString("0.000", Num.Inv)},{c.ISet.ToString("0.0000", Num.Inv)}";
                }
            case "OUTP:RANG?": return $"{_ch[0].RatedV:0}V/{_ch[0].RatedA:0}A";
            case "VOLT": case "VOLT:LEV":
                {
                    var c = src ?? _ch[0];
                    if (Num.TryParseScpi(args, out var v)) c.VSet = Math.Sign(c.RatedV) * Math.Min(Math.Abs(v), Math.Abs(c.RatedV) * 1.07);
                    return null;
                }
            case "CURR": case "CURR:LEV":
                {
                    var c = src ?? _ch[0];
                    if (Num.TryParseScpi(args, out var v)) c.ISet = Math.Clamp(v, 0, c.RatedA * 1.07);
                    return null;
                }
            case "OUTP:OVP:VAL?": return Num.Scpi(Ch(args).Ovp);
            case "OUTP:OCP:VAL?": return Num.Scpi(Ch(args).Ocp);
            case "OUTP:OVP?": case "OUTP:OVP:STAT?": return Ch(args).OvpOn ? "ON" : "OFF";
            case "OUTP:OCP?": case "OUTP:OCP:STAT?": return Ch(args).OcpOn ? "ON" : "OFF";
            case "OUTP:OVP:QUES?": case "OUTP:OVP:ALAR?": return Ch(args).OvpTrip ? "YES" : "NO";
            case "OUTP:OCP:QUES?": case "OUTP:OCP:ALAR?": return Ch(args).OcpTrip ? "YES" : "NO";
            case "OUTP:OVP:CLEAR": Ch(args).OvpTrip = false; return null;
            case "OUTP:OCP:CLEAR": Ch(args).OcpTrip = false; return null;
            case "OUTP:OVP:VAL": { var (c, v) = ChVal(args); if (Num.TryParseScpi(v, out var d)) c.Ovp = d; return null; }
            case "OUTP:OCP:VAL": { var (c, v) = ChVal(args); if (Num.TryParseScpi(v, out var d)) c.Ocp = d; return null; }
            case "OUTP:OVP": case "OUTP:OVP:STAT": { var (c, v) = ChVal(args); c.OvpOn = v is "ON" or "1"; return null; }
            case "OUTP:OCP": case "OUTP:OCP:STAT": { var (c, v) = ChVal(args); c.OcpOn = v is "ON" or "1"; return null; }
        }
        return cmd.EndsWith('?') ? "0" : null;
    }
}
