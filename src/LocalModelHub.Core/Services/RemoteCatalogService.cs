using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
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
}

public sealed class HfLfsInfo
{
    public long Size { get; set; }
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

public sealed record RepoFileOption(string Path, string Label, long Bytes)
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
    private const int HfMaxRepos = 400;
    private const int MsMaxRepos = 260;
    private const int OllamaTopModels = 16;
    private const int OllamaVariantsPerModel = 3;
    private const int OllamaQuantsPerVariant = 2;
    private const int MaxParallelRequests = 16;

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

    private static readonly (string Query, int Limit)[] MsQueries =
    [
        ("gguf", 120), ("coder gguf", 50), ("qwen gguf", 50), ("qwen3 gguf", 40),
        ("deepseek gguf", 40), ("glm gguf", 25), ("reasoning gguf", 25),
        ("internlm gguf", 20), ("minimax gguf", 20), ("step gguf", 20), ("baichuan gguf", 15),
        ("kimi gguf", 15), ("hunyuan gguf", 15), ("ernie gguf", 15), ("moe gguf", 15)
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Regex QuantRegex = new(
        @"(?<q>(?:UD[-_])?(?:IQ\d+_(?:XXS|XS|S|M|L)|IQ\d+_[A-Z0-9]+|Q\d+_K(?:_[A-Z0-9]+)?|Q\d+_\d+|Q\d+|MXFP4|BF16|FP16|F16|F32))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ParamsRegex = new(@"(\d+(?:\.\d+)?)\s*[bB](?![a-zA-Z])", RegexOptions.Compiled);
    private static readonly Regex ActiveParamsRegex = new(@"A(\d+(?:\.\d+)?)\s*[bB](?![a-zA-Z])", RegexOptions.Compiled);
    private static readonly Regex ContextRegex = new(@"(\d+)\s*[kK](?![a-zA-Z])", RegexOptions.Compiled);
    private static readonly Regex SplitRegex = new(@"-\d{5}-of-\d{5}", RegexOptions.Compiled);
    private static readonly Regex OllamaTagRegex = new(@"[^a-zA-Z0-9._-]", RegexOptions.Compiled);

    private static readonly string[] QuantPreference =
    [
        "Q4_K_M", "Q4_K_S", "Q5_K_M", "Q5_K_S", "Q8_0", "Q4_0", "Q6_K", "Q5_0",
        "Q3_K_M", "Q3_K_S", "Q2_K", "MXFP4", "F16", "BF16", "F32"
    ];

    private static readonly string[] NonTextMarkers =
    [
        "text-to-image", "text-to-video", "text-to-audio", "image-to-image", "image-segmentation",
        "image-classification", "object-detection", "depth-estimation", "image-feature-extraction",
        "automatic-speech-recognition", "text-to-speech", "audio-classification", "voice-activity"
    ];

    private static readonly string[] ChineseOrgs =
    [
        "qwen", "alibaba", "deepseek", "deepseek-ai", "zhipu", "zhipuai", "thudm", "baichuan", "01-ai",
        "internlm", "openbmb", "tencent", "bytedance", "moonshot", "moonshotai", "stepfun", "minimax",
        "modelbest", "shanghai_ai_laboratory", "iic", "xiaomi", "huawei", "baidu", "sensetime",
        "meituan", "antgroup", "iflytek", "01ai", "teleai", "gientech", "unisound", "wenge"
    ];

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _throttle = new(MaxParallelRequests, MaxParallelRequests);
    private bool _preferMirror;

    public RemoteCatalogService(HttpMessageHandler? handler = null, bool preferHfMirror = true)
    {
        _preferMirror = preferHfMirror;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("LocalModelHub/1.4");
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
        var cached = BuildCachedLookup(previous);
        var tasks = new List<Task<SourceFetch>>();
        if (sourceIds.Contains(HuggingFaceId))
        {
            tasks.Add(FetchHuggingFaceAsync(progress, cancellationToken, cached));
        }

        if (sourceIds.Contains(ModelScopeId))
        {
            tasks.Add(FetchModelScopeAsync(progress, cancellationToken, cached));
        }

        if (sourceIds.Contains(OllamaId))
        {
            tasks.Add(FetchOllamaAsync(progress, cancellationToken, BuildOllamaLookup(previous), previous?.UpdatedAt ?? ""));
        }

        var results = await Task.WhenAll(tasks);
        var models = results
            .SelectMany(result => result.Models)
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(model => model.Downloads)
            .ThenBy(model => model.ParametersB)
            .Take(MaxModels)
            .ToList();

        var statuses = results
            .Select(result => new CatalogSourceStatus(
                result.Id,
                result.Name,
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

    private async Task<SourceFetch> FetchHuggingFaceAsync(
        IProgress<string>? progress,
        CancellationToken ct,
        IReadOnlyDictionary<string, ModelEntry> cached)
    {
        progress?.Report("正在从 HuggingFace 社区同步…");
        var host = _preferMirror ? HuggingFaceMirrorHost : HuggingFaceHost;
        var alternate = _preferMirror ? HuggingFaceHost : HuggingFaceMirrorHost;
        var repos = new ConcurrentDictionary<string, HfModelInfo>(StringComparer.OrdinalIgnoreCase);
        var failures = new ConcurrentBag<string>();

        async Task CollectAsync(string search, int limit)
        {
            try
            {
                var query = string.IsNullOrWhiteSpace(search) ? "" : $"&search={Uri.EscapeDataString(search)}";
                var url = $"{host}/api/models?filter=gguf&sort=downloads&direction=-1&limit={limit}{query}";
                string json;
                try
                {
                    json = await GetStringAsync(url, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    var fallback = $"{alternate}/api/models?filter=gguf&sort=downloads&direction=-1&limit={limit}{query}";
                    json = await GetStringAsync(fallback, ct);
                }

                foreach (var repo in ParseHuggingFaceSearch(json))
                {
                    if (!IsGgufRepo(repo.Id) || IsNonTextRepo(repo.Tags)) continue;
                    repos.TryAdd(repo.Id, repo);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ShortError(ex));
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
            return new SourceFetch(HuggingFaceId, "HuggingFace 社区", [], reason);
        }

        var models = await MapAsync(
            selected,
            async repo =>
            {
                if (cached.TryGetValue("hf:" + repo.Id, out var known))
                {
                    known.Downloads = repo.Downloads;
                    return known;
                }

                try
                {
                    var json = await GetStringAsync(
                        $"{host}/api/models/{EscapeRepo(repo.Id)}/tree/main", ct);
                    return BuildHuggingFaceEntry(repo, ParseHuggingFaceTree(json));
                }
                catch
                {
                    return null;
                }
            },
            ct);

        var valid = models.Where(model => model is not null).Select(model => model!).ToList();
        progress?.Report($"HuggingFace 社区：同步到 {valid.Count} 个模型");
        return new SourceFetch(HuggingFaceId, "HuggingFace 社区", valid, null);
    }

    private async Task<SourceFetch> FetchModelScopeAsync(
        IProgress<string>? progress,
        CancellationToken ct,
        IReadOnlyDictionary<string, ModelEntry> cached)
    {
        progress?.Report("正在从魔搭 ModelScope 同步…");
        var repos = new ConcurrentDictionary<string, MsModelInfo>(StringComparer.OrdinalIgnoreCase);
        var failures = new ConcurrentBag<string>();

        async Task CollectAsync(string search, int limit)
        {
            try
            {
                var url = $"{ModelScopeHost}/openapi/v1/models?search={Uri.EscapeDataString(search)}" +
                          $"&page_size={limit}&page=1&sort=downloads";
                var json = await GetStringAsync(url, ct);
                foreach (var repo in ParseModelScopeSearch(json))
                {
                    if (!IsGgufRepo(repo.Id) || IsNonTextRepo(repo.Tasks) || IsNonTextRepo(repo.Tags)) continue;
                    repos.TryAdd(repo.Id, repo);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ShortError(ex));
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
            return new SourceFetch(ModelScopeId, "魔搭 ModelScope", [], reason);
        }

        var models = await MapAsync(
            selected,
            async repo =>
            {
                if (cached.TryGetValue("ms:" + repo.Id, out var known))
                {
                    known.Downloads = repo.Downloads;
                    return known;
                }

                try
                {
                    var json = await GetStringAsync(
                        $"{ModelScopeHost}/api/v1/models/{EscapeRepo(repo.Id)}/repo/files?Revision=master&Recursive=true",
                        ct);
                    return BuildModelScopeEntry(repo, ParseModelScopeFiles(json));
                }
                catch
                {
                    return null;
                }
            },
            ct);

        var valid = models.Where(model => model is not null).Select(model => model!).ToList();
        progress?.Report($"魔搭 ModelScope：同步到 {valid.Count} 个模型");
        return new SourceFetch(ModelScopeId, "魔搭 ModelScope", valid, null);
    }

    /// <summary>按来源 + 仓库名索引上一次同步结果，重复同步时跳过树/文件列表请求。</summary>
    private static Dictionary<string, ModelEntry> BuildCachedLookup(RemoteCatalogCache? previous)
    {
        var lookup = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in previous?.Models ?? [])
        {
            if (model.Quants.Length == 0) continue;
            var prefix = model.Origin switch
            {
                "HuggingFace" => "hf:",
                "ModelScope" => "ms:",
                _ => ""
            };

            if (string.IsNullOrWhiteSpace(prefix)) continue;

            var repo = !string.IsNullOrWhiteSpace(model.HfRepo)
                ? model.HfRepo
                : model.Quants[0].Sources.FirstOrDefault()?.Repo;
            if (string.IsNullOrWhiteSpace(repo)) continue;
            lookup.TryAdd(prefix + repo, model);
        }

        return lookup;
    }

    /// <summary>按 Ollama 模型名归集上次同步的条目（一个名字会对应多个变体条目）。</summary>
    private static Dictionary<string, List<ModelEntry>> BuildOllamaLookup(RemoteCatalogCache? previous)
    {
        var lookup = new Dictionary<string, List<ModelEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in previous?.Models ?? [])
        {
            if (model.Quants.Length == 0 || !model.Origin.Equals("Ollama", StringComparison.OrdinalIgnoreCase)) continue;

            var repo = model.Quants[0].Sources.FirstOrDefault()?.Repo ?? "";
            if (!repo.StartsWith("library/", StringComparison.OrdinalIgnoreCase)) continue;

            var name = repo["library/".Length..];
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!lookup.TryGetValue(name, out var list))
            {
                list = [];
                lookup[name] = list;
            }

            list.Add(model);
        }

        return lookup;
    }

    private static bool IsRecent(string timestamp, TimeSpan window)
    {
        if (!DateTime.TryParseExact(timestamp, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
        {
            return false;
        }

        var age = DateTime.Now - time;
        return age >= TimeSpan.Zero && age < window;
    }

    private async Task<SourceFetch> FetchOllamaAsync(
        IProgress<string>? progress,
        CancellationToken ct,
        IReadOnlyDictionary<string, List<ModelEntry>> cached,
        string cachedUpdatedAt)
    {
        progress?.Report("正在从 Ollama 官方库同步…");
        var results = new List<ModelEntry>();

        // Ollama 没有下载量数据，刚同步过的目录没必要再逐个抓 tags 与 manifest。
        var reuseRecent = IsRecent(cachedUpdatedAt, TimeSpan.FromHours(6));

        try
        {
            var listing = await GetStringAsync($"{OllamaLibraryHost}/library?sort=popular", ct);
            var names = ParseOllamaLibrary(listing).Take(OllamaTopModels).ToList();

            var batches = await MapAsync(names, async name =>
            {
                ct.ThrowIfCancellationRequested();
                if (reuseRecent && cached.TryGetValue(name, out var recent) && recent.Count > 0)
                {
                    return recent;
                }

                try
                {
                    var tagsHtml = await GetStringAsync($"{OllamaLibraryHost}/library/{Uri.EscapeDataString(name)}/tags", ct);
                    var tags = ParseOllamaTags(name, tagsHtml);
                    var selection = SelectOllamaQuants(tags);

                    // 标签集合没有变化时直接复用上次结果，跳过逐个 manifest 请求。
                    if (cached.TryGetValue(name, out var known) && CoversOllamaTags(known, selection))
                    {
                        return known;
                    }

                    return await BuildOllamaEntriesAsync(name, selection, ct);
                }
                catch
                {
                    // 单个模型失败不影响其它模型
                    return new List<ModelEntry>();
                }
            }, ct);

            foreach (var batch in batches.Where(item => item is not null))
            {
                results.AddRange(batch!);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SourceFetch(OllamaId, "Ollama 官方库", [], ShortError(ex));
        }

        progress?.Report($"Ollama 官方库：同步到 {results.Count} 个模型");
        return new SourceFetch(OllamaId, "Ollama 官方库", results, null);
    }

    /// <summary>把 tags 归并成「变体 → 前两个量化档」，与构建逻辑保持同一套选取规则。</summary>
    private static List<(string Variant, List<(string Quant, string Tag)> Quants)> SelectOllamaQuants(
        IReadOnlyList<string> tags)
    {
        var variants = new Dictionary<string, List<(string Quant, string Tag)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var split = SplitOllamaTag(tag);
            if (split is null) continue;
            if (!variants.TryGetValue(split.Value.Variant, out var list))
            {
                list = [];
                variants[split.Value.Variant] = list;
            }

            list.Add((split.Value.Quant, tag));
        }

        return variants
            .OrderBy(pair => pair.Key.Length)
            .Take(OllamaVariantsPerModel)
            .Select(pair => (
                Variant: pair.Key,
                Quants: pair.Value.OrderBy(item => QuantRank(item.Quant)).Take(OllamaQuantsPerVariant).ToList()))
            .ToList();
    }

    /// <summary>上次结果已覆盖本次要抓的全部 tag 时，可直接复用。</summary>
    private static bool CoversOllamaTags(
        IReadOnlyList<ModelEntry> entries,
        IReadOnlyList<(string Variant, List<(string Quant, string Tag)> Quants)> selection)
    {
        if (entries.Count == 0 || selection.Count == 0) return false;

        var known = entries
            .SelectMany(entry => entry.Quants)
            .SelectMany(quant => quant.Sources)
            .Where(source => source.Revision.Length > 0)
            .Select(source => source.Revision)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return selection
            .SelectMany(item => item.Quants)
            .All(item => known.Contains(item.Tag));
    }

    private async Task<List<ModelEntry>> BuildOllamaEntriesAsync(
        string name,
        IReadOnlyList<(string Variant, List<(string Quant, string Tag)> Quants)> selection,
        CancellationToken ct)
    {
        var entries = new List<ModelEntry>();
        foreach (var (variant, quants) in selection)
        {
            var built = new List<ModelQuant>();
            foreach (var (quantLabel, tag) in quants)
            {
                var manifest = await ReadOllamaManifestAsync(name, tag, ct);
                if (manifest is null) continue;

                built.Add(new ModelQuant
                {
                    Label = quantLabel,
                    TotalBytes = manifest.Size,
                    Files =
                    [
                        new ModelFile
                        {
                            Path = $"{SanitizeFileName(name)}-{SanitizeFileName(variant)}-{SanitizeFileName(quantLabel)}.gguf",
                            Bytes = manifest.Size,
                            Digest = manifest.Digest
                        }
                    ],
                    Sources =
                    [
                        new ModelSource
                        {
                            Provider = "Ollama",
                            Repo = $"library/{name}",
                            Revision = tag
                        }
                    ]
                });
            }

            if (built.Count == 0) continue;
            entries.Add(BuildOllamaEntry(name, variant, built));
        }

        return entries;
    }

    private async Task<OllamaLayer?> ReadOllamaManifestAsync(string name, string tag, CancellationToken ct)
    {
        try
        {
            var json = await GetStringAsync(
                $"{OllamaRegistryHost}/v2/library/{Uri.EscapeDataString(name)}/manifests/{Uri.EscapeDataString(tag)}",
                ct);
            return ParseOllamaManifest(json);
        }
        catch
        {
            return null;
        }
    }

    private ModelEntry BuildOllamaEntry(string name, string variant, List<ModelQuant> quants)
    {
        var display = string.Join(' ', new[] { name, variant }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var (parameters, active) = GuessParameters(variant);
        if (parameters <= 0)
        {
            (parameters, active) = GuessParameters(display);
        }

        if (parameters <= 0 && quants.Count > 0)
        {
            parameters = EstimateParameters(quants[0].TotalBytes);
            active = parameters;
        }

        var caps = InferCapabilities($"{name} {variant}", [], parameters);
        return new ModelEntry
        {
            Id = BuildId("ollama", $"{name}/{variant}"),
            Name = display,
            DisplayName = display,
            Developer = "Ollama 官方库",
            Country = "US",
            Family = name,
            ParametersB = parameters,
            ActiveParametersB = Math.Min(active > 0 ? active : parameters, parameters),
            ContextLength = 32768,
            ReleaseYear = DateTime.Now.Year,
            License = "",
            Description = $"Ollama 官方库 · {name} · 共 {quants.Count} 种量化",
            Tags = ["社区", "GGUF", name],
            Capabilities = caps,
            Quants = quants.ToArray(),
            Origin = "Ollama",
            Downloads = 0
        };
    }

    private async Task<IReadOnlyList<T?>> MapAsync<TIn, T>(
        IEnumerable<TIn> items,
        Func<TIn, Task<T?>> selector,
        CancellationToken ct)
    {
        var tasks = items.Select(async item =>
        {
            await _throttle.WaitAsync(ct);
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

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static string ShortError(Exception ex)
    {
        var message = ex is TaskCanceledException ? "请求超时" : ex.Message;
        return message.Length <= 120 ? message : message[..120];
    }

    // ---------- 解析 ----------

    public static IReadOnlyList<HfModelInfo> ParseHuggingFaceSearch(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<HfModelInfo>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<HfFileInfo> ParseHuggingFaceTree(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<HfFileInfo>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static HfModelInfo? ParseHuggingFaceModel(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<HfModelInfo>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析仓库链接或 owner/name 形式的仓库 ID；无法识别时返回 null。</summary>
    public static (string Provider, string Repo)? ParseRepoReference(string input, string defaultProvider)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var text = input.Trim();
        var provider = defaultProvider;
        string path;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if (host.Contains("modelscope")) provider = "ModelScope";
            else if (host.Contains("huggingface") || host.Contains("hf-mirror")) provider = "HuggingFace";
            path = uri.AbsolutePath;
        }
        else
        {
            path = text;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !segment.Equals("models", StringComparison.OrdinalIgnoreCase) &&
                              !segment.Equals("datasets", StringComparison.OrdinalIgnoreCase) &&
                              !segment.Equals("tree", StringComparison.OrdinalIgnoreCase) &&
                              !segment.Equals("files", StringComparison.OrdinalIgnoreCase) &&
                              !segment.Equals("resolve", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (segments.Count > 0)
        {
            var first = segments[0].ToLowerInvariant();
            if (first.Contains("huggingface") || first.Contains("hf-mirror") || first.Contains("modelscope"))
            {
                if (first.Contains("modelscope")) provider = "ModelScope";
                else if (first.Contains("huggingface") || first.Contains("hf-mirror")) provider = "HuggingFace";
                segments.RemoveAt(0);
            }
        }

        if (segments.Count < 2) return null;
        return (provider, $"{segments[0]}/{segments[1]}");
    }

    /// <summary>从仓库文件列表中筛出可单独使用的 GGUF 主权重（过滤附属文件与分片）。</summary>
    public static IReadOnlyList<RepoFileOption> BuildRepoFileOptions(IEnumerable<ModelFile> files)
    {
        return files
            .Where(file => file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Where(file => !IsAuxiliaryFile(file.Path))
            .Where(file => !SplitRegex.IsMatch(file.Path))
            .Select(file => new RepoFileOption(file.Path, ExtractQuantLabel(file.Path) ?? "GGUF", file.Bytes))
            .OrderBy(option => QuantRank(option.Label))
            .ThenByDescending(option => option.Bytes)
            .ToList();
    }

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
        var display = CleanDisplayName(repoName);
        var developer = repo.Split('/')[0];
        var license = "";
        var downloads = 0L;
        IReadOnlyList<ModelFile> files;

        if (provider.Equals("ModelScope", StringComparison.OrdinalIgnoreCase))
        {
            var json = await GetStringAsync(
                $"{ModelScopeHost}/api/v1/models/{EscapeRepo(repo)}/repo/files?Revision=master&Recursive=true",
                cancellationToken);
            files = ParseModelScopeFiles(json)
                .Where(file => file.Type.Equals("blob", StringComparison.OrdinalIgnoreCase))
                .Select(file => new ModelFile { Path = file.Path, Bytes = file.Size })
                .ToList();

            try
            {
                var searchJson = await GetStringAsync(
                    $"{ModelScopeHost}/openapi/v1/models?search={Uri.EscapeDataString(repoName)}" +
                    "&page_size=20&page=1&sort=downloads",
                    cancellationToken);
                var match = ParseModelScopeSearch(searchJson)
                    .FirstOrDefault(item => item.Id.Equals(repo, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    downloads = match.Downloads;
                    license = NormalizeLicense(match.License);
                    if (!string.IsNullOrWhiteSpace(match.DisplayName))
                    {
                        display = CleanDisplayName(match.DisplayName);
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
                json = await GetStringAsync($"{host}/api/models/{EscapeRepo(repo)}/tree/main", cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                host = _preferMirror ? HuggingFaceHost : HuggingFaceMirrorHost;
                json = await GetStringAsync($"{host}/api/models/{EscapeRepo(repo)}/tree/main", cancellationToken);
            }

            files = ParseHuggingFaceTree(json)
                .Select(file => new ModelFile { Path = file.Path, Bytes = file.ActualSize })
                .ToList();

            try
            {
                var detail = ParseHuggingFaceModel(
                    await GetStringAsync($"{host}/api/models/{EscapeRepo(repo)}", cancellationToken));
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
            GuessCountry(developer),
            license,
            downloads,
            BuildRepoFileOptions(files));
    }

    public static (double ParametersB, double ActiveB) GuessParametersFromName(string display) =>
        GuessParameters(display);

    public static int GuessContextFromName(string display) => GuessContext(display);

    public static ModelEntry? BuildHuggingFaceEntry(HfModelInfo repo, IReadOnlyList<HfFileInfo> files)
    {
        var modelFiles = files
            .Where(file => file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Select(file => new ModelFile { Path = file.Path, Bytes = file.ActualSize })
            .ToList();

        var quants = BuildQuantList(modelFiles, "HuggingFace", repo.Id, "main");
        if (quants.Length == 0) return null;

        var repoName = repo.Id.Split('/').Last();
        var developer = repo.Id.Contains('/') ? repo.Id.Split('/')[0] : "社区";
        var display = CleanDisplayName(repoName);
        var (parameters, active) = GuessParameters(display);
        if (parameters <= 0)
        {
            parameters = EstimateParameters(quants[0].TotalBytes);
            active = parameters;
        }

        var license = repo.Tags.FirstOrDefault(tag =>
            tag.StartsWith("license:", StringComparison.OrdinalIgnoreCase))?[8..] ?? "";
        var caps = InferCapabilities(display, repo.Tags, parameters);
        var releaseYear = ParseYear(repo.CreatedAt);

        return new ModelEntry
        {
            Id = BuildId("hf", repo.Id),
            Name = display,
            DisplayName = display,
            Developer = developer,
            Country = GuessCountry(developer),
            Family = display.Split(' ')[0],
            ParametersB = parameters,
            ActiveParametersB = Math.Min(active > 0 ? active : parameters, parameters),
            ContextLength = GuessContext(display),
            ReleaseYear = releaseYear,
            License = license,
            Description = BuildDescription("HuggingFace 社区", repo.Downloads, license, repo.Id),
            Tags = ["社区", "GGUF", repo.Id],
            Capabilities = caps,
            Quants = quants,
            HfRepo = repo.Id,
            Origin = "HuggingFace",
            Downloads = repo.Downloads
        };
    }

    public static IReadOnlyList<MsModelInfo> ParseModelScopeSearch(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("models", out var models))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<MsModelInfo>>(models.GetRawText(), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<MsFileInfo> ParseModelScopeFiles(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Data", out var data) ||
                !data.TryGetProperty("Files", out var files))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<MsFileInfo>>(files.GetRawText(), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static ModelEntry? BuildModelScopeEntry(MsModelInfo repo, IReadOnlyList<MsFileInfo> files)
    {
        var modelFiles = files
            .Where(file => file.Type.Equals("blob", StringComparison.OrdinalIgnoreCase))
            .Where(file => file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Select(file => new ModelFile { Path = file.Path, Bytes = file.Size })
            .ToList();

        var quants = BuildQuantList(modelFiles, "ModelScope", repo.Id, "master");
        if (quants.Length == 0) return null;

        var repoName = repo.Id.Split('/').Last();
        var developer = repo.Id.Contains('/') ? repo.Id.Split('/')[0] : "社区";
        var display = CleanDisplayName(string.IsNullOrWhiteSpace(repo.DisplayName) ? repoName : repo.DisplayName);
        var (parameters, active) = GuessParameters(display);
        if (repo.Params > 0)
        {
            parameters = Math.Round(repo.Params / 1_000_000_000d, 1);
        }

        if (parameters <= 0)
        {
            parameters = EstimateParameters(quants[0].TotalBytes);
        }

        if (active <= 0 || active > parameters) active = parameters;

        var license = NormalizeLicense(repo.License);
        var tags = repo.Tasks.Concat(repo.Tags).ToArray();
        var caps = InferCapabilities(display, tags, parameters);

        return new ModelEntry
        {
            Id = BuildId("ms", repo.Id),
            Name = display,
            DisplayName = display,
            Developer = developer,
            Country = GuessCountry(developer),
            Family = display.Split(' ')[0],
            ParametersB = parameters,
            ActiveParametersB = active,
            ContextLength = GuessContext(display),
            ReleaseYear = ParseYear(repo.CreatedAt),
            License = license,
            Description = BuildDescription("魔搭 ModelScope", repo.Downloads, license, repo.Id),
            Tags = ["社区", "GGUF", repo.Id],
            Capabilities = caps,
            Quants = quants,
            HfRepo = repo.Id,
            Origin = "ModelScope",
            Downloads = repo.Downloads
        };
    }

    public static IReadOnlyList<string> ParseOllamaLibrary(string html)
    {
        var names = new List<string>();
        foreach (Match match in Regex.Matches(html, @"href=""/library/([a-zA-Z0-9._-]+)"""))
        {
            var name = match.Groups[1].Value;
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }

        return names;
    }

    public static IReadOnlyList<string> ParseOllamaTags(string modelName, string html)
    {
        var prefix = $@"href=""/library/{Regex.Escape(modelName)}:";
        var tags = new List<string>();
        foreach (Match match in Regex.Matches(html, prefix + @"([^""]+)"""))
        {
            var tag = match.Groups[1].Value;
            if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
        }

        return tags;
    }

    public static OllamaLayer? ParseOllamaManifest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("layers", out var layers)) return null;

            foreach (var layer in layers.EnumerateArray())
            {
                var mediaType = layer.TryGetProperty("mediaType", out var type) ? type.GetString() : null;
                if (!string.Equals(mediaType, "application/vnd.ollama.image.model", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var digest = layer.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
                var size = layer.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
                if (string.IsNullOrWhiteSpace(digest) || size <= 0) return null;
                return new OllamaLayer(digest, size);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>把 qwen3:30b-a3b-q4_K_M 拆成 (30b-a3b, Q4_K_M)；没有量化后缀的标签返回 null。</summary>
    public static (string Variant, string Quant)? SplitOllamaTag(string tag)
    {
        var index = tag.LastIndexOf('-');
        if (index <= 0 || index >= tag.Length - 1) return null;

        var quant = tag[(index + 1)..];
        if (ExtractQuantLabel(quant + ".gguf") is null) return null;

        var variant = tag[..index];
        if (variant.Length == 0) return null;
        return (variant, quant.Replace('-', '_').ToUpperInvariant());
    }

    public static string? ExtractQuantLabel(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (SplitRegex.IsMatch(name)) name = SplitRegex.Replace(name, "");

        Match? last = null;
        foreach (Match match in QuantRegex.Matches(name))
        {
            last = match;
        }

        if (last is null) return null;
        var label = last.Groups["q"].Value.Replace('_', '_').ToUpperInvariant();
        return label.Replace("UD_", "UD-");
    }

    /// <summary>把 GGUF 文件按量化标签分组，过滤 mmproj / imatrix / LoRA 等附属文件。</summary>
    public static ModelQuant[] BuildQuantList(
        IReadOnlyList<ModelFile> files,
        string provider,
        string repo,
        string revision)
    {
        var eligible = files
            .Where(file => !IsAuxiliaryFile(file.Path))
            .Select(file => (File: file, Label: ExtractQuantLabel(file.Path)))
            .Where(item => item.Label is not null)
            .ToList();

        var quants = new List<ModelQuant>();
        foreach (var group in eligible.GroupBy(item => item.Label!, StringComparer.OrdinalIgnoreCase))
        {
            var byDirectory = group
                .GroupBy(item => Path.GetDirectoryName(item.File.Path) ?? "")
                .OrderBy(dir => dir.Key.Count(c => c == '/'))
                .ThenByDescending(dir => dir.Count())
                .First();

            var filesInGroup = byDirectory
                .Select(item => new ModelFile { Path = item.File.Path, Bytes = item.File.Bytes, Digest = item.File.Digest })
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            quants.Add(new ModelQuant
            {
                Label = group.Key,
                TotalBytes = filesInGroup.Sum(file => file.Bytes),
                Files = filesInGroup,
                Sources = [new ModelSource { Provider = provider, Repo = repo, Revision = revision }]
            });
        }

        return quants
            .OrderBy(quant => QuantRank(quant.Label))
            .ThenBy(quant => quant.TotalBytes)
            .Take(5)
            .ToArray();
    }

    private static bool IsAuxiliaryFile(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.Contains("mmproj")
               || lower.Contains("mtp/")
               || lower.Contains("mtp-")
               || lower.Contains("-mtp")
               || lower.Contains("imatrix")
               || lower.Contains("lora")
               || lower.Contains("adapter")
               || lower.Contains("tokenizer");
    }

    private static bool IsGgufRepo(string repoId)
    {
        var lower = repoId.ToLowerInvariant();
        return lower.Contains("gguf")
               && !lower.Contains("lora")
               && !lower.Contains("adapter")
               && !lower.Contains("imatrix");
    }

    /// <summary>来源标注为图像/语音/视频等任务时跳过，避免非文本模型混入模型库。</summary>
    private static bool IsNonTextRepo(IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            var lower = label.ToLowerInvariant();
            foreach (var marker in NonTextMarkers)
            {
                if (lower.Contains(marker)) return true;
            }
        }

        return false;
    }

    private static int QuantRank(string label)
    {
        var normalized = label.Replace('-', '_');
        if (normalized.StartsWith("UD_", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[3..];
        }

        var index = Array.FindIndex(QuantPreference, item =>
            item.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 99 : index;
    }

    private static (double Parameters, double Active) GuessParameters(string text)
    {
        var activeMatch = ActiveParamsRegex.Match(text);
        var active = activeMatch.Success && double.TryParse(activeMatch.Groups[1].Value, out var a) ? a : 0;

        foreach (Match match in ParamsRegex.Matches(text))
        {
            if (!double.TryParse(match.Groups[1].Value, out var value)) continue;
            if (active > 0) return (value, active);
            return (value, value);
        }

        return (0, 0);
    }

    private static double EstimateParameters(long bytes)
    {
        if (bytes <= 0) return 0;
        return Math.Round(bytes / Formatters.Gib / 0.62, 1);
    }

    private static int GuessContext(string name)
    {
        var match = ContextRegex.Match(name);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var k))
        {
            return Math.Clamp(k * 1024, 2048, 1_048_576);
        }

        return 32768;
    }

    private static string GuessCountry(string developer)
    {
        var lower = developer.ToLowerInvariant();
        return ChineseOrgs.Any(org => lower.Contains(org)) ? "CN" : "";
    }

    private static string[] InferCapabilities(string name, string[] tags, double parametersB)
    {
        var haystack = (name + " " + string.Join(' ', tags)).ToLowerInvariant();
        var capabilities = new List<string>();

        if (haystack.Contains("coder") || haystack.Contains("code")) capabilities.Add(ModelCapabilities.Coding);
        if (haystack.Contains("thinking") || haystack.Contains("reason") || haystack.Contains("r1"))
        {
            capabilities.Add(ModelCapabilities.Reasoning);
        }

        if (haystack.Contains("instruct") || haystack.Contains("chat") || haystack.Contains("conversational"))
        {
            capabilities.Add(ModelCapabilities.Chat);
        }

        if (haystack.Contains("tool") || haystack.Contains("function-call")) capabilities.Add(ModelCapabilities.Tools);
        if (parametersB > 0 && parametersB < 2) capabilities.Add(ModelCapabilities.Lightweight);
        if (haystack.Contains("multilingual") || haystack.Contains("qwen") || haystack.Contains("glm") ||
            haystack.Contains("internlm") || haystack.Contains("minimax"))
        {
            capabilities.Add(ModelCapabilities.Multilingual);
        }

        if (capabilities.Count == 0) capabilities.Add(ModelCapabilities.Chat);
        return capabilities.Distinct().ToArray();
    }

    private static string CleanDisplayName(string name)
    {
        var cleaned = Regex.Replace(name, @"[-_ ]?gguf$", "", RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? name : cleaned;
    }

    private static string NormalizeLicense(string license)
    {
        if (string.IsNullOrWhiteSpace(license)) return "";
        return license.Trim() switch
        {
            "Apache License 2.0" => "Apache-2.0",
            "MIT License" => "MIT",
            "Apache-2.0" => "Apache-2.0",
            _ => license.Trim()
        };
    }

    private static string BuildDescription(string sourceName, long downloads, string license, string repo)
    {
        var parts = new List<string> { sourceName };
        if (downloads > 0) parts.Add(FormatDownloads(downloads));
        if (!string.IsNullOrWhiteSpace(license)) parts.Add(license);
        parts.Add(repo);
        return string.Join(" · ", parts);
    }

    private static string FormatDownloads(long downloads) => downloads switch
    {
        >= 10_000 => $"{downloads / 10_000d:0.#} 万次下载",
        _ => $"{downloads} 次下载"
    };

    private static int ParseYear(string value) =>
        DateTime.TryParse(value, out var date) ? date.Year : DateTime.Now.Year;

    public static string BuildId(string prefix, string rawKey)
    {
        var slug = Regex.Replace(rawKey.ToLowerInvariant(), "[^a-z0-9.]+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)))[..6].ToLowerInvariant();
        return string.IsNullOrWhiteSpace(slug) ? $"{prefix}-{hash}" : $"{prefix}-{slug}-{hash}";
    }

    private static string SanitizeFileName(string value) =>
        OllamaTagRegex.Replace(value, "-").Trim('-');

    private static string EscapeRepo(string repo) =>
        string.Join('/', repo.Split('/').Select(Uri.EscapeDataString));

    // ---------- 缓存 ----------

    public RemoteCatalogCache LoadCache() => LoadCacheFrom(AppPaths.RemoteCatalogFile);

    public void SaveCache(RemoteCatalogCache cache) => SaveCacheTo(AppPaths.RemoteCatalogFile, cache);

    public static RemoteCatalogCache LoadCacheFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return new RemoteCatalogCache();
            var json = File.ReadAllText(path);
            var cache = JsonSerializer.Deserialize<RemoteCatalogCache>(json, JsonOptions);
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
        File.WriteAllText(temp, JsonSerializer.Serialize(cache, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private sealed record SourceFetch(string Id, string Name, IReadOnlyList<ModelEntry> Models, string? Error);
}
