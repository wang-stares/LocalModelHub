using System.IO.Compression;
using System.Text.Json;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

public sealed record RuntimeProgress(string Stage, double Percent, string Message);

public sealed record RuntimeInstall(string ServerPath, string Variant, string Tag);

/// <summary>管理 llama.cpp 的 Windows 预编译运行时（下载、解压、定位 llama-server.exe）。</summary>
public sealed class RuntimeManager
{
    private const string RepoUrl = "https://github.com/ggml-org/llama.cpp";
    private const string PinnedTag = "b11963";

    private readonly DownloadService _downloadService;
    private readonly SettingsService _settings;

    public RuntimeManager(DownloadService downloadService, SettingsService settings)
    {
        _downloadService = downloadService;
        _settings = settings;
    }

    public string? FindExistingServer()
    {
        var custom = _settings.Settings.RuntimePath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;

        try
        {
            return Directory.EnumerateFiles(AppPaths.RuntimeDirectory, "llama-server.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>已安装运行时的版本标签（如 b11963）；读取 runtime.json，缺失时从目录名推断。</summary>
    public string? GetInstalledTag()
    {
        var marker = Path.Combine(AppPaths.RuntimeDirectory, "runtime.json");
        try
        {
            if (File.Exists(marker))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(marker));
                if (document.RootElement.TryGetProperty("tag", out var tag) &&
                    tag.ValueKind == JsonValueKind.String)
                {
                    var text = tag.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }
        catch
        {
            // 标记文件损坏时回退到目录名
        }

        var server = FindExistingServer();
        var directory = Path.GetFileName(Path.GetDirectoryName(server ?? ""));
        if (string.IsNullOrWhiteSpace(directory)) return null;

        var dash = directory.IndexOf('-');
        var candidate = dash > 0 ? directory[..dash] : directory;
        return candidate.StartsWith('b') && candidate.Length is >= 4 and <= 10 ? candidate : null;
    }

    /// <summary>已安装运行时使用的后端（cuda / vulkan / cpu）；无法判断时返回 auto。</summary>
    public string GetInstalledVariant()
    {
        var marker = Path.Combine(AppPaths.RuntimeDirectory, "runtime.json");
        try
        {
            if (File.Exists(marker))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(marker));
                if (document.RootElement.TryGetProperty("variant", out var variant) &&
                    variant.ValueKind == JsonValueKind.String)
                {
                    return variant.GetString() ?? "auto";
                }
            }
        }
        catch
        {
            // ignore
        }

        return "auto";
    }

    /// <summary>列出本地已安装运行时的标签（去重、按字典序）；用于自动降级时切换到其他版本。</summary>
    public IReadOnlyList<string> ListInstalledTags()
    {
        var tags = new List<string>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(AppPaths.RuntimeDirectory))
            {
                var name = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(name)) continue;
                var tag = name.Split('-')[0];
                if (tag.StartsWith('b') && tag.Length is >= 4 and <= 10 && !tags.Contains(tag))
                {
                    tags.Add(tag);
                }
            }
        }
        catch
        {
            // 运行时目录不存在或不可读
        }

        tags.Sort(StringComparer.Ordinal);
        return tags;
    }

    /// <summary>查找指定标签对应的 llama-server.exe；未安装该标签时返回 null。</summary>
    public string? FindServerForTag(string tag)
    {
        try
        {
            return Directory.EnumerateFiles(AppPaths.RuntimeDirectory, "llama-server.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path =>
                {
                    var directory = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
                    return directory.Equals(tag, StringComparison.OrdinalIgnoreCase) ||
                           directory.StartsWith(tag + "-", StringComparison.OrdinalIgnoreCase);
                });
        }
        catch
        {
            return null;
        }
    }

    public async Task<RuntimeInstall> EnsureAsync(
        GpuInfo? gpu,
        IProgress<RuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existing = FindExistingServer();
        if (existing is not null)
        {
            return new RuntimeInstall(existing, "existing", "local");
        }

        AppPaths.EnsureCreated();
        foreach (var stale in Directory.GetFiles(AppPaths.DownloadsDirectory, "llama-*.zip"))
        {
            try { File.Delete(stale); } catch { /* ignore */ }
        }

        progress?.Report(new RuntimeProgress("resolve", 0, "获取 llama.cpp 最新版本…"));
        var tag = await GetLatestTagAsync(cancellationToken);
        return await InstallWithFallbackAsync(tag, gpu, progress, cancellationToken);
    }

    /// <summary>检查并在用户确认后升级到上游最新运行时；已有本地运行时不会阻止升级。</summary>
    public async Task<RuntimeInstall> UpdateAsync(
        GpuInfo? gpu,
        IProgress<RuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        AppPaths.EnsureCreated();
        progress?.Report(new RuntimeProgress("resolve", 0, "获取 llama.cpp 最新版本…"));
        var tag = await GetLatestTagAsync(cancellationToken);
        return await InstallWithFallbackAsync(tag, gpu, progress, cancellationToken);
    }

    private async Task<RuntimeInstall> InstallWithFallbackAsync(
        string tag,
        GpuInfo? gpu,
        IProgress<RuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var variant = ResolveVariant(gpu);
        var candidates = variant switch
        {
            "cuda" => new[] { "cuda-12.4", "cuda-13.4", "vulkan", "cpu" },
            "vulkan" => new[] { "vulkan", "cpu" },
            _ => new[] { "cpu" }
        };

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var install = await InstallVariantAsync(tag, candidate, progress, cancellationToken);
                SaveMarker(tag, candidate, install);
                return install;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            "无法下载 llama.cpp 运行时。可以在“设置”中手动指定 llama-server.exe 路径，或检查网络后重试。" +
            (lastError is null ? "" : $"（{lastError.Message}）"), lastError);
    }

    private string ResolveVariant(GpuInfo? gpu)
    {
        var configured = _settings.Settings.RuntimeVariant;
        if (!string.IsNullOrWhiteSpace(configured) &&
            !configured.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            configured is "cuda" or "vulkan" or "cpu")
        {
            return configured;
        }

        if (gpu is null) return "cpu";
        return gpu.Vendor switch
        {
            "NVIDIA" => "cuda",
            "AMD" or "Intel" => "vulkan",
            _ => "cpu"
        };
    }

    /// <summary>通过 GitHub Releases 重定向解析上游最新标签；失败时回退到内置固定版本。</summary>
    public async Task<string> GetLatestTagAsync(CancellationToken cancellationToken) =>
        await TryGetLatestTagAsync(cancellationToken) ?? PinnedTag;

    /// <summary>查询上游最新标签；网络失败返回 null，便于「检查更新」区分失败与已是最新。</summary>
    public async Task<string?> TryGetLatestTagAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _downloadService.Client.GetAsync(
                $"{RepoUrl}/releases/latest", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var finalUri = response.RequestMessage?.RequestUri?.ToString() ?? "";
            var index = finalUri.LastIndexOf("/tag/", StringComparison.Ordinal);
            if (index >= 0)
            {
                var tag = finalUri[(index + 5)..].Trim('/');
                if (tag.StartsWith('b') && tag.Length is >= 4 and <= 10) return tag;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 网络不可用或重定向被拦截
        }

        return null;
    }

    private async Task<RuntimeInstall> InstallVariantAsync(
        string tag,
        string variant,
        IProgress<RuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var suffix = variant == "cpu" ? "cpu" : variant;
        var assetName = $"llama-{tag}-bin-win-{suffix}-x64.zip";
        var assetUrl = $"{RepoUrl}/releases/download/{tag}/{assetName}";
        var targetDirectory = Path.Combine(AppPaths.RuntimeDirectory, $"{tag}-{variant}");
        Directory.CreateDirectory(targetDirectory);

        var mainZip = Path.Combine(AppPaths.DownloadsDirectory, assetName);
        var mainProgress = MapProgress(progress, $"下载推理引擎 · {variant}", 0, 80);
        await _downloadService.DownloadFileAsync(assetUrl, mainZip, 0, mainProgress, cancellationToken);

        var cudartZip = "";
        if (variant.StartsWith("cuda", StringComparison.Ordinal))
        {
            var cudartName = $"cudart-llama-bin-win-{variant}-x64.zip";
            var cudartUrl = $"{RepoUrl}/releases/download/{tag}/{cudartName}";
            cudartZip = Path.Combine(AppPaths.DownloadsDirectory, cudartName);
            var cudartProgress = MapProgress(progress, "下载 CUDA 运行库", 80, 92);
            try
            {
                await _downloadService.DownloadFileAsync(cudartUrl, cudartZip, 0, cudartProgress, cancellationToken);
            }
            catch (HttpRequestException)
            {
                cudartZip = "";
            }
        }

        progress?.Report(new RuntimeProgress("extract", 92, "解压运行时…"));
        ZipFile.ExtractToDirectory(mainZip, targetDirectory, overwriteFiles: true);
        if (!string.IsNullOrEmpty(cudartZip) && File.Exists(cudartZip))
        {
            ZipFile.ExtractToDirectory(cudartZip, targetDirectory, overwriteFiles: true);
        }

        var server = Directory.EnumerateFiles(targetDirectory, "llama-server.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (server is null)
        {
            throw new FileNotFoundException("运行时包中未找到 llama-server.exe");
        }

        progress?.Report(new RuntimeProgress("done", 100, "推理引擎已就绪"));
        return new RuntimeInstall(server, variant, tag);
    }

    private static IProgress<DownloadProgress> MapProgress(
        IProgress<RuntimeProgress>? progress,
        string stage,
        double startPercent,
        double endPercent)
    {
        return new Progress<DownloadProgress>(p =>
        {
            var local = p.TotalBytes > 0 ? Math.Clamp(p.ReceivedBytes * 100d / p.TotalBytes, 0, 100) : 0;
            var percent = startPercent + (endPercent - startPercent) * local / 100d;
            var speed = p.BytesPerSecond > 0 ? $" · {p.BytesPerSecond / 1024 / 1024:0.0} MB/s" : "";
            progress?.Report(new RuntimeProgress(stage, percent, $"{stage}{speed}"));
        });
    }

    private static void SaveMarker(string tag, string variant, RuntimeInstall install)
    {
        var marker = Path.Combine(AppPaths.RuntimeDirectory, "runtime.json");
        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            tag,
            variant,
            serverPath = install.ServerPath,
            installedAt = DateTime.Now
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
