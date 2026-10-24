namespace NeteaseGameDownloader.Core;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NeteaseGameDownloader.Models;

/// <summary>
/// 产品注册表与解析辅助（对应 Python 的 products.py）。
/// 新增产品只需在 PRODUCTS 中登记一条 ProductSpec。
/// </summary>
public static partial class Products
{
    // ---------------------------------------------------------------------
    // 官方接口
    // ---------------------------------------------------------------------
    public const string MumuVersionApi = "https://api.mumu.nie.netease.com";
    public const string MumuDlApiWin = "https://mumu.nie.netease.com/api/dl/win";
    public const string MumuDlApiMac = "https://mumu.nie.netease.com/api/dl/mac";
    public const string EggyPage = "https://adl.netease.com/d/g/party/c/gw";
    public const string MumuPage = "https://adl.netease.com/d/g/mumu/c/gw";

    private static readonly string[] ApkFallback = [@"https://[^\s""'<>]+\.apk(?:[^\s""'<>]*)"];
    private static readonly string[] WinFallback =
    [
        @"https://[^\s""'<>]+\.exe(?:[^\s""'<>]*)",
        @"https://[^\s""'<>]+\.zip(?:[^\s""'<>]*)",
        @"https://[^\s""'<>]+\.dmg(?:[^\s""'<>]*)",
    ];

    public static readonly Dictionary<string, ProductSpec> PRODUCTS = new()
    {
        ["eggy-android"] = new ProductSpec
        {
            Key = "eggy-android",
            DisplayName = "蛋仔派对 · Android APK",
            Short = "蛋仔派对 APK",
            Desc = "网易官方渠道最新版蛋仔派对安卓安装包",
            Platform = "android",
            PageUrl = $"{EggyPage}?type=android",
            LinkVar = "android_link",
            TypeVar = "android_type",
            Ua = Utils.USER_AGENT_ANDROID,
            DefaultOutput = "蛋仔派对_android.apk",
            FallbackPatterns = ApkFallback,
        },
        ["eggy-pc"] = new ProductSpec
        {
            Key = "eggy-pc",
            DisplayName = "蛋仔派对 · PC 端游安装包",
            Short = "蛋仔派对 PC",
            Desc = "蛋仔派对 Windows 端游官方安装包",
            Platform = "windows",
            PageUrl = $"{EggyPage}?type=pc",
            LinkVar = "pc_link",
            TypeVar = "pc_type",
            Ua = Utils.USER_AGENT_DEFAULT,
            DefaultOutput = "蛋仔派对_pc.exe",
            FallbackPatterns = WinFallback,
        },
        ["mumu-windows"] = new ProductSpec
        {
            Key = "mumu-windows",
            DisplayName = "MuMu模拟器 · Windows 版（MuMu 6）",
            Short = "MuMu模拟器 Win",
            Desc = "网易 MuMu 模拟器 Windows 最新版（MuMu 6 代际，官方直连接口）",
            Platform = "windows",
            PageUrl = $"{MumuPage}?type=pc",
            LinkVar = "pc_link",
            Ua = Utils.USER_AGENT_DEFAULT,
            DefaultOutput = "MuMu模拟器_windows.exe",
            FallbackPatterns = WinFallback,
            VersionApi = MumuVersionApi,
            DirectApi = MumuDlApiWin,
            GenerationHint = "mumu6",
        },
        ["mumu-mac"] = new ProductSpec
        {
            Key = "mumu-mac",
            DisplayName = "MuMu模拟器 · macOS 版",
            Short = "MuMu模拟器 Mac",
            Desc = "网易 MuMu 模拟器 macOS 版（官方直连接口，支持 Intel / Apple 芯片）",
            Platform = "macos",
            PageUrl = $"{MumuPage}?type=pc",
            LinkVar = "mac_link",
            Ua = Utils.USER_AGENT_DEFAULT,
            DefaultOutput = "MuMu模拟器_mac.dmg",
            FallbackPatterns = WinFallback,
            VersionApi = MumuVersionApi,
            DirectApi = MumuDlApiMac,
        },
    };

    public static ProductSpec GetProduct(string key)
    {
        if (PRODUCTS.TryGetValue(key, out var p)) return p;
        throw new ArgumentException($"未知产品: {key}（可选: {string.Join(", ", PRODUCTS.Keys)}）");
    }

    public static IReadOnlyList<ProductSpec> ListProducts() => [.. PRODUCTS.Values];

    // ---------------------------------------------------------------------
    // 版本号 / 文件名
    // ---------------------------------------------------------------------
    private static readonly Regex VersionRe = _versionRe();

