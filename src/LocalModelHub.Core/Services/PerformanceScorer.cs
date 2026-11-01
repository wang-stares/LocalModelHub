namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>首页性能评分的分项满分，UI 与算法共用，避免两端硬编码。</summary>
public static class PerformanceScoreScale
{
    public const double Cpu = 30;
    public const double Memory = 20;
    public const double Gpu = 35;
    public const double Storage = 15;
    public const double Total = Cpu + Memory + Gpu + Storage;
}

/// <summary>
/// 把硬件规格与实测跑分折算成 0-100 的综合能力分（v2）：CPU 30 + 内存 20 + 显卡 35 + 存储 15。
/// 评分面向本地 LLM 推理：指令集、内存带宽、可用显存与磁盘余量比单纯的核心数更重要。
/// 只使用稳定量（总内存、总显存、实测带宽），不含「当前可用内存 / 当前显存占用」，
/// 否则开个浏览器再点检测，同一台机器就会得到不同的分数。
/// </summary>
public sealed class PerformanceScorer
{
    public const string CalibrationVersion = "v2.1";

    private const double AssumedMemoryBandwidthGBps = 51;

    public PerformanceScore Score(HardwareProfile hw, BenchmarkResult? benchmark = null)
    {
        var cpu = hw.Cpu;
        var coresNorm = Math.Clamp(cpu.PhysicalCores / 16d, 0, 1);
        var clockNorm = Math.Clamp(cpu.MaxClockMhz / 5500d, 0, 1);
        var instructionNorm = hw.HasAvx512 ? 1.0 : hw.HasAvx2 ? 0.75 : 0.2;
        var cpuKnown = cpu.PhysicalCores > 0 || cpu.MaxClockMhz > 0;
        var specCpuNorm = cpuKnown
            ? 0.45 * coresNorm + 0.3 * clockNorm + 0.25 * instructionNorm
            : 0;

        var measuredCpu = benchmark is { CpuGflops: > 0 };
        double cpuNorm;
        if (benchmark is { CpuGflops: > 0 })
        {
            // 60 GFLOPS ≈ 单核 AVX2/FMA 在 3.5 GHz 附近的实际上限；向量化内核测出的值不应长期顶格。
            cpuNorm = 0.65 * Math.Clamp(benchmark.CpuGflops / 60d, 0, 1) + 0.35 * specCpuNorm;
        }
        else
        {
            cpuNorm = specCpuNorm;
        }

        // 内存：容量决定能装多大模型，带宽决定 CPU 推理的解码速度；不再计入瞬时可用比例。
        var capacityNorm = Math.Clamp(hw.TotalRamGb / 64d, 0, 1);
        var measuredBandwidth = benchmark is { MemoryBandwidthGBps: > 0 } ? benchmark.MemoryBandwidthGBps : 0;
        var memoryBandwidthNorm = measuredBandwidth > 0
            ? Math.Clamp(measuredBandwidth / 90d, 0, 1)
            : 0.55; // 没有实测时按 DDR4 双通道中位值
        var memoryNorm = 0.65 * capacityNorm + 0.35 * memoryBandwidthNorm;

        // 显卡：独显按带宽（解码速度）与可用显存（可加载规模）计分；核显只按带宽给少量分，不参与显存卸载。
        var bestGpu = GpuDatabase.GetBestGpu(hw);
        double gpuPoints = 0;
        if (bestGpu is not null)
        {
            var bandwidthNorm = Math.Clamp(bestGpu.BandwidthGBps / 900d, 0, 1);
            var vramNorm = Math.Clamp(bestGpu.UsableVramGb / 24d, 0, 1);
            gpuPoints = bestGpu.IsIntegrated
                ? PerformanceScoreScale.Gpu * 0.15 * bandwidthNorm
                : PerformanceScoreScale.Gpu * (0.6 * bandwidthNorm + 0.4 * vramNorm);
        }

        // 存储：模型文件与 KV cache 都落盘，剩余空间决定一次能下载多大的模型。
        double storageNorm = 0;
        if (hw.DiskTotalBytes > 0)
        {
            var freeNorm = Math.Clamp(hw.DiskFreeBytes / (300d * Formatters.Gib), 0, 1);
            var totalNorm = Math.Clamp(hw.DiskTotalBytes / (1024d * Formatters.Gib), 0, 1);
            storageNorm = 0.6 * freeNorm + 0.4 * totalNorm;
        }

        var cpuPoints = PerformanceScoreScale.Cpu * cpuNorm;
        var memoryPoints = PerformanceScoreScale.Memory * memoryNorm;
        var storagePoints = PerformanceScoreScale.Storage * storageNorm;
        var total = Math.Clamp((int)Math.Round(cpuPoints + memoryPoints + gpuPoints + storagePoints), 0, 100);
        var tier = total switch
        {
            >= 78 => "发烧级",
            >= 58 => "高性能",
            >= 35 => "主流",
            _ => "入门"
        };

        var bandwidthGb = measuredBandwidth > 0 ? measuredBandwidth : AssumedMemoryBandwidthGBps;
        var bandwidthSource = measuredBandwidth > 0 ? Provenance.Measured : Provenance.Assumed;
        var capability = BuildCapability(hw, bestGpu, bandwidthGb, cpuNorm);
        var components = BuildComponents(
            hw, bestGpu, benchmark, cpuPoints, memoryPoints, gpuPoints, storagePoints,
            bandwidthGb, bandwidthSource);
        var summary = BuildSummary(hw, bestGpu, benchmark, total);

        var confidence = measuredCpu && measuredBandwidth > 0 ? 0.9 : measuredCpu || measuredBandwidth > 0 ? 0.7 : 0.5;
        if (bestGpu?.Gpu.VramEstimated == true) confidence -= 0.1;

        return new PerformanceScore(
            total,
            Math.Round(cpuPoints, 1),
            Math.Round(memoryPoints, 1),
            Math.Round(gpuPoints, 1),
            Math.Round(storagePoints, 1),
            tier,
            summary,
            bestGpu)
        {
            Components = components,
            Capability = capability,
            Confidence = Math.Clamp(confidence, 0.3, 1.0),
            ScoredAt = hw.DetectedAt,
            CalibrationVersion = CalibrationVersion
        };
    }

