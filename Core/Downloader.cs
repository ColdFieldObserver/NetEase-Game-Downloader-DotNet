namespace NeteaseGameDownloader.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NeteaseGameDownloader.Models;

/// <summary>下载引擎抛出的致命错误（服务器行为异常等）。</summary>
public class DownloadEngineException(string msg) : Exception(msg) { }

internal sealed class PausedException(long received) : Exception
{
    public long Received { get; } = received;
}

internal sealed class StalledException : Exception { }

internal sealed class SignExpiredException(int status) : Exception
{
    public int Status { get; } = status;
}

/// <summary>
/// 分段区间记账（对应 Python 的 _SegmentLedger）。
/// 记录单个分段内已完成的字节区间，并计算"从头开始的连续前缀长度"，
/// 持久化到状态文件的只能是连续前缀，避免拆分并发下载时把洞当已完成。
/// </summary>
internal sealed class SegmentLedger
{
    public long Start { get; }                 // 闭区间起点
    public long End { get; }                   // 闭区间终点
    private List<(long s, long e)> _ranges = [];
    private long _prefix;

    public SegmentLedger(long start, long end, long done = 0)
    {
        Start = start;
        End = end;
        if (done > 0) Add(start, start + done - 1);
    }

    public long Size => End - Start + 1;

    public void Add(long s, long e)
    {
        if (e < s) return;
        var ranges = _ranges;
        var i = 0;
        while (i < ranges.Count && ranges[i].e < s - 1) i++;
        var merged = (s, e);
        var j = i;
        while (j < ranges.Count && ranges[j].s <= e + 1)
        {
            merged.s = Math.Min(merged.s, ranges[j].s);
            merged.e = Math.Max(merged.e, ranges[j].e);
            j++;
        }
        _ranges = [.. ranges.Take(i), merged, .. ranges.Skip(j)];
        RecomputePrefix();
    }

    private void RecomputePrefix()
    {
        var pos = Start;
        foreach (var (s, e) in _ranges)
        {
            if (s > pos) break;
            pos = Math.Max(pos, e + 1);
            if (pos > End + 1) { pos = End + 1; break; }
        }
        _prefix = pos - Start;
    }

    public long Prefix => _prefix;

    public long Received => _ranges.Sum(r => r.e - r.s + 1);

    public bool Complete => _prefix >= Size;

    public List<(long s, long e)> Holes()
    {
        var outp = new List<(long, long)>();
        var pos = Start;
        foreach (var (s, e) in _ranges)
        {
            if (s > pos) outp.Add((pos, s - 1));
            pos = Math.Max(pos, e + 1);
        }
        if (pos <= End) outp.Add((pos, End));
        return outp;
    }
}

internal readonly struct DownloadTask(int seg, long start, long end)
{
    public int Seg { get; } = seg; public long Start { get; } = start; public long End { get; } = end;

    public long Size => End - Start + 1;
}

