namespace NeteaseGameDownloader.Core;

using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NeteaseGameDownloader.Models;

/// <summary>
/// 页面解析模块（对应 Python 的 parser.py）。
/// 从网易 adl 统一分发页（JS 变量形式）动态解析最新安装包地址，
/// 兼容三元表达式 / 简单字符串变量 / 兜底正则三种结构。
/// </summary>
public static class Parser
{
    private const int PageRetries = 3;

    public static async Task<string> FetchPageAsync(string url, string userAgent)
    {
        Utils.LogInfo($"正在获取下载页面: {url}");
        Exception? lastErr = null;
        for (var attempt = 0; attempt < PageRetries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(userAgent);
                req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                req.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
                using var resp = await HttpClients.Redirect.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var page = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(page))
                    throw new InvalidOperationException("下载页面内容为空");
                return page;
            }
            catch (Exception e)
            {
                lastErr = e;
                if (attempt < PageRetries - 1)
                    Utils.LogWarn($"页面获取失败({e.Message})，重试 {attempt + 1}/{PageRetries - 1} ...");
            }
        }
        throw new HttpRequestException($"获取下载页面失败: {lastErr?.Message}");
    }

    public static string ParseLink(string pageText, string linkVar, string? typeVar,
                                   string[] fallbackPatterns)
    {
        string? downloadUrl = null;

        // 1. 三元表达式形式
        var ml = Regex.Match(
            pageText,
            $@"var\s+{Regex.Escape(linkVar)}\s*=\s*(\w+)\s*\?\s*""([^""]+)""\s*:\s*""([^""]+)""\s*;",
            RegexOptions.IgnoreCase);
        if (ml.Success)
        {
            var exprVar = ml.Groups[1].Value;
            var trueBranch = ml.Groups[2].Value;
            var falseBranch = ml.Groups[3].Value;
            var isTrue = false;
            var mt = Regex.Match(pageText,
                $@"var\s+{Regex.Escape(exprVar)}\s*=\s*(true|false)\s*;",
                RegexOptions.IgnoreCase);
            if (mt.Success)
            {
                isTrue = mt.Groups[1].Value.Equals("true", StringComparison.InvariantCultureIgnoreCase);
                Utils.LogInfo($"解析到 {exprVar}: {mt.Groups[1].Value}");
            }
            else if (typeVar is not null)
            {
                var mt2 = Regex.Match(pageText,
                    $@"var\s+{Regex.Escape(typeVar)}\s*=\s*(true|false)\s*;",
                    RegexOptions.IgnoreCase);
                isTrue = mt2.Success && mt2.Groups[1].Value.Equals("true", StringComparison.InvariantCultureIgnoreCase);
            }
            downloadUrl = isTrue ? trueBranch : falseBranch;
            Utils.LogInfo($"通过三元表达式解析到下载地址 ({(isTrue ? "true" : "false")} 分支)");
        }

        // 2. 简单字符串变量形式
        if (string.IsNullOrEmpty(downloadUrl))
        {
            var ms = Regex.Match(pageText,
                $@"var\s+{Regex.Escape(linkVar)}\s*=\s*""([^""]+)""\s*;",
                RegexOptions.IgnoreCase);
            if (ms.Success && ms.Groups[1].Value.StartsWith("http"))
            {
                downloadUrl = ms.Groups[1].Value;
                Utils.LogInfo($"通过字符串变量 {linkVar} 解析到下载地址");
            }
        }

        // 3. 兜底正则
        if (string.IsNullOrEmpty(downloadUrl))
        {
            Utils.LogWarn($"未匹配到 {linkVar} 变量，尝试直接扫描页面中的下载链接...");
            foreach (var pat in fallbackPatterns)
            {
                var m = Regex.Match(pageText, pat, RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    downloadUrl = m.Groups[0].Value;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException($"无法从页面中解析出 {linkVar} 下载地址（页面结构可能已变更）");

        // HTML 实体反转义
        return System.Net.WebUtility.HtmlDecode(downloadUrl);
    }
}
