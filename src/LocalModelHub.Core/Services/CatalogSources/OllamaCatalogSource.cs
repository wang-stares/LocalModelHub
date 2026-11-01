using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>Ollama 官方库目录源：读取 library 列表、tags 与 registry manifest。</summary>
public sealed class OllamaCatalogSource : ICatalogSource
{
    private const int OllamaTopModels = 16;
    private const int OllamaVariantsPerModel = 3;
    private const int OllamaQuantsPerVariant = 2;

    public string Id => RemoteCatalogService.OllamaId;

    public async Task<CatalogFetchResult> FetchAsync(
        CatalogFetchContext context,
        CancellationToken cancellationToken)
    {
        context.Progress?.Report("正在从 Ollama 官方库同步…");
        var results = new List<ModelEntry>();

        // Ollama 没有下载量数据，刚同步过的目录没必要再逐个抓 tags 与 manifest。
        var reuseRecent = CatalogCacheIndex.IsRecent(context.PreviousUpdatedAt, TimeSpan.FromHours(6));

        try
        {
            var listing = await context.Http.GetStringAsync(
                $"{RemoteCatalogService.OllamaLibraryHost}/library?sort=popular", cancellationToken);
            var names = RemoteCatalogParsers.ParseOllamaLibrary(listing)
                .Take(OllamaTopModels)
                .ToList();

            var batches = await context.Http.MapAsync(
                names,
                async name =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reuseRecent && context.OllamaCache.TryGetValue(name, out var recent) && recent.Count > 0)
                    {
                        return recent;
                    }

                    try
                    {
                        var tagsHtml = await context.Http.GetStringAsync(
                            $"{RemoteCatalogService.OllamaLibraryHost}/library/{Uri.EscapeDataString(name)}/tags",
                            cancellationToken);
                        var tags = RemoteCatalogParsers.ParseOllamaTags(name, tagsHtml);
                        var selection = SelectOllamaQuants(tags);

                        // 标签集合没有变化时直接复用上次结果，跳过逐个 manifest 请求。
                        if (context.OllamaCache.TryGetValue(name, out var known) &&
                            CoversOllamaTags(known, selection))
                        {
                            return known;
                        }

                        return await BuildOllamaEntriesAsync(name, selection, context, cancellationToken);
                    }
                    catch
                    {
                        // 单个模型失败不影响其它模型
                        return new List<ModelEntry>();
                    }
                },
                cancellationToken);

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
            return new CatalogFetchResult(Id, [], RemoteCatalogHttp.ShortError(ex));
        }

        context.Progress?.Report($"Ollama 官方库：同步到 {results.Count} 个模型");
        return new CatalogFetchResult(Id, results);
    }

    /// <summary>把 tags 归并成「变体 → 前两个量化档」，与构建逻辑保持同一套选取规则。</summary>
    internal static List<(string Variant, List<(string Quant, string Tag)> Quants)> SelectOllamaQuants(
        IReadOnlyList<string> tags)
    {
        var variants = new Dictionary<string, List<(string Quant, string Tag)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var split = RemoteCatalogParsers.SplitOllamaTag(tag);
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
                Quants: pair.Value
                    .OrderBy(item => RemoteCatalogParsers.QuantRank(item.Quant))
                    .Take(OllamaQuantsPerVariant)
                    .ToList()))
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

    private static async Task<List<ModelEntry>> BuildOllamaEntriesAsync(
        string name,
        IReadOnlyList<(string Variant, List<(string Quant, string Tag)> Quants)> selection,
        CatalogFetchContext context,
        CancellationToken cancellationToken)
    {
        var entries = new List<ModelEntry>();
        foreach (var (variant, quants) in selection)
        {
            var built = new List<ModelQuant>();
            foreach (var (quantLabel, tag) in quants)
            {
                var manifest = await ReadOllamaManifestAsync(name, tag, context, cancellationToken);
                if (manifest is null) continue;

                built.Add(new ModelQuant
                {
                    Label = quantLabel,
                    TotalBytes = manifest.Size,
                    Files =
                    [
                        new ModelFile
                        {
                            Path = $"{RemoteCatalogParsers.SanitizeFileName(name)}-" +
                                   $"{RemoteCatalogParsers.SanitizeFileName(variant)}-" +
                                   $"{RemoteCatalogParsers.SanitizeFileName(quantLabel)}.gguf",
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

    private static async Task<OllamaLayer?> ReadOllamaManifestAsync(
        string name,
        string tag,
        CatalogFetchContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await context.Http.GetStringAsync(
                $"{RemoteCatalogService.OllamaRegistryHost}/v2/library/" +
                $"{Uri.EscapeDataString(name)}/manifests/{Uri.EscapeDataString(tag)}",
                cancellationToken);
            return RemoteCatalogParsers.ParseOllamaManifest(json);
        }
        catch
        {
            return null;
        }
    }

    private static ModelEntry BuildOllamaEntry(string name, string variant, List<ModelQuant> quants)
    {
        var display = string.Join(' ', new[] { name, variant }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var (parameters, active) = RemoteCatalogParsers.GuessParametersFromName(variant);
        if (parameters <= 0)
        {
            (parameters, active) = RemoteCatalogParsers.GuessParametersFromName(display);
        }

        if (parameters <= 0 && quants.Count > 0)
        {
            parameters = RemoteCatalogParsers.EstimateParameters(quants[0].TotalBytes);
            active = parameters;
        }

        var caps = RemoteCatalogParsers.InferCapabilities($"{name} {variant}", [], parameters);
        return new ModelEntry
        {
            Id = RemoteCatalogParsers.BuildId("ollama", $"{name}/{variant}"),
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
}
