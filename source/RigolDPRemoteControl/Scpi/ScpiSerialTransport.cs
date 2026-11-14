using System.IO;
using System.IO.Ports;
using System.Text;

namespace RigolDPRemoteControl.Scpi;

/// <summary>
/// SCPI over RS232 (COM port) – needed for the DP700 series, which has no LAN interface; also works for DP800/DP2000.
/// Settings as in the DP700 programming guide: 8 data bits, no parity, 1 stop bit, no flow control, "\n" terminator;
/// the baud rate must match the instrument (default 9600).
/// Uses the managed System.IO.Ports (shipped with the application): no VISA. A USB-RS232 adapter only needs the
/// adapter's own Windows driver, which Windows Update normally installs automatically.
/// </summary>
public sealed class ScpiSerialTransport : IScpiTransport
{
    private readonly string _portName;
    private readonly int _baud;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SerialPort? _port;

    public ScpiSerialTransport(string portName, int baud)
    {
        _portName = portName.ToUpperInvariant();
        _baud = baud <= 0 ? 9600 : baud;
    }

    public string Address => $"{_portName} @ {_baud} baud";
    public bool IsConnected => _port?.IsOpen == true;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Pause between commands: the DP700 needs up to 100 ms to process a command.</summary>
    public int MinCommandGapMs { get; set; } = 5;

    public static bool IsSerialPortName(string host)
        => host.Length >= 4 && host.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && host[3..].All(char.IsDigit);

    public async Task ConnectAsync(CancellationToken ct)
    {
        Close();
        await Task.Run(() =>
        {
            var p = new SerialPort(_portName, _baud, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                NewLine = "\n",
                Encoding = Encoding.ASCII,
                ReadTimeout = (int)Math.Max(500, Timeout.TotalMilliseconds),
                WriteTimeout = (int)Math.Max(500, Timeout.TotalMilliseconds),
                DtrEnable = true,
                RtsEnable = true,
            };
            try
            {
                p.Open();
                p.DiscardInBuffer();
                p.DiscardOutBuffer();
            }
            catch (Exception ex)
            {
                p.Dispose();
                throw new ScpiException($"Cannot open {_portName}: {ex.Message}", ex);
            }
            _port = p;
        }, ct).ConfigureAwait(false);
    }

    public async Task WriteAsync(string command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await Task.Run(() => Send(command), ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<string> QueryAsync(string command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var p = _port ?? throw new ScpiException($"{_portName} is not open.");
                try { p.DiscardInBuffer(); } catch { }
                Send(command);
                try
                {
                    var line = p.ReadLine();
                    return line.TrimEnd('\r').Trim();
                }
                catch (TimeoutException)
                {
                    // a late answer is discarded before the next query, so the port can stay open
                    throw new ScpiException($"Timeout waiting for response from {_portName} (check cable, baud rate {_baud}).");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    Close();
                    throw new ScpiException($"Read from {_portName} failed: {ex.Message}", ex);
                }
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private long _lastIo;

    private void Send(string command)
    {
        var p = _port ?? throw new ScpiException($"{_portName} is not open.");
        var gap = MinCommandGapMs - (Environment.TickCount64 - _lastIo);
        if (gap > 0) Thread.Sleep((int)gap);
        try { p.Write(command + "\n"); }
        catch (Exception ex)
        {
            Close();
            throw new ScpiException($"Write to {_portName} failed: {ex.Message}", ex);
        }
        finally { _lastIo = Environment.TickCount64; }
    }

    public void Close()
    {
        var p = _port;
        _port = null;
        try { p?.Close(); } catch { }
        try { p?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        Close();
        _gate.Dispose();
    }
}
