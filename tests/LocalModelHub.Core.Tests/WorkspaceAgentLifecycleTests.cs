using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class WorkspaceAgentLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-agent-{Guid.NewGuid():N}");
    private readonly WorkspaceService _workspace = new();
    private readonly MemoryService _memory;
    private readonly FakeChatCompletion _chat = new();

    public WorkspaceAgentLifecycleTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "old");
        _workspace.SetRoot(_root);
        _memory = new MemoryService(Path.Combine(_root, "memory.json"));
    }

    [Fact]
    public async Task Goal_mode_plans_executes_and_verifies()
    {
        var act = 0;
        _chat.Handler = call =>
        {
            if (call.MaxTokens == 512) return Text("1. 读取 a.txt\n2. 修改 a.txt\n3. 复核改动");
            if (call.MaxTokens == 700) return Text("""{"pass": true, "summary": "完成"}""");

            return act++ switch
            {
                0 => Tool("read_file", """{"path":"a.txt"}"""),
                1 => Tool("replace_in_file", """{"path":"a.txt","old_text":"old","new_text":"new"}"""),
                _ => Text("完成：a.txt 已更新")
            };
        };

        var result = await Agent(AgentMode.Goal).RunAsync(
            [ChatMessage.User("把 a.txt 里的 old 改成 new")], null, null, CancellationToken.None);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.NotNull(result.Verification);
        Assert.True(result.Verification!.Passed);
        Assert.Equal(3, result.Trajectory!.Plan.Steps.Count);
        Assert.Equal(100, result.Trajectory.Score);
        Assert.Contains(result.Activities, activity => activity.Name == "plan");
        Assert.Contains(result.Activities, activity => activity.Name == "verify");
        Assert.Contains(result.Activities, activity => activity.Name == "replace_in_file");
        Assert.Contains(_chat.Calls[1].Messages, message => message.Content.Contains(AgentProtocol.PlanMarker));
    }

    [Fact]
    public async Task Goal_mode_can_apply_patch_after_reading()
    {
        var act = 0;
        _chat.Handler = call =>
        {
            if (call.MaxTokens == 512) return Text("1. 读取 a.txt\n2. 用补丁修改\n3. 验收");
            if (call.MaxTokens == 700) return Text("""{"pass": true, "summary": "完成"}""");

            return act++ switch
            {
                0 => Tool("read_file", """{"path":"a.txt"}"""),
                1 => Tool("apply_patch",
                    """{"patch":"--- a/a.txt\n+++ b/a.txt\n@@ -1,1 +1,1 @@\n-old\n+new\n"}"""),
                _ => Text("完成：a.txt 已用补丁更新")
            };
        };

        var result = await Agent(AgentMode.Goal).RunAsync(
            [ChatMessage.User("把 a.txt 里的 old 改成 new")], null, null, CancellationToken.None);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Contains(result.Activities, activity => activity.Name == "apply_patch");
        Assert.True(result.Verification!.Passed);
        Assert.Equal(100, result.Trajectory!.Score);
    }

    [Fact]
    public async Task Blind_overwrite_is_blocked_until_file_is_read()
    {
        var act = 0;
        _chat.Handler = call =>
        {
            if (call.MaxTokens == 512) return Text("1. 修改 a.txt\n2. 验收");
            if (call.MaxTokens == 700) return Text("""{"pass": true, "summary": "完成"}""");

            return act++ switch
            {
                0 => Tool("write_file", """{"path":"a.txt","content":"blind"}"""),
                1 => Tool("read_file", """{"path":"a.txt"}"""),
                2 => Tool("write_file", """{"path":"a.txt","content":"new"}"""),
                _ => Text("完成：a.txt 已更新为 new")
            };
        };

        var result = await Agent(AgentMode.Goal).RunAsync(
            [ChatMessage.User("把 a.txt 改成 new")], null, null, CancellationToken.None);

        Assert.Contains(result.Activities, activity => activity.Name == "write_file" && activity.Summary.Contains("已写入"));
        Assert.Contains(result.Activities, activity => activity.Summary.Contains("未读先改"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal(92, result.Trajectory!.Score);
    }

    [Fact]
    public async Task Duplicate_tool_call_is_blocked_and_penalized()
    {
        var act = 0;
        _chat.Handler = call =>
        {
            if (call.MaxTokens == 512) return Text("1. 读取 a.txt\n2. 复核");
            if (call.MaxTokens == 700) return Text("""{"pass": true, "summary": "完成"}""");

            return act++ switch
            {
                0 or 1 => Tool("read_file", """{"path":"a.txt"}"""),
                _ => Text("完成：a.txt 内容为 old")
            };
        };

        var result = await Agent(AgentMode.Goal).RunAsync(
            [ChatMessage.User("读取 a.txt")], null, null, CancellationToken.None);

        Assert.Contains(result.Activities, activity => activity.Summary.Contains("重复调用已拦截"));
        Assert.True(result.Trajectory!.FailedCalls >= 1);
        Assert.True(result.Trajectory.Score < 100);
    }

    [Fact]
    public async Task Failed_verification_triggers_repair_and_recheck()
    {
        var act = 0;
        var verify = 0;
        _chat.Handler = call =>
        {
            if (call.MaxTokens == 512) return Text("1. 修改 a.txt\n2. 补写 b.txt\n3. 验收");
            if (call.MaxTokens == 700)
            {
                verify++;
                return Text(verify == 1
                    ? """{"pass": false, "summary": "未通过", "issues": ["b.txt 没有写入"]}"""
                    : """{"pass": true, "summary": "完成"}""");
            }

            return act++ switch
            {
                0 => Tool("read_file", """{"path":"a.txt"}"""),
                1 => Tool("replace_in_file", """{"path":"a.txt","old_text":"old","new_text":"new"}"""),
                2 => Tool("write_file", """{"path":"b.txt","content":"done"}"""),
                _ => Text("已补上 b.txt")
            };
        };

        var result = await Agent(AgentMode.Goal).RunAsync(
            [ChatMessage.User("把 a.txt 更新为 new，并新建 b.txt")], null, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_root, "b.txt")));
        Assert.True(result.Verification!.Passed);
        Assert.Contains(result.Activities, activity => activity.Name == "repair");
        Assert.Equal(2, verify);
    }

    [Fact]
    public async Task Verification_failure_is_skipped_not_blocking()
    {
        var act = 0;
        _chat.Handler = call =>
        {
            if (call.MaxTokens == 512) return Text("1. 读取 a.txt");
            if (call.MaxTokens == 700) throw new InvalidOperationException("验收服务不可用");

            return act++ switch
            {
                0 => Tool("read_file", """{"path":"a.txt"}"""),
                _ => Text("完成：a.txt 内容为 old")
            };
        };

        var result = await Agent(AgentMode.Goal).RunAsync(
            [ChatMessage.User("读取 a.txt")], null, null, CancellationToken.None);

        Assert.NotNull(result.Verification);
        Assert.False(result.Verification!.Verified);
        Assert.Equal("跳过", result.Verification.OutcomeLabel);
        Assert.NotNull(result.Trajectory);
    }

    [Fact]
    public async Task Goal_mode_uses_search_and_command_validation()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "alpha.cs"), "class Alpha { }");

        var act = 0;
        _chat.Handler = call =>
        {
            if (call.MaxTokens == 512) return Text("1. 搜索 Alpha\n2. 运行测试\n3. 验收");
            if (call.MaxTokens == 700) return Text("""{"pass": true, "summary": "测试通过"}""");

            return act++ switch
            {
                0 => Tool("search_code", """{"query":"alpha","include":"*.cs"}"""),
                1 => Tool("run_command", """{"command":"dotnet test"}"""),
                _ => Text("已完成：搜索到 Alpha，测试通过")
            };
        };
        var runner = new FakeCommandRunner();

        var result = await Agent(AgentMode.Goal, allowCommands: true, commands: runner).RunAsync(
            [ChatMessage.User("搜索 Alpha 类并运行测试")], null, null, CancellationToken.None);

        Assert.Single(runner.Commands);
        Assert.Equal("dotnet test", runner.Commands[0]);
        Assert.Contains(result.Activities, activity => activity.Name == "localize");
        Assert.Contains(result.Activities, activity => activity.Name == "search_code");
        Assert.Contains(result.Activities, activity => activity.Name == "run_command");
        Assert.Contains(result.Activities, activity => activity.Summary.Contains("退出码 0"));
        Assert.True(result.Verification!.Passed);
    }

    [Fact]
    public async Task Plan_mode_checks_required_sections_without_verifier_call()
    {
        _chat.Handler = _ => Text("目标：修改入口\n1. 读取 a.txt");

        var result = await Agent(AgentMode.Plan).RunAsync(
            [ChatMessage.User("给出修改计划")], null, null, CancellationToken.None);

        Assert.Null(result.Verification);
        Assert.Single(_chat.Calls);
        var check = Assert.Single(result.Activities);
        Assert.Equal("verify", check.Name);
        Assert.Contains("风险", check.Summary);
        Assert.Contains("验收", check.Summary);
    }

    private WorkspaceAgent Agent(
        AgentMode mode,
        bool allowCommands = false,
        ICommandRunner? commands = null) =>
        new(_chat, _workspace, _memory, mode,
            allowMemoryWrites: false, allowCommands: allowCommands, commands: commands);

    private static ChatTurn Text(string content) => new() { Content = content };

    private static ChatTurn Tool(string name, string arguments)
    {
        var turn = new ChatTurn();
        turn.ToolCalls.Add(new ToolCall("call-1", name, arguments));
        return turn;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
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

    private sealed class FakeCommandRunner : ICommandRunner
    {
        public List<string> Commands { get; } = [];

        public Task<CommandResult> RunAsync(
            string command,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new CommandResult(true, 0, "Tests passed", command));
        }
    }

    private sealed record Call(int MaxTokens, bool HasTools, IReadOnlyList<ChatMessage> Messages);
}
