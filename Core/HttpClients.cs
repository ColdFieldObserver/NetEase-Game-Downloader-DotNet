namespace NeteaseGameDownloader.Core;

using System.Net;
using System.Net.Http;

/// <summary>
/// 共享的 HttpClient 实例（线程安全、连接池复用）。
/// 禁用压缩（Accept-Encoding: identity）以保证 Range 字节位置精确；
/// 用 keep-alive 复用连接。下载引擎与解析器共用。
/// 单个请求的读超时由引擎的停滞看门狗（CancellationToken）控制。
/// </summary>
public static class HttpClients
{
    public static HttpClient Redirect { get; } = Create(true);
    public static HttpClient NoRedirect { get; } = Create(false);

    private static HttpClient Create(bool autoRedirect)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = autoRedirect,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 64,
            UseCookies = false,
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan, // 由引擎层按读超时/看门狗控制
        };
        client.DefaultRequestHeaders.ConnectionClose = false;
        client.DefaultRequestHeaders.Add("Accept-Encoding", "identity");
        return client;
    }
}
