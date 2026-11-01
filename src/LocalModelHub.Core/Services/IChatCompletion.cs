using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 工作区 Agent 依赖的最小对话能力：本地 llama-server 实现它，测试可替换成脚本桩。
/// 只暴露带工具的非流式调用，避免把流式细节带进生命周期逻辑。
/// </summary>
public interface IChatCompletion
{
    /// <summary>服务端实际可用的上下文窗口（token）。实现方未提供时按 8192 估算。</summary>
    int ContextTokens => 8192;

    Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokens,
        CancellationToken cancellationToken);

    /// <summary>
    /// 带流式回调与约束解码的扩展调用。默认实现退化为非流式，
    /// 保证测试桩与其它实现零改动即可编译。
    /// </summary>
    Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokens,
        ChatRequestOptions requestOptions,
        CancellationToken cancellationToken) =>
        ChatAsync(messages, tools, maxTokens, cancellationToken);

    /// <summary>当前已加载模型的能力画像；未探测或后端不支持时返回 null。</summary>
    ModelProfile? ModelProfile => null;

    /// <summary>当前模型名（用于按模型覆盖提示词）；未知时返回 null，退回默认提示词。</summary>
    string? ModelName => null;
}