    private static IReadOnlyList<ScoreComponent> BuildComponents(
        HardwareProfile hw,
        GpuCapability? gpu,
        BenchmarkResult? benchmark,
        double cpuPoints,
        double memoryPoints,
        double gpuPoints,
        double storagePoints,
        double bandwidthGb,
        Provenance bandwidthSource)
    {
        var cpu = hw.Cpu;
        var features = string.Join(" / ", new[]
        {
            hw.HasAvx2 ? "AVX2" : null,
            hw.HasAvx512 ? "AVX512" : null
        }.Where(text => text is not null));
        var cpuMetric = $"{cpu.PhysicalCores} 核 {cpu.LogicalCores} 线程 · {cpu.MaxClockMhz} MHz"
                        + (features.Length > 0 ? $" · {features}" : "")
                        + (benchmark is { CpuGflops: > 0 } measured ? $" · 实测 {measured.CpuGflops:0.#} GFLOPS" : " · 未实测");
        var usableBandwidth = Math.Min(bandwidthGb, Math.Max(2, cpu.PhysicalCores) * 9d);
        var speed7B = Math.Clamp(usableBandwidth * (gpu is { IsIntegrated: false } ? 0.55 : 0.58) / 4.4, 0.5, 200);
        var cpuMeaning = $"7B Q4 {((gpu is { IsIntegrated: false }) ? "满显存" : "纯 CPU")}约 {Math.Round(speed7B)} tok/s";

        var memoryMetric = $"{hw.TotalRamGb:0.#} GB · "
                           + (bandwidthSource == Provenance.Measured
                               ? $"实测带宽 {bandwidthGb:0.#} GB/s"
                               : "带宽未实测（按 DDR4 双通道 51 GB/s）");
        var memoryMeaning = $"可容纳约 {MaxParamsB(hw.TotalRamGb * 0.8):0.#}B 模型（Q4_K_M · 8K）";

        var gpuMetric = gpu is null
            ? "未检测到可用显存"
            : $"{gpu.Gpu.Name} · {gpu.UsableVramGb:0.#} GB 可用显存 · 带宽 {gpu.BandwidthGBps:0} GB/s";
        var gpuMeaning = gpu is { IsIntegrated: false }
            ? $"全量卸载上限约 {MaxParamsB(gpu.UsableVramGb * 0.9):0.#}B Q4"
            : "核显不参与显存卸载，模型走 CPU + 内存";
        var gpuSource = gpu is null
            ? Provenance.Assumed
            : gpu.Gpu.VramEstimated ? Provenance.Estimated : Provenance.Lookup;

        var storageMetric = hw.DiskTotalBytes > 0
            ? $"{Formatters.Bytes(hw.DiskFreeBytes)} 可用 / 共 {Formatters.Bytes(hw.DiskTotalBytes)}"
            : "未能读取磁盘信息";
        var count7B = hw.DiskFreeBytes > 0 ? hw.DiskFreeBytes / (4.4 * Formatters.Gib) : 0;
        var storageMeaning = count7B > 0 ? $"可存约 {count7B:0} 个 7B Q4 模型" : "磁盘空间未知";

        return
        [
            new ScoreComponent("CPU", cpuPoints, PerformanceScoreScale.Cpu,
                benchmark is { CpuGflops: > 0 } ? Provenance.Measured : Provenance.Estimated,
                cpuMetric, cpuMeaning),
            new ScoreComponent("内存", memoryPoints, PerformanceScoreScale.Memory, bandwidthSource,
                memoryMetric, memoryMeaning),
            new ScoreComponent("显卡", gpuPoints, PerformanceScoreScale.Gpu, gpuSource,
                gpuMetric, gpuMeaning),
            new ScoreComponent("存储", storagePoints, PerformanceScoreScale.Storage,
                hw.DiskTotalBytes > 0 ? Provenance.Measured : Provenance.Assumed,
                storageMetric, storageMeaning)
        ];
    }

