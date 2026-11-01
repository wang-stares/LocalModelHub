using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

public sealed record CatalogSourceDefinition(string Id, string Name, string Description, string Homepage);

public sealed record CatalogSourceStatus(
    string Id,
    string Name,
    int Count,
    string? Error,
    string FetchedAt);

public sealed class RemoteCatalogCache
{
    public int Version { get; set; } = 1;
    public string UpdatedAt { get; set; } = "";
    public List<CatalogSourceStatus> Sources { get; set; } = [];
    public List<ModelEntry> Models { get; set; } = [];
}

public sealed class HfModelInfo
{
    public string Id { get; set; } = "";
    public long Downloads { get; set; }
    public long Likes { get; set; }
    public string[] Tags { get; set; } = [];
    public string CreatedAt { get; set; } = "";
}

public sealed class HfFileInfo
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public HfLfsInfo? Lfs { get; set; }

    public long ActualSize => Lfs is { Size: > 0 } ? Lfs.Size : Size;

    /// <summary>LFS 文件的 SHA-256（lfs.oid）；非 LFS 文件为空。</summary>
    public string? Digest => string.IsNullOrWhiteSpace(Lfs?.Sha256) ? null : $"sha256:{Lfs.Sha256}";
}

public sealed class HfLfsInfo
{
    public long Size { get; set; }

    /// <summary>HuggingFace API 的 oid 字段即文件 SHA-256（64 位十六进制）。</summary>
    [JsonPropertyName("oid")]
    public string Sha256 { get; set; } = "";
}

public sealed class MsModelInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long Downloads { get; set; }
    public long Likes { get; set; }
    public string License { get; set; } = "";
    public string[] Tasks { get; set; } = [];
    public string[] Tags { get; set; } = [];
    public long Params { get; set; }
    public long FileSize { get; set; }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";
}

public sealed class MsFileInfo
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Type { get; set; } = "blob";
}

public sealed record OllamaLayer(string Digest, long Size);

public sealed record RepoFileOption(string Path, string Label, long Bytes, string? Digest = null)
{
    public string Display => $"{Label} · {Formatters.Bytes(Bytes)} · {Path}";
}

/// <summary>仓库解析结果，用于「添加自定义模型」时自动填充表单。</summary>
public sealed record RepoInspection(
    string Provider,
    string Repo,
    string DisplayName,
    string Developer,
    string Country,
    string License,
    long Downloads,
    IReadOnlyList<RepoFileOption> Files);

/// <summary>
/// 从 HuggingFace / 魔搭 / Ollama 官方库动态抓取 GGUF 模型清单。
/// 结果以 JSON 缓存落盘，离线时继续使用上次同步的目录。
/// </summary>
public sealed class RemoteCatalogService
{
    public const string HuggingFaceId = "huggingface";
    public const string ModelScopeId = "modelscope";
    public const string OllamaId = "ollama";

    public static readonly CatalogSourceDefinition[] Sources =
    [
        new(HuggingFaceId, "HuggingFace 社区", "全球最大的开源模型社区，按下载量抓取 GGUF 仓库", "https://huggingface.co"),
        new(ModelScopeId, "魔搭 ModelScope", "阿里云模型社区，国内网络访问更稳定", "https://modelscope.cn"),
        new(OllamaId, "Ollama 官方库", "Ollama 官方模型库，直接读取 registry 中的 GGUF 权重", "https://ollama.com/library")
    ];

    public const string HuggingFaceHost = "https://huggingface.co";
    public const string HuggingFaceMirrorHost = "https://hf-mirror.com";
    public const string ModelScopeHost = "https://modelscope.cn";
    public const string OllamaRegistryHost = "https://registry.ollama.ai";
    public const string OllamaLibraryHost = "https://ollama.com";

    private const int MaxModels = 900;

    private readonly RemoteCatalogHttp _http;
    private readonly IReadOnlyList<ICatalogSource> _sources;
    private bool _preferMirror;

    /// <param name="sources">可注入自定义目录源（测试或插件化）；默认使用 HF / ModelScope / Ollama 三源。</param>
    public RemoteCatalogService(
        HttpMessageHandler? handler = null,
        bool preferHfMirror = true,
        IReadOnlyList<ICatalogSource>? sources = null)
    {
        _preferMirror = preferHfMirror;
        _http = new RemoteCatalogHttp(handler);
        _sources = sources ??
        [
            new HuggingFaceCatalogSource(),
            new ModelScopeCatalogSource(),
            new OllamaCatalogSource()
        ];
    }

    /// <summary>HuggingFace 下载默认走国内镜像；可在设置中切换。</summary>
    public bool PreferHfMirror
    {
        get => _preferMirror;
        set => _preferMirror = value;
    }

