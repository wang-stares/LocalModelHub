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
/// </summary>
public sealed class PerformanceScorer
{
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

        double cpuNorm;
        if (benchmark is { CpuGflops: > 0 })
        {
            cpuNorm = 0.65 * Math.Clamp(benchmark.CpuGflops / 45d, 0, 1) + 0.35 * specCpuNorm;
        }
        else
        {
            cpuNorm = specCpuNorm;
        }

        // 内存：容量决定能装多大模型，可用比例反映系统压力，带宽决定 CPU 推理的解码速度。
        var capacityNorm = Math.Clamp(hw.TotalRamGb / 64d, 0, 1);
        var availableNorm = hw.TotalRamGb > 0
            ? Math.Clamp(hw.AvailableRamGb / hw.TotalRamGb / 0.75, 0, 1)
            : 0;
        var memoryBandwidthNorm = benchmark is { MemoryBandwidthGBps: > 0 }
            ? Math.Clamp(benchmark.MemoryBandwidthGBps / 90d, 0, 1)
            : 0.55; // 没有实测时按 DDR4 双通道中位值
        var memoryNorm = 0.55 * capacityNorm + 0.25 * availableNorm + 0.2 * memoryBandwidthNorm;

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

        var summary = BuildSummary(hw, bestGpu, benchmark, total);
        return new PerformanceScore(
            total,
            Math.Round(cpuPoints, 1),
            Math.Round(memoryPoints, 1),
            Math.Round(gpuPoints, 1),
            Math.Round(storagePoints, 1),
            tier,
            summary,
            bestGpu);
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
