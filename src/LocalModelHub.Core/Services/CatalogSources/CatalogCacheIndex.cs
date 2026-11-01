using System.Globalization;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>把上一次同步缓存整理成「来源 + 仓库」索引，供各源跳过重复的树 / 文件请求。</summary>
public static class CatalogCacheIndex
{
    public static Dictionary<string, ModelEntry> Build(RemoteCatalogCache? previous)
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
    public static Dictionary<string, List<ModelEntry>> BuildOllama(RemoteCatalogCache? previous)
    {
        var lookup = new Dictionary<string, List<ModelEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in previous?.Models ?? [])
        {
            if (model.Quants.Length == 0 ||
                !model.Origin.Equals("Ollama", StringComparison.OrdinalIgnoreCase)) continue;

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

    public static bool IsRecent(string timestamp, TimeSpan window)
    {
        if (!DateTime.TryParseExact(timestamp, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
        {
            return false;
        }

        var age = DateTime.Now - time;
        return age >= TimeSpan.Zero && age < window;
    }
}
