namespace NeteaseGameDownloader.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

/// <summary>
/// 断点续传状态（对应 Python 的 DownloadState）。
/// 记录 URL 指纹（规范化 URL）、文件指纹（大小/ETag/Last-Modified）与每段已下载字节数。
/// 恢复时校验指纹一致才复用状态，否则重新开始。
/// </summary>
public sealed class DownloadState
{
    private const string StateSuffix = ".dlstate.json";
    private const int StateVersion = 3;
    private static readonly int[] CompatVersions = [2, 3];

    public string Url { get; set; } = "";
    public string UrlKey { get; set; } = "";          // 身份比对用（规范化 URL）
    public long TotalSize { get; set; }
    public long SegmentSize { get; set; }
    public Dictionary<string, string?> Fingerprint { get; set; } = [];
    public int Version { get; set; } = StateVersion;
    public List<long> Progress { get; set; } = [];

    public DownloadState() { }

    public DownloadState(string url, long totalSize, long segmentSize,
                         Dictionary<string, string?> fingerprint, List<long>? progress = null)
    {
        Url = url;
        UrlKey = Utils.CanonicalUrl(url);
        TotalSize = totalSize;
        SegmentSize = segmentSize;
        Fingerprint = fingerprint;
        Version = StateVersion;
        var n = SegmentCount();
        Progress = (progress is not null && progress.Count == n) ? progress : NewZero(n);
    }

    public int SegmentCount()
    {
        if (TotalSize <= 0) return 0;
        return (int)((TotalSize + SegmentSize - 1) / SegmentSize);
    }

    public long Downloaded => Sum(Progress);

    public bool IsComplete() => SegmentCount() > 0 && Downloaded >= TotalSize;

    public static string PathFor(string outputPath)
        => Path.GetFullPath(outputPath) + StateSuffix;

    public void Save(string outputPath)
    {
        var path = PathFor(outputPath);
        var data = new
        {
            version = Version,
            url = Url,
            total_size = TotalSize,
            segment_size = SegmentSize,
            fingerprint = Fingerprint,
            progress = Progress,
        };
        var dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".dlstate-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts()));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    public static DownloadState? Load(string outputPath, string url, long totalSize,
                                      long segmentSize, Dictionary<string, string?> fingerprint)
    {
        var path = Path.GetFullPath(outputPath) + StateSuffix;
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var v) ||
                !Array.Exists(CompatVersions, x => x == v.GetInt32()))
                return null;

            var savedKey = root.TryGetProperty("url_key", out var uk) && uk.ValueKind == JsonValueKind.String
                ? uk.GetString()!
                : Utils.CanonicalUrl(root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "");
            if (savedKey != Utils.CanonicalUrl(url)) return null;

            if (!root.TryGetProperty("total_size", out var ts) || ts.GetInt64() != totalSize)
                return null;

            var fp = new Dictionary<string, string?>();
            if (root.TryGetProperty("fingerprint", out var fpel) && fpel.ValueKind == JsonValueKind.Object)
                foreach (var p in fpel.EnumerateObject())
                    fp[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;

            // ETag / Last-Modified 任一匹配即可
            if (fingerprint.TryGetValue("etag", out var et) && !string.IsNullOrEmpty(et))
            {
                if (!fp.TryGetValue("etag", out var fet) || fet != et) return null;
            }
            else if (fingerprint.TryGetValue("last_modified", out var lm) && !string.IsNullOrEmpty(lm))
            {
                if (!fp.TryGetValue("last_modified", out var flm) || flm != lm) return null;
            }

            if (!root.TryGetProperty("segment_size", out var ss) ||
                root.ValueKind == JsonValueKind.Undefined)
                return null;
            var segSize = ss.GetInt64();
            if (!root.TryGetProperty("progress", out var pr) || pr.ValueKind != JsonValueKind.Array)
                return null;

            var progress = new List<long>();
            foreach (var item in pr.EnumerateArray()) progress.Add(item.GetInt64());

            if (segSize != segmentSize)
                progress = Rescale(progress, segSize, segmentSize, totalSize);

            return new DownloadState(url, totalSize, segmentSize, fingerprint, progress);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Remove(string outputPath)
    {
        try { File.Delete(Path.GetFullPath(outputPath) + StateSuffix); }
        catch (IOException) { /* ignore */ }
    }

    /// <summary>分段大小改变时，把旧进度换算成新分段的进度（仅完整段可靠继承）。</summary>
    private static List<long> Rescale(List<long> progress, long oldSeg, long newSeg, long total)
    {
        var nNew = (int)((total + newSeg - 1) / newSeg);
        var newProgress = NewZero(nNew);
        for (var i = 0; i < progress.Count; i++)
        {
            var done = progress[i];
            if (done <= 0) continue;
            var segStart = i * oldSeg;
            var segEnd = segStart + done; // [segStart, segEnd) 已完成
            if (done != Math.Min(oldSeg, total - segStart)) continue;
            var pos = segStart;
            while (pos < segEnd)
            {
                var idx = (int)(pos / newSeg);
                newProgress[idx] = Math.Min(newSeg, total - idx * newSeg);
                pos += newSeg;
            }
        }
        return newProgress;
    }

    private static long Sum(List<long> xs)
    {
        long s = 0;
        foreach (var x in xs) s += x;
        return s;
    }

    private static List<long> NewZero(int n)
    {
        var list = new List<long>(n);
        for (var i = 0; i < n; i++) list.Add(0);
        return list;
    }

    private static JsonSerializerOptions JsonOpts() => new() { WriteIndented = false };
}
