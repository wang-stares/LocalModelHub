using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 工作区 Agent 依赖的最小对话能力：本地 llama-server 实现它，测试可替换成脚本桩。
/// 只暴露带工具的非流式调用，避免把流式细节带进生命周期逻辑。
/// </summary>
public interface IChatCompletion
{
    Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokens,
        CancellationToken cancellationToken);
}
