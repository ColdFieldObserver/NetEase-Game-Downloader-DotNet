namespace NeteaseGameDownloader.Models;

using System.Collections.Generic;

/// <summary>
/// 一次即时校验的结果（对应 Python 的 VerifyResult）。
/// </summary>
public sealed class VerifyResult
{
    public bool Ok { get; set; }
    public string Md5 { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string? ExpectedMd5 { get; set; }
    public bool? Md5Match { get; set; }        // null = 未提供期望值
    public string? ApkCertSha256 { get; set; }
    public bool? ApkOfficial { get; set; }     // null = 非 APK / 未校验
    public List<string> Messages { get; set; } = [];
    public List<string> Removed { get; set; } = [];

    public bool Failed => !Ok;

    public string Summary()
    {
        var parts = new List<string> { Ok ? "校验通过" : "校验未通过" };
        if (!string.IsNullOrEmpty(Md5)) parts.Add($"MD5 {Md5[..Math.Min(12, Md5.Length)]}…");
        if (!string.IsNullOrEmpty(Sha256)) parts.Add($"SHA256 {Sha256[..Math.Min(12, Sha256.Length)]}…");
        if (!string.IsNullOrEmpty(ApkCertSha256))
        {
            var tag = ApkOfficial == true ? "网易官方证书" : "非内置官方指纹";
            parts.Add($"APK 签名 {ApkCertSha256[..Math.Min(12, ApkCertSha256.Length)]}…（{tag}）");
        }
        return string.Join(" | ", parts);
    }
}
