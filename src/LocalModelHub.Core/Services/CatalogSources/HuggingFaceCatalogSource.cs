using System.Collections.Concurrent;
using System.Net.Http;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>HuggingFace 社区目录源：多关键词抓取 GGUF 仓库，再按仓库批量取文件树。</summary>
public sealed class HuggingFaceCatalogSource : ICatalogSource
{
    private const int HfMaxRepos = 400;

    /// <summary>多个关键词各抓一页，覆盖热门、编程、推理等不同用途；结果按仓库去重。</summary>
    private static readonly (string Query, int Limit)[] HfQueries =
    [
        ("", 120), ("coder", 50), ("instruct", 40), ("reasoning", 35),
        ("qwen", 40), ("qwen3", 30), ("deepseek", 35), ("llama", 30), ("mistral", 25),
        ("gemma", 25), ("glm", 25), ("phi", 20), ("minicpm", 20),
        ("yi", 20), ("internlm", 20), ("baichuan", 15), ("moe", 20),
        ("kimi", 15), ("minimax", 15), ("hunyuan", 15), ("ernie", 15),
        ("step", 15), ("gpt-oss", 15), ("smollm", 15), ("lfm", 15), ("nanbeige", 10)
    ];

    public string Id => RemoteCatalogService.HuggingFaceId;

    public async Task<CatalogFetchResult> FetchAsync(
        CatalogFetchContext context,
        CancellationToken cancellationToken)
    {
        context.Progress?.Report("正在从 HuggingFace 社区同步…");
        var host = context.PreferHfMirror
            ? RemoteCatalogService.HuggingFaceMirrorHost
            : RemoteCatalogService.HuggingFaceHost;
        var alternate = context.PreferHfMirror
            ? RemoteCatalogService.HuggingFaceHost
            : RemoteCatalogService.HuggingFaceMirrorHost;
        var repos = new ConcurrentDictionary<string, HfModelInfo>(StringComparer.OrdinalIgnoreCase);
        var failures = new ConcurrentBag<string>();

        async Task CollectAsync(string search, int limit)
        {
            try
            {
                var query = string.IsNullOrWhiteSpace(search)
                    ? ""
                    : $"&search={Uri.EscapeDataString(search)}";
                var url = $"{host}/api/models?filter=gguf&sort=downloads&direction=-1&limit={limit}{query}";
                string json;
                try
                {
                    json = await context.Http.GetStringAsync(url, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    var fallback = $"{alternate}/api/models?filter=gguf&sort=downloads&direction=-1&limit={limit}{query}";
                    json = await context.Http.GetStringAsync(fallback, cancellationToken);
                }

                foreach (var repo in RemoteCatalogParsers.ParseHuggingFaceSearch(json))
                {
                    if (!RemoteCatalogParsers.IsGgufRepo(repo.Id) ||
                        RemoteCatalogParsers.IsNonTextRepo(repo.Tags)) continue;
                    repos.TryAdd(repo.Id, repo);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(RemoteCatalogHttp.ShortError(ex));
            }
        }

        // 多个关键词并发抓取，任何一个失败都不影响其它关键词的结果。
        await Task.WhenAll(HfQueries.Select(item => CollectAsync(item.Query, item.Limit)));

        var selected = repos.Values
            .OrderByDescending(repo => repo.Downloads)
            .Take(HfMaxRepos)
            .ToList();

        if (selected.Count == 0)
        {
            var reason = failures.IsEmpty ? "未获取到 GGUF 仓库" : failures.First();
            return new CatalogFetchResult(Id, [], reason);
        }

        var models = await context.Http.MapAsync(
            selected,
            async repo =>
            {
                if (context.Cached.TryGetValue("hf:" + repo.Id, out var known))
                {
                    known.Downloads = repo.Downloads;
                    return known;
                }

                try
                {
                    var json = await context.Http.GetStringAsync(
                        $"{host}/api/models/{RemoteCatalogParsers.EscapeRepo(repo.Id)}/tree/main",
                        cancellationToken);
                    return RemoteCatalogParsers.BuildHuggingFaceEntry(
                        repo, RemoteCatalogParsers.ParseHuggingFaceTree(json));
                }
                catch
                {
                    return null;
                }
            },
            cancellationToken);

        var valid = models.Where(model => model is not null).Select(model => model!).ToList();
        context.Progress?.Report($"HuggingFace 社区：同步到 {valid.Count} 个模型");
        return new CatalogFetchResult(Id, valid);
    }
}
