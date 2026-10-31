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
        return new ModelCatalog
        {
            Version = builtin.Version,
            UpdatedAt = builtin.UpdatedAt,
            SyncedAt = remote?.UpdatedAt ?? "",
            RemoteCount = remoteAdded.Count,
            Models = models
        };
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
