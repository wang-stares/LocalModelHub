using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services.Abstractions;

/// <summary>一次 Agent 运行的输入：模式、历史与工具权限；回调用于界面实时展示。</summary>
public sealed record AgentRunRequest(
    AgentMode Mode,
    IReadOnlyList<ChatMessage> History,
    string? MemoryContext = null,
    AgentPlan? InitialPlan = null,
    bool AllowMemoryWrites = true,
    bool AllowCommands = false,
    bool TextToolProtocol = false,
    bool SelfVerify = true,
    Action<ToolActivity>? OnActivity = null,
    Action<string>? OnDelta = null);

/// <summary>Agent 资源预算：单步 tokens、工具结果裁剪、Plan / Goal 模式轮次上限。</summary>
public sealed record AgentTuning(int MaxTokens, int ObservationChars, int PlanRounds, int GoalRounds)
{
    public static AgentTuning Default { get; } = new(
        AgentTuningLimits.DefaultMaxTokens,
        AgentTuningLimits.DefaultObservationChars,
        AgentTuningLimits.DefaultPlanRounds,
        AgentTuningLimits.DefaultGoalRounds);

    /// <summary>从设置读取并归一化到合法区间。</summary>
    public static AgentTuning FromSettings(AppSettings settings)
    {
        var normalized = AgentTuningLimits.Normalize(settings);
        return new AgentTuning(
            normalized.MaxTokens,
            normalized.ObservationChars,
            normalized.PlanRounds,
            normalized.GoalRounds);
    }
}

/// <summary>Agent 执行入口：把请求映射成一次工作区工具循环。</summary>
public interface IAgentRunner
{
    Task<AgentResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken);
}
