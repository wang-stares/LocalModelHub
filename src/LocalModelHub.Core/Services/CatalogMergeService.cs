namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>把内置目录、用户自定义模型与远程同步结果合并成一份完整目录。</summary>
public static class CatalogMergeService
{
    public static ModelCatalog Merge(
        ModelCatalog builtin,
        IReadOnlyList<ModelEntry> custom,
        RemoteCatalogCache? remote)
    {
        var models = new List<ModelEntry>(builtin.Models.Count + custom.Count + (remote?.Models.Count ?? 0));
        models.AddRange(builtin.Models);
        models.AddRange(custom);

        var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            knownIds.Add(model.Id);
            foreach (var key in RepoKeys(model)) knownRepos.Add(key);
        }

        var remoteAdded = new List<ModelEntry>();
        foreach (var model in remote?.Models ?? [])
        {
            if (string.IsNullOrWhiteSpace(model.Id) || model.Quants.Length == 0) continue;
            if (!knownIds.Add(model.Id)) continue;
            if (RepoKeys(model).Any(knownRepos.Contains)) continue;

            foreach (var key in RepoKeys(model)) knownRepos.Add(key);
            remoteAdded.Add(model);
        }

        models.AddRange(remoteAdded);
        var catalog = new ModelCatalog
        {
            Version = builtin.Version,
            UpdatedAt = builtin.UpdatedAt,
            SyncedAt = remote?.UpdatedAt ?? "",
            RemoteCount = remoteAdded.Count,
            Models = models
        };

        SanitizeQuantSizes(catalog.Models);
        return catalog;
    }

    /// <summary>
    /// 把多个目录源的结果合并成一份列表：跳过失败源，同一模型 id 只保留最先出现的条目。
    /// </summary>
    public static IReadOnlyList<ModelEntry> MergeSources(
        IEnumerable<CatalogFetchResult> results,
        int limit = 900)
    {
        var merged = new List<ModelEntry>(Math.Min(limit * 2, 2000));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            if (!string.IsNullOrWhiteSpace(result.Error)) continue;
            foreach (var model in result.Models)
            {
                if (string.IsNullOrWhiteSpace(model.Id)) continue;
                if (model.Quants.Length == 0) continue;
                if (!seen.Add(model.Id)) continue;
                merged.Add(model);
            }
        }

        return merged
            .OrderByDescending(model => model.Downloads)
            .ThenBy(model => model.ParametersB)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// 量化体积单调性校验：低位宽的体积不应大于高位宽（升量化反而“更能装”是元数据错误）。
    /// 违例时按参数量 × 位宽系数重算偏差更大的一档，并标记「估算」，UI 数字前加“约”。
    /// </summary>
    private static void SanitizeQuantSizes(IEnumerable<ModelEntry> models)
    {
        foreach (var model in models)
        {
            if (model.ParametersB <= 0 || model.Quants.Length < 2) continue;

            // 两轮足够：第一轮修正明显偏差，第二轮处理修正后暴露出的新倒挂。
            for (var pass = 0; pass < 2; pass++)
            {
                var ordered = model.Quants
                    .OrderBy(quant => RecommendationEngine.QuantQuality(quant.Label))
                    .ThenBy(quant => quant.Label, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var changed = false;
                for (var index = 1; index < ordered.Count; index++)
                {
                    var lower = ordered[index - 1];
                    var higher = ordered[index];
                    if (RecommendationEngine.QuantQuality(lower.Label) ==
                        RecommendationEngine.QuantQuality(higher.Label)) continue;
                    if (lower.TotalBytes <= higher.TotalBytes) continue;

                    var lowerEstimate = EstimateBytes(model.ParametersB, lower.Label);
                    var higherEstimate = EstimateBytes(model.ParametersB, higher.Label);
                    if (lowerEstimate <= 0 || higherEstimate <= 0) continue;

                    var lowerDeviation = Deviation(lower.TotalBytes, lowerEstimate);
                    var higherDeviation = Deviation(higher.TotalBytes, higherEstimate);
                    var target = lowerDeviation >= higherDeviation ? lower : higher;
                    var estimate = target == lower ? lowerEstimate : higherEstimate;
                    target.TotalBytes = estimate;
                    target.Note = "估算";
                    changed = true;
                }

                if (!changed) break;
            }
        }
    }

    private static double Deviation(long actual, long expected) =>
        expected <= 0 ? 0 : Math.Abs(actual - expected) / (double)expected;

    /// <summary>按参数量 × 位宽系数估算 GGUF 体积（1.02 为元数据开销）；未知量化返回 0。</summary>
    private static long EstimateBytes(double parametersB, string label)
    {
        var text = (label ?? "").ToUpperInvariant();
        var bits = text switch
        {
            var value when value.Contains("F32") => 32,
            var value when value.Contains("BF16") || value.Contains("F16") => 16,
            var value when value.Contains("Q8") || value.Contains("IQ8") => 8.5,
            var value when value.Contains("Q6") => 6.6,
            var value when value.Contains("Q5") => 5.5,
            var value when value.Contains("Q4") || value.Contains("IQ4") => 4.5,
            var value when value.Contains("Q3") || value.Contains("IQ3") => 3.4,
            var value when value.Contains("Q2") || value.Contains("IQ2") => 2.6,
            var value when value.Contains("IQ1") || value.Contains("Q1") => 1.7,
            _ => 0
        };

        return bits <= 0 ? 0 : (long)(parametersB * 1e9 * bits / 8 * 1.02);
    }

    private static IEnumerable<string> RepoKeys(ModelEntry model)
    {
        if (!string.IsNullOrWhiteSpace(model.HfRepo)) yield return "HuggingFace:" + model.HfRepo;

        foreach (var quant in model.Quants)
        {
            foreach (var source in quant.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.Repo)) continue;
                var revision = source.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
                    ? "#" + source.Revision
                    : "";
                yield return $"{source.Provider}:{source.Repo}{revision}";
            }
        }
    }
}

public sealed record CatalogSyncSummary(int Count, IReadOnlyList<CatalogSourceStatus> Sources, string UpdatedAt)
{
    public string Describe()
    {
        var failed = Sources.Where(source => !string.IsNullOrWhiteSpace(source.Error)).ToList();
        var text = $"已从 {Sources.Count} 个源同步 {Count} 个模型";
        if (failed.Count > 0)
        {
            text += "；" + string.Join("；", failed.Select(source => $"{source.Name}：{source.Error}"));
        }

        return text;
    }
}
