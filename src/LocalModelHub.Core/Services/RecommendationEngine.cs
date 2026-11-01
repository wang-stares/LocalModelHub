using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>
/// 根据显存 / 可用内存为每个模型估算可行性与速度，并按「能跑 → 能力 → 质量 → 速度」排序。
/// </summary>
public sealed class RecommendationEngine
{
    private const double Gib = Formatters.Gib;
    private const double BalancedQuality = 4.5;

    private static readonly Regex NamePattern = new(@"[^A-Z0-9.]+", RegexOptions.Compiled);
    private static readonly Regex QuantTokenPattern = new(@"\b(GGUF|GPTQ|AWQ|MLX|EXL2|Q\d+(_[A-Z0-9]+)?|IQ\d+(_[A-Z0-9]+)?)\b", RegexOptions.Compiled);
    private static readonly Regex UnstableNamePattern = new(@"\b(RC\d*|PREVIEW|BETA|EXPERIMENTAL|DEV|TEST)\b", RegexOptions.Compiled);

    public IReadOnlyList<ModelRecommendation> Recommend(
        HardwareProfile hw,
        PerformanceScore performance,
        ModelCatalog catalog,
        RecommendationOptions? options = null)
    {
        options ??= new RecommendationOptions();

        // 同名同参数量的多个仓库（官方 / 第三方转制）只保留评分最高的一条。
        var best = new Dictionary<string, ModelRecommendation>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in catalog.Models)
        {
            if (model.Quants.Length == 0) continue;

            var recommendation = RecommendBestQuant(hw, performance, model, options).Recommendation;
            var key = DedupKey(model);
            if (!best.TryGetValue(key, out var current) || recommendation.SortScore > current.SortScore)
            {
                best[key] = recommendation;
            }
        }