    public async Task<RemoteCatalogCache> SyncAsync(
        IReadOnlyCollection<string> sourceIds,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        RemoteCatalogCache? previous = null)
    {
        var context = new CatalogFetchContext(
            _http,
            PreferHfMirror,
            CatalogCacheIndex.Build(previous),
            CatalogCacheIndex.BuildOllama(previous),
            previous?.UpdatedAt ?? "",
            progress);

        var enabled = _sources.Where(source => sourceIds.Contains(source.Id)).ToList();
        var results = await Task.WhenAll(
            enabled.Select(source => FetchSafeAsync(source, context, cancellationToken)));
        var models = CatalogMergeService.MergeSources(results, MaxModels).ToList();

        var statuses = results
            .Select(result => new CatalogSourceStatus(
                result.SourceId,
                SourceName(result.SourceId),
                result.Models.Count,
                result.Error,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm")))
            .ToList();

        return new RemoteCatalogCache
        {
            Version = 1,
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Sources = statuses,
            Models = models
        };
    }

    /// <summary>各源返回后统一兜底：单源异常不拖垮整次同步，状态里带上可诊断信息。</summary>
    private static async Task<CatalogFetchResult> FetchSafeAsync(
        ICatalogSource source,
        CatalogFetchContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await source.FetchAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"catalog source {source.Id} failed: {ex.Message}");
            return new CatalogFetchResult(source.Id, [], RemoteCatalogHttp.ShortError(ex));
        }
    }

    private static string SourceName(string id) =>
        Sources.FirstOrDefault(source => source.Id == id)?.Name ?? id;

