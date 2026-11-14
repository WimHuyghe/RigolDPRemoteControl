using RigolDPRemoteControl.Infrastructure;
using RigolDPRemoteControl.Scpi;

namespace RigolDPRemoteControl.Devices;

public sealed class DeviceIdentity
{
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string FirmwareVersion { get; init; } = "";
    public string MacAddress { get; set; } = "";
    public string ScpiVersion { get; set; } = "";
    public string RawIdn { get; init; } = "";
    public string Description { get; set; } = "";
}

public sealed record Measurement(double Voltage, double Current, double Power);
public sealed record Setpoints(double Voltage, double Current, double RatedVoltage, double RatedCurrent);
public sealed record ProtectionSettings(double OvpValue, bool OvpEnabled, double OcpValue, bool OcpEnabled);

/// <summary>
/// Rigol DP SCPI command set (DP700 / DP800 / DP900 / DP2000 programming guides – the families share the same commands:
/// :APPL?, :MEAS:ALL?, :OUTP, :OUTP:MODE?, :SOURn:VOLT/CURR, :OUTP:OVP/OCP…). Single-channel models omit the channel argument.
/// Thread-safe: the underlying transport serializes all I/O.
/// </summary>
public sealed class Dp800Device : IDisposable
{
    private readonly IScpiTransport _io;

    public Dp800Device(IScpiTransport transport)
    {
        _io = transport;
    }

    public IScpiTransport Transport => _io;
    public DeviceIdentity? Identity { get; private set; }
    public ModelSpec? Spec { get; private set; }
    public int ChannelCount => Spec?.Channels.Count ?? 0;
    public bool IsSingleChannel => ChannelCount == 1;
    public bool IsConnected => _io.IsConnected;

    // ----- connection / identification -------------------------------------------------------

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _io.ConnectAsync(ct).ConfigureAwait(false);
        var idn = await _io.QueryAsync("*IDN?", ct).ConfigureAwait(false);
        // e.g. "RIGOL TECHNOLOGIES,DP832,DP8C1234567890,00.01.14"
        var parts = idn.Split(',');
        if (parts.Length < 2) throw new ScpiException($"Unexpected *IDN? response: '{idn}'");
        var id = new DeviceIdentity
        {
            RawIdn = idn,
            Manufacturer = parts[0].Trim(),
            Model = parts[1].Trim(),
            SerialNumber = parts.Length > 2 ? parts[2].Trim() : "",
            FirmwareVersion = parts.Length > 3 ? parts[3].Trim() : "",
        };
        if (!ModelCatalog.IsSupportedModel(id.Model))
            throw new ScpiException($"{id.Manufacturer} {id.Model} is not a supported Rigol DP700/DP800/DP900/DP2000 power supply.");

        Spec = ModelCatalog.Resolve(id.Model);
        id.Description = Spec.Description;
        Identity = id;