/// <summary>
/// 多线程断点续传下载器（对应 Python 的 ThreadedDownloader），异步实现。
/// 功能：区间队列 + 尾部拆分、自适应并发、停滞看门狗、签名刷新、暂停/恢复/取消、416 识别。
/// </summary>
public sealed class DownloadEngine
{
    /// <summary>观察被弃任务的异常（含取消态），防止 UnobservedTaskException。</summary>
    private static void SwallowTask(Task t)
    {
        if (t.IsCompletedSuccessfully) return;
        _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.NotOnRanToCompletion);
    }

    // ---------------------------------------------------------------------
    // 常量
    // ---------------------------------------------------------------------
    public const int DefaultThreads = 8;
    public const int MaxThreads = 32;
    private const int AutoMaxThreads = 16;
    private const long MinSegmentSize = 1L * 1024 * 1024;
    private const long MaxSegmentSize = 16L * 1024 * 1024;
    private const int DefaultRetries = 5;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private const int ReadBlock = 256 * 1024;
    private const int WriteBuffer = 1024 * 1024;
    private const long ProgressFlushBytes = 1L * 1024 * 1024;
    private const double StateFlushInterval = 1.0;
    private const double UiPushInterval = 0.1;
    private const int ProbeRetries = 3;
    private const long SplitMinSize = 2L * 1024 * 1024;
    private const int MaxSplitsPerSegment = 8;
    private const double StallTimeout = 45.0;
    private const double WorkerRampDelay = 0.008;
    private const double WorkerRampMax = 0.12;
    private const double AutoscaleInterval = 0.6;
    private const double AutoscaleMinElapsed = 0.8;
    private const double IdleGrace = 1.5;
    private const long SmallFileBytes = 16L * 1024 * 1024;
    private const int MaxDownloadPasses = 4;
    private const int UrlRefreshMax = 3;
    private const double UrlRefreshMinInterval = 5.0;

    private static readonly string[] GenericNamePrefixes = ["蛋仔派对", "MuMu模拟器", "mumu", "eggy", "download"];

    // ---------------------------------------------------------------------
    // 配置与状态
    // ---------------------------------------------------------------------
    private string _url;
    private string _canonical;
    private readonly int _retries;
    private readonly TimeSpan _timeout;
    private readonly string? _expectedMd5;
    private readonly bool _forceRestart;
    private readonly bool _autoVerify;
    private readonly bool _keepChecksum;
    private readonly Func<Task<string?>>? _urlRefresher;
    private readonly bool _preferServerName;
    private readonly ISink _sink;

    private readonly bool _autoThreads;
    private int _threads;
    private readonly bool _autoscale;
    private int _scaleCap;

    public long TotalSize { get; private set; }
    public bool AcceptRanges { get; private set; }
    public Dictionary<string, string?> Fingerprint { get; private set; } = [];
    public string? SuggestedName { get; private set; }
    public string OutputPath { get; private set; }
    public VerifyResult? Result { get; private set; }
    public List<string> RemovedFiles { get; private set; } = [];

    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _pauseEvent = new(false);
    private readonly Lock _stateLock = new();
    private readonly Lock _qLock = new();
    private readonly Lock _logLock = new();

    private SpeedMeter? _meter;
    private DownloadState? _state;
    private SegmentLedger[] _ledgers = [];
    private readonly Queue<DownloadTask> _tasks = new();
    private readonly Dictionary<int, int> _splits = [];
    private int _activeTasks;
    private int _workersRunning;
    private int _workersTarget;
    private long _segmentSize;
    private int _refreshCount;
    private double _lastRefresh;
    private readonly List<DownloadEngineException> _workerErrors = [];
    private double _startedAt;
    private double _finishedAt;

    private readonly Dictionary<int, DateTime> _workerSeen = [];
    private readonly Dictionary<int, CancellationTokenSource> _abortCts = [];
    private readonly Dictionary<int, FileStream> _handles = [];
    private readonly Lock _handlesLock = new();
    private readonly List<Task> _workers = [];

    public DownloadEngine(
        string url, string outputPath, int threads = DefaultThreads,
        int retries = DefaultRetries, string? expectedMd5 = null,
        bool forceRestart = false, bool autoVerify = true, ISink? sink = null,
        bool keepChecksum = false, Func<Task<string?>>? urlRefresher = null,
        bool preferServerName = true, bool? autoscale = null)
    {
        _url = url;
        _canonical = Utils.CanonicalUrl(url);
        OutputPath = Path.GetFullPath(outputPath);
        _retries = Math.Max(0, retries);
        _timeout = DefaultTimeout;
        _expectedMd5 = expectedMd5?.ToLowerInvariant();
        _forceRestart = forceRestart;
        _autoVerify = autoVerify;
        _keepChecksum = keepChecksum;
        _urlRefresher = urlRefresher;
        _preferServerName = preferServerName;
        _sink = sink ?? new NullSink();

        if (threads <= 0) { _autoThreads = true; _threads = AutoMaxThreads; }
        else { _autoThreads = false; _threads = Math.Min(threads, MaxThreads); }
        _autoscale = autoscale ?? _autoThreads;
        _scaleCap = _autoThreads ? AutoMaxThreads : Math.Min(MaxThreads, Math.Max(1, _threads));
    }

    // =====================================================================
    // 外部控制
    // =====================================================================
    public void Pause() => _pauseEvent.Set();
    public void Resume() => _pauseEvent.Reset();
    public void Cancel() { _cts.Cancel(); _pauseEvent.Reset(); }
    public bool IsPaused => _pauseEvent.IsSet;
    public bool IsCanceled => _cts.IsCancellationRequested;

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // =====================================================================
    // 探测
    // =====================================================================
    public async Task<Dictionary<string, object>> ProbeAsync()
    {
        _sink.Phase(Phases.Probe, "连接官方 CDN…");
        Exception? lastErr = null;
        for (var attempt = 0; attempt < ProbeRetries; attempt++)
        {
            if (attempt > 0) await Task.Delay((int)Math.Min(Math.Pow(2, attempt - 1) * 1000, 5000));
            try
            {
                var info = await ProbeOnceAsync();
                _sink.Phase(Phases.Probe, $"{Utils.FormatSize(TotalSize)} · Range {(AcceptRanges ? "支持" : "不支持")}");
                return info;
            }
            catch (SignExpiredException e)
            {
                lastErr = e;
                if (await MaybeRefreshUrlAsync(e.Status)) continue;
                if (attempt < ProbeRetries - 1)
                    Utils.LogWarn($"探测失败(HTTP {e.Status})，重试 {attempt + 1}/{ProbeRetries - 1} ...");
            }
            catch (Exception e)
            {
                lastErr = e;
                if (attempt < ProbeRetries - 1)
                    Utils.LogWarn($"探测失败({e.Message})，重试 {attempt + 1}/{ProbeRetries - 1} ...");
            }
        }
        throw new DownloadEngineException($"探测文件信息失败（已重试 {ProbeRetries} 次）: {lastErr?.Message}");
    }

    private async Task<Dictionary<string, object>> ProbeOnceAsync()
    {
        var (resp, finalUrl) = await SendWithRedirectsAsync(_url, new RangeItemHeaderValue(0, 0));
        try
        {
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw new SignExpiredException((int)resp.StatusCode);
            resp.EnsureSuccessStatusCode();
            _url = finalUrl;
            _canonical = Utils.CanonicalUrl(_url);

            if (resp.Content.Headers.ContentDisposition?.FileName is { } cd)
            {
                var raw = cd.Trim('"');
                SuggestedName = Utils.SanitizeFilename(raw);
            }
            TotalSize = 0;
            if (resp.StatusCode == HttpStatusCode.PartialContent)
            {
                AcceptRanges = true;
                var cr = resp.Content.Headers.ContentRange;
                if (cr is not null && cr.HasLength) TotalSize = cr.Length!.Value;
            }
            else
            {
                var cl = resp.Content.Headers.ContentLength;
                if (cl.HasValue) TotalSize = cl.Value;
            }
            Fingerprint = new Dictionary<string, string?>
            {
                ["etag"] = resp.Headers.ETag?.Tag?.Trim('"'),
                ["last_modified"] = resp.Content.Headers.LastModified?.ToString("R"),
            };
        }
        finally { resp.Dispose(); }

        if (TotalSize <= 0)
            throw new InvalidOperationException("无法获取文件大小（服务器未返回 Content-Length / Content-Range）");
        return new Dictionary<string, object>
        {
            ["url"] = _url, ["total_size"] = TotalSize,
            ["accept_ranges"] = AcceptRanges, ["suggested_name"] = (object?)SuggestedName!,
        };
    }

    // =====================================================================
    // HTTP 发送（手动跟随重定向，便于拿到最终 URL 与 403/404）
    // =====================================================================
    private async Task<(HttpResponseMessage resp, string finalUrl)> SendWithRedirectsAsync(
        string url, RangeItemHeaderValue? range, bool followRedirects = true)
    {
        var currentUrl = url;
        HttpResponseMessage? resp = null;
        var ct = _cts.Token;
        for (var i = 0; i < 6; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            req.Headers.UserAgent.ParseAdd(Utils.USER_AGENT_DEFAULT);
            req.Headers.AcceptEncoding.TryParseAdd("identity");
            req.Headers.ConnectionClose = false;
            if (range is not null) req.Headers.Range = new RangeHeaderValue(range.From, range.To);
            resp = await HttpClients.NoRedirect.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!followRedirects) break;
            if (!IsRedirect(resp.StatusCode)) break;
            var loc = resp.Headers.Location?.OriginalString;
            resp.Dispose();
            if (string.IsNullOrEmpty(loc)) break;
            if (loc.StartsWith("//")) loc = "https:" + loc;
            currentUrl = new Uri(new Uri(currentUrl), loc).AbsoluteUri;
        }
        return (resp!, currentUrl);
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private FileStream GetHandle(int wid)
    {
        lock (_handlesLock)
        {
            if (_handles.TryGetValue(wid, out var fh) && !(fh.SafeFileHandle.IsClosed || fh.CanWrite == false))
                return fh;
            fh = new FileStream(OutputPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, WriteBuffer, FileOptions.SequentialScan);
            _handles[wid] = fh;
            return fh;
        }
    }

    private void CloseHandles()
    {
        lock (_handlesLock)
        {
            foreach (var fh in _handles.Values)
            {
                try { fh.Flush(); fh.Dispose(); } catch { /* ignore */ }
            }
            _handles.Clear();
        }
    }

    // =====================================================================
    // 分段与磁盘
    // =====================================================================
    private long PickSegmentSize()
    {
        var target = TotalSize / (Math.Max(1, _workersTarget) * 8);
        return Math.Max(MinSegmentSize, Math.Min(MaxSegmentSize, target == 0 ? MinSegmentSize : target));
    }

    private int PlanWorkers(int pendingTasks = 0)
    {
        int workers;
        if (_autoThreads)
        {
            workers = TotalSize switch
            {
                <= 0 => 4,
                < 8 * 1024 * 1024 => 2,
                < 64 * 1024 * 1024 => 4,
                < 256 * 1024 * 1024 => 8,
                < 1024 * 1024 * 1024 => 12,
                _ => 16
            };
            var cap = Math.Max(2, Math.Min(AutoMaxThreads, (int)(TotalSize / (4 * 1024 * 1024))));
            if (cap == 0) cap = 2;
            workers = Math.Max(1, Math.Min(workers, cap));
        }
        else
        {
            workers = _threads;
        }
        if (pendingTasks > 0) workers = Math.Min(workers, pendingTasks);
        return Math.Max(1, workers);
    }

    private void EnsureDiskSpace(long needed)
    {
        var dir = Path.GetDirectoryName(OutputPath) ?? ".";
        var free = new DriveInfo(dir).AvailableFreeSpace;
        if (needed > free)
            throw new DownloadEngineException(
                $"磁盘空间不足: 需要 {Utils.FormatSize(needed)}，目标盘可用 {Utils.FormatSize(free)}");
    }

    private void Preallocate(long size)
    {
        using var f = new FileStream(OutputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, WriteBuffer, FileOptions.SequentialScan);
        f.SetLength(size);
    }

    // =====================================================================
    // 进度推送
    // =====================================================================
    private void PushProgress(bool force = false)
    {
        if (_meter is null) return;
        var snap = _meter.Snapshot();
        int pending, active, segDone;
        lock (_qLock) { pending = _tasks.Count; active = _activeTasks; }
        segDone = _ledgers.Count(l => l is not null && l.Complete);
        var ev = new DownloadProgress
        {
            Done = snap.Done, Total = snap.Total, Speed = snap.Speed, Eta = snap.Eta,
            Percent = snap.Percent, Elapsed = snap.Elapsed, History = snap.History,
            AverageSpeed = snap.AverageSpeed, WorkersActive = active,
            WorkersTotal = _workersTarget, TasksPending = pending, SegmentsDone = segDone,
            SegmentsTotal = _ledgers.Length, Paused = IsPaused, Phase = Phases.Download,
        };
        _sink.Progress(ev);
    }

    // =====================================================================
    // 区间下载
    // =====================================================================
    private async Task<long> FetchRangeAsync(int wid, long start, long end)
    {
        var ct = _cts.Token;
        var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_logLock) { _workerSeen[wid] = DateTime.UtcNow; }
        var abort = new CancellationTokenSource();
        _abortCts[wid] = abort;

        HttpResponseMessage? resp = null;
        FileStream? f = null;
        long received = 0;
        long pending = 0;
        try
        {
            var (r, finalUrl) = await SendWithRedirectsAsync(_url, new RangeItemHeaderValue(start, end));
            resp = r;
            var status = (int)resp.StatusCode;
            if (status is 403 or 404)
                throw new SignExpiredException(status);
            if (status == 416)
                throw new DownloadEngineException(
                    "服务器返回 416（Range Not Satisfiable）：远端文件已变更，请强制重新下载");
            resp.EnsureSuccessStatusCode();
            if (resp.StatusCode != HttpStatusCode.PartialContent)
                throw new DownloadEngineException($"服务器未按 Range 响应 (HTTP {status})");

            f = GetHandle(wid);
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[ReadBlock];
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, abort.Token);
            try
            {
                while (true)
                {
                    if (ct.IsCancellationRequested) { Flush(); return received; }
                    if (IsPaused) { Flush(); throw new PausedException(received); }
                    if (abort.IsCancellationRequested) { Flush(); throw new StalledException(); }
                    var readTask = stream.ReadAsync(buf, 0, buf.Length, linked.Token);
                    var delayTask = Task.Delay(TimeSpan.FromSeconds(StallTimeout), linked.Token);
                    var completed = await Task.WhenAny(readTask, delayTask);
                    if (completed != readTask)
                    {
                        SwallowTask(readTask);   // 观察被弃任务的异常，避免 UnobservedTaskException
                        abort.Cancel();   // 触发重连
                        Flush();
                        throw new StalledException();
                    }
                    SwallowTask(delayTask);      // 超时计时器被弃（取消态），同样观察
                    var n = await readTask;
                    if (n == 0) break;     // 连接干净结束
                    f.Seek(start + received, SeekOrigin.Begin);
                    f.Write(buf, 0, n);
                    received += n;
                    pending += n;
                    lock (_logLock) { _workerSeen[wid] = DateTime.UtcNow; }
                    if (pending >= ProgressFlushBytes) Flush();
                }
            }
            finally { linked.Dispose(); }

            if (ct.IsCancellationRequested || IsPaused) return received;
            var expected = end - start + 1;
            if (received < expected)
                throw new DownloadEngineException(
                    $"分段被截断: 期望 {Utils.FormatSize(expected)}，实收 {Utils.FormatSize(received)}");
            return received;
        }
        finally
        {
            resp?.Dispose();
            Flush();
            try { abort.Dispose(); } catch { /* ignore */ }
            lock (_logLock) { _abortCts.Remove(wid); }
        }

        void Flush()
        {
            if (pending <= 0) return;
            Commit(wid, start, received, pending);
            pending = 0;
        }
    }

    private void Commit(int wid, long rangeStart, long received, long pending)
    {
        if (pending <= 0) return;
        var writtenEnd = rangeStart + received - 1;
        var segIdx = SegOf(rangeStart);
        lock (_stateLock)
        {
            var ledger = segIdx >= 0 && segIdx < _ledgers.Length ? _ledgers[segIdx] : null;
            if (ledger is not null)
            {
                ledger.Add(Math.Min(rangeStart, writtenEnd), writtenEnd);
                if (_state is not null && segIdx < _state.Progress.Count)
                    _state.Progress[segIdx] = ledger.Prefix;
            }
        }
        _meter?.Add(pending);
    }

    private int SegOf(long offset) => (int)Math.Min(_ledgers.Length - 1, offset / _segmentSize);

    private async Task<bool> MaybeRefreshUrlAsync(int status)
    {
        if (_urlRefresher is null) return false;
        var now = Now();
        if (_refreshCount >= UrlRefreshMax || now - _lastRefresh < UrlRefreshMinInterval) return false;
        _refreshCount++;
        _lastRefresh = now;
        try
        {
            var newUrl = await _urlRefresher();
            if (newUrl is not null && newUrl != _url)
            {
                _url = newUrl;
                _canonical = Utils.CanonicalUrl(newUrl);
                Utils.LogWarn($"下载链接已刷新（原签名可能已过期，HTTP {status}）");
                return true;
            }
        }
        catch (Exception e) { Utils.LogDebug($"刷新下载地址异常: {e.Message}"); }
        return false;
    }

    // =====================================================================
    // 单区间执行（重试 / 暂停 / 停滞重连）
    // =====================================================================
    private async Task RunTaskAsync(int wid, DownloadTask task)
    {
        var delay = 1.0;
        Exception? lastErr = null;
        for (var attempt = 0; attempt <= _retries; attempt++)
        {
            if (_cts.IsCancellationRequested) return;
            await WaitIfPaused(wid);
            if (_cts.IsCancellationRequested) return;

            var start = FirstHoleStart(task);
            if (start is null || start.Value > task.End) return;
            long s = start.Value;
            try
            {
                var received = await FetchRangeAsync(wid, s, task.End);
                if (s + received - 1 >= task.End) return;  // 区间完成
                return;
            }
            catch (PausedException) { return; }
            catch (StalledException)
            {
                lastErr = new StalledException();
                Utils.LogWarn($"连接停滞 >{StallTimeout:F0}s，主动重连该区间…");
            }
            catch (SignExpiredException e)
            {
                lastErr = e;
                if (await MaybeRefreshUrlAsync(e.Status)) continue;
                Utils.LogWarn($"服务器返回 HTTP {e.Status}，稍后重试…");
            }
            catch (DownloadEngineException e)
            {
                if (e.Message.Contains("416")) throw;
                lastErr = e;
                Utils.LogWarn($"区间异常({e.Message})，重试中…");
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            {
                lastErr = e;
            }

            if (_cts.IsCancellationRequested || IsPaused) return;
            if (attempt < _retries)
            {
                var jitter = delay * (0.5 + Random.Shared.NextDouble());
                await Task.Delay((int)Math.Min(jitter, 30) * 1000, _cts.Token);
                delay = Math.Min(delay * 2, 30);
            }
            else break;
        }
        if (_cts.IsCancellationRequested || IsPaused) return;
        throw new DownloadEngineException(
            $"区间 {Utils.FormatSize(task.Start)}-{Utils.FormatSize(task.End)} 重试 {_retries} 次后仍失败: {lastErr?.Message}");
    }

    private async Task WaitIfPaused(int wid)
    {
        if (!IsPaused) return;
        while (IsPaused && !_cts.IsCancellationRequested)
        {
            await Task.Delay(150, _cts.Token);
            PushProgress();
        }
    }

    private long? FirstHoleStart(DownloadTask task)
    {
        lock (_stateLock)
        {
            var ledger = _ledgers[task.Seg];
            foreach (var (s, e) in ledger.Holes())
            {
                if (e >= task.Start && s <= task.End)
                    return Math.Max(s, task.Start);
            }
        }
        return null;
    }

    // =====================================================================
    // 任务队列（尾部拆分）
    // =====================================================================
    private DownloadTask? NextTask()
    {
        lock (_qLock)
        {
            if (_tasks.Count == 0) return null;
            var task = _tasks.Dequeue();
            if (_tasks.Count == 0 && task.Size > SplitMinSize && _workersRunning > 1 &&
                _splits.GetValueOrDefault(task.Seg) < MaxSplitsPerSegment)
            {
                var mid = task.Start + (task.Size / (2 * ReadBlock)) * ReadBlock;
                if (mid > task.Start && mid < task.End)
                {
                    _splits[task.Seg] = _splits.GetValueOrDefault(task.Seg) + 1;
                    _tasks.Enqueue(new DownloadTask(task.Seg, mid + 1, task.End));
                    task = new DownloadTask(task.Seg, task.Start, mid);
                }
            }
            _activeTasks++;
            return task;
        }
    }

    private void ReleaseTask()
    {
        lock (_qLock) _activeTasks = Math.Max(0, _activeTasks - 1);
    }

    private void RequeueRemaining(DownloadTask task)
    {
        List<(long s, long e)> holes;
        lock (_stateLock)
        {
            var ledger = _ledgers[task.Seg];
            holes = [.. ledger.Holes()
                .Where(h => h.e >= task.Start && h.s <= task.End)
                .Select(h => (Math.Max(h.s, task.Start), Math.Min(h.e, task.End)))];
        }
        lock (_qLock)
        {
            foreach (var (s, e) in holes.OrderByDescending(h => h.s))
                if (e >= s) _tasks.Enqueue(new DownloadTask(task.Seg, s, e));
        }
    }

    // =====================================================================
    // worker
    // =====================================================================
    private async Task WorkerAsync(int wid)
    {
        Interlocked.Increment(ref _workersRunning);
        Exception? error = null;
        try
        {
            DateTime? idleSince = null;
            while (!_cts.IsCancellationRequested)
            {
                if (IsPaused) { await WaitIfPaused(wid); idleSince = null; continue; }
                var task = NextTask();
                if (task is null)
                {
                    var now = DateTime.UtcNow;
                    idleSince ??= now;
                    int active;
                    lock (_qLock) { active = _activeTasks; }
                    if (active > 0 && (now - idleSince.Value).TotalSeconds < IdleGrace)
                    {
                        await Task.Delay(50, _cts.Token);
                        continue;
                    }
                    break;
                }
                idleSince = null;
                var t = task.Value;
                try { await RunTaskAsync(wid, t); }
                finally
                {
                    ReleaseTask();
                    if (IsPaused) RequeueRemaining(t);
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (DownloadEngineException e) { error = e; }
        catch (Exception e) { error = new DownloadEngineException($"worker 异常: {e.Message}"); }
        finally
        {
            Interlocked.Decrement(ref _workersRunning);
            lock (_logLock) { _workerSeen.Remove(wid); _abortCts.Remove(wid); }
            if (_handles.TryGetValue(wid, out var fh))
            {
                try { fh.Flush(); fh.Dispose(); } catch { /* ignore */ }
                lock (_handlesLock) { _handles.Remove(wid); }
            }
            if (error is not null)
            {
                _cts.Cancel();
                lock (_logLock) _workerErrors.Add((DownloadEngineException)error);
            }
        }
    }

    private async Task WorkerStaggeredAsync(int wid)
    {
        await Task.Delay((int)Math.Min(WorkerRampMax, wid * WorkerRampDelay) * 1000, _cts.Token);
        if (_cts.IsCancellationRequested) return;
        await WorkerAsync(wid);
    }

    // =====================================================================
    // 监督循环
    // =====================================================================
    private async Task SuperviseAsync()
    {
        var lastFlush = Now();
        var lastFlushedDone = -1L;
        var lastScale = Now();
        while (true)
        {
            var pending = 0; var active = 0;
            lock (_qLock) { pending = _tasks.Count; active = _activeTasks; }
            var running = _workersRunning;
            var allDone = _workers.All(w => w.IsCompleted);
            if (allDone && active == 0 && running == 0) break;

            var now = Now();
            PushProgress();

            // 停滞看门狗（仅记录告警）
            var utc = DateTime.UtcNow;
            lock (_logLock)
            {
                foreach (var kv in _workerSeen)
                {
                    if ((utc - kv.Value).TotalSeconds > StallTimeout && (!_abortCts.ContainsKey(kv.Key) || !_abortCts[kv.Key].IsCancellationRequested))
                    {
                        if (_abortCts.TryGetValue(kv.Key, out var a)) a.Cancel();
                        Utils.LogWarn($"线程 {kv.Key} 已 {(utc - kv.Value).TotalSeconds:F0}s 无数据，标记重连");
                    }
                }
            }

            // 状态落盘
            if (now - lastFlush >= StateFlushInterval && _state is not null)
            {
                var doneNow = _state.Downloaded;
                if (doneNow != lastFlushedDone)
                {
                    try { _state.Save(OutputPath); lastFlushedDone = doneNow; }
                    catch (IOException) { /* ignore */ }
                }
                lastFlush = now;
            }

            // 自适应并发
            if (_autoscale && now - lastScale >= AutoscaleInterval && now - _startedAt >= AutoscaleMinElapsed &&
                !_cts.IsCancellationRequested && !IsPaused)
            {
                lastScale = now;
                await MaybeScaleUpAsync(pending);
            }

            await Task.Delay((int)(UiPushInterval * 1000), _cts.Token);
        }
        PushProgress(force: true);
    }

    private async Task MaybeScaleUpAsync(int pending)
    {
        var cap = _scaleCap;
        if (_workersTarget >= cap || pending < 2 || _workerErrors.Count > 0) return;
        if (_meter is null) return;
        var snap = _meter.Snapshot();
        var active = Math.Max(1, _workersRunning);
        var perConn = snap.Speed / active;
        if (perConn < 128 * 1024) return;
        var add = perConn > 1024 * 1024 && pending >= 4 ? 4 : perConn > 512 * 1024 ? 2 : 1;
        for (var i = 0; i < add; i++)
        {
            if (_workersTarget >= cap) break;
            var wid = _workersTarget;
            _workersTarget++;
            _workers.Add(Task.Run(() => WorkerStaggeredAsync(wid)));
        }
        Utils.LogDebug($"自适应并发提升到 {_workersTarget}");
    }

    // =====================================================================
    // 主入口
    // =====================================================================
    public async Task<bool> DownloadAsync()
    {
        _startedAt = Now();
        _workerErrors.Clear();
        try
        {
            var ok = await DownloadInnerAsync();
            _finishedAt = Now();
            _sink.Result(ok, ok ? "" : "下载未完成");
            if (ok) _sink.Phase(Phases.Done, OutputPath);
            return ok;
        }
        catch (OperationCanceledException)
        {
            _finishedAt = Now();
            SaveState();
            _sink.Phase(Phases.Canceled, "已取消（进度已保存，可续传）");
            _sink.Result(false, "已取消");
            return false;
        }
        finally { CloseHandles(); }
    }

    private async Task<bool> DownloadInnerAsync()
    {
        Utils.EnsureDir(OutputPath);
        // 1. 探测
        await ProbeAsync();
        Utils.LogInfo($"文件大小: {Utils.FormatSize(TotalSize)}  Range支持: {(AcceptRanges ? "是" : "否")}");

        if (_preferServerName && SuggestedName is not null &&
            Path.GetFileName(OutputPath).ToLowerInvariant().StartsWithAny(GenericNamePrefixes))
        {
            var newPath = Path.Combine(Path.GetDirectoryName(OutputPath)!, SuggestedName);
            if (newPath != OutputPath && !File.Exists(OutputPath + ".dlstate.json"))
            {
                Utils.LogInfo($"采用服务器建议文件名: {SuggestedName}");
                OutputPath = newPath;
            }
        }

        // 2. 不支持 Range -> 单线程回退
        if (!AcceptRanges)
        {
            EnsureDiskSpace(TotalSize);
            _meter = new SpeedMeter(TotalSize, 0);
            await DownloadSingleStreamAsync();
            return await FinalizeAsync();
        }

        // 3. 恢复或新建状态
        _workersTarget = PlanWorkers();
        _segmentSize = PickSegmentSize();
        DownloadState? state = null;
        if (!_forceRestart)
        {
            state = DownloadState.Load(OutputPath, _url, TotalSize, _segmentSize, Fingerprint);
            if (state is null && File.Exists(OutputPath + ".dlstate.json"))
                Utils.LogWarn("已有状态与当前文件不匹配（URL/大小/指纹变化），将重新下载");
        }
        if (state is not null && state.IsComplete() && File.Exists(OutputPath) &&
            new FileInfo(OutputPath).Length == TotalSize)
        {
            Utils.LogSuccess($"文件已完整下载: {OutputPath}");
            DownloadState.Remove(OutputPath);
            _meter = new SpeedMeter(TotalSize, TotalSize);
            return await FinalizeAsync(skipDownloadCheck: true);
        }

        var resumed = state is not null;
        state ??= new DownloadState(_url, TotalSize, _segmentSize, Fingerprint);
        _state = state;

        // 4. 预分配
        var need = TotalSize;
        var cur = File.Exists(OutputPath) ? new FileInfo(OutputPath).Length : -1;
        EnsureDiskSpace(Math.Max(0, TotalSize - state.Downloaded));
        if (cur != need || _forceRestart)
        {
            if (File.Exists(OutputPath)) File.Delete(OutputPath);
            Preallocate(need);
            if (cur != need)
            {
                lock (_stateLock)
                {
                    state.Progress = [.. Enumerable.Repeat(0L, state.SegmentCount())];
                }
                if (resumed) Utils.LogWarn("目标文件与断点状态不一致，进度已重置");
            }
        }

        // 5. 构建分段账本 + 任务队列
        var nSeg = state.SegmentCount();
        _ledgers = new SegmentLedger[nSeg];
        _splits.Clear();
        lock (_qLock) _tasks.Clear();
        var pending = 0;
        for (var i = 0; i < nSeg; i++)
        {
            var segStart = i * _segmentSize;
            var segEnd = Math.Min(segStart + _segmentSize, TotalSize) - 1;
            var done = state.Progress.Count > i ? state.Progress[i] : 0L;
            _ledgers[i] = new SegmentLedger(segStart, segEnd, done);
            foreach (var (s, e) in _ledgers[i].Holes())
            {
                _tasks.Enqueue(new DownloadTask(i, s, e));
                pending++;
            }
        }

        var initialDone = _ledgers.Sum(l => l.Received);
        if (resumed && initialDone > 0)
            Utils.LogInfo($"发现断点状态，已完成 {Utils.FormatSize(initialDone)} " +
                          $"(剩余 {Utils.FormatSize(TotalSize - initialDone)})");

        if (pending == 0)
        {
            Utils.LogSuccess($"文件已完整下载: {OutputPath}");
            DownloadState.Remove(OutputPath);
            _meter = new SpeedMeter(TotalSize, TotalSize);
            return await FinalizeAsync(skipDownloadCheck: true);
        }

        _workersTarget = PlanWorkers(pending);
        Utils.LogInfo($"共 {nSeg} 个分段（每段 {Utils.FormatSize(_segmentSize)}），" +
                      $"待下载 {pending} 个区间，使用 {_workersTarget} 个线程" +
                      (_autoscale ? "（自适应）" : ""));

        _meter = new SpeedMeter(TotalSize, initialDone);
        _sink.Phase(Phases.Download, $"{Utils.FormatSize(TotalSize)} · {_workersTarget} 线程");

        // 6. 启动 worker + 监督循环（多轮补跑）
        _workers.Clear();
        var poolMax = Math.Min(MaxThreads, Math.Max(_workersTarget, _scaleCap));
        try
        {
            for (var wid = 0; wid < _workersTarget; wid++)
            {
                var w = wid;
                _workers.Add(Task.Run(() => WorkerStaggeredAsync(w)));
            }
            for (var pass = 0; pass < MaxDownloadPasses; pass++)
            {
                await SuperviseAsync();
                if (_cts.IsCancellationRequested || _workerErrors.Count > 0) break;
                if (RemainingBytes() <= 0) break;
                if (_tasks.Count == 0) RebuildTasksFromLedgers();
                for (var wid = 0; wid < _workersTarget; wid++)
                {
                    var w = wid;
                    _workers.Add(Task.Run(() => WorkerStaggeredAsync(w)));
                }
            }
        }
        finally { await Task.WhenAll(_workers); }

        CloseHandles();
        var remaining = RemainingBytes();

        if (_cts.IsCancellationRequested)
        {
            SaveState();
            var doneNow = TotalSize - remaining;
            Utils.LogWarn($"已中断，进度已保存 ({Utils.FormatSize(doneNow)}/{Utils.FormatSize(TotalSize)})。" +
                          $"重新运行相同命令即可从断点继续。");
            return false;
        }
        if (_workerErrors.Count > 0)
        {
            SaveState();
            Utils.LogError(_workerErrors[0].Message);
            Utils.LogInfo($"进度已保存 ({Utils.FormatSize(TotalSize - remaining)}/" +
                          $"{Utils.FormatSize(TotalSize)})，重新运行命令可续传。");
            _sink.Phase(Phases.Failed, _workerErrors[0].Message);
            return false;
        }
        if (remaining > 0)
        {
            SaveState();
            Utils.LogWarn($"仍有 {Utils.FormatSize(remaining)} 未完成，进度已保存，重新运行命令可续传");
            return false;
        }

        DownloadState.Remove(OutputPath);
        return await FinalizeAsync();
    }

    private long RemainingBytes()
    {
        lock (_stateLock) return _ledgers.Sum(l => l.Size - l.Prefix);
    }

    private void RebuildTasksFromLedgers()
    {
        lock (_qLock)
        {
            _tasks.Clear();
            lock (_stateLock)
            {
                for (var idx = 0; idx < _ledgers.Length; idx++)
                {
                    var lg = _ledgers[idx];
                    if (lg is null) continue;
                    foreach (var (s, e) in lg.Holes())
                        _tasks.Enqueue(new DownloadTask(idx, s, e));
                }
            }
            _activeTasks = 0;
        }
    }

    private void SaveState()
    {
        if (_state is null) return;
        try { _state.Save(OutputPath); }
        catch (IOException e) { Utils.LogWarn($"状态保存失败: {e.Message}"); }
    }

    // =====================================================================
    // 单线程回退
    // =====================================================================
    private async Task DownloadSingleStreamAsync()
    {
        Utils.LogWarn("服务器不支持 Range 请求，回退为单线程流式下载");
        var tmpPath = OutputPath + ".part";
        var delay = 1.0;
        _sink.Phase(Phases.Download, $"{Utils.FormatSize(TotalSize)} · 单线程（不支持 Range）");
        var ct = _cts.Token;
        for (var attempt = 0; attempt <= _retries; attempt++)
        {
            if (_cts.IsCancellationRequested) throw new OperationCanceledException();
            var downloaded = File.Exists(tmpPath) ? new FileInfo(tmpPath).Length : 0L;
            try
            {
                var (resp, _) = await SendWithRedirectsAsync(_url, downloaded > 0 ? new RangeItemHeaderValue(downloaded, null) : null);
                try
                {
                    resp.EnsureSuccessStatusCode();
                    var useAppend = downloaded > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                    long pending = 0;
                    using var f = new FileStream(tmpPath, useAppend ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite, WriteBuffer, FileOptions.SequentialScan);
                    if (!useAppend) downloaded = 0;
                    var stream = await resp.Content.ReadAsStreamAsync(ct);
                    var buf = new byte[ReadBlock];
                    while (true)
                    {
                        if (_cts.IsCancellationRequested) throw new OperationCanceledException();
                        while (IsPaused && !_cts.IsCancellationRequested)
                        {
                            await Task.Delay(150, ct);
                            PushProgress();
                        }
                        var n = await stream.ReadAsync(buf, ct);
                        if (n == 0) break;
                        f.Write(buf, 0, n);
                        downloaded += n; pending += n;
                        if (pending >= ProgressFlushBytes) { _meter!.Add(pending); pending = 0; PushProgress(); }
                    }
                    _meter?.SetDone(downloaded);
                    File.Move(tmpPath, OutputPath, overwrite: true);
                    return;
                }
                finally { resp.Dispose(); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                if (attempt < _retries)
                {
                    Utils.LogWarn($"下载中断({e.Message})，{delay:F0}s 后重试 ({attempt + 1}/{_retries})...");
                    await Task.Delay((int)Math.Min(delay, 30) * 1000, ct);
                    delay = Math.Min(delay * 2, 30);
                }
                else throw new DownloadEngineException($"下载失败: {e.Message}");
            }
        }
    }

    // =====================================================================
    // 完成后的即时校验与清理
    // =====================================================================
    private async Task<bool> FinalizeAsync(bool skipDownloadCheck = false)
    {
        var actual = File.Exists(OutputPath) ? new FileInfo(OutputPath).Length : 0;
        if (!skipDownloadCheck && actual != TotalSize)
        {
            Utils.LogError($"文件大小不匹配: 期望 {Utils.FormatSize(TotalSize)}，实际 {Utils.FormatSize(actual)}");
            _sink.Phase(Phases.Failed, "文件大小不匹配");
            return false;
        }

        var elapsed = _finishedAt == 0 ? Now() - _startedAt : _finishedAt - _startedAt;
        if (elapsed < 0) elapsed = 0;
        var avg = elapsed > 0 ? TotalSize / elapsed : 0;

        if (!_autoVerify)
        {
            DownloadState.Remove(OutputPath);
            if (!_keepChecksum) RemovedFiles = Verify.CleanupChecksumFiles(OutputPath);
            ReportDone(actual, elapsed, avg, null);
            return true;
        }

        _sink.Phase(Phases.Verify, "计算 MD5 / SHA256 …", 0.0);
        var lastRatio = 0.0;
        var res = await Task.Run(() => Verify.VerifyNow(
            OutputPath, _expectedMd5, true, _keepChecksum,
            ratio =>
            {
                if (ratio - lastRatio >= 0.25 || ratio >= 1.0)
                {
                    lastRatio = ratio;
                    _sink.Phase(Phases.Verify, $"计算 MD5 / SHA256 … {ratio * 100:F0}%", ratio);
                }
            }, _url));
        Result = res;
        RemovedFiles = res.Removed;

        if (!res.Ok)
        {
            foreach (var m in res.Messages) Utils.LogError(m);
            _sink.Phase(Phases.Failed, res.Summary());
            return false;
        }

        _sink.Phase(Phases.Cleanup, res.Removed.Count > 0 ? $"已清理 {res.Removed.Count} 个校验附属文件" : "无残留校验文件");
        DownloadState.Remove(OutputPath);
        ReportDone(actual, elapsed, avg, res);
        return true;
    }

    private void ReportDone(long size, double elapsed, double avg, VerifyResult? res)
    {
        foreach (var m in (res?.Messages ?? []))
        {
            if (m.StartsWith("MD5 校验失败")) Utils.LogError(m);
            else Utils.LogSuccess(m);
        }
        Utils.LogSuccess($"下载完成: {OutputPath} ({Utils.FormatSize(size)})" +
                         (avg > 0 ? $"，耗时 {elapsed:F1}s，平均 {Utils.FormatSpeed(avg)}" : ""));
    }
}

/// <summary>静默 sink（库调用 / 测试用）。</summary>
internal sealed class NullSink : ISink
{
    public void Log(string level, string message) { }
    public void Phase(string name, string detail = "", double? ratio = null) { }
    public void Progress(DownloadProgress snap) { }
    public void Result(bool ok, string message = "") { }
}

internal static class StringExt
{
    public static bool StartsWithAny(this string s, IEnumerable<string> prefixes)
    {
        foreach (var p in prefixes)
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
