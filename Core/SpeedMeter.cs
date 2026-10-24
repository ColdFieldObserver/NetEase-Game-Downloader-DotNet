namespace NeteaseGameDownloader.Core;

using System;
using System.Collections.Generic;

/// <summary>
/// 线程安全的速度/进度测量器（对应 Python 的 SpeedMeter）。
/// worker 线程只调用 Add(n)；UI 线程调用 Snapshot() 读取 done/speed/eta/history。
/// </summary>
public sealed class SpeedMeter
{
    private readonly Lock _lock = new();
    private readonly double _window;
    private readonly int _historyLen;
    private readonly double _sampleInterval;
    private long _done;
    private readonly List<(double time, long bytes)> _samples = [];
    private readonly Queue<double> _history;
    private double _lastSample;
    private double _start;
    private double _finishedAt;

    public long Total { get; }

    public SpeedMeter(long total = 0, long initial = 0, double window = 5.0,
                      int historyLen = 48, double sampleInterval = 0.5)
    {
        Total = Math.Max(0, total);
        _window = window;
        _historyLen = historyLen;
        _sampleInterval = sampleInterval;
        _history = new Queue<double>(historyLen);
        _start = Now();
        _lastSample = _start;
        _done = Math.Max(0, initial);
        _samples.Add((_start, _done));
        _history.Enqueue(0.0);
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public void Add(long n)
    {
        if (n <= 0) return;
        var now = Now();
        lock (_lock)
        {
            _done += n;
            _samples.Add((now, _done));
            var cutoff = now - _window * 1.5;
            while (_samples.Count > 2 && _samples[0].time < cutoff)
                _samples.RemoveAt(0);
            if (now - _lastSample >= _sampleInterval)
            {
                _lastSample = now;
                _history.Enqueue(SpeedLocked(now));
                while (_history.Count > _historyLen) _history.Dequeue();
            }
        }
    }

    public void SetDone(long value)
    {
        lock (_lock) _done = Math.Max(0, value);
    }

    private double SpeedLocked(double now)
    {
        if (_samples.Count < 2) return 0.0;
        var (t0, b0) = _samples[0];
        var dt = now - t0;
        if (dt <= 0.05) return 0.0;
        return Math.Max(0.0, (_done - b0) / dt);
    }

    public double Speed()
    {
        var now = Now();
        lock (_lock) return SpeedLocked(now);
    }

    public double Elapsed()
    {
        lock (_lock)
        {
            var end = _finishedAt == 0 ? nowSafe() : _finishedAt;
            return Math.Max(0.0, end - _start);
        }
    }

    private static double nowSafe() => Now();

    public void Finish()
    {
        lock (_lock)
        {
            if (_finishedAt == 0) _finishedAt = Now();
        }
    }

    public SpeedSnapshot Snapshot()
    {
        var now = Now();
        long done;
        double speed, elapsed;
        List<double> history;
        lock (_lock)
        {
            done = _done;
            speed = SpeedLocked(now);
            history = [.. _history];
            elapsed = (_finishedAt == 0 ? now : _finishedAt) - _start;
        }
        var total = Total;
        var eta = (speed > 0 && total > 0 && done < total) ? (total - done) / speed : (double?)null;
        var pct = total > 0 ? done / (double)total * 100.0 : 0.0;
        var avg = elapsed > 0.5 ? done / elapsed : speed;
        return new SpeedSnapshot
        {
            Done = done,
            Total = total,
            Speed = speed,
            Eta = eta,
            Percent = pct,
            Elapsed = Math.Max(0.0, elapsed),
            History = history,
            AverageSpeed = avg,
        };
    }
}

public sealed class SpeedSnapshot
{
    public long Done { get; set; }
    public long Total { get; set; }
    public double Speed { get; set; }
    public double? Eta { get; set; }
    public double Percent { get; set; }
    public double Elapsed { get; set; }
    public List<double> History { get; set; } = [];
    public double AverageSpeed { get; set; }
}