    /// <summary>能力画像：只依赖稳定预算，回答「能跑多大的模型」而不是「多少分」。</summary>
    private static CapabilityProfile BuildCapability(
        HardwareProfile hw,
        GpuCapability? gpu,
        double bandwidthGb,
        double cpuNorm)
    {
        var discrete = gpu is { IsIntegrated: false };
        var vramBudgetGb = discrete ? gpu!.UsableVramGb * 0.9 : 0;
        var comfortable = discrete ? (int)Math.Floor(MaxParamsB(vramBudgetGb)) : 0;
        var playable = (int)Math.Floor(MaxParamsB(hw.TotalRamGb * 0.8));

        var maxContext = 2048;
        if (comfortable > 0)
        {
            for (var context = 2048; context <= 32768; context += 2048)
            {
                var weight = 0.62 * comfortable;
                var kv = RecommendationEngine.EstimateKvCacheGb(comfortable, context);
                var compute = 0.3 + weight * 0.03;
                if (weight + kv + compute + 0.6 > vramBudgetGb) break;
                maxContext = context;
            }
        }

        var bottleneck = !discrete
            ? $"内存带宽 {bandwidthGb:0} GB/s"
            : comfortable < playable
                ? $"显存 {gpu!.UsableVramGb:0.#} GB"
                : cpuNorm < 0.45 ? "CPU" : "无";

        var hint = !discrete
            ? "加装独立显卡可显著提速；当前只能 CPU + 内存推理。"
            : comfortable < playable
                ? "更大显存的显卡可把「部分卸载」变成「完整装入」。"
                : cpuNorm < 0.45
                    ? "处理器较旧，建议降低上下文或选择更小的模型。"
                    : "当前配置没有明显短板，可优先选择质量更高的量化。";

        return new CapabilityProfile(comfortable, playable, maxContext, bottleneck, hint);
    }

    private static string BuildSummary(
        HardwareProfile hw,
        GpuCapability? gpu,
        BenchmarkResult? benchmark,
        int total)
    {
        var dedicated = gpu is { IsIntegrated: false };
        var usableGb = dedicated ? gpu!.UsableVramGb : 0;
        var ramGb = hw.TotalRamGb * 0.8;
        var vramMaxB = dedicated ? MaxParamsB(usableGb) : 0;
        var ramMaxB = MaxParamsB(ramGb);
        var headline = dedicated
            ? $"显存可完整装入约 {vramMaxB:0.#} B、内存可运行约 {ramMaxB:0.#} B（Q4_K_M · 8K 上下文）"
            : $"无独显：内存可运行约 {ramMaxB:0.#} B（Q4_K_M · 8K 上下文的 CPU 推理）";

        // 7B Q4_K_M 权重约 4.4 GB：给出可感知的解码速度估算。
        var bandwidthGbps = dedicated
            ? gpu!.BandwidthGBps
            : benchmark is { MemoryBandwidthGBps: > 0 } ? benchmark.MemoryBandwidthGBps : 51;
        var usableBandwidth = Math.Min(bandwidthGbps, Math.Max(2, hw.Cpu.PhysicalCores) * 9d);
        var speed7b = Math.Clamp(usableBandwidth * (dedicated ? 0.55 : 0.58) / 4.4, 0.5, 200);
        var speedText = dedicated
            ? $"7B Q4 满显存预计约 {Math.Round(speed7b)} tok/s"
            : $"7B Q4 纯 CPU 预计约 {Math.Round(speed7b)} tok/s";

        var memory = gpu is null
            ? $"无独立显卡，将使用 CPU + 内存推理（{hw.TotalRamGb:0} GB）"
            : dedicated
                ? $"可用显存 {gpu!.UsableVramGb:0.#} GB · 内存 {hw.TotalRamGb:0} GB"
                : $"核显不参与显存卸载 · 内存 {hw.TotalRamGb:0} GB";

        return $"{headline}。{memory}。{speedText}。";
    }

    /// <summary>
    /// 按 Q4_K_M 权重 + 8K 上下文 KV + 计算缓冲 + 0.6 GB 系统预留，估算给定预算下可承载的参数规模。
    /// 与 RecommendationEngine 的成本模型保持一致，避免「总分高」被误写成「可流畅运行 30B」。
    /// </summary>
    private static double MaxParamsB(double budgetGb)
    {
        var best = 0.0;
        for (var p = 0.5; p <= 300; p += 0.5)
        {
            var weight = 0.62 * p;
            var kv = Math.Min(2.6, 0.05 * p);
            var compute = 0.3 + weight * 0.03;
            if (weight + kv + compute + 0.6 > budgetGb) break;
            best = p;
        }

        return best;
    }
}
