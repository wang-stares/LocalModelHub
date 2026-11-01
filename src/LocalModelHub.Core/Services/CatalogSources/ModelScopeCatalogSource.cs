using System.Collections.Concurrent;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>魔搭 ModelScope 目录源：国内网络更稳定，抓取 GGUF 仓库后读取仓库文件列表。</summary>
public sealed class ModelScopeCatalogSource : ICatalogSource
{
    private const int MsMaxRepos = 260;

    private static readonly (string Query, int Limit)[] MsQueries =
    [
        ("gguf", 120), ("coder gguf", 50), ("qwen gguf", 50), ("qwen3 gguf", 40),
        ("deepseek gguf", 40), ("glm gguf", 25), ("reasoning gguf", 25),
        ("internlm gguf", 20), ("minimax gguf", 20), ("step gguf", 20), ("baichuan gguf", 15),
        ("kimi gguf", 15), ("hunyuan gguf", 15), ("ernie gguf", 15), ("moe gguf", 15)
    ];

    public string Id => RemoteCatalogService.ModelScopeId;

    public async Task<CatalogFetchResult> FetchAsync(
        CatalogFetchContext context,
        CancellationToken cancellationToken)
    {
        context.Progress?.Report("正在从魔搭 ModelScope 同步…");
        var repos = new ConcurrentDictionary<string, MsModelInfo>(StringComparer.OrdinalIgnoreCase);
        var failures = new ConcurrentBag<string>();

        async Task CollectAsync(string search, int limit)
        {
            try
            {
                var url = $"{RemoteCatalogService.ModelScopeHost}/openapi/v1/models" +
                          $"?search={Uri.EscapeDataString(search)}&page_size={limit}&page=1&sort=downloads";
                var json = await context.Http.GetStringAsync(url, cancellationToken);
                foreach (var repo in RemoteCatalogParsers.ParseModelScopeSearch(json))
                {
                    if (!RemoteCatalogParsers.IsGgufRepo(repo.Id) ||
                        RemoteCatalogParsers.IsNonTextRepo(repo.Tasks) ||
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

        await Task.WhenAll(MsQueries.Select(item => CollectAsync(item.Query, item.Limit)));

        var selected = repos.Values
            .OrderByDescending(repo => repo.Downloads)
            .Take(MsMaxRepos)
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
                if (context.Cached.TryGetValue("ms:" + repo.Id, out var known))
                {
                    known.Downloads = repo.Downloads;
                    return known;
                }

                try
                {
                    var json = await context.Http.GetStringAsync(
                        $"{RemoteCatalogService.ModelScopeHost}/api/v1/models/" +
                        $"{RemoteCatalogParsers.EscapeRepo(repo.Id)}/repo/files?Revision=master&Recursive=true",
                        cancellationToken);
                    return RemoteCatalogParsers.BuildModelScopeEntry(
                        repo, RemoteCatalogParsers.ParseModelScopeFiles(json));
                }
                catch
                {
                    return null;
                }
            },
            cancellationToken);

        var valid = models.Where(model => model is not null).Select(model => model!).ToList();
        context.Progress?.Report($"魔搭 ModelScope：同步到 {valid.Count} 个模型");
        return new CatalogFetchResult(Id, valid);
    }
}
