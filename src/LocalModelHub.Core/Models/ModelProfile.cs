namespace LocalModelHub.Core.Models;

/// <summary>模型能力探测结果（按模型文件指纹 + chat template 指纹缓存，加载后探测一次）。</summary>
public sealed record ModelProfile(
    string ModelFingerprint,
    string TemplateHash,
    bool SupportsNativeTools,
    bool SupportsJsonGrammar,
    int NCtx,
    DateTime ProbedAt)
{
    /// <summary>探测来源：真实工具调用请求 / 仅凭 chat template 推断。</summary>
    public bool ProbedByRequest { get; init; }

    public string ToolModeLabel => SupportsNativeTools ? "原生" : "文本协议";

    public string ToolModeText => $"工具调用：{ToolModeLabel}";

    public string ProbeSourceText => ProbedByRequest ? "实测" : "模板推断";
}
