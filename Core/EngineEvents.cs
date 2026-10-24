namespace NeteaseGameDownloader.Core;

/// <summary>
/// 引擎与界面之间的事件契约（对应 Python 的 sink.py）。
/// 引擎只向 ISink 推送结构化事件；界面（WPF）在 UI 线程读取 JobStatus 快照渲染。
/// </summary>
public static class Phases
{
    public const string Idle = "idle";
    public const string Resolve = "resolve";
    public const string Probe = "probe";
    public const string Download = "download";
    public const string Verify = "verify";
    public const string Cleanup = "cleanup";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Canceled = "canceled";

    private static readonly Dictionary<string, string> Labels = new()
    {
        [Idle] = "待命", [Resolve] = "解析最新版本", [Probe] = "探测文件信息",
        [Download] = "下载中", [Verify] = "完整性校验", [Cleanup] = "清理临时文件",
        [Done] = "已完成", [Failed] = "失败", [Canceled] = "已取消",
    };

    public static string Label(string phase) => Labels.TryGetValue(phase, out var v) ? v : phase;
}

/// <summary>一次进度推送的完整快照。</summary>
public sealed class DownloadProgress
{
    public long Done { get; set; }
    public long Total { get; set; }
    public double Speed { get; set; }
    public double? Eta { get; set; }
    public double Percent { get; set; }
    public double Elapsed { get; set; }
    public List<double> History { get; set; } = [];
    public double AverageSpeed { get; set; }
    public int WorkersActive { get; set; }
    public int WorkersTotal { get; set; }
    public int TasksPending { get; set; }
    public int SegmentsDone { get; set; }
    public int SegmentsTotal { get; set; }
    public bool Paused { get; set; }
    public string Phase { get; set; } = Phases.Idle;
}

/// <summary>引擎事件接收器（界面实现）。</summary>
public interface ISink
{
    void Log(string level, string message);
    void Phase(string name, string detail = "", double? ratio = null);
    void Progress(DownloadProgress snap);
    void Result(bool ok, string message = "");
}
