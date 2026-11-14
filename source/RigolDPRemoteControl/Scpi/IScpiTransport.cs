namespace RigolDPRemoteControl.Scpi;

/// <summary>A line-based SCPI transport (one command per line, responses terminated by '\n').</summary>
public interface IScpiTransport : IDisposable
{
    string Address { get; }
    bool IsConnected { get; }
    TimeSpan Timeout { get; set; }

    Task ConnectAsync(CancellationToken ct);
    Task WriteAsync(string command, CancellationToken ct = default);
    Task<string> QueryAsync(string command, CancellationToken ct = default);
    void Close();
}

public class ScpiException : Exception
{
    public ScpiException(string message, Exception? inner = null) : base(message, inner) { }
}
