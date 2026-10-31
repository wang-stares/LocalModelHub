using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;

namespace LocalModelHub.App.Services;

/// <summary>把“启动模型”的完整流程封装：准备运行时 → 读取 GGUF → 计算卸载层数 → 启动 llama-server。</summary>
public sealed class ModelLauncher
{
    /// <summary>启动失败的自动降级：临时覆盖本次启动的上下文、GPU 层数、KV 量化、FlashAttention、装载模式与运行时路径，不写回用户设置。</summary>
    public sealed record LaunchTuning(
        int? ContextSize = null,
        int? GpuLayers = null,
        string? CacheType = null,
        string? FlashAttention = null,
        string? LoadMode = null,
        string? ServerPath = null);

    public async Task<DownloadedModel> LaunchAsync(
        DownloadedModel model,
        IProgress<RuntimeProgress>? runtimeProgress,
        IProgress<string>? statusProgress,
        CancellationToken cancellationToken,
        LaunchTuning? tuning = null)
    {
        var services = AppServices.Current;
        var settings = services.Settings;

        if (services.Server.IsRunning)
        {
            statusProgress?.Report("正在停止当前模型…");
            await services.Server.StopAsync();
        }

        var gpu = services.Performance.BestGpu?.Gpu ?? services.Hardware.Gpus.FirstOrDefault();
        var runtime = !string.IsNullOrWhiteSpace(tuning?.ServerPath) && File.Exists(tuning.ServerPath)
            ? new RuntimeInstall(tuning.ServerPath!, "override", "local")
            : await services.Runtime.EnsureAsync(gpu, runtimeProgress, cancellationToken);
        statusProgress?.Report("读取模型信息…");

        var info = await Task.Run(() => GgufReader.Read(model.PrimaryPath), cancellationToken);
        var context = Math.Clamp(
            model.ContextLength > 0 ? Math.Min(settings.ContextSize, model.ContextLength) : settings.ContextSize,
            1024,
            131072);
        if (tuning?.ContextSize is { } tunedContext)
        {
            context = Math.Clamp(tunedContext, 1024, 131072);
        }

        var layers = info.BlockCount > 0
            ? RecommendationEngine.ComputeGpuLayers(services.Performance, model.TotalBytes, info.BlockCount, context)
            : -1; // 无法读取层数时交给 llama.cpp 的 -ngl auto
        if (tuning?.GpuLayers is { } tunedLayers) layers = tunedLayers;
        var threads = settings.Threads > 0 ? settings.Threads : services.Hardware.Cpu.PhysicalCores;
        if (threads <= 0) threads = Environment.ProcessorCount;
        var port = FindFreePort(settings.ServerPort);
        var host = settings.ApiExposeLan ? "0.0.0.0" : "127.0.0.1";
        var apiKey = string.IsNullOrWhiteSpace(settings.ApiKey) ? null : settings.ApiKey.Trim();
        var cacheType = tuning?.CacheType ??
                        (string.IsNullOrWhiteSpace(settings.KvCacheType) ? "f16" : settings.KvCacheType);
        var flashAttention = tuning?.FlashAttention ?? settings.FlashAttention;

        statusProgress?.Report($"正在加载 {model.DisplayName}…");
        await services.Server.StartAsync(
            runtime.ServerPath,
            model.PrimaryPath,
            new LaunchOptions(
                port,
                context,
                layers,
                threads,
                model.DisplayName,
                host,
                apiKey,
                FlashAttention: flashAttention,
                CacheTypeK: cacheType,
                CacheTypeV: cacheType,
                CpuMoeLayers: settings.CpuMoeLayers,
                ParallelSlots: settings.ParallelSlots,
                LoadMode: tuning?.LoadMode ?? settings.LoadMode,
                ReasoningFormat: settings.ReasoningEnabled ? "deepseek" : "none",
                EnableMetrics: settings.MetricsEnabled),
            cancellationToken);

        settings.LastModelPath = model.PrimaryPath;
        services.SettingsService.Save();
        return model;
    }

    public static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", path);
            }
            else
            {
                Process.Start("xdg-open", path);
            }
        }
        catch
        {
            // ignore
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private static int FindFreePort(int start)
    {
        for (var port = start; port < start + 50 && port < 65535; port++)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // 端口被占用，尝试下一个
            }
        }

        return start;
    }
}
