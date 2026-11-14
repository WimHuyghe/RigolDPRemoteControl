using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using RigolDPRemoteControl.Infrastructure;

namespace RigolDPRemoteControl.Trend;

/// <summary>
/// Trend file format (CSV, one row per channel sample, readable by Excel):
/// <code>
/// # Rigol DP Remote Control trend file
/// # Started,2026-11-08 10:00:00.000
/// # Series,&lt;key&gt;,&lt;label&gt;,&lt;color&gt;
/// Timestamp,Label,SeriesKey,Voltage [V],Current [A],Power [W],Mode
/// 2026-11-08 10:00:00.123,Logic (CH1 192.168.1.10),192.168.1.10:5555/CH1,5.0012,0.1000,0.5001,CV
/// </code>
/// </summary>
public static class TrendFile
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";
    public const string Header = "Timestamp,Label,SeriesKey,Voltage [V],Current [A],Power [W],Mode";

    public static string FormatRow(DateTime t, string label, string key, double v, double i, double p, string mode)
        => string.Join(",",
            t.ToString(TimeFormat, CultureInfo.InvariantCulture),
            Csv.Escape(label), Csv.Escape(key),
            v.ToString("0.#####", CultureInfo.InvariantCulture),
            i.ToString("0.#####", CultureInfo.InvariantCulture),
            p.ToString("0.#####", CultureInfo.InvariantCulture),
            mode);

    public static string SeriesLine(TrendSeries s) => $"# Series,{Csv.Escape(s.Key)},{Csv.Escape(s.Label)},{s.ColorHex}";

    public static TrendDataSet Load(string path)
    {
        var data = new TrendDataSet(false) { SourceFile = path };
        var map = new Dictionary<string, TrendSeries>();
        TrendSeries Get(string key, string label, string? color)
        {
            if (!map.TryGetValue(key, out var s))
            {
                s = new TrendSeries(key, label, color ?? TrendColors.Next(map.Values.Select(x => x.ColorHex)));
                map[key] = s;
                data.Series.Add(s);
            }
            return s;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                var f = Csv.Split(line);
                if (f.Count >= 4 && f[0].Trim() == "# Series")
                {
                    var s = Get(f[1], f[2], f[3]);
                    s.Label = f[2];
                    if (f[3].StartsWith('#')) s.ColorHex = f[3];
                }
                continue;
            }
            if (line.StartsWith("Timestamp", StringComparison.OrdinalIgnoreCase)) continue;

            var c = Csv.Split(line);
            if (c.Count < 6) continue;
            if (!DateTime.TryParseExact(c[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                && !DateTime.TryParse(c[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                continue;
            var ser = Get(c[2], c[1], null);
            if (ser.Label != c[1] && c[1].Length > 0) ser.Label = c[1];
            Num.TryParseScpi(c[3], out var v);
            Num.TryParseScpi(c[4], out var i);
            Num.TryParseScpi(c[5], out var p);
            ser.Add(t.ToOADate(), v, i, p, TrendSeries.ModeCode(c.Count > 6 ? c[6].Trim() : ""));
        }
        return data;
    }

    /// <summary>Export series data (optionally limited to a time range) in the trend file format, merged in time order.</summary>
    public static void Export(string path, IEnumerable<TrendSeries> series, double xMin = double.MinValue, double xMax = double.MaxValue,
        string? note = null)
    {
        var list = series.ToList();
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine("# Rigol DP Remote Control trend file");
        w.WriteLine($"# Exported,{DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture)}");
        if (note != null) w.WriteLine("# " + note.Replace('\n', ' '));
        foreach (var s in list) w.WriteLine(SeriesLine(s));
        w.WriteLine(Header);

        // snapshot the data so sampling threads are not blocked during the file write
        var snaps = new List<(TrendSeries s, double[] t, double[] v, double[] i, double[] p, byte[] m)>();
        foreach (var s in list)
        {
            lock (s.Sync)
            {
                int i0 = Decimator.LowerBound(s.T, xMin);
                int i1 = Decimator.UpperBound(s.T, xMax);
                int n = Math.Max(0, i1 - i0);
                snaps.Add((s, s.T.GetRange(i0, n).ToArray(), s.V.GetRange(i0, n).ToArray(), s.I.GetRange(i0, n).ToArray(),
                    s.P.GetRange(i0, n).ToArray(), s.M.GetRange(i0, n).ToArray()));
            }
        }
        var pq = new PriorityQueue<(int si, int idx), double>();
        for (int k = 0; k < snaps.Count; k++) if (snaps[k].t.Length > 0) pq.Enqueue((k, 0), snaps[k].t[0]);
        while (pq.TryDequeue(out var e, out _))
        {
            var sn = snaps[e.si];
            w.WriteLine(FormatRow(DateTime.FromOADate(sn.t[e.idx]), sn.s.Label, sn.s.Key, sn.v[e.idx], sn.i[e.idx], sn.p[e.idx],
                TrendSeries.ModeText(sn.m[e.idx])));
            if (e.idx + 1 < sn.t.Length) pq.Enqueue((e.si, e.idx + 1), sn.t[e.idx + 1]);
        }
    }
}

/// <summary>
/// Writes live samples to the trend file on a background task (sampling threads only enqueue strings).
/// </summary>
public sealed class TrendRecorder : IDisposable
{
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<string, string> _declared = new();
    private readonly object _fileLock = new();
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public string? CurrentFile { get; private set; }
    public bool IsRecording => _writer != null;
    public string? LastError { get; private set; }

    public void Start(string file, IEnumerable<TrendSeries> knownSeries)
    {
        Stop();
        lock (_fileLock)
        {
            _writer = new StreamWriter(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            CurrentFile = file;
            _declared.Clear();
            _writer.WriteLine("# Rigol DP Remote Control trend file");
            _writer.WriteLine($"# Started,{DateTime.Now.ToString(TrendFile.TimeFormat, CultureInfo.InvariantCulture)}");
            foreach (var s in knownSeries)
            {
                _writer.WriteLine(TrendFile.SeriesLine(s));
                _declared[s.Key] = s.Label + "|" + s.ColorHex;
            }
            _writer.WriteLine(TrendFile.Header);
            _writer.Flush();
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _task = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { }
                FlushQueue();
            }
        });
    }

    public void Record(TrendSeries s, DateTime t, double v, double i, double p, string mode)
    {
        if (_writer == null) return;
        var decl = s.Label + "|" + s.ColorHex;
        if (!_declared.TryGetValue(s.Key, out var d) || d != decl)
        {
            _declared[s.Key] = decl;
            _queue.Enqueue(TrendFile.SeriesLine(s));
        }
        _queue.Enqueue(TrendFile.FormatRow(t, s.Label, s.Key, v, i, p, mode));
    }

    private void FlushQueue()
    {
        lock (_fileLock)
        {
            if (_writer == null) { _queue.Clear(); return; }
            try
            {
                while (_queue.TryDequeue(out var line)) _writer.WriteLine(line);
                _writer.Flush();
                LastError = null;
            }
            catch (Exception ex) { LastError = ex.Message; }
        }
    }

    /// <summary>Copy everything recorded so far to <paramref name="newFile"/> and continue recording into that file.</summary>
    public void SaveAsAndContinue(string newFile)
    {
        lock (_fileLock)
        {
            if (_writer == null || CurrentFile == null) throw new InvalidOperationException("Not recording.");
            while (_queue.TryDequeue(out var line)) _writer.WriteLine(line);
            _writer.Flush();
            _writer.Dispose();
            if (!string.Equals(Path.GetFullPath(CurrentFile), Path.GetFullPath(newFile), StringComparison.OrdinalIgnoreCase))
                File.Copy(CurrentFile, newFile, true);
            _writer = new StreamWriter(new FileStream(newFile, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            CurrentFile = newFile;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(2000); } catch { }
        _cts?.Dispose();
        _cts = null;
        _task = null;
        lock (_fileLock)
        {
            if (_writer != null)
            {
                try
                {
                    while (_queue.TryDequeue(out var line)) _writer.WriteLine(line);
                    _writer.Dispose();
                }
                catch { }
            }
            _writer = null;
        }
    }

    public void Dispose() => Stop();
}