    /// <summary>读取仓库元数据与 GGUF 文件列表，供添加自定义模型时自动填充。</summary>
    public async Task<RepoInspection?> InspectRepoAsync(
        string input,
        string defaultProvider,
        CancellationToken cancellationToken)
    {
        var parsed = ParseRepoReference(input, defaultProvider);
        if (parsed is not { } value) return null;
        var (provider, repo) = value;

        var repoName = repo.Split('/').Last();
        var display = RemoteCatalogParsers.CleanDisplayName(repoName);
        var developer = repo.Split('/')[0];
        var license = "";
        var downloads = 0L;
        IReadOnlyList<ModelFile> files;

        if (provider.Equals("ModelScope", StringComparison.OrdinalIgnoreCase))
        {
            var json = await _http.GetStringAsync(
                $"{ModelScopeHost}/api/v1/models/{EscapeRepo(repo)}/repo/files?Revision=master&Recursive=true",
                cancellationToken);
            files = ParseModelScopeFiles(json)
                .Where(file => file.Type.Equals("blob", StringComparison.OrdinalIgnoreCase))
                .Select(file => new ModelFile
                {
                    Path = file.Path,
                    Bytes = file.Size,
                    Digest = string.IsNullOrWhiteSpace(file.Sha256) ? null : $"sha256:{file.Sha256}"
                })
                .ToList();

            try
            {
                var searchJson = await _http.GetStringAsync(
                    $"{ModelScopeHost}/openapi/v1/models?search={Uri.EscapeDataString(repoName)}" +
                    "&page_size=20&page=1&sort=downloads",
                    cancellationToken);
                var match = ParseModelScopeSearch(searchJson)
                    .FirstOrDefault(item => item.Id.Equals(repo, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    downloads = match.Downloads;
                    license = RemoteCatalogParsers.NormalizeLicense(match.License);
                    if (!string.IsNullOrWhiteSpace(match.DisplayName))
                    {
                        display = RemoteCatalogParsers.CleanDisplayName(match.DisplayName);
                    }
                }
            }
            catch
            {
                // 元数据接口失败不影响文件列表
            }
        }
        else
        {
            var host = _preferMirror ? HuggingFaceMirrorHost : HuggingFaceHost;
            string json;
            try
            {
                json = await _http.GetStringAsync($"{host}/api/models/{EscapeRepo(repo)}/tree/main", cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                host = _preferMirror ? HuggingFaceHost : HuggingFaceMirrorHost;
                json = await _http.GetStringAsync($"{host}/api/models/{EscapeRepo(repo)}/tree/main", cancellationToken);
            }

            files = ParseHuggingFaceTree(json)
                .Select(file => new ModelFile { Path = file.Path, Bytes = file.ActualSize, Digest = file.Digest })
                .ToList();

            try
            {
                var detail = ParseHuggingFaceModel(
                    await _http.GetStringAsync($"{host}/api/models/{EscapeRepo(repo)}", cancellationToken));
                if (detail is not null)
                {
                    downloads = detail.Downloads;
                    license = detail.Tags.FirstOrDefault(tag =>
                        tag.StartsWith("license:", StringComparison.OrdinalIgnoreCase))?[8..] ?? "";
                }
            }
            catch
            {
                // 元数据接口失败不影响文件列表
            }
        }

        return new RepoInspection(
            provider,
            repo,
            display,
            developer,
            RemoteCatalogParsers.GuessCountry(developer),
            license,
            downloads,
            BuildRepoFileOptions(files));
    }

    // ---------- 解析（转发到 RemoteCatalogParsers，保持公开签名） ----------

    public static IReadOnlyList<HfModelInfo> ParseHuggingFaceSearch(string json) =>
        RemoteCatalogParsers.ParseHuggingFaceSearch(json);

    public static IReadOnlyList<HfFileInfo> ParseHuggingFaceTree(string json) =>
        RemoteCatalogParsers.ParseHuggingFaceTree(json);

    public static HfModelInfo? ParseHuggingFaceModel(string json) =>
        RemoteCatalogParsers.ParseHuggingFaceModel(json);

    public static (string Provider, string Repo)? ParseRepoReference(string input, string defaultProvider) =>
        RemoteCatalogParsers.ParseRepoReference(input, defaultProvider);

    /// <summary>从仓库文件列表中筛出可单独使用的 GGUF 主权重（过滤附属文件与分片）。</summary>
    public static IReadOnlyList<RepoFileOption> BuildRepoFileOptions(IEnumerable<ModelFile> files) =>
        RemoteCatalogParsers.BuildRepoFileOptions(files);

    public static (double ParametersB, double ActiveB) GuessParametersFromName(string display) =>
        RemoteCatalogParsers.GuessParametersFromName(display);

    public static int GuessContextFromName(string display) =>
        RemoteCatalogParsers.GuessContextFromName(display);

    public static ModelEntry? BuildHuggingFaceEntry(HfModelInfo repo, IReadOnlyList<HfFileInfo> files) =>
        RemoteCatalogParsers.BuildHuggingFaceEntry(repo, files);

    public static IReadOnlyList<MsModelInfo> ParseModelScopeSearch(string json) =>
        RemoteCatalogParsers.ParseModelScopeSearch(json);

    public static IReadOnlyList<MsFileInfo> ParseModelScopeFiles(string json) =>
        RemoteCatalogParsers.ParseModelScopeFiles(json);

    public static ModelEntry? BuildModelScopeEntry(MsModelInfo repo, IReadOnlyList<MsFileInfo> files) =>
        RemoteCatalogParsers.BuildModelScopeEntry(repo, files);

    public static IReadOnlyList<string> ParseOllamaLibrary(string html) =>
        RemoteCatalogParsers.ParseOllamaLibrary(html);

    public static IReadOnlyList<string> ParseOllamaTags(string modelName, string html) =>
        RemoteCatalogParsers.ParseOllamaTags(modelName, html);

    public static OllamaLayer? ParseOllamaManifest(string json) =>
        RemoteCatalogParsers.ParseOllamaManifest(json);

    public static (string Variant, string Quant)? SplitOllamaTag(string tag) =>
        RemoteCatalogParsers.SplitOllamaTag(tag);

    public static string? ExtractQuantLabel(string fileName) =>
        RemoteCatalogParsers.ExtractQuantLabel(fileName);

    public static ModelQuant[] BuildQuantList(
        IReadOnlyList<ModelFile> files,
        string provider,
        string repo,
        string revision) =>
        RemoteCatalogParsers.BuildQuantList(files, provider, repo, revision);

    public static string BuildId(string prefix, string rawKey) =>
        RemoteCatalogParsers.BuildId(prefix, rawKey);

    internal static bool IsGgufRepo(string repoId) =>
        RemoteCatalogParsers.IsGgufRepo(repoId);

    internal static bool IsNonTextRepo(IEnumerable<string> labels) =>
        RemoteCatalogParsers.IsNonTextRepo(labels);

    internal static int QuantRank(string label) =>
        RemoteCatalogParsers.QuantRank(label);

    internal static double EstimateParameters(long bytes) =>
        RemoteCatalogParsers.EstimateParameters(bytes);

    internal static string[] InferCapabilities(string name, string[] tags, double parametersB) =>
        RemoteCatalogParsers.InferCapabilities(name, tags, parametersB);

    internal static string SanitizeFileName(string value) =>
        RemoteCatalogParsers.SanitizeFileName(value);

    internal static string EscapeRepo(string repo) =>
        RemoteCatalogParsers.EscapeRepo(repo);

    // ---------- 缓存 ----------

    public RemoteCatalogCache LoadCache() => LoadCacheFrom(AppPaths.RemoteCatalogFile);

    public void SaveCache(RemoteCatalogCache cache) => SaveCacheTo(AppPaths.RemoteCatalogFile, cache);

    public static RemoteCatalogCache LoadCacheFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return new RemoteCatalogCache();
            var json = File.ReadAllText(path);
            var cache = JsonSerializer.Deserialize<RemoteCatalogCache>(json, RemoteCatalogParsers.JsonOptions);
            if (cache is null) return new RemoteCatalogCache();

            cache.Models = cache.Models
                .Where(model => !string.IsNullOrWhiteSpace(model.Id) && model.Quants.Length > 0)
                .ToList();
            foreach (var model in cache.Models) model.IsCustom = false;
            return cache;
        }
        catch
        {
            return new RemoteCatalogCache();
        }
    }

    public static void SaveCacheTo(string path, RemoteCatalogCache cache)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(cache, RemoteCatalogParsers.JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
