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

    /// <summary>主结论：能跑多大的模型（P1-20）。</summary>
    [ObservableProperty] private string _capabilityHeadline = "点一下就能知道这台电脑适合运行多大的本地模型。";
    [ObservableProperty] private string _capabilityBottleneck = "";
    [ObservableProperty] private string _capabilityHint = "";
    [ObservableProperty] private string _capabilityContext = "";
    [ObservableProperty] private string _confidenceText = "";
    [ObservableProperty] private string _scoreChangeNote = "";
    [ObservableProperty] private bool _hasScoreChangeNote;

    partial void OnScoreChangeNoteChanged(string value) =>
        HasScoreChangeNote = !string.IsNullOrWhiteSpace(value);

    [ObservableProperty] private string _cpuMeaning = "";
    [ObservableProperty] private string _memoryMeaning = "";
    [ObservableProperty] private string _gpuMeaning = "";
    [ObservableProperty] private string _storageMeaning = "";
    [ObservableProperty] private string _sourcesText = "";

    public void Initialize() => _ = QuickDetectAsync();

    [RelayCommand]
    private async Task DetectAsync()
    {
        if (IsDetecting) return;
        IsDetecting = true;
        DetectStatus = "正在读取硬件信息…";
        var previousTotal = _services.Performance.Total;
        var hadDetection = HasDetection;
        try
        {
            // 检测前采样一次：检测期间可能有程序释放 / 占用内存，沿用旧快照会让结论失真。
            _services.RefreshBudgetSnapshot();
            var progress = new Progress<string>(text => DetectStatus = text);
            var hardware = await Task.Run(() => _services.HardwareDetector.Detect(progress, _services.Settings.ModelsDirectory));
            _services.Hardware = hardware;

            DetectStatus = "正在运行 CPU 基准测试…";
            var benchmark = await Task.Run(() => _services.Benchmark.Run(progress, l3CacheKb: hardware.Cpu.L3CacheKb));
            _services.LastBenchmark = benchmark;
            // 实测结果落盘：重启后同一台机器沿用同一口径，换设备 / 跨大版本自动失效。
            _services.SaveMeasuredProfile(hardware, benchmark);
            _services.Performance = _services.ScoreHardware(hardware, benchmark, _services.BenchmarkMeasuredAt);

            var diff = _services.Performance.Total - previousTotal;
            ScoreChangeNote = hadDetection && Math.Abs(diff) > 5
                ? $"本次与上次相差 {diff:+#;-#;0} 分，可能受系统负载影响；建议关闭后台程序后重测。"
                : "";

            BenchmarkText = $"实测 CPU {benchmark.CpuGflops:0.#} GFLOPS · 内存带宽 {benchmark.MemoryBandwidthGBps:0.#} GB/s · 用时 {benchmark.Duration.TotalSeconds:0.0}s"
                + (benchmark.LowConfidence ? $" · {benchmark.QualityText}（波动 {benchmark.StdevRatio:P0}）" : "");
            Refresh();
            DetectionCompleted?.Invoke(this, EventArgs.Empty);
            DetectStatus = "检测完成";
        }
        catch (Exception ex)
        {
            AppLog.Error("detect", "硬件检测失败", ex);
            DetectStatus = $"检测失败：{ex.Message}";
        }
        finally
        {
            IsDetecting = false;
        }
    }

    public void SetTopCards(IReadOnlyList<ModelCardViewModel> ordered)
    {
        var suitable = ordered.Where(c => c.Fit <= FitLevel.Playable).Take(3).ToList();
        if (suitable.Count == 0)
        {
            suitable = ordered.OrderBy(c => c.Model.ParametersB).Take(3).ToList();
        }

        // 原地替换而不是 Clear + Add：避免每次评估刷新都重建列表、滚动位置跳动。
        for (var i = 0; i < suitable.Count; i++)
        {
            if (i >= TopCards.Count) TopCards.Add(suitable[i]);
            else if (!ReferenceEquals(TopCards[i], suitable[i])) TopCards[i] = suitable[i];
        }

        while (TopCards.Count > suitable.Count) TopCards.RemoveAt(TopCards.Count - 1);

        BestCard = suitable.FirstOrDefault();
    }

    /// <summary>工作台主行动：启动排在第一位的模型，不需要先进模型库。</summary>
    [ObservableProperty] private ModelCardViewModel? _bestCard;

    public bool HasBestCard => BestCard is not null;

    partial void OnBestCardChanged(ModelCardViewModel? value) =>
        OnPropertyChanged(nameof(HasBestCard));

    /// <summary>预算快照刷新后只更新内存 / 显存一行，不重置评分与检测状态。</summary>
    public void RefreshBudget()
    {
        var hw = _services.Hardware;
        if (hw.DetectedAt == DateTime.MinValue) return;

        RamTitle = $"{hw.TotalRamGb:0.#} GB";
        RamDetail = $"当前可用 {hw.AvailableRamGb:0.#} GB · 推理建议占用不超过 {hw.TotalRamGb * 0.8:0.#} GB";

        var gpu = _services.Performance.BestGpu;
        if (gpu is not null)
        {
            GpuTitle = gpu.Gpu.Name;
            GpuDetail = gpu.IsIntegrated
                ? $"核显 · 共享内存 {Formatters.Gb(gpu.Gpu.SharedSystemMemoryBytes)} · 建议使用 CPU 推理"
                : $"{gpu.UsableVramGb:0.#} GB 可用显存 · 带宽约 {gpu.BandwidthGBps:0} GB/s · {gpu.Gpu.Vendor}"
                  + (gpu.Gpu.VramEstimated ? " · 驱动未报告总显存，按 4 GB 保守估算" : "");
        }
    }

    private async Task QuickDetectAsync()
    {
        try
        {
            _services.RefreshBudgetSnapshot();
            var hardware = await Task.Run(() => _services.HardwareDetector.Detect(null, _services.Settings.ModelsDirectory));
            _services.Hardware = hardware;

            // 启动只读指纹并复用上次实测，不再用另一套「无基准」口径重算出不同的分数。
            var reused = _services.LoadMeasuredProfile(hardware);
            _services.Performance = _services.ScoreHardware(hardware, _services.LastBenchmark, _services.BenchmarkMeasuredAt);
            BenchmarkText = reused && _services.LastBenchmark is { } measured
                ? $"沿用上次实测：CPU {measured.CpuGflops:0.#} GFLOPS · 内存带宽 {measured.MemoryBandwidthGBps:0.#} GB/s"
                  + (_services.BenchmarkMeasuredAt is { } at ? $" · 采样于 {at:MM-dd HH:mm}" : "")
                : "尚未实测：当前分数为规格估算，点「一键检测」获得实测口径。";
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
        if (_services.HardwareChanged)
        {
            ScoreChangeNote = _services.HardwareChangeNote;
        }

        CpuPercent = Math.Clamp(score.CpuPoints / PerformanceScoreScale.Cpu * 100, 0, 100);
        MemoryPercent = Math.Clamp(score.MemoryPoints / PerformanceScoreScale.Memory * 100, 0, 100);
        GpuPercent = Math.Clamp(score.GpuPoints / PerformanceScoreScale.Gpu * 100, 0, 100);
        StoragePercent = Math.Clamp(score.StoragePoints / PerformanceScoreScale.Storage * 100, 0, 100);
        CpuPointsText = $"{score.CpuPoints:0.#} / {PerformanceScoreScale.Cpu:0}";
        MemoryPointsText = $"{score.MemoryPoints:0.#} / {PerformanceScoreScale.Memory:0}";
        GpuPointsText = $"{score.GpuPoints:0.#} / {PerformanceScoreScale.Gpu:0}";
        StoragePointsText = $"{score.StoragePoints:0.#} / {PerformanceScoreScale.Storage:0}";

        // 能力画像优先于总分：先说「能跑多大」，四个子分只作为解释。
        var capability = detected ? score.Capability : null;
        CapabilityHeadline = capability?.Headline ?? "点一下就能知道这台电脑适合运行多大的本地模型。";
        CapabilityBottleneck = capability is null ? "" : capability.BottleneckText;
        CapabilityHint = capability?.UpgradeHint ?? "";
        CapabilityContext = capability is null ? "" : capability.ContextText;
        ConfidenceText = detected
            ? $"{score.ConfidenceText} · {score.Confidence:P0}"
            : "";

        CpuMeaning = ComponentMeaning(score, "CPU");
        MemoryMeaning = ComponentMeaning(score, "内存");
        GpuMeaning = ComponentMeaning(score, "显卡");
        StorageMeaning = ComponentMeaning(score, "存储");
        SourcesText = string.Join(" · ",
            score.Components.Select(component => $"{component.Name} {component.SourceText}"));

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
                    : $"{gpu.UsableVramGb:0.#} GB 可用显存 · 带宽约 {gpu.BandwidthGBps:0} GB/s · {gpu.Gpu.Vendor}"
                      + (gpu.Gpu.VramEstimated ? " · 驱动未报告总显存，按 4 GB 保守估算" : "");
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

            DetectedAtText = _services.BenchmarkMeasuredAt is { } measured
                ? $"实测于 {measured:MM-dd HH:mm}"
                : $"检测于 {hw.DetectedAt:HH:mm} · 未实测";
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

    private static string ComponentMeaning(PerformanceScore score, string name)
    {
        var component = score.Components.FirstOrDefault(item => item.Name == name);
        return component is null
            ? ""
            : $"{component.Metric}\n{component.Meaning}\n来源：{component.SourceText}";
    }
}
