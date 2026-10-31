using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.App.Services;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;

namespace LocalModelHub.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;

    public DashboardViewModel()
    {
        Refresh();
    }

    public ObservableCollection<ModelCardViewModel> TopCards { get; } = [];

    public event EventHandler? DetectionCompleted;

    /// <summary>跳转到「我的模型 · 为你推荐」，由主窗口注入。</summary>
    public Action? OpenRecommendations { get; set; }

    [RelayCommand]
    private void ViewAllRecommendations() => OpenRecommendations?.Invoke();

    [ObservableProperty] private int _scoreTotal;
    [ObservableProperty] private string _scoreTier = "未检测";
    [ObservableProperty] private string _scoreSummary = "点一下就能知道这台电脑适合运行多大的本地模型。";
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private double _gpuPercent;
    [ObservableProperty] private double _storagePercent;
    [ObservableProperty] private string _cpuPointsText = $"0 / {PerformanceScoreScale.Cpu:0}";
    [ObservableProperty] private string _memoryPointsText = $"0 / {PerformanceScoreScale.Memory:0}";
    [ObservableProperty] private string _gpuPointsText = $"0 / {PerformanceScoreScale.Gpu:0}";
    [ObservableProperty] private string _storagePointsText = $"0 / {PerformanceScoreScale.Storage:0}";

    [ObservableProperty] private string _cpuTitle = "尚未检测";
    [ObservableProperty] private string _cpuDetail = "点击“一键检测”读取处理器信息";
    [ObservableProperty] private string _gpuTitle = "尚未检测";
    [ObservableProperty] private string _gpuDetail = "读取显卡型号与可用显存";
    [ObservableProperty] private string _ramTitle = "—";
    [ObservableProperty] private string _ramDetail = "读取物理内存容量";
    [ObservableProperty] private string _diskTitle = "—";
    [ObservableProperty] private string _diskDetail = "检查模型目录所在磁盘空间";

    [ObservableProperty] private string _detectedAtText = "尚未检测";
    [ObservableProperty] private string _benchmarkText = "跑分尚未运行，评分仅基于硬件规格";
    [ObservableProperty] private string _recommendHeader = "热门模型";
    [ObservableProperty] private bool _isDetecting;
    [ObservableProperty] private string _detectStatus = "";
    [ObservableProperty] private bool _hasDetection;

    public void Initialize() => _ = QuickDetectAsync();

    [RelayCommand]
    private async Task DetectAsync()
    {
        if (IsDetecting) return;
        IsDetecting = true;
        DetectStatus = "正在读取硬件信息…";
        try
        {
            var progress = new Progress<string>(text => DetectStatus = text);
            var hardware = await Task.Run(() => _services.HardwareDetector.Detect(progress, _services.Settings.ModelsDirectory));
            _services.Hardware = hardware;

            DetectStatus = "正在运行 CPU 基准测试…";
            var benchmark = await Task.Run(() => _services.Benchmark.Run(progress));
            _services.LastBenchmark = benchmark;
            _services.Performance = _services.Scorer.Score(hardware, benchmark);

            BenchmarkText = $"实测 CPU {benchmark.CpuGflops:0.#} GFLOPS · 内存带宽 {benchmark.MemoryBandwidthGBps:0.#} GB/s · 用时 {benchmark.Duration.TotalSeconds:0.0}s";
            Refresh();
            DetectionCompleted?.Invoke(this, EventArgs.Empty);
            DetectStatus = "检测完成";
        }
        catch (Exception ex)
        {
            DetectStatus = $"检测失败：{ex.Message}";
        }
        finally
        {
            IsDetecting = false;
        }
    }

    public void SetTopCards(IReadOnlyList<ModelCardViewModel> ordered)
    {
        TopCards.Clear();
        var suitable = ordered.Where(c => c.Fit <= FitLevel.Playable).Take(3).ToList();
        if (suitable.Count == 0)
        {
            suitable = ordered.OrderBy(c => c.Model.ParametersB).Take(3).ToList();
        }

        foreach (var card in suitable) TopCards.Add(card);
    }

    private async Task QuickDetectAsync()
    {
        try
        {
            var hardware = await Task.Run(() => _services.HardwareDetector.Detect(null, _services.Settings.ModelsDirectory));
            _services.Hardware = hardware;
            _services.Performance = _services.Scorer.Score(hardware);
            Refresh();
            DetectionCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 硬件探测失败时保持“未检测”状态，用户仍可手动重试
        }
    }

    private void Refresh()
    {
        var hw = _services.Hardware;
        var score = _services.Performance;
        var detected = hw.DetectedAt != DateTime.MinValue;

        ScoreTotal = score.Total;
        ScoreTier = detected ? score.Tier : "未检测";
        ScoreSummary = detected ? score.Summary : "点一下就能知道这台电脑适合运行多大的本地模型。";
        HasDetection = detected;

        CpuPercent = Math.Clamp(score.CpuPoints / PerformanceScoreScale.Cpu * 100, 0, 100);
        MemoryPercent = Math.Clamp(score.MemoryPoints / PerformanceScoreScale.Memory * 100, 0, 100);
        GpuPercent = Math.Clamp(score.GpuPoints / PerformanceScoreScale.Gpu * 100, 0, 100);
        StoragePercent = Math.Clamp(score.StoragePoints / PerformanceScoreScale.Storage * 100, 0, 100);
        CpuPointsText = $"{score.CpuPoints:0.#} / {PerformanceScoreScale.Cpu:0}";
        MemoryPointsText = $"{score.MemoryPoints:0.#} / {PerformanceScoreScale.Memory:0}";
        GpuPointsText = $"{score.GpuPoints:0.#} / {PerformanceScoreScale.Gpu:0}";
        StoragePointsText = $"{score.StoragePoints:0.#} / {PerformanceScoreScale.Storage:0}";

        if (detected)
        {
            CpuTitle = hw.Cpu.Name;
            var features = string.Join(" · ", new[]
            {
                hw.HasAvx2 ? "AVX2" : "无 AVX2",
                hw.HasAvx512 ? "AVX512" : null
            }.Where(f => f is not null));
            CpuDetail = $"{hw.Cpu.PhysicalCores} 核 {hw.Cpu.LogicalCores} 线程 · {hw.Cpu.MaxClockMhz} MHz · {features}";

            var gpu = score.BestGpu;
            if (gpu is not null)
            {
                GpuTitle = gpu.Gpu.Name;
                GpuDetail = gpu.IsIntegrated
                    ? $"核显 · 共享内存 {Formatters.Gb(gpu.Gpu.SharedSystemMemoryBytes)} · 建议使用 CPU 推理"
                    : $"{gpu.UsableVramGb:0.#} GB 可用显存 · 带宽约 {gpu.BandwidthGBps:0} GB/s · {gpu.Gpu.Vendor}";
            }
            else
            {
                var anyGpu = hw.Gpus.FirstOrDefault();
                GpuTitle = anyGpu?.Name ?? "未检测到独立显卡";
                GpuDetail = anyGpu is null ? "将使用 CPU + 内存进行推理" : "显存不足，将自动回退到 CPU 推理";
            }

            RamTitle = $"{hw.TotalRamGb:0.#} GB";
            RamDetail = $"当前可用 {hw.AvailableRamGb:0.#} GB · 推理建议占用不超过 {hw.TotalRamGb * 0.8:0.#} GB";

            if (hw.DiskTotalBytes > 0)
            {
                DiskTitle = $"{Formatters.Bytes(hw.DiskFreeBytes)} 可用";
                DiskDetail = $"磁盘总容量 {Formatters.Bytes(hw.DiskTotalBytes)} · 模型目录所在位置";
            }
            else
            {
                DiskTitle = "未知";
                DiskDetail = "未能读取磁盘信息";
            }

            DetectedAtText = $"检测于 {hw.DetectedAt:HH:mm}";
            RecommendHeader = "最适合这台电脑的模型";
        }
        else
        {
            CpuTitle = "尚未检测";
            CpuDetail = "点击“一键检测”读取处理器信息";
            GpuTitle = "尚未检测";
            GpuDetail = "读取显卡型号与可用显存";
            RamTitle = "—";
            RamDetail = "读取物理内存容量";
            DiskTitle = "—";
            DiskDetail = "检查模型目录所在磁盘空间";
            DetectedAtText = "尚未检测";
            RecommendHeader = "热门模型";
        }
    }
}
