// Port of netpeekier/history.py
//
// Rolling activity log. The monitor feeds per-process byte deltas here each
// tick. Samples aggregate in memory and flush once per interval (30 s
// default): one JSON line per executable that moved data in that window.
//
// Format (one JSON object per line, append-only) in log/history.jsonl:
//   {"t": <epoch>, "exe": "...", "name": "...", "up": <bytes>, "down": <bytes>,
//    "secs": <window seconds>}

using System.Text.Json;

namespace NetPeeker.Core;

public sealed class HistoryLogger
{
    private sealed class Bucket
    {
        public required string Name { get; set; }
        public double Up   { get; set; }
        public double Down { get; set; }
    }

    private readonly string _path;
    private readonly double _interval;            // seconds
    private readonly Dictionary<string, Bucket> _bucket = new();
    private readonly object _gate = new();

    private double _windowStart;
    private double _lastFlush;

    public HistoryLogger(string path, double intervalSeconds = 30.0)
    {
        _path = path;
        _interval = intervalSeconds;
        _windowStart = Now();
        _lastFlush   = _windowStart;
    }

    public void Record(string exe, string name, double upBytes, double downBytes)
    {
        if (string.IsNullOrEmpty(exe) || (upBytes <= 0 && downBytes <= 0)) return;
        lock (_gate)
        {
            if (!_bucket.TryGetValue(exe, out var b))
                _bucket[exe] = b = new Bucket { Name = name };
            b.Up   += Math.Max(0, upBytes);
            b.Down += Math.Max(0, downBytes);
        }
    }

    public void MaybeFlush()
    {
        var now = Now();
        if (now - _lastFlush < _interval) return;
        Flush(now);
    }

    public void Flush(double? nowOverride = null)
    {
        var now = nowOverride ?? Now();
        var secs = Math.Max(1.0, now - _windowStart);

        Dictionary<string, Bucket> drained;
        lock (_gate)
        {
            if (_bucket.Count == 0)
            {
                _lastFlush   = now;
                _windowStart = now;
                return;
            }
            drained = new Dictionary<string, Bucket>(_bucket);
            _bucket.Clear();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var sw = new StreamWriter(_path, append: true);
            foreach (var (exe, b) in drained)
            {
                var line = JsonSerializer.Serialize(new
                {
                    t    = Math.Round(now, 1),
                    exe  = exe,
                    name = b.Name,
                    up   = (long)b.Up,
                    down = (long)b.Down,
                    secs = Math.Round(secs, 1),
                });
                sw.WriteLine(line);
            }
        }
        catch
        {
            // Same fail-soft policy as the Python build — never let logging
            // failures bubble into the monitor.
        }
        finally
        {
            _lastFlush   = now;
            _windowStart = now;
        }
    }

    private static double Now() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}
