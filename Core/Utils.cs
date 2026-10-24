namespace NeteaseGameDownloader.Core;

using System;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

/// <summary>
/// 通用工具：日志、格式化、URL 规范化、文件与目录辅助。
/// 日志出口统一走 LogHub，GUI 可注册 sink 收集日志（不再直接打印到控制台）。
/// </summary>
public static class Utils
{
    // ---------------------------------------------------------------------
    // 常量
    // ---------------------------------------------------------------------
    public const string USER_AGENT_DEFAULT =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public const string USER_AGENT_ANDROID =
        "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36";

    // ---------------------------------------------------------------------
    // 日志
    // ---------------------------------------------------------------------
    public static void LogInfo(string msg) => LogHub.Emit("info", msg);
    public static void LogWarn(string msg) => LogHub.Emit("warn", msg);
    public static void LogError(string msg) => LogHub.Emit("error", msg);
    public static void LogSuccess(string msg) => LogHub.Emit("ok", msg);
    public static void LogDebug(string msg)
    {
        if (Environment.GetEnvironmentVariable("EGGY_DL_DEBUG") is { } v &&
            (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)))
            LogHub.Emit("debug", msg);
    }

    // ---------------------------------------------------------------------
    // 格式化
    // ---------------------------------------------------------------------
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    public static string FormatSize(double bytes)
    {
        if (bytes <= 0) return "0 B";
        int power = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), SizeUnits.Length - 1);
        return $"{bytes / Math.Pow(1024, power):F2} {SizeUnits[power]}";
    }

    public static string FormatSizeShort(double bytes)
    {
        if (bytes <= 0) return "0B";
        int power = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), SizeUnits.Length - 1);
        double val = bytes / Math.Pow(1024, power);
        return val < 100 ? $"{val:F1}{SizeUnits[power][0]}" : $"{val:F0}{SizeUnits[power][0]}";
    }

    public static string FormatSpeed(double bytesPerSec) => $"{FormatSize(bytesPerSec)}/s";

    public static string FormatEta(double? seconds)
    {
        if (seconds is null || seconds < 0 || double.IsInfinity(seconds!.Value))
            return "--:--";
        int s = (int)seconds.Value;
        int h = s / 3600;
        int m = (s % 3600) / 60;
        int sec = s % 60;
        return h > 0 ? $"{h}:{m:D2}:{sec:D2}" : $"{m:D2}:{sec:D2}";
    }

    // ---------------------------------------------------------------------
    // URL 规范化
    // ---------------------------------------------------------------------
    private static readonly HashSet<string> VolatileParams =
    [
        "key1", "key2", "n", "_t", "t", "nonce", "dfuid", "sign",
        "token", "auth", "expires", "ts", "rand", "callback",
    ];

    /// <summary>
    /// 规范化 URL：去掉易变的签名/随机查询参数，保留 scheme+host+path 与稳定参数。
    /// 用于断点状态的身份比对（网易 CDN 每次解析都会换 key1/key2/n 签名串）。
    /// </summary>
    public static string CanonicalUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        url = url.Trim();
        try
        {
            var uri = new Uri(url);
            var query = uri.Query.TrimStart('?');
            if (string.IsNullOrEmpty(query)) return BuildCanonical(uri, "");
            var keep = new List<string>();
            foreach (var kv in query.Split('&'))
            {
                if (string.IsNullOrEmpty(kv)) continue;
                var key = kv.Split('=', 2)[0].ToLowerInvariant();
                if (!VolatileParams.Contains(key)) keep.Add(kv);
            }
            keep.Sort();
            return BuildCanonical(uri, string.Join("&", keep));
        }
        catch (UriFormatException)
        {
            return url;
        }
    }

    private static string BuildCanonical(Uri uri, string query)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        if (string.IsNullOrEmpty(path)) path = "/";
        var builder = new UriBuilder(uri.Scheme.ToLowerInvariant(), uri.Host.ToLowerInvariant())
        {
            Path = path,
            Query = query,
        };
        return builder.ToString().TrimEnd('?');
    }

    // ---------------------------------------------------------------------
    // 文件与目录
    // ---------------------------------------------------------------------
    public static void EnsureDir(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public static string SanitizeFilename(string name)
    {
        if (string.IsNullOrEmpty(name)) return "download.bin";
        foreach (var ch in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*', '\0' })
            name = name.Replace(ch, '_');
        name = name.Trim('.', ' ');
        return string.IsNullOrEmpty(name) ? "download.bin" : name;
    }
}

/// <summary>
/// 线程安全的日志分发器。引擎与核心模块调用 Emit；GUI 注册 sink 收集日志。
/// </summary>
public static class LogHub
{
    private static readonly Lock Lock = new();
    private static readonly List<Action<string, string>> Sinks = [];

    public static void AddSink(Action<string, string> fn)
    {
        lock (Lock) Sinks.Add(fn);
    }

    public static void RemoveSink(Action<string, string> fn)
    {
        lock (Lock)
        {
            try { Sinks.Remove(fn); } catch { /* ignore */ }
        }
    }

    public static void Emit(string level, string message)
    {
        List<Action<string, string>> sinks;
        lock (Lock) sinks = [.. Sinks];
        if (sinks.Count == 0)
        {
            var tag = level switch
            {
                "info" => "[INFO]", "warn" => "[WARN]", "error" => "[ERROR]",
                "ok" => "[ OK ]", "debug" => "[DEBUG]", _ => $"[{level.ToUpperInvariant()}]"
            };
            Console.WriteLine($"{tag} {message}");
            return;
        }
        foreach (var fn in sinks)
        {
            try { fn(level, message); }
            catch { /* sink 不应抛异常影响下载 */ }
        }
    }
}
