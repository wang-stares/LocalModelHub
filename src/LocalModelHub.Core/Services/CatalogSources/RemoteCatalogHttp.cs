using System.Net;
using System.Net.Http;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 远程目录同步用的共享 HTTP 客户端：统一超时 / UA / 并发上限，
/// 让各源只关心「抓什么」，不再各自 new HttpClient。
/// </summary>
public sealed class RemoteCatalogHttp
{
    private const int MaxParallelRequests = 16;

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _throttle = new(MaxParallelRequests, MaxParallelRequests);

    public RemoteCatalogHttp(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("LocalModelHub/1.4");
    }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>按并发上限分批执行异步选择器，保持输入顺序。</summary>
    public async Task<IReadOnlyList<T?>> MapAsync<TIn, T>(
        IEnumerable<TIn> items,
        Func<TIn, Task<T?>> selector,
        CancellationToken cancellationToken)
    {
        var tasks = items.Select(async item =>
        {
            await _throttle.WaitAsync(cancellationToken);
            try
            {
                return await selector(item);
            }
            finally
            {
                _throttle.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    public static string ShortError(Exception ex)
    {
        var message = ex is TaskCanceledException ? "请求超时" : ex.Message;
        return message.Length <= 120 ? message : message[..120];
    }
}
