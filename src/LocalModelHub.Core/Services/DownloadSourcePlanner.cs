namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

public sealed record DownloadSourceCandidate(string Provider, string Label, string Url);

/// <summary>
/// 按「自动 / 手动单选多选 / 失败换源」设置，为一个文件规划候选下载地址。
/// 自动模式优先使用模型的原生源，再按魔搭 → HuggingFace → Ollama 依次尝试；
/// 手动模式只使用勾选的源，开启换源后追加其余可用源。
/// </summary>
public static class DownloadSourcePlanner
{
    public static readonly string[] Providers = ["ModelScope", "HuggingFace", "Ollama"];

    public static IReadOnlyList<DownloadSourceCandidate> Build(
        ModelEntry model,
        ModelQuant quant,
        ModelFile file,
        AppSettings settings)
    {
        var hfRepo = ResolveRepo(quant, "HuggingFace") ?? model.HfRepo;
        var msRepo = ResolveRepo(quant, "ModelScope") ?? model.HfRepo;
        var ollama = quant.Sources.FirstOrDefault(source => ProviderIs(source, "Ollama"));

        var available = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(hfRepo)) available.Add("HuggingFace");
        if (!string.IsNullOrWhiteSpace(msRepo)) available.Add("ModelScope");
        if (ollama is not null && !string.IsNullOrWhiteSpace(file.Digest)) available.Add("Ollama");
        if (available.Count == 0) return [];

        var native = quant.Sources.FirstOrDefault()?.Provider ?? "";
        var order = new List<string>(3);
        if (settings.DownloadAuto)
        {
            AddIfAvailable(order, available, native);
            foreach (var provider in Providers) AddIfAvailable(order, available, provider);
        }
        else
        {
            foreach (var provider in settings.DownloadProviders) AddIfAvailable(order, available, provider);
            if (settings.AutoSwitchSource)
            {
                foreach (var provider in Providers) AddIfAvailable(order, available, provider);
            }
        }

        var switchHost = settings.DownloadAuto || settings.AutoSwitchSource;
        var candidates = new List<DownloadSourceCandidate>(order.Count + 1);
        foreach (var provider in order)
        {
            switch (provider)
            {
                case "HuggingFace":
                    var preferred = settings.UseHfMirror
                        ? RemoteCatalogService.HuggingFaceMirrorHost
                        : RemoteCatalogService.HuggingFaceHost;
                    Add(candidates, "HuggingFace", HostLabel(preferred),
                        DownloadUrlBuilder.BuildHuggingFace(hfRepo!, file.Path, preferred));
                    if (switchHost)
                    {
                        var alternate = settings.UseHfMirror
                            ? RemoteCatalogService.HuggingFaceHost
                            : RemoteCatalogService.HuggingFaceMirrorHost;
                        Add(candidates, "HuggingFace", HostLabel(alternate),
                            DownloadUrlBuilder.BuildHuggingFace(hfRepo!, file.Path, alternate));
                    }

                    break;
                case "ModelScope":
                    Add(candidates, "ModelScope", "ModelScope",
                        DownloadUrlBuilder.BuildModelScope(msRepo!, file.Path, "master"));
                    break;
                case "Ollama":
                    Add(candidates, "Ollama", "Ollama", DownloadUrlBuilder.Build(ollama!, file, settings));
                    break;
            }
        }

        return candidates;
    }

    private static string? ResolveRepo(ModelQuant quant, string provider) =>
        quant.Sources.FirstOrDefault(source => ProviderIs(source, provider))?.Repo;

    private static bool ProviderIs(ModelSource source, string provider) =>
        source.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase);

    private static void AddIfAvailable(List<string> order, List<string> available, string provider)
    {
        var match = available.FirstOrDefault(item => item.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (match is not null && !order.Contains(match)) order.Add(match);
    }

    private static void Add(List<DownloadSourceCandidate> candidates, string provider, string label, string url)
    {
        if (candidates.Any(candidate => candidate.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) return;
        candidates.Add(new DownloadSourceCandidate(provider, label, url));
    }

    private static string HostLabel(string host) =>
        host == RemoteCatalogService.HuggingFaceMirrorHost ? "HuggingFace 镜像" : "HuggingFace 官方";
}
