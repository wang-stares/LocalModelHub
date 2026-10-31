using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

public sealed record RuntimeProgress(string Stage, double Percent, string Message);

public sealed record RuntimeInstall(string ServerPath, string Variant, string Tag);

/// <summary>管理 llama.cpp 的 Windows 预编译运行时（下载、解压、定位 llama-server.exe）。</summary>
public sealed class RuntimeManager
{
    private const string RepoUrl = "https://github.com/ggml-org/llama.cpp";
    private const string ApiUrl = "https://api.github.com/repos/ggml-org/llama.cpp";
    private const string PinnedTag = "b11963";

    private static readonly IReadOnlyDictionary<string, string> EmptyDigests = new Dictionary<string, string>();

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

        // runtime.json 记录的是当前运行时；优先使用，便于冒烟验证发现被替换成坏文件的二进制。
        var marked = ReadMarkerServer();
        if (!string.IsNullOrWhiteSpace(marked) && File.Exists(marked)) return marked;

        try
        {
            return Directory.EnumerateFiles(AppPaths.RuntimeDirectory, "llama-server.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"枚举运行时目录失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>读取 runtime.json 中的 serverPath；标记缺失、损坏或字段缺失时返回 null。</summary>
    private static string? ReadMarkerServer()
    {
        var marker = Path.Combine(AppPaths.RuntimeDirectory, "runtime.json");
        try
        {
            if (!File.Exists(marker)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            return document.RootElement.TryGetProperty("serverPath", out var path) &&
                   path.ValueKind == JsonValueKind.String
                ? path.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"读取运行时标记失败：{ex.Message}");
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
            if (VerifyServer(existing, out var error))
            {
                return new RuntimeInstall(existing, "existing", "local");
            }

            // 自定义路径写错了要立即报错：继续下载也无法覆盖用户的显式配置。
            var custom = _settings.Settings.RuntimePath;
            if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom) &&
                Path.GetFullPath(custom).Equals(Path.GetFullPath(existing), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"设置中指定的 llama-server.exe 无法运行：{error}。请更换路径，或清空该设置让应用自动安装。");
            }

            progress?.Report(new RuntimeProgress(
                "verify", 0, $"现有运行时无法运行（{error}），尝试其它已安装版本…"));
            foreach (var candidate in EnumerateServers()
                         .Where(path => !path.Equals(existing, StringComparison.OrdinalIgnoreCase))
                         .Take(8))
            {
                if (VerifyServer(candidate, out _))
                {
                    RememberWorkingRuntime(candidate);
                    return new RuntimeInstall(candidate, "existing", "local");
                }
            }

            progress?.Report(new RuntimeProgress("verify", 0, "已安装的运行时均无法运行，开始重新下载…"));
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

    /// <summary>
    /// 把冒烟验证通过的已安装运行时写回 runtime.json（目录名形如 b11963-cuda-12.4），
    /// 避免每次启动都重复验证同一个坏二进制。目录名不符合约定时保持不变。
    /// </summary>
    private static void RememberWorkingRuntime(string serverPath)
    {
        var directory = Path.GetDirectoryName(serverPath);
        if (string.IsNullOrEmpty(directory)) return;

        var folder = Path.GetFileName(directory);
        var dash = folder.IndexOf('-');
        if (dash < 4) return;

        var tag = folder[..dash];
        if (!tag.StartsWith('b') || tag.Length > 10) return;

        SaveMarker(tag, folder[(dash + 1)..], new RuntimeInstall(serverPath, "existing", tag));
    }

    private static IEnumerable<string> EnumerateServers()
    {
        try
        {
            return Directory.EnumerateFiles(AppPaths.RuntimeDirectory, "llama-server.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch
        {
            return [];
        }
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
            "cuda" => CudaCandidates(
                IsBlackwell(gpu?.Name),
                gpu is null ? null : GpuMemoryProbe.QueryDriverVersion()),
            "vulkan" => new[] { "vulkan", "cpu" },
            _ => new[] { "cpu" }
        };

        Exception? lastError = null;
        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                progress?.Report(new RuntimeProgress("install", 0, $"准备 {candidate} 后端…"));
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
                failures.Add($"{candidate}：{ShortMessage(ex.Message)}");
                progress?.Report(new RuntimeProgress("fallback", 0, $"{candidate} 不可用，尝试下一个后端…"));
            }
        }

        var detail = failures.Count == 0
            ? ""
            : "（已尝试：" + string.Join("；", failures) + "）";
        throw new InvalidOperationException(
            "无法下载并验证 llama.cpp 运行时。可以在“设置”中手动指定 llama-server.exe 路径，或检查网络后重试。" + detail,
            lastError);
    }

    /// <summary>NVIDIA Blackwell（RTX 50 系 / B100 / B200）：只有新驱动 + CUDA 13 才能稳定运行。</summary>
    public static bool IsBlackwell(string? gpuName) =>
        !string.IsNullOrWhiteSpace(gpuName) &&
        (Regex.IsMatch(gpuName, @"RTX\s*50\d\d", RegexOptions.IgnoreCase) ||
         Regex.IsMatch(gpuName, @"\bB[12]00\b", RegexOptions.IgnoreCase) ||
         gpuName.Contains("Blackwell", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 后端候选顺序：Blackwell + 驱动 ≥ 570 优先 CUDA 13.x，其余沿用 CUDA 12.4 优先；
    /// 两种顺序都以 vulkan → cpu 收尾，保证坏后端能被跳过。
    /// </summary>
    public static string[] CudaCandidates(bool blackwell, Version? driverVersion)
    {
        if (blackwell && driverVersion is { Major: >= 570 })
        {
            return ["cuda-13.4", "cuda-12.4", "vulkan", "cpu"];
        }

        return ["cuda-12.4", "cuda-13.4", "vulkan", "cpu"];
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
        var assetName = $"llama-{tag}-bin-win-{variant}-x64.zip";
        var assetUrl = $"{RepoUrl}/releases/download/{tag}/{assetName}";
        var targetDirectory = Path.Combine(AppPaths.RuntimeDirectory, $"{tag}-{variant}");
        Directory.CreateDirectory(targetDirectory);

        // 发布页的 digest 字段即资产 SHA-256；取不到（离线 / 限流）时跳过校验并明确提示。
        var digests = await GetReleaseDigestsAsync(tag, cancellationToken);
        progress?.Report(digests.Count == 0
            ? new RuntimeProgress("verify", 0, "未能获取发布页校验值，跳过安装包完整性校验")
            : new RuntimeProgress("verify", 0, $"将校验 {variant} 安装包的 SHA-256…"));

        var mainZip = Path.Combine(AppPaths.DownloadsDirectory, assetName);
        var mainProgress = MapProgress(progress, $"下载推理引擎 · {variant}", 0, 80);
        await DownloadVerifiedAsync(assetUrl, assetName, mainZip, digests, mainProgress, cancellationToken);

        var cudartZip = "";
        if (variant.StartsWith("cuda", StringComparison.Ordinal))
        {
            var cudartName = $"cudart-llama-bin-win-{variant}-x64.zip";
            var cudartUrl = $"{RepoUrl}/releases/download/{tag}/{cudartName}";
            cudartZip = Path.Combine(AppPaths.DownloadsDirectory, cudartName);
            var cudartProgress = MapProgress(progress, "下载 CUDA 运行库", 80, 92);
            try
            {
                await DownloadVerifiedAsync(cudartUrl, cudartName, cudartZip, digests, cudartProgress, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
            {
                cudartZip = "";
                progress?.Report(new RuntimeProgress(
                    "install", 80, $"CUDA 运行库不可用（{ShortMessage(ex.Message)}），继续尝试主程序…"));
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
            TryDeleteDirectory(targetDirectory);
            throw new FileNotFoundException("运行时包中未找到 llama-server.exe");
        }

        progress?.Report(new RuntimeProgress("verify", 95, $"验证 {variant} 后端能否运行…"));
        if (!VerifyServer(server, out var error))
        {
            TryDeleteDirectory(targetDirectory);
            throw new InvalidOperationException($"{variant} 后端无法启动：{error}");
        }

        progress?.Report(new RuntimeProgress("done", 100, "推理引擎已就绪"));
        return new RuntimeInstall(server, variant, tag);
    }

    /// <summary>
    /// 冒烟验证：执行 `llama-server.exe --version`，确认二进制与后端 DLL 能加载。
    /// 退出码非 0（如 0xC0000005 缺 CUDA 运行库或显卡不受支持）即判定该后端不可用。
    /// </summary>
    public static bool VerifyServer(string serverPath, out string error)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(serverPath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                error = "无法启动进程";
                return false;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Debug.WriteLine($"终止验证进程失败：{ex.Message}");
                }

                error = "启动后 10 秒内未退出（可能缺少运行库或显卡不受支持）";
                return false;
            }

            var output = (stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult()).Trim();
            if (process.ExitCode != 0)
            {
                error = $"退出码 {process.ExitCode}（0x{(uint)process.ExitCode:X8}）：{FirstLines(output, 3)}";
                return false;
            }

            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 读取 GitHub Releases API 中每个资产的 SHA-256 摘要（"sha256:…"）。
    /// 失败（离线、限流、字段缺失）返回空字典，由调用方跳过校验。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> GetReleaseDigestsAsync(
        string tag,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{ApiUrl}/releases/tags/{Uri.EscapeDataString(tag)}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _downloadService.Client.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"获取 {tag} 资产摘要失败：HTTP {(int)response.StatusCode}");
                return EmptyDigests;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var digests = ParseAssetDigests(json);
            return digests.Count == 0 ? EmptyDigests : digests;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            Debug.WriteLine($"获取 {tag} 资产摘要失败：{ex.Message}");
            return EmptyDigests;
        }
    }

    /// <summary>
    /// 从 GitHub Releases API 的 JSON 中提取「资产名 → sha256:…」；无 digest 字段的资产跳过。
    /// 解析失败返回空字典，让调用方退化为“不校验”而不是安装失败。
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseAssetDigests(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                return EmptyDigests;
            }

            var digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) && FileIntegrity.NormalizeSha256(digest) is not null)
                {
                    digests[name] = digest!;
                }
            }

            return digests;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"解析发布资产摘要失败：{ex.Message}");
            return EmptyDigests;
        }
    }

    /// <summary>下载资产并按已发布的 SHA-256 校验；不匹配时删除文件并重试一次。</summary>
    private async Task DownloadVerifiedAsync(
        string url,
        string assetName,
        string destination,
        IReadOnlyDictionary<string, string> digests,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var expected = digests.TryGetValue(assetName, out var digest) ? digest : null;
        for (var attempt = 1; ; attempt++)
        {
            await _downloadService.DownloadFileAsync(url, destination, 0, progress, cancellationToken);
            if (await FileIntegrity.VerifyFileAsync(destination, expected, cancellationToken)) return;

            try
            {
                File.Delete(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"删除校验失败的安装包失败：{ex.Message}");
            }

            if (attempt >= 2)
            {
                throw new InvalidDataException(
                    $"{assetName} 完整性校验失败：SHA-256 与发布页不一致，已重新下载仍未通过");
            }
        }
    }

    private static string FirstLines(string? text, int count)
    {
        if (string.IsNullOrWhiteSpace(text)) return "无输出";
        var lines = text.Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(count)
            .ToArray();
        var joined = string.Join(" / ", lines);
        return joined.Length <= 240 ? joined : joined[..240] + "…";
    }

    private static string ShortMessage(string? message)
    {
        var text = (message ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length <= 160 ? text : text[..160] + "…";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"清理运行时目录失败（{path}）：{ex.Message}");
        }
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