        // DP700 has no LAN interface (RS232 only): do not ask for a MAC address (it would only time out)
        id.MacAddress = Spec.HasLan
            ? await TryQueryAsync(":SYST:COMM:LAN:MAC?", ct).ConfigureAwait(false) ?? ""
            : "– (no LAN interface)";
        id.ScpiVersion = await TryQueryAsync(":SYST:VERS?", ct).ConfigureAwait(false) ?? "";
    }

    public void Disconnect() => _io.Close();

    /// <summary>Return the front panel to local control (unlocks the keys).</summary>
    public async Task GoLocalAsync()
    {
        if (!_io.IsConnected) return;
        try { await _io.WriteAsync(":SYST:LOC").ConfigureAwait(false); } catch { }
    }

    private async Task<string?> TryQueryAsync(string cmd, CancellationToken ct)
    {
        try { return await _io.QueryAsync(cmd, ct).ConfigureAwait(false); }
        catch (ScpiException)
        {
            // Some optional commands are not supported on all firmware versions; reconnect if the socket was dropped
            if (!_io.IsConnected) await _io.ConnectAsync(ct).ConfigureAwait(false);
            return null;
        }
    }

    // ----- helpers ---------------------------------------------------------------------------

    /// <summary>Channel argument for query commands ("CH2" or empty for single-channel models).</summary>
    private string Q(int ch) => IsSingleChannel ? "" : $" CH{ch}";
    /// <summary>Channel prefix for write commands with optional channel ("CH2," or empty).</summary>
    private string W(int ch) => IsSingleChannel ? "" : $"CH{ch},";

    private static double ParseNum(string s)
        => Num.TryParseScpi(s, out var v) ? v : double.NaN;

    private static (double v, double a) ParseRating(string s)
    {
        // "30V/3A" or "-30V/2A" or "20V/10A"
        var p = s.Trim().Trim('"').Split('/');
        if (p.Length != 2) return (double.NaN, double.NaN);
        return (ParseNum(p[0].TrimEnd('V', 'v')), ParseNum(p[1].TrimEnd('A', 'a')));
    }

    // ----- read ------------------------------------------------------------------------------

    public async Task<Measurement> MeasureAsync(int ch, CancellationToken ct = default)
    {
        var r = await _io.QueryAsync(":MEAS:ALL?" + Q(ch), ct).ConfigureAwait(false);
        var p = r.Split(',');
        if (p.Length < 3) throw new ScpiException($"Unexpected :MEAS:ALL? response '{r}'");
        return new Measurement(ParseNum(p[0]), ParseNum(p[1]), ParseNum(p[2]));
    }

    /// <summary>Returns "CV", "CC" or "UR".</summary>
    public async Task<string> ReadModeAsync(int ch, CancellationToken ct = default)
        => (await _io.QueryAsync(":OUTP:MODE?" + Q(ch), ct).ConfigureAwait(false)).Trim().ToUpperInvariant();

    public async Task<bool> ReadOutputAsync(int ch, CancellationToken ct = default)
    {
        var r = (await _io.QueryAsync(":OUTP?" + Q(ch), ct).ConfigureAwait(false)).Trim().ToUpperInvariant();
        return r == "ON" || r == "1";
    }

    public async Task<Setpoints> ReadSetpointsAsync(int ch, CancellationToken ct = default)
    {
        var spec = Spec?.Channels[Math.Clamp(ch - 1, 0, ChannelCount - 1)];
        double ratedV = spec?.RatedVoltage ?? double.NaN, ratedA = spec?.RatedCurrent ?? double.NaN;

        if (IsSingleChannel)
        {
            // ":APPL?" -> "5.000,1.0000"; ":OUTP:RANG?" -> "20V/10A"
            var r = await _io.QueryAsync(":APPL?", ct).ConfigureAwait(false);
            var p = r.Split(',');
            if (p.Length < 2) throw new ScpiException($"Unexpected :APPL? response '{r}'");
            double v = ParseNum(p[^2]);
            double a = ParseNum(p[^1]);
            if (Spec?.SingleChannelWithRanges == true) // DP811/DP813 only; the DP700 has a single fixed range
            {
                var rng = await _io.QueryAsync(":OUTP:RANG?", ct).ConfigureAwait(false);
                var (rv, ra) = ParseRating(rng);
                if (!double.IsNaN(rv)) { ratedV = rv; ratedA = ra; }
            }
            return new Setpoints(v, a, ratedV, ratedA);
        }
        else
        {
            // ":APPL? CH1" -> "CH1:30V/3A,5.000,1.0000"
            var r = await _io.QueryAsync($":APPL? CH{ch}", ct).ConfigureAwait(false);
            var p = r.Trim('"').Split(',');
            if (p.Length < 3) throw new ScpiException($"Unexpected :APPL? response '{r}'");
            var head = p[0];
            int colon = head.IndexOf(':');
            if (colon >= 0)
            {
                var (rv, ra) = ParseRating(head[(colon + 1)..]);
                if (!double.IsNaN(rv)) { ratedV = rv; ratedA = ra; }
            }
            return new Setpoints(ParseNum(p[1]), ParseNum(p[2]), ratedV, ratedA);
        }
    }

    public async Task<ProtectionSettings> ReadProtectionAsync(int ch, CancellationToken ct = default)
    {
        var ovp = ParseNum(await _io.QueryAsync(":OUTP:OVP:VAL?" + Q(ch), ct).ConfigureAwait(false));
        var ovpOn = IsOn(await _io.QueryAsync(":OUTP:OVP?" + Q(ch), ct).ConfigureAwait(false));
        var ocp = ParseNum(await _io.QueryAsync(":OUTP:OCP:VAL?" + Q(ch), ct).ConfigureAwait(false));
        var ocpOn = IsOn(await _io.QueryAsync(":OUTP:OCP?" + Q(ch), ct).ConfigureAwait(false));
        return new ProtectionSettings(ovp, ovpOn, ocp, ocpOn);
    }

    /// <summary>Returns whether OVP / OCP has tripped on the channel.</summary>
    public async Task<(bool ovp, bool ocp)> ReadTripAsync(int ch, CancellationToken ct = default)
    {
        var ovp = await _io.QueryAsync(":OUTP:OVP:QUES?" + Q(ch), ct).ConfigureAwait(false);
        var ocp = await _io.QueryAsync(":OUTP:OCP:QUES?" + Q(ch), ct).ConfigureAwait(false);
        return (IsYes(ovp), IsYes(ocp));
    }

    private static bool IsOn(string s) { s = s.Trim().ToUpperInvariant(); return s == "ON" || s == "1"; }
    private static bool IsYes(string s) { s = s.Trim().ToUpperInvariant(); return s == "YES" || s == "1"; }

    // ----- write -----------------------------------------------------------------------------

    public Task SetVoltageAsync(int ch, double volts, CancellationToken ct = default)
        => _io.WriteAsync(IsSingleChannel ? $":VOLT {Num.Scpi(volts)}" : $":SOUR{ch}:VOLT {Num.Scpi(volts)}", ct);

    public Task SetCurrentAsync(int ch, double amps, CancellationToken ct = default)
        => _io.WriteAsync(IsSingleChannel ? $":CURR {Num.Scpi(amps)}" : $":SOUR{ch}:CURR {Num.Scpi(amps)}", ct);

    public Task SetOutputAsync(int ch, bool on, CancellationToken ct = default)
        => _io.WriteAsync($":OUTP {W(ch)}{(on ? "ON" : "OFF")}", ct);

    public async Task SetProtectionAsync(int ch, ProtectionSettings p, CancellationToken ct = default)
    {
        await _io.WriteAsync($":OUTP:OVP:VAL {W(ch)}{Num.Scpi(p.OvpValue)}", ct).ConfigureAwait(false);
        await _io.WriteAsync($":OUTP:OVP {W(ch)}{(p.OvpEnabled ? "ON" : "OFF")}", ct).ConfigureAwait(false);
        await _io.WriteAsync($":OUTP:OCP:VAL {W(ch)}{Num.Scpi(p.OcpValue)}", ct).ConfigureAwait(false);
        await _io.WriteAsync($":OUTP:OCP {W(ch)}{(p.OcpEnabled ? "ON" : "OFF")}", ct).ConfigureAwait(false);
    }

    /// <summary>Switches only the OVP / OCP enable states (levels are not changed).</summary>
    public async Task SetProtectionStateAsync(int ch, bool ovpOn, bool ocpOn, CancellationToken ct = default)
    {
        await _io.WriteAsync($":OUTP:OVP {W(ch)}{(ovpOn ? "ON" : "OFF")}", ct).ConfigureAwait(false);
        await _io.WriteAsync($":OUTP:OCP {W(ch)}{(ocpOn ? "ON" : "OFF")}", ct).ConfigureAwait(false);
    }

    public async Task ClearTripAsync(int ch, CancellationToken ct = default)
    {
        await _io.WriteAsync(":OUTP:OVP:CLEAR" + Q(ch), ct).ConfigureAwait(false);
        await _io.WriteAsync(":OUTP:OCP:CLEAR" + Q(ch), ct).ConfigureAwait(false);
    }

    /// <summary>Reads the next entry of the instrument error queue ("0,\"No error\"" when empty).</summary>
    public Task<string> ReadErrorAsync(CancellationToken ct = default) => _io.QueryAsync(":SYST:ERR?", ct);

    public void Dispose() => _io.Dispose();
}
