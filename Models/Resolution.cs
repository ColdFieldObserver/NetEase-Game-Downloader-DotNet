namespace NeteaseGameDownloader.Models;

using System.Collections.Generic;

/// <summary>
/// 一次解析的完整结果（对应 Python 的 Resolution）。
/// </summary>
public sealed class Resolution
{
    public string Url { get; set; } = "";             // 实际用于下载的地址
    public string Source { get; set; } = "";          // manual / official-api / adl-page / fallback
    public ProductSpec? Product { get; set; }
    public string Canonical { get; set; } = "";        // 规范化后的地址（断点指纹用）
    public string Filename { get; set; } = "";         // URL 中的文件名
    public string? Version { get; set; }               // 从文件名提取的版本号
    public List<string> Notes { get; set; } = [];   // 告警/说明（UI 展示）
    public List<string> Attempts { get; set; } = [];// 各步骤尝试记录

    public void Note(string msg)
    {
        if (!Notes.Contains(msg)) Notes.Add(msg);
    }

    public string Brief()
    {
        var label = Source switch
        {
            "manual" => "手动指定",
            "official-api" => "官方直连接口",
            "adl-page" => "adl 分发页",
            "fallback" => "页面兜底扫描",
            _ => Source
        };
        var ver = string.IsNullOrEmpty(Version) ? "" : $" v{Version}";
        return $"{label}{ver}";
    }
}