        return best.Values
            .OrderByDescending(r => r.SortScore)
            .ThenByDescending(r => r.EstimatedTokensPerSecond)
            .ToList();
    }

    /// <summary>
    /// 为一个模型逐档量化评估，返回最适合本机的一档：
    /// 能进显存时选质量最高的量化；只能用 CPU 时选接近 Q4 的平衡档；都跑不动时选最小体积。
    /// </summary>
    public (int Index, ModelRecommendation Recommendation) RecommendBestQuant(
        HardwareProfile hw,
        PerformanceScore performance,
        ModelEntry model,
        RecommendationOptions? options = null)
    {
        options ??= new RecommendationOptions();
        var bestIndex = 0;
        ModelRecommendation? best = null;
        var evaluated = new List<ModelRecommendation>(model.Quants.Length);
        for (var index = 0; index < model.Quants.Length; index++)
        {
            var candidate = Evaluate(hw, performance, model, model.Quants[index], options);
            evaluated.Add(candidate);
            if (best is null || IsBetter(candidate, best))
            {
                best = candidate;
                bestIndex = index;
            }
        }

        if (best is null)
        {
            best = Evaluate(hw, performance, model, model.PrimaryQuant, options);
            evaluated.Add(best);
        }

        // 同模型其它量化的对比行：体积 / 速度 / 质量 / 结论，供卡片并排展示。
        if (evaluated.Count > 1)
        {
            var alternatives = evaluated
                .Select((item, index) => new QuantTradeoff(
                    item.Quant.Label,
                    Math.Round(item.Quant.TotalBytes / Formatters.Gib, 1),
                    Math.Max(0.1, Math.Round(item.EstimatedTokensPerSecond * 0.75, 1)),
                    Math.Round(item.EstimatedTokensPerSecond * 1.25, 1),
                    QuantQuality(item.Quant.Label),
                    item.Fit,
                    index == bestIndex))
                .ToList();
            best = best with { Alternatives = alternatives };
        }

        return (bestIndex, best);
    }

    private static bool IsBetter(ModelRecommendation candidate, ModelRecommendation current)
    {
        var candidateRank = FitSortRank(candidate.Fit);
        var currentRank = FitSortRank(current.Fit);
        if (candidateRank != currentRank) return candidateRank > currentRank;

        var candidateQuality = QuantQuality(candidate.Quant.Label);
        var currentQuality = QuantQuality(current.Quant.Label);

        // 能上显卡时，质量优先；只能靠 CPU/内存时，选体积与质量更均衡的档位。
        if (candidate.Fit is FitLevel.Perfect or FitLevel.Good)
        {
            if (Math.Abs(candidateQuality - currentQuality) > 0.001)
            {
                return candidateQuality > currentQuality;
            }

            return candidate.EstimatedTokensPerSecond > current.EstimatedTokensPerSecond;
        }

        var candidateDistance = Math.Abs(candidateQuality - BalancedQuality);
        var currentDistance = Math.Abs(currentQuality - BalancedQuality);
        if (Math.Abs(candidateDistance - currentDistance) > 0.001)
        {
            return candidateDistance < currentDistance;
        }

        return candidate.Quant.TotalBytes < current.Quant.TotalBytes;
    }

    /// <summary>量化档位的质量分（0-8），用于推荐排序与卡片说明。</summary>
    public static double QuantQuality(string label)
    {
        var text = (label ?? "").Trim().ToUpperInvariant();
        if (text.Length == 0) return 4.0;
        if (text.Contains("F32")) return 8.0;
        if (text.Contains("BF16") || text.Contains("F16")) return 7.6;
        if (text.StartsWith("Q8") || text.Contains("IQ8")) return 7.0;
        if (text.StartsWith("Q6")) return 6.0;
        if (text.StartsWith("Q5")) return 5.2;
        if (text.StartsWith("Q4") || text.Contains("IQ4")) return 4.5;
        if (text.StartsWith("Q3") || text.Contains("IQ3")) return 3.4;
        if (text.StartsWith("Q2") || text.Contains("IQ2")) return 2.2;
        if (text.StartsWith("IQ1") || text.StartsWith("Q1")) return 1.4;
        return 4.0;
    }

    /// <summary>
    /// 按常见 GQA 配置估算 KV cache 体积（FP16）：小模型用窄 KV，大模型用宽 KV。
    /// </summary>
    public static double EstimateKvCacheGb(double parametersB, int contextTokens) =>
        EstimateKvCacheGb(parametersB, contextTokens, "f16", "f16");

    /// <summary>按 K/V 量化位宽估算 KV cache：q8_0 约为 f16 的 53%，q4_0 约为 28%。</summary>
    public static double EstimateKvCacheGb(
        double parametersB,
        int contextTokens,
        string kvTypeK,
        string kvTypeV)
    {
        var (layers, kvWidth) = parametersB switch
        {
            < 1 => (24, 128),
            < 3.5 => (28, 256),
            < 9 => (32, 512),
            < 16 => (40, 640),
            < 32 => (56, 768),
            < 80 => (64, 1024),
            _ => (80, 1024)
        };

        var perTokenBytes = layers * kvWidth * (KvBytesPerElement(kvTypeK) + KvBytesPerElement(kvTypeV));
        return Math.Max(0.05, perTokenBytes * contextTokens / Gib);
    }

    /// <summary>每种 KV 量化类型每元素占用的字节数（含 block scale 开销）。</summary>
    public static double KvBytesPerElement(string? type) => (type ?? "f16").Trim().ToLowerInvariant() switch
    {
        "f32" => 4.0,
        "bf16" or "f16" => 2.0,
        "q8_0" => 1.0625,   // 8.5 bit
        "q5_1" => 0.75,
        "q5_0" => 0.6875,
        "q4_1" => 0.625,
        "q4_0" or "iq4_nl" => 0.5625,
        _ => 2.0
    };

    /// <summary>llama.cpp 只在 FlashAttention 开启时支持量化 V cache，关闭时按 f16 计。</summary>
    public static string EffectiveKvTypeV(RecommendationOptions options) =>
        options.FlashAttention ? options.KvCacheTypeV : "f16";

    public ModelRecommendation Evaluate(
        HardwareProfile hw,
        PerformanceScore performance,
        ModelEntry model,
        ModelQuant quant,
        RecommendationOptions? options = null)
    {
        options ??= new RecommendationOptions();
        var sizeGb = quant.TotalBytes / Gib;

        // 模型自带的上下文上限优先，避免给 2K 模型按 8K 估 KV。
        var declaredCtx = model.ContextLength > 0 ? model.ContextLength : options.ContextTokens;
        var ctx = Math.Max(2048, Math.Min(options.ContextTokens, declaredCtx));

        var gpu = performance.BestGpu;
        var discrete = gpu is not null && !gpu.IsIntegrated;

        // 其它程序实际占用的显存先从预算里扣掉，避免按「刚开机」的空闲值高估。
        var reservedVramGb = gpu is null
            ? 0
            : Math.Clamp(options.ReservedVramGb, 0, gpu.UsableVramGb);
        var usableVramGb = gpu is null ? 0 : Math.Max(0, gpu.UsableVramGb - reservedVramGb);

        // 内存预算以总量比例为主；实测可用内存明显偏低时才收紧（页面缓存可回收，不完全按瞬时值）。
        var ramBudgetGb = hw.TotalRamGb * options.UsableRamRatio;
        if (hw.AvailableRamGb > 0.2 && hw.AvailableRamGb < ramBudgetGb * 0.5)
        {
            // 瞬时可用内存不到预算一半时才降级；页面缓存可被回收，按 1.5 倍留余量。
            ramBudgetGb = Math.Max(hw.AvailableRamGb * 1.5, ramBudgetGb * 0.5);
        }

        var usableRamGb = Math.Max(0, ramBudgetGb - (gpu?.IsIntegrated == true ? 1.5 : 0));

        // 显存在 Windows 下还会被桌面/驱动/碎片占用，按九成预算判断「完整装入」。
        var vramBudgetGb = usableVramGb * 0.9;

        var plan = PlanFit(model.ParametersB, sizeGb, ctx, usableVramGb, usableRamGb, vramBudgetGb, discrete, options);
        var fit = plan.Fit;
        var (suggestedCtx, contextNote, contextLimited) = SuggestContext(
            model.ParametersB, sizeGb, ctx, usableVramGb, usableRamGb, vramBudgetGb, discrete, fit, options);

        var activeSizeGb = model.IsMoe && model.ParametersB > 0
            ? Math.Max(0.4, sizeGb * model.ActiveParametersB / model.ParametersB)
            : sizeGb;

        // CPU 解码速度 ≈ 内存带宽 ÷ 每 token 需要读的权重；MoE 只读激活专家，但有路由开销。
        var cpuThreads = hw.Cpu.PhysicalCores > 0 ? hw.Cpu.PhysicalCores : Environment.ProcessorCount;
        var threadCapGb = cpuThreads * 9.0;
        var cpuEffectiveSize = model.IsMoe ? Math.Max(0.4, activeSizeGb * 1.6) : sizeGb;
        var cpuSpeed = Math.Min(options.CpuMemoryBandwidthGBps * 0.58, threadCapGb) / Math.Max(0.4, cpuEffectiveSize);
        if (!hw.HasAvx2) cpuSpeed *= 0.6;
        else if (hw.HasAvx512) cpuSpeed *= 1.08;
        if (model.IsMoe) cpuSpeed *= 1.15;

        var gpuSpeed = gpu is null
            ? 0
            : Math.Max(0.5, gpu.BandwidthGBps * 0.55 / Math.Max(0.4, activeSizeGb));

        double estimated;
        if (fit == FitLevel.Perfect)
        {
            estimated = gpuSpeed;
        }
        else if (fit == FitLevel.Good && gpu is not null)
        {
            var vramShare = Math.Clamp(vramBudgetGb / plan.FullVramGb, 0.12, 0.95);
            var ramShare = 1 - vramShare;
            // 未卸载的层每生成一个 token 都要经 PCIe 搬运，不能只按比例混合两条算力。
            var transferGb = sizeGb * ramShare * 0.4;
            var transferSeconds = transferGb / Math.Max(1.5, options.PcieEffectiveGBps);
            estimated = 1.0 / (vramShare / Math.Max(1, gpuSpeed) + ramShare / Math.Max(0.5, cpuSpeed) + transferSeconds);
        }
        else
        {
            estimated = cpuSpeed;
        }

        estimated = Math.Round(Math.Clamp(estimated, 0.3, 180), 1);

        // 首字延迟（prefill）受算力限制，而不是内存带宽：按「有效算力 × 量化点积加速 × 0.45 效率」估算。
        var cpuGflops = options.CpuGflops > 0 ? options.CpuGflops : Math.Max(20, cpuThreads * 80d);
        var gpuTflops = gpu is null ? 0 : GpuDatabase.EstimateTflops(gpu.Gpu, gpu.IsIntegrated);
        double prefillTflops;
        if (fit == FitLevel.Perfect && gpuTflops > 0)
        {
            prefillTflops = gpuTflops;
        }
        else if (fit == FitLevel.Good && gpuTflops > 0)
        {
            var share = Math.Clamp(usableVramGb / plan.FullVramGb, 0.1, 0.95);
            prefillTflops = share * gpuTflops + (1 - share) * cpuGflops / 1000d;
        }
        else
        {
            prefillTflops = cpuGflops / 1000d;
        }

        // MoE 每 token 只计算激活专家，prefill 计算量也随之下降。
        var computeParamsB = model.IsMoe
            ? Math.Max(0.5, model.ActiveParametersB)
            : Math.Max(0.5, model.ParametersB);
        var promptTokens = Math.Max(64, options.PrefillPromptTokens);
        var prefillSeconds = 2d * computeParamsB * 1e9 * promptTokens /
                             (Math.Max(0.02, prefillTflops) * 1e12 * 0.45 * QuantComputeFactor(quant.Label));
        prefillSeconds = Math.Round(Math.Clamp(prefillSeconds, 0.05, 300), 1);

        // 分层排序：完美运行 > 显存+内存 > CPU 可用速度 > CPU 慢速；同层内再比能力、量化、热度与速度。
        var speedOk = estimated >= options.MinUsefulTokensPerSecond;
        var tierScore = fit switch
        {
            FitLevel.Perfect => 24_000_000,
            FitLevel.Good => 22_000_000,
            FitLevel.Playable => speedOk ? 18_000_000 : 12_000_000,
            _ => 0
        };

        var qualityBoost = (int)Math.Round(QuantQuality(quant.Label) * 600);
        var speedBoost = (int)(Math.Min(60, estimated) * 40);
        var popularityBoost = model.Downloads > 0
            ? (int)Math.Min(4000, Math.Log10(model.Downloads + 1) * 700)
            : 0;
        var recencyBoost = model.ReleaseYear switch
        {
            >= 2026 => 500,
            2025 => 600,
            2024 => 350,
            >= 2022 => 100,
            _ => 0
        };
        var contextBoost = model.ContextLength switch
        {
            >= 32768 => 400,
            >= 16384 => 250,
            >= 8192 => 100,
            > 0 and < 4096 => -300,
            _ => 0
        };

        var sortScore = tierScore
            + SuitabilityScore(model) * 1000
            + qualityBoost
            + speedBoost
            + popularityBoost
            + recencyBoost
            + contextBoost
            - QualityPenalty(model);

        var recommendation = new ModelRecommendation(
            model,
            quant,
            fit,
            estimated,
            prefillSeconds,
            Math.Round(plan.NeedRamGb, 1),
            Math.Round(plan.FullVramGb, 1),
            sortScore,
            BuildReason(fit, estimated, gpu, usableVramGb, plan.FullVramGb, sizeGb, model),
            suggestedCtx,
            contextNote,
            reservedVramGb,
            contextLimited);

        // 速度区间与依据、启动参数、场景标签、结构化理由、置信度：让推荐从「一个结论」变成「可决策」。
        var measured = options.CpuGflops > 0;
        var speedBasis = fit switch
        {
            FitLevel.Perfect => "显存带宽查表",
            FitLevel.Good => "按卸载比例估算",
            _ => measured ? "实测带宽" : "默认带宽估算"
        };
        var confidence = 0.45;
        if (measured) confidence += 0.2;
        if (measured && options.CpuMemoryBandwidthGBps > 10) confidence += 0.1;
        if (gpu is not null && !gpu.Gpu.VramEstimated && !gpu.IsIntegrated) confidence += 0.15;
        if (quant.TotalBytes > 0) confidence += 0.05;
        if (model.ParametersB > 0) confidence += 0.05;

        var blocks = EstimateBlockCount(model.ParametersB);
        var gpuLayers = discrete
            ? fit == FitLevel.Perfect
                ? 999
                : ComputeGpuLayers(
                    performance, quant.TotalBytes, blocks, suggestedCtx,
                    options.KvCacheTypeK, EffectiveKvTypeV(options), options.FlashAttention)
            : 0;
        var kvV = EffectiveKvTypeV(options);
        var commandLine = $"-ngl {gpuLayers} -c {suggestedCtx} -ctk {options.KvCacheTypeK} -ctv {kvV}"
                          + $" -fa {(options.FlashAttention ? "on" : "off")} -b 256 -ub 128"
                          + (options.CpuMoeLayers > 0 ? $" --n-cpu-moe {options.CpuMoeLayers}" : "");

        var overheadGb = Math.Max(0, plan.FullVramGb - sizeGb * 1.08);
        var reasons = new List<string>
        {
            fit switch
            {
                FitLevel.Perfect => $"权重 {sizeGb:0.#} GB + KV/缓冲 {overheadGb:0.#} GB，可完整装入 {usableVramGb:0.#} GB 显存",
                FitLevel.Good => $"显存差 {Math.Max(0, plan.FullVramGb - vramBudgetGb):0.#} GB，需卸载部分层到内存",
                FitLevel.Playable => gpu is null
                    ? $"无独显，{sizeGb:0.#} GB 权重走 CPU + 内存"
                    : $"显存不足以完整卸载，{sizeGb:0.#} GB 权重走 CPU + 内存",
                _ => $"内存预算不足：需要约 {plan.NeedRamGb:0.#} GB"
            },
            measured
                ? $"速度按实测 CPU {options.CpuGflops:0.#} GFLOPS / 带宽 {options.CpuMemoryBandwidthGBps:0.#} GB/s 估算"
                : "速度按 DDR4 双通道 51 GB/s 的默认带宽估算"
        };
        if (model.IsMoe && model.ActiveParametersB > 0)
        {
            reasons.Add($"MoE 每 token 只激活 {model.ActiveParametersB:0.#}B，速度快于同等总参数模型");
        }

        if (contextLimited)
        {
            reasons.Add(contextNote);
        }

        return recommendation with
        {
            Speed = new SpeedRange(
                Math.Max(0.1, Math.Round(estimated * 0.75, 1)),
                Math.Round(estimated * 1.25, 1),
                speedBasis),
            Launch = new LaunchSuggestion(
                gpuLayers, suggestedCtx, options.KvCacheTypeK, kvV,
                options.FlashAttention, options.CpuMoeLayers, commandLine),
            Scenarios = BuildScenarios(model),
            Reasons = reasons,
            Confidence = Math.Clamp(confidence, 0.35, 0.95)
        };
    }

    /// <summary>按常见模型规模估算层数，仅用于启动参数的 -ngl 建议。</summary>
    public static int EstimateBlockCount(double parametersB) => parametersB switch
    {
        < 1 => 24,
        < 3.5 => 28,
        < 9 => 32,
        < 16 => 40,
        < 32 => 56,
        < 80 => 64,
        _ => 80
    };

    /// <summary>场景标签优先取模型自带的 Capabilities，再按上下文与参数量补足。</summary>
    private static IReadOnlyList<string> BuildScenarios(ModelEntry model)
    {
        var scenarios = new List<string>();
        foreach (var capability in model.Capabilities)
        {
            if (scenarios.Count >= 3) break;
            if (string.IsNullOrWhiteSpace(capability)) continue;
            if (capability.Equals(ModelCapabilities.Chat, StringComparison.Ordinal)) continue;
            if (scenarios.Contains(capability)) continue;
            scenarios.Add(capability);
        }

        if (model.ContextLength >= 32768) scenarios.Add("长文档");
        if (model.ParametersB is > 0 and < 3.5 && scenarios.Count < 4) scenarios.Add("轻量设备");
        if (scenarios.Count == 0) scenarios.Add(model.IsMoe ? "MoE 性价比" : "通用对话");
        return scenarios.Count > 4 ? scenarios[..4] : scenarios;
    }

    private readonly record struct FitPlan(FitLevel Fit, double FullVramGb, double NeedRamGb);

    /// <summary>给定上下文下的显存 / 内存需求与适配等级，供主评估与建议上下文复用。</summary>
    private static FitPlan PlanFit(
        double parametersB,
        double sizeGb,
        int contextTokens,
        double usableVramGb,
        double usableRamGb,
        double vramBudgetGb,
        bool hasDiscreteGpu,
        RecommendationOptions options)
    {
        // KV cache 按 K/V 各自的量化位宽计算；计算缓冲随权重规模与上下文增长。
        var kvGb = EstimateKvCacheGb(parametersB, contextTokens,
            options.KvCacheTypeK, EffectiveKvTypeV(options));
        var computeBufferGb = 0.3 + sizeGb * 0.03 + contextTokens / 8192d * 0.12;
        var overheadGb = kvGb + computeBufferGb;
        var fullVramGb = sizeGb * 1.08 + overheadGb;
        var needRamGb = sizeGb * 1.06 + overheadGb + 0.8;

        var fit = FitLevel.Insufficient;
        if (hasDiscreteGpu && vramBudgetGb >= fullVramGb)
        {
            fit = FitLevel.Perfect;
        }
        else if (hasDiscreteGpu && vramBudgetGb >= fullVramGb * 0.62 &&
                 usableRamGb >= sizeGb * 0.6 + 1)
        {
            fit = FitLevel.Good;
        }
        else if (usableRamGb >= needRamGb)
        {
            fit = FitLevel.Playable;
        }
        else if (usableRamGb >= sizeGb * 0.98)
        {
            fit = FitLevel.Tight;
        }

        return new FitPlan(fit, fullVramGb, needRamGb);
    }

    /// <summary>
    /// 从当前生效的上下文按 2K 递减，找到仍能维持该量化最佳适配档位的长度：
    /// KV cache 与计算缓冲随上下文线性增长，超长上下文会把「完整装入」拖成「部分卸载」。
    /// </summary>
    private static (int Tokens, string Note, bool Limited) SuggestContext(
        double parametersB,
        double sizeGb,
        int currentCtx,
        double usableVramGb,
        double usableRamGb,
        double vramBudgetGb,
        bool hasDiscreteGpu,
        FitLevel currentFit,
        RecommendationOptions options)
    {
        // 适配等级随上下文单调变差，2K 时的档位就是该量化在本机的最好结果。
        var best = PlanFit(parametersB, sizeGb, 2048, usableVramGb, usableRamGb, vramBudgetGb, hasDiscreteGpu, options).Fit;
        if (best > FitLevel.Playable)
        {
            return (2048, "即使 2K 上下文，显存/内存预算仍然不够，可能加载失败。", false);
        }

        for (var candidate = currentCtx; candidate >= 2048; candidate -= 2048)
        {
            var plan = PlanFit(parametersB, sizeGb, candidate, usableVramGb, usableRamGb, vramBudgetGb, hasDiscreteGpu, options);
            if (plan.Fit != best) continue;

            return candidate >= currentCtx
                ? (candidate, $"当前 {currentCtx / 1024}K 上下文在该硬件的最佳适配档内。", false)
                : (candidate,
                    $"建议上下文 {candidate / 1024}K：当前 {currentCtx / 1024}K 会从「{FitText(best)}」降到「{FitText(currentFit)}」。",
                    true);
        }

        return (2048, "即使 2K 上下文，显存/内存预算仍然不够，可能加载失败。", false);
    }

    private static string FitText(FitLevel fit) => fit switch
    {
        FitLevel.Perfect => "完整装入显存",
        FitLevel.Good => "部分卸载",
        FitLevel.Playable => "CPU 运行",
        FitLevel.Tight => "勉强可跑",
        _ => "内存不足"
    };

    /// <summary>量化越激进，prefill 的点积吞吐越高；F16 直接吃算力，最慢。</summary>
    private static double QuantComputeFactor(string label)
    {
        var upper = label.ToUpperInvariant();
        if (upper.Contains("Q2") || upper.Contains("Q3")) return 2.0;
        if (upper.Contains("Q4")) return 1.8;
        if (upper.Contains("Q5") || upper.Contains("Q6")) return 1.6;
        if (upper.Contains("Q8")) return 1.2;
        if (upper.Contains("F16") || upper.Contains("BF16")) return 0.7;
        if (upper.Contains("F32")) return 0.35;
        return 1.5;
    }

    /// <summary>按参数量给出「能承担多复杂任务」的粗略分档（0-2800）。</summary>
    public static int SuitabilityScore(ModelEntry model)
    {
        var effective = model.IsMoe
            ? (model.ParametersB + model.ActiveParametersB * 2) / 3
            : model.ParametersB;

        return effective switch
        {
            < 1.0 => 0,
            < 2.0 => 300,
            < 3.5 => 600,
            < 6 => 1000,
            < 10 => 1400,
            < 16 => 1800,
            < 32 => 2200,
            < 70 => 2500,
            _ => 2800
        };
    }

    /// <summary>实验性版本、几乎无人使用的仓库、参数量未知的模型降权。</summary>
    private static int QualityPenalty(ModelEntry model)
    {
        var name = $"{model.Name} {model.DisplayName}".ToUpperInvariant();
        var penalty = 0;
        if (UnstableNamePattern.IsMatch(name)) penalty += 1500;
        if (model.Downloads is > 0 and < 1000) penalty += 1200;
        if (model.ParametersB <= 0) penalty += 800;
        return penalty;
    }

    /// <summary>同名同参数量的仓库视为同一模型，避免推荐列表被重复仓库占满。</summary>
    private static string DedupKey(ModelEntry model)
    {
        var name = model.DisplayName.ToUpperInvariant();
        name = QuantTokenPattern.Replace(name, " ");
        name = NamePattern.Replace(name, "");
        return $"{name}|{model.ParametersB:0.#}";
    }

    /// <summary>根据模型层数与可用显存规划卸载到 GPU 的层数。</summary>
    public static int ComputeGpuLayers(
        PerformanceScore performance,
        long modelBytes,
        int blockCount,
        int contextTokens,
        string kvTypeK = "f16",
        string kvTypeV = "f16",
        bool flashAttention = true)
    {
        var gpu = performance.BestGpu;
        if (gpu is null || gpu.IsIntegrated || blockCount <= 0) return 0;

        var sizeGb = modelBytes / Gib;
        var effectiveV = flashAttention ? kvTypeV : "f16";
        // 这里只有层数、没有真实 KV 宽度，按常见的 512 维估计；K/V 量化位宽与评估口径保持一致。
        var kvGb = blockCount * 512 * (KvBytesPerElement(kvTypeK) + KvBytesPerElement(effectiveV)) *
                   contextTokens / Gib;
        var overheadGb = 0.35 + contextTokens / 8192d * 0.12 + kvGb;
        var fullNeed = sizeGb * 1.07 + overheadGb;
        if (gpu.UsableVramGb >= fullNeed) return 999;

        var budget = Math.Max(0, gpu.UsableVramGb - 0.6);
        var ratio = Math.Clamp(budget / Math.Max(0.5, sizeGb * 1.07), 0, 0.98);
        return Math.Max(0, (int)Math.Floor(blockCount * ratio));
    }

    private static int FitSortRank(FitLevel fit) => fit switch
    {
        FitLevel.Perfect => 5,
        FitLevel.Good => 4,
        FitLevel.Playable => 3,
        FitLevel.Tight => 2,
        _ => 1
    };

    private static string BuildReason(
        FitLevel fit,
        double estimated,
        GpuCapability? gpu,
        double usableVramGb,
        double fullVramGb,
        double sizeGb,
        ModelEntry model)
    {
        var small = model.ParametersB > 0 && model.ParametersB < 3.5
            ? " 参数量较小，适合轻量问答；复杂任务建议选更大模型。"
            : "";

        return fit switch
        {
            FitLevel.Perfect => $"{gpu!.Gpu.Name} 可完整加载，预计 {estimated:0} tok/s。{small}",
            FitLevel.Good => $"显存不足 {fullVramGb - usableVramGb:0.#} GB，部分层将回退到内存，预计 {estimated:0} tok/s。{small}",
            FitLevel.Playable => gpu is null
                ? $"未检测到可用独显，将使用 CPU + 内存推理，预计 {estimated:0} tok/s。{small}"
                : $"显存不足以完整卸载模型，改为 CPU + 内存推理，预计 {estimated:0} tok/s。{small}",
            FitLevel.Tight => $"内存低于推荐值（模型 {sizeGb:0.#} GB），可能卡顿或加载失败。",
            _ => $"内存不足：需要约 {sizeGb * 1.05 + 0.8:0.#} GB 可用内存。"
        };
    }
}
