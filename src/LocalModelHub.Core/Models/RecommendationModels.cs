namespace LocalModelHub.Core.Models;

public enum FitLevel
{
    Perfect = 0,
    Good = 1,
    Playable = 2,
    Tight = 3,
    Insufficient = 4
}

public sealed record ModelRecommendation(
    ModelEntry Model,
    ModelQuant Quant,
    FitLevel Fit,
    double EstimatedTokensPerSecond,
    double PrefillSeconds,
    double RequiredRamGb,
    double RequiredVramGb,
    int SortScore,
    string Reason,
    int SuggestedContextTokens = 2048,
    string ContextNote = "",
    double ReservedVramGb = 0,
    bool ContextLimited = false)
{
    /// <summary>建议上下文（按 2K 取整），与当前设置分开，避免长上下文设置把结论套到所有模型。</summary>
    public string SuggestedContextLabel => $"{SuggestedContextTokens / 1024}K";

    /// <summary>建议上下文确实可运行且低于当前设置时，才值得在卡片上提示。</summary>
    public bool HasContextAdvice => ContextLimited;

    /// <summary>当前被其它程序占用的显存是否大到需要在提示里说明。</summary>
    public bool HasReservedVram => ReservedVramGb >= 0.5;

    public string FitLabel => Fit switch
    {
        FitLevel.Perfect => "完美运行",
        FitLevel.Good => "显存 + 内存",
        FitLevel.Playable => "CPU 运行",
        FitLevel.Tight => "勉强可跑",
        _ => "内存不足"
    };

    /// <summary>面向用户的适配度解释，用于卡片提示与图例。</summary>
    public string FitDescription => Fit switch
    {
        FitLevel.Perfect => "模型可完整装入显卡显存，速度最快。",
        FitLevel.Good => "显存装不下全部层，一部分层放到内存，仍可用。",
        FitLevel.Playable => "显存不足或没有独显，将用 CPU + 内存推理，速度较慢。",
        FitLevel.Tight => "可用内存很紧张，可能卡顿或加载失败。",
        _ => "当前硬件无法顺畅通入这个量化版本。"
    };

    /// <summary>生成速度给区间而不是单点：带宽、驱动与上下文都会让实测偏离估算。</summary>
    public string SpeedLabel => EstimatedTokensPerSecond >= 1
        ? $"约 {Math.Max(1, EstimatedTokensPerSecond * 0.75):0}–{EstimatedTokensPerSecond * 1.25:0} tok/s"
        : "< 1 tok/s";

    /// <summary>固定长度提示（512 token）下从点击发送到首个 token 的时间估算。</summary>
    public string PrefillLabel => PrefillSeconds >= 1
        ? $"首字约 {PrefillSeconds:0} s"
        : $"首字 < 1 s";
}

public sealed class RecommendationOptions
{
    public int ContextTokens { get; set; } = 8192;
    public double UsableRamRatio { get; set; } = 0.8;

    /// <summary>K cache 量化类型（f16 / q8_0 / q4_0 等），直接决定每个元素的字节数。</summary>
    public string KvCacheTypeK { get; set; } = "f16";

    /// <summary>V cache 量化类型；关闭 FlashAttention 时 llama.cpp 会拒绝量化 V，评估按 f16 处理。</summary>
    public string KvCacheTypeV { get; set; } = "f16";

    /// <summary>显式指定的 GPU 卸载层数；-1 表示由 llama.cpp 自动决定。</summary>
    public int GpuLayers { get; set; } = -1;

    public bool FlashAttention { get; set; } = true;

    public int ParallelSlots { get; set; } = 1;

    /// <summary>当前运行时后端（cuda / vulkan / cpu），仅用于展示与诊断。</summary>
    public string Backend { get; set; } = "cuda";

    /// <summary>CPU 推理时的内存带宽（GB/s）；DDR4 双通道约 51，DDR5 双通道约 89。</summary>
    public double CpuMemoryBandwidthGBps { get; set; } = 51;

    /// <summary>实测 CPU FP32 算力（GFLOPS）；0 表示按物理核数保守估算。</summary>
    public double CpuGflops { get; set; }

    /// <summary>首字延迟估算用的提示长度（token）。</summary>
    public int PrefillPromptTokens { get; set; } = 512;

    /// <summary>部分卸载时跨 PCIe 搬运的有效带宽（GB/s），PCIe 4.0 x16 实际约 10。</summary>
    public double PcieEffectiveGBps { get; set; } = 10;

    /// <summary>低于该速度视为「能加载但不实用」，排序时降到 CPU 慢速档。</summary>
    public double MinUsefulTokensPerSecond { get; set; } = 3.0;

    /// <summary>
    /// 当前被其它程序（浏览器、播放器等）占用的显存（GB），评估时先从可用显存里扣除；
    /// 未知时为 0，不改变原有结论。
    /// </summary>
    public double ReservedVramGb { get; set; }
}
