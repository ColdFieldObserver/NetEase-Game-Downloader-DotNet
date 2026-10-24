namespace NeteaseGameDownloader.Models;

/// <summary>
/// 一个可下载产品的元数据（对应 Python 的 ProductSpec）。
/// </summary>
public sealed class ProductSpec
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string PageUrl { get; init; } = "";        // adl 分发页 URL
    public string LinkVar { get; init; } = "";        // 页面中存放下载地址的 JS 变量名
    public string Ua { get; init; } = "";             // 抓取页面用的 User-Agent
    public string DefaultOutput { get; init; } = "";   // 默认输出文件名
    public string Short { get; init; } = "";           // UI 短名
    public string Desc { get; init; } = "";            // 一句话说明
    public string Platform { get; init; } = "";        // windows / android / macos
    public string? TypeVar { get; init; }              // 三元表达式的 type 变量名
    public string[] FallbackPatterns { get; init; } = [];
    public string? VersionApi { get; init; }            // 官方版本 API base
    public string? DirectApi { get; init; }             // 官方最新直链接口（302 跳转）
    public string? GenerationHint { get; init; }        // 期望代际标识（如 mumu6）
}
