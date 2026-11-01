using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services.Abstractions;

namespace LocalModelHub.Core.Services;

/// <summary>IAgentRunner 的默认实现：每次按请求与当前预算构造 WorkspaceAgent。</summary>
public sealed class AgentRunner : IAgentRunner
{
    private readonly IChatCompletion _server;
    private readonly IWorkspace _workspace;
    private readonly IMemoryStore _memory;
    private readonly ICommandRunner? _commands;
    private readonly Func<AgentTuning> _tuning;

    /// <param name="commands">命令白名单执行器；为空时使用默认 CommandService。</param>
    /// <param name="tuning">预算提供者；为空时使用文档默认值。</param>
    public AgentRunner(
        IChatCompletion server,
        IWorkspace workspace,
        IMemoryStore memory,
        ICommandRunner? commands = null,
        Func<AgentTuning>? tuning = null)
    {
        _server = server;
        _workspace = workspace;
        _memory = memory;
        _commands = commands;
        _tuning = tuning ?? (() => AgentTuning.Default);
    }

    public Task<AgentResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        var tuning = _tuning();
        var agent = new WorkspaceAgent(
            _server,
            _workspace,
            _memory,
            request.Mode,
            request.AllowMemoryWrites,
            request.AllowCommands,
            _commands,
            request.TextToolProtocol,
            request.SelfVerify,
            tuning.MaxTokens,
            tuning.ObservationChars,
            tuning.PlanRounds,
            tuning.GoalRounds);

        return agent.RunAsync(
            request.History,
            request.MemoryContext,
            request.OnActivity,
            cancellationToken,
            request.InitialPlan,
            request.OnDelta);
    }
}
