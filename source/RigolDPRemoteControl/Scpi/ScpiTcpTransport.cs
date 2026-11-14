using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace RigolDPRemoteControl.Scpi;

/// <summary>
/// SCPI over a raw TCP socket ("SOCKET" resource, port 5555 on DP800 instruments).
/// No VISA / NI drivers are needed: this is a plain .NET socket.
/// All I/O is serialized by a semaphore so the polling thread and user commands never interleave.
/// </summary>
public sealed class ScpiTcpTransport : IScpiTransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _rxBuf = new byte[4096];
    private readonly StringBuilder _line = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private long _lastIo;

    public ScpiTcpTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public string Address => $"{_host}:{_port}";
    public bool IsConnected => _client?.Connected == true && _stream != null;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Minimum gap between two commands. DP800 firmware can drop commands that arrive back-to-back.</summary>
    public int MinCommandGapMs { get; set; } = 3;

    public async Task ConnectAsync(CancellationToken ct)
    {
        Close();
        var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 8192, SendBufferSize = 8192 };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, Timeout.TotalSeconds)));
        try
        {
            await client.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new ScpiException($"Connection to {Address} timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            client.Dispose();
            throw new ScpiException($"Cannot connect to {Address}: {ex.Message}", ex);
        }
        _client = client;
        _stream = client.GetStream();
        _line.Clear();
    }

    public async Task WriteAsync(string command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendAsync(command, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> QueryAsync(string command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DiscardStale();
            await SendAsync(command, ct).ConfigureAwait(false);
            return await ReadLineAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task SendAsync(string command, CancellationToken ct)
    {
        var stream = _stream ?? throw new ScpiException($"Not connected to {Address}.");
        var gap = MinCommandGapMs - (Stopwatch.GetTimestamp() - _lastIo) * 1000.0 / Stopwatch.Frequency;
        if (gap > 0) await Task.Delay(TimeSpan.FromMilliseconds(gap), ct).ConfigureAwait(false);

        var bytes = Encoding.ASCII.GetBytes(command + "\n");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            await stream.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Close();
            throw new ScpiException($"Write to {Address} failed: {ex.Message}", ex);
        }
        finally
        {
            _lastIo = Stopwatch.GetTimestamp();
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var stream = _stream ?? throw new ScpiException($"Not connected to {Address}.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            while (true)
            {
                // A complete line may already be buffered
                for (int i = 0; i < _line.Length; i++)
                {
                    if (_line[i] == '\n')
                    {
                        var result = _line.ToString(0, i).TrimEnd('\r');
                        _line.Remove(0, i + 1);
                        _lastIo = Stopwatch.GetTimestamp();
                        return result.Trim();
                    }
                }
                int n = await stream.ReadAsync(_rxBuf, cts.Token).ConfigureAwait(false);
                if (n <= 0) throw new ScpiException($"Connection closed by {Address}.");
                _line.Append(Encoding.ASCII.GetString(_rxBuf, 0, n));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A timed-out response might arrive later and desynchronise the stream: drop the connection.
            Close();
            throw new ScpiException($"Timeout waiting for response from {Address}.");
        }
        catch (ScpiException)
        {
            Close();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Close();
            throw new ScpiException($"Read from {Address} failed: {ex.Message}", ex);
        }
    }

    private void DiscardStale()
    {
        try
        {
            _line.Clear();
            var s = _stream;
            while (s != null && s.DataAvailable) s.Read(_rxBuf, 0, _rxBuf.Length);
        }
        catch { /* ignored */ }
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        Close();
        _gate.Dispose();
    }
}
