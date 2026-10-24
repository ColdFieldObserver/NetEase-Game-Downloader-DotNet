namespace NeteaseGameDownloader.Core;

using System;
using System.Threading.Tasks;
using NeteaseGameDownloader.Models;

/// <summary>
/// 下载地址统一解析链（对应 Python 的 resolver.py）。
/// 优先级：手动指定 URL → 官方直连接口 → adl 分发页 → 兜底。
/// </summary>
public static class Resolver
{
    public static async Task<Resolution> ResolveViaDirectApiAsync(ProductSpec product, Resolution res)
    {
        if (string.IsNullOrEmpty(product.DirectApi)) return res;
        try
        {
            var url = await Products.ResolveDirectLinkAsync(product.DirectApi, product.Ua);
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http"))
            {
                res.Attempts.Add("官方直连接口返回空地址");
                return res;
            }
            res.Url = url;
            res.Source = "official-api";
            res.Attempts.Add($"官方直连接口命中: {Products.UrlBasename(url)}");
            Utils.LogInfo($"官方直连接口 -> {Products.UrlBasename(url)}");
            if (Products.IsStaleMumuLink(product, url))
                res.Note("官方直连接口返回的仍是旧代际（MuMu12）安装包，" +
                         "如与官网版本不符请到 mumu.163.com 核对");
        }
        catch (Exception e)
        {
            res.Attempts.Add($"官方直连接口失败: {e.Message}");
            Utils.LogDebug($"direct_api 失败: {e.Message}");
        }
        return res;
    }

    public static async Task<Resolution> ResolveViaPageAsync(ProductSpec product, Resolution res)
    {
        try
        {
            var pageText = await Parser.FetchPageAsync(product.PageUrl, product.Ua);
            var url = Parser.ParseLink(pageText, product.LinkVar, product.TypeVar, product.FallbackPatterns);
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http"))
            {
                res.Attempts.Add("adl 分发页未解析出有效地址");
                return res;
            }
            res.Url = url;
            res.Source = "adl-page";
            res.Attempts.Add($"adl 分发页命中: {Products.UrlBasename(url)}");
            Utils.LogInfo($"adl 分发页 -> {Products.UrlBasename(url)}");
            if (Products.IsStaleMumuLink(product, url))
                res.Note("⚠ 该链接来自 adl 分发页，仍是旧的 MuMu12 代际安装包" +
                         "（官方直连接口当前不可达，已自动降级）；" +
                         "官网最新为 MuMu 6，建议稍后重试以获取最新版");
        }
        catch (Exception e)
        {
            res.Attempts.Add($"adl 分发页解析失败: {e.Message}");
        }
        return res;
    }

    public static async Task<Resolution> ResolveProductAsync(ProductSpec product, string? manualUrl)
    {
        var res = new Resolution { Product = product };

        if (!string.IsNullOrWhiteSpace(manualUrl))
        {
            res.Url = manualUrl.Trim();
            res.Source = "manual";
            Fill(res);
            return res;
        }

        // 1. 官方直连接口
        if (product.DirectApi is not null)
        {
            await ResolveViaDirectApiAsync(product, res);
            if (res.Source == "official-api")
            {
                Fill(res);
                return res;
            }
        }

        // 2. adl 分发页
        await ResolveViaPageAsync(product, res);
        if (res.Source == "adl-page")
        {
            Fill(res);
            return res;
        }

        throw new InvalidOperationException(
            $"无法解析 {product.DisplayName} 的下载地址。\n" +
            string.Join("\n", res.Attempts.ConvertAll(a => "  - " + a)) +
            "\n可用手动指定下载地址。");
    }

    /// <summary>下载中途 CDN 签名过期返回 403 时重新解析地址；失败返回 null。</summary>
    public static async Task<string?> RefreshUrlAsync(Resolution resolution)
    {
        if (resolution.Product is null || resolution.Source == "manual") return null;
        try
        {
            var newRes = await ResolveProductAsync(resolution.Product, null);
            if (newRes.Canonical != resolution.Canonical && !string.IsNullOrEmpty(newRes.Url))
            {
                Utils.LogWarn("下载地址已刷新（原链接签名可能已过期）");
                return newRes.Url;
            }
            return newRes.Url;
        }
        catch (Exception e)
        {
            Utils.LogDebug($"重新解析失败: {e.Message}");
            return null;
        }
    }

    private static void Fill(Resolution res)
    {
        res.Canonical = Utils.CanonicalUrl(res.Url);
        res.Filename = Products.UrlBasename(res.Url);
        res.Version = Products.ExtractVersion(res.Url);
    }
}
