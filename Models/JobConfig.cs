namespace NeteaseGameDownloader.Models;

/// <summary>
/// 一次下载任务的全部参数（由 GUI 表单产生，对应 Python 的 JobConfig）。
/// </summary>
public sealed class JobConfig
{
    public string ProductKey { get; set; } = "eggy-android";
    public string? Output { get; set; }
    public int Threads { get; set; } = 8;
    public int Retries { get; set; } = 5;
    public string? Url { get; set; }
    public string? ExpectedMd5 { get; set; }
    public bool Verify { get; set; } = true;
    public bool KeepChecksum { get; set; }
    public bool Force { get; set; }
    public bool InfoOnly { get; set; }
    public bool AutoThreads { get; set; }
    public bool AssumeYes { get; set; }
    public bool Quiet { get; set; }

    /// <summary>实际交给引擎的线程数：自适应时传 0。</summary>
    public int ThreadValue => AutoThreads ? 0 : Math.Max(1, Threads);
}
