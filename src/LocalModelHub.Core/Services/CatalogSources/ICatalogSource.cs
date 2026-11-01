using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>单个远程目录源的抓取结果；Error 非空表示该源本次失败，其余源继续。</summary>
public sealed record CatalogFetchResult(
    string SourceId,
    IReadOnlyList<ModelEntry> Models,
    string? Error = null);

/// <summary>一次同步的共享上下文：HTTP 客户端、镜像开关、上次缓存的索引与进度回调。</summary>
public sealed record CatalogFetchContext(
    RemoteCatalogHttp Http,
    bool PreferHfMirror,
    IReadOnlyDictionary<string, ModelEntry> Cached,
    IReadOnlyDictionary<string, List<ModelEntry>> OllamaCache,
    string PreviousUpdatedAt,
    IProgress<string>? Progress);

/// <summary>远程目录源（HuggingFace / ModelScope / Ollama）的统一契约。</summary>
public interface ICatalogSource
{
    string Id { get; }

    Task<CatalogFetchResult> FetchAsync(CatalogFetchContext context, CancellationToken cancellationToken);
}