    public static string? ExtractVersion(string url)
    {
        var name = url.Split('?')[0].TrimEnd('/').Split('/')[^1];
        var m = VersionRe.Match(name);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string UrlBasename(string url)
    {
        var name = url.Split('?')[0].TrimEnd('/').Split('/')[^1];
        return name;
    }

    // ---------------------------------------------------------------------
    // MuMu 代际识别
    // ---------------------------------------------------------------------
    private static readonly string[] Mumu12Markers =
        ["mumu12", "nemuinstaller", "mumuinstaller_3", "mumuinstaller_4"];

    public static string MumuGeneration(string url)
    {
        var name = UrlBasename(url).ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) return "unknown";
        if (name.Contains("mumu12") || name.StartsWith("mumuinstaller")) return "mumu12";
        var ver = ExtractVersion(url);
        if (name.StartsWith("mumu_") && ver is not null)
            return "mumu6" == ver[..1] || "56789".Contains(ver[..1]) ? "mumu6" : "unknown";
        if (name.StartsWith("mumuplayer")) return "mumu-mac";
        foreach (var marker in Mumu12Markers)
            if (name.Contains(marker)) return "mumu12";
        return "unknown";
    }

    public static bool IsStaleMumuLink(ProductSpec product, string url)
    {
        if (product.GenerationHint != "mumu6") return false;
        return MumuGeneration(url) == "mumu12";
    }

    // ---------------------------------------------------------------------
    // 官方版本 API（交叉核对，不可达仅返回 null）
    // ---------------------------------------------------------------------
    public static async Task<Dictionary<string, object>?> FetchMumuVersionInfoAsync(
        string apiBase = MumuVersionApi)
    {
        try
        {
            var url = $"{apiBase}/api/website/download_version_info?usage=0";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(Utils.USER_AGENT_DEFAULT);
            using var resp = await HttpClients.Redirect.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("errcode", out var ec) || ec.GetInt32() != 100)
                return null;
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;

            System.Text.Json.JsonElement? win = null;
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("platform", out var pf) && pf.GetString() == "win")
                {
                    win = item;
                    break;
                }
            }
            if (win is null) return null;

            var engine = GetString(win.Value, "default_download_engine")
                        ?? GetString(win.Value, "engine") ?? "";

            var compStrings = new List<string>();
            if (win.Value.TryGetProperty("components", out var comps) && comps.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var c in comps.EnumerateArray())
                {
                    var name = GetString(c, "name") ?? "";
                    var ver = GetString(c, "version");
                    if (name == engine && ver is not null)
                        compStrings.Add($"{engine}@{ver}");
                }
            }

            var result = new Dictionary<string, object>
            {
                ["version"] = GetString(win.Value, "version") ?? "",
                ["update_time"] = GetString(win.Value, "update_time") ?? "",
                ["engine"] = engine,
                ["default_download_engine"] = GetString(win.Value, "default_download_engine") ?? "",
                ["installer_hint"] = compStrings.Count > 0 ? string.Join(" ", compStrings) : engine,
                ["components"] = string.Join(", ", compStrings),
            };
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? GetString(System.Text.Json.JsonElement e, string prop)
    {
        return e.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()
            : null;
    }

    // ---------------------------------------------------------------------
    // 官方直连接口（302 Location）
    // ---------------------------------------------------------------------
    public static async Task<string?> ResolveDirectLinkAsync(
        string endpoint, string ua = Utils.USER_AGENT_DEFAULT, int retries = 3)
    {
        Exception? lastErr = null;
        for (var attempt = 0; attempt < Math.Max(1, retries); attempt++)
        {
            if (attempt > 0)
                await Task.Delay(Math.Min((int)Math.Pow(2, attempt - 1) * 1000, 5000));
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                req.Headers.UserAgent.ParseAdd(ua);
                using var resp = await HttpClients.NoRedirect.SendAsync(req);
                var loc = resp.Headers.Location?.OriginalString;
                if ((resp.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect
                        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
                        or HttpStatusCode.PermanentRedirect) && !string.IsNullOrEmpty(loc))
                {
                    if (loc.StartsWith("//")) loc = "https:" + loc;
                    return loc;
                }
                if (resp.IsSuccessStatusCode && !string.IsNullOrEmpty(loc))
                    return loc;
                lastErr = new InvalidOperationException($"接口未返回跳转地址 (HTTP {(int)resp.StatusCode})");
            }
            catch (Exception e)
            {
                lastErr = e;
            }
        }
        if (lastErr is not null)
            throw new InvalidOperationException($"官方直链接口不可用: {lastErr.Message}");
        return null;
    }

    [GeneratedRegex(@"(\d+\.\d+(?:\.\d+){1,3})", RegexOptions.Compiled)]
    private static partial Regex _versionRe();
}
