namespace NeteaseGameDownloader.Core;

using System;
using System.Collections.Generic;
using System.Threading;

/// <summary>
/// 线程安全的任务状态快照，同时作为引擎的 ISink。
/// UI 线程只读 Snapshot()；引擎线程通过 Log/Phase/Progress/Result 写入。
/// </summary>
public sealed class JobStatus : ISink
{
    private readonly Lock _lock = new();
    private readonly Queue<(double time, string level, string message)> _logs = new(300);
    private ConfirmRequest? _confirm;

    public string PhaseName { get; set; } = Phases.Idle;
    public string Detail { get; set; } = "";
    public double? PhaseRatio { get; set; }
    public string ProductKey { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string ProductDesc { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Url { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Version { get; set; }
    public string? RemoteVersion { get; set; }
    public List<string> Notes { get; set; } = [];
    public List<string> Attempts { get; set; } = [];
    public string OutputPath { get; set; } = "";
    public long Total { get; set; }
    public long Done { get; set; }
    public double Speed { get; set; }
    public double AvgSpeed { get; set; }
    public double? Eta { get; set; }
    public double Percent { get; set; }
    public double Elapsed { get; set; }
    public List<double> History { get; set; } = [];
    public int WorkersActive { get; set; }
    public int WorkersTotal { get; set; }
    public int TasksPending { get; set; }
    public int SegmentsDone { get; set; }
    public int SegmentsTotal { get; set; }
    public bool Paused { get; set; }
    public bool Finished { get; set; }
    public bool? Ok { get; set; }
    public string Error { get; set; } = "";
    public Dictionary<string, object> ResultInfo { get; set; } = [];
    public int? ExitCode { get; set; }
    public double StartedAt { get; set; }
    public DownloadEngine? Control { get; set; }
    public bool IsInfo { get; set; }

    // ---------------- ISink ----------------
    public void Log(string level, string message)
    {
        lock (_lock) _logs.Enqueue((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, level, message));
    }

    public IReadOnlyList<(double time, string level, string message)> Logs()
    {
        lock (_lock) return new List<(double, string, string)>(_logs);
    }

    public void Phase(string name, string detail = "", double? ratio = null)
    {
        lock (_lock)
        {
            PhaseName = name;
            if (!string.IsNullOrEmpty(detail)) Detail = detail;
            PhaseRatio = ratio;
        }
    }

    public void Progress(DownloadProgress snap)
    {
        lock (_lock)
        {
            Done = snap.Done;
            Total = snap.Total;
            Speed = snap.Speed;
            AvgSpeed = snap.AverageSpeed;
            Eta = snap.Eta;
            Percent = snap.Percent;
            Elapsed = snap.Elapsed;
            History = [.. snap.History];
            WorkersActive = snap.WorkersActive;
            WorkersTotal = snap.WorkersTotal;
            TasksPending = snap.TasksPending;
            SegmentsDone = snap.SegmentsDone;
            SegmentsTotal = snap.SegmentsTotal;
            Paused = snap.Paused;
        }
    }

    public void Result(bool ok, string message = "")
    {
        lock (_lock)
        {
            Ok = ok;
            if (!string.IsNullOrEmpty(message) && !ok) Error = message;
        }
    }

    // ---------------- 确认交互 ----------------
    public sealed class ConfirmRequest(string prompt, bool defaultYes)
    {
        public string Prompt { get; } = prompt; public bool DefaultYes { get; } = defaultYes; public bool? Answer { get; set; }
        public ManualResetEventSlim Event { get; } = new(false);
    }

    public bool Ask(string prompt, bool defaultYes = false, double? timeout = null)
    {
        var req = new ConfirmRequest(prompt, defaultYes);
        lock (_lock) _confirm = req;
        bool got;
        if (timeout is null)
        {
            req.Event.Wait();
            got = true;
        }
        else
        {
            got = req.Event.Wait(TimeSpan.FromSeconds(timeout.Value));
        }
        lock (_lock) _confirm = null;
        if (!got || req.Answer is null) return defaultYes;
        return req.Answer.Value;
    }

    public ConfirmRequest? PendingConfirm()
    {
        lock (_lock) return _confirm;
    }

    public void AnswerConfirm(bool value)
    {
        ConfirmRequest? req;
        lock (_lock) req = _confirm;
        if (req is not null) { req.Answer = value; req.Event.Set(); }
    }

    // ---------------- 快照 ----------------
    public void Reset()
    {
        lock (_lock)
        {
            _logs.Clear();
            PhaseName = Phases.Idle;
            Detail = "";
            PhaseRatio = null;
            Url = "";
            Source = "";
            Version = null;
            RemoteVersion = null;
            Notes = [];
            Attempts = [];
            OutputPath = "";
            Total = 0;
            Done = 0;
            Speed = 0;
            AvgSpeed = 0;
            Eta = null;
            Percent = 0;
            Elapsed = 0;
            History = [];
            WorkersActive = 0;
            WorkersTotal = 0;
            TasksPending = 0;
            SegmentsDone = 0;
            SegmentsTotal = 0;
            Paused = false;
            Finished = false;
            Ok = null;
            Error = "";
            ResultInfo = [];
            ExitCode = null;
            StartedAt = 0;
            Control = null;
            IsInfo = false;
        }
    }

    public JobSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new JobSnapshot
            {
                Phase = PhaseName, PhaseLabel = Phases.Label(PhaseName), Detail = Detail, PhaseRatio = PhaseRatio,
                ProductKey = ProductKey, ProductName = ProductName, ProductDesc = ProductDesc,
                Platform = Platform, Url = Url, Source = Source, Version = Version,
                RemoteVersion = RemoteVersion, Notes = [.. Notes],
                OutputPath = OutputPath, Total = Total, Done = Done, Speed = Speed,
                AvgSpeed = AvgSpeed, Eta = Eta, Percent = Percent, Elapsed = Elapsed,
                History = [.. History], WorkersActive = WorkersActive,
                WorkersTotal = WorkersTotal, TasksPending = TasksPending,
                SegmentsDone = SegmentsDone, SegmentsTotal = SegmentsTotal,
                Paused = Paused, Finished = Finished, Ok = Ok, Error = Error,
                ResultInfo = new Dictionary<string, object>(ResultInfo),
                ExitCode = ExitCode,
                IsInfo = IsInfo,
            };
        }
    }
}

/// <summary>JobStatus 的可读快照（UI 线程使用）。</summary>
public sealed class JobSnapshot
{
    public string Phase { get; set; } = "";
    public string PhaseLabel { get; set; } = "";
    public string Detail { get; set; } = "";
    public double? PhaseRatio { get; set; }
    public string ProductKey { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string ProductDesc { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Url { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Version { get; set; }
    public string? RemoteVersion { get; set; }
    public List<string> Notes { get; set; } = [];
    public string OutputPath { get; set; } = "";
    public long Total { get; set; }
    public long Done { get; set; }
    public double Speed { get; set; }
    public double AvgSpeed { get; set; }
    public double? Eta { get; set; }
    public double Percent { get; set; }
    public double Elapsed { get; set; }
    public List<double> History { get; set; } = [];
    public int WorkersActive { get; set; }
    public int WorkersTotal { get; set; }
    public int TasksPending { get; set; }
    public int SegmentsDone { get; set; }
    public int SegmentsTotal { get; set; }
    public bool Paused { get; set; }
    public bool Finished { get; set; }
    public bool? Ok { get; set; }
    public string Error { get; set; } = "";
    public Dictionary<string, object> ResultInfo { get; set; } = [];
    public int? ExitCode { get; set; }
    public bool IsInfo { get; set; }
}
