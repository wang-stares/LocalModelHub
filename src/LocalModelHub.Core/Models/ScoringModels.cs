namespace LocalModelHub.Core.Models;

/// <summary>每个关键数字的来源，避免把公式推算与实测值混在一起展示。</summary>
public enum Provenance
{
    /// <summary>本机实测（基准 / 硬件探测）。</summary>
    Measured,

    /// <summary>查表（GPU 数据库、内置型号表）。</summary>
    Lookup,

    /// <summary>按公式推算。</summary>
    Estimated,

    /// <summary>没有数据时的保守默认值。</summary>
    Assumed
}

public static class ProvenanceText
{
    public static string Label(this Provenance source) => source switch
    {
        Provenance.Measured => "实测",
        Provenance.Lookup => "查表",
        Provenance.Estimated => "估算",
        _ => "默认"
    };
}

/// <summary>评分分项：分数 + 绝对指标 + 一句人话含义 + 来源。</summary>
public sealed record ScoreComponent(
    string Name,
    double Points,
    double MaxPoints,
    Provenance Source,
    string Metric,
    string Meaning)
{
    public string PointsText => $"{Points:0.#} / {MaxPoints:0}";
    public string SourceText => Source.Label();
}

/// <summary>
/// 能力画像：把分数翻译成「能跑多大的模型」。由稳定输入推导，不依赖当前可用内存。
/// </summary>
public sealed record CapabilityProfile(
    int ComfortableParamsB,
    int PlayableParamsB,
    int MaxContextTokens,
    string Bottleneck,
    string UpgradeHint)
{
    public string Headline => ComfortableParamsB > 0
        ? $"流畅运行 {ComfortableParamsB}B · 可跑 {PlayableParamsB}B"
        : $"可用 {PlayableParamsB}B（CPU 推理）";

    public string BottleneckText => $"瓶颈：{Bottleneck}";

    public string ContextText => $"完整装入时建议上下文 {MaxContextTokens / 1024}K";
}
