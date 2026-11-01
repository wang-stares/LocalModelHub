using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>P2-6：Agent 资源预算的默认值、归一化与运行期透传。</summary>
public sealed class AgentTuningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-tuning-{Guid.NewGuid():N}");
    private readonly WorkspaceService _workspace = new();
    private readonly FakeChatCompletion _chat = new();

    public AgentTuningTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "old");
        _workspace.SetRoot(_root);
    }

    [Fact]
    public void Default_agent_settings_match_documented_budget()
    {
        var settings = new AppSettings();

        Assert.Equal(4, settings.AgentPlanRounds);
        Assert.Equal(10, settings.AgentGoalRounds);
        Assert.Equal(200, settings.AgentReadLines);
        Assert.Equal(400, settings.AgentMaxReadWindow);
        Assert.Equal(400, settings.AgentObservationChars);
        Assert.Equal(2048, settings.AgentMaxTokens);

        var normalized = AgentTuningLimits.Normalize(new AppSettings
        {
            AgentPlanRounds = 0,
            AgentGoalRounds = 99,
            AgentReadLines = 10,
            AgentMaxReadWindow = 5000,
            AgentObservationChars = 10,
            AgentMaxTokens = 1
        });

        Assert.Equal(1, normalized.PlanRounds);
        Assert.Equal(40, normalized.GoalRounds);
        Assert.Equal(50, normalized.ReadLines);
        Assert.Equal(2000, normalized.MaxReadWindow);
        Assert.Equal(120, normalized.ObservationChars);
        Assert.Equal(256, normalized.MaxTokens);
    }

    [Fact]
    public void Normalize_keeps_max_read_window_not_smaller_than_default_lines()
    {
        var normalized = AgentTuningLimits.Normalize(new AppSettings
        {
            AgentReadLines = 500,
            AgentMaxReadWindow = 300
        });

        Assert.Equal(500, normalized.ReadLines);
        Assert.Equal(500, normalized.MaxReadWindow);
    }

    [Fact]
    public async Task Agent_forwards_configured_max_tokens()
    {
        _chat.Handler = _ => new ChatTurn { Content = "完成" };
        var memory = new MemoryService(Path.Combine(_root, "memory.json"));
        var agent = new WorkspaceAgent(
            _chat, _workspace, memory, AgentMode.Chat, allowMemoryWrites: false, maxTokens: 256);

        var result = await agent.RunAsync(
            [ChatMessage.User("你好")], null, null, CancellationToken.None);

        Assert.Equal("完成", result.Text);
        Assert.Single(_chat.Calls);
        Assert.Equal(256, _chat.Calls[0].MaxTokens);
    }

    [Fact]
    public async Task Agent_honors_configured_goal_rounds()
    {
        _chat.Handler = call =>
        {
            if (call.MaxTokens == 512) return new ChatTurn { Content = "1. 列出文件" };
            if (call.MaxTokens == 700) return new ChatTurn { Content = """{"pass": true, "summary": "完成"}""" };

            var turn = new ChatTurn();
            turn.ToolCalls.Add(new ToolCall("call-1", "list_files", """{"path":"."}"""));
            return turn;
        };
        var memory = new MemoryService(Path.Combine(_root, "memory.json"));
        var agent = new WorkspaceAgent(
            _chat, _workspace, memory, AgentMode.Goal, allowMemoryWrites: false, goalRounds: 1);

        var result = await agent.RunAsync(
            [ChatMessage.User("列出文件")], null, null, CancellationToken.None);

        Assert.Single(_chat.Calls, call => call.MaxTokens == 2048);
        Assert.True(result.Activities.Count(activity => activity.Name == "list_files") <= 1);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试
        }
    }

    private sealed class FakeChatCompletion : IChatCompletion
    {
        public List<Call> Calls { get; } = [];
        public Func<Call, ChatTurn> Handler { get; set; } = _ => new ChatTurn();

        public Task<ChatTurn> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokens,
            CancellationToken cancellationToken)
        {
            var call = new Call(maxTokens, tools is { Count: > 0 }, [.. messages]);
            Calls.Add(call);
            return Task.FromResult(Handler(call));
        }
    }

    private sealed record Call(int MaxTokens, bool HasTools, IReadOnlyList<ChatMessage> Messages);
}
