namespace LocalModelHub.Core.Services;

/// <summary>
/// Agent 资源预算的默认值与合法区间（P2-6）。设置页与服务共用，
/// 保证「界面上能填的值」和「运行时真正接受的值」口径一致。
/// </summary>
public static class AgentTuningLimits
{
    public const int DefaultPlanRounds = 4;
    public const int DefaultGoalRounds = 10;
    public const int DefaultReadLines = 200;
    public const int DefaultMaxReadWindow = 400;
    public const int DefaultObservationChars = 400;
    public const int DefaultMaxTokens = 2048;

    public const int PlanRoundsMin = 1;
    public const int PlanRoundsMax = 20;
    public const int GoalRoundsMin = 1;
    public const int GoalRoundsMax = 40;
    public const int ReadLinesMin = 50;
    public const int ReadLinesMax = 800;
    public const int MaxReadWindowMax = 2000;
    public const int ObservationCharsMin = 120;
    public const int ObservationCharsMax = 4000;
    public const int MaxTokensMin = 256;
    public const int MaxTokensMax = 8192;

    /// <summary>把设置中的值收敛到合法区间；最大读窗口不小于默认读窗口。</summary>
    public static (
        int PlanRounds,
        int GoalRounds,
        int ReadLines,
        int MaxReadWindow,
        int ObservationChars,
        int MaxTokens) Normalize(AppSettings settings)
    {
        var planRounds = Math.Clamp(settings.AgentPlanRounds, PlanRoundsMin, PlanRoundsMax);
        var goalRounds = Math.Clamp(settings.AgentGoalRounds, GoalRoundsMin, GoalRoundsMax);
        var readLines = Math.Clamp(settings.AgentReadLines, ReadLinesMin, ReadLinesMax);
        var maxWindow = Math.Clamp(settings.AgentMaxReadWindow, readLines, MaxReadWindowMax);
        var observation = Math.Clamp(
            settings.AgentObservationChars, ObservationCharsMin, ObservationCharsMax);
        var maxTokens = Math.Clamp(settings.AgentMaxTokens, MaxTokensMin, MaxTokensMax);
        return (planRounds, goalRounds, readLines, maxWindow, observation, maxTokens);
    }

    /// <summary>读取并直接应用归一化后的值，避免调用点反复写 clamp。</summary>
    public static void ApplyTo(AppSettings settings)
    {
        var normalized = Normalize(settings);
        settings.AgentPlanRounds = normalized.PlanRounds;
        settings.AgentGoalRounds = normalized.GoalRounds;
        settings.AgentReadLines = normalized.ReadLines;
        settings.AgentMaxReadWindow = normalized.MaxReadWindow;
        settings.AgentObservationChars = normalized.ObservationChars;
        settings.AgentMaxTokens = normalized.MaxTokens;
    }
}
