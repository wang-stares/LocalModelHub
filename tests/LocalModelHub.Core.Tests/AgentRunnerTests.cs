using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using LocalModelHub.Core.Services.Abstractions;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>P2-1：IAgentRunner 把请求映射成一次完整的工作区工具循环。</summary>
public sealed class AgentRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-runner-{Guid.NewGuid():N}");
    private readonly WorkspaceService _workspace = new();
    private readonly FakeChatCompletion _chat = new();

    public AgentRunnerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "old");
        _workspace.SetRoot(_root);
    }

    [Fact]
    public async Task Runner_maps_request_into_workspace_agent()
    {
        var actCalls = 0;
        _chat.Handler = call =>
        {
            if (call.MaxTokens == 512) return new ChatTurn { Content = "1. 在工作区新建 b.txt，内容 new" };
            if (call.MaxTokens == 700) return new ChatTurn { Content = """{"pass":true,"summary":"已创建 b.txt"}""" };

            actCalls++;
            if (actCalls == 1)
            {
                var turn = new ChatTurn();
                turn.ToolCalls.Add(new ToolCall("call-1", "write_file", """{"path":"b.txt","content":"new"}"""));
                return turn;
            }

            return new ChatTurn { Content = "完成" };
        };

        var memory = new MemoryService(Path.Combine(_root, "memory.json"));
        var runner = new AgentRunner(
            _chat, _workspace, memory, tuning: () => new AgentTuning(2048, 400, 4, 10));
        var activities = new List<ToolActivity>();
        var request = new AgentRunRequest(
            AgentMode.Goal,
            [ChatMessage.User("把 new 写进 b.txt")],
            AllowMemoryWrites: false,
            OnActivity: activities.Add);

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "b.txt")));
        Assert.True(result.Verification?.Passed);
        Assert.Contains(activities, activity => activity.Name == "write_file");
        Assert.Contains(_chat.Calls, call => call.MaxTokens == 512);
        Assert.Contains(_chat.Calls, call => call.MaxTokens == 700);
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
