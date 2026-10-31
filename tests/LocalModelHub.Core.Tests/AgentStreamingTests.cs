using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>文本工具协议、流式回调与约束解码（验收 JSON / 缩小 schema 重问）。</summary>
public sealed class AgentStreamingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-stream-{Guid.NewGuid():N}");
    private readonly WorkspaceService _workspace = new();
    private readonly MemoryService _memory;
    private readonly ScriptedChat _chat = new();

    public AgentStreamingTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "old");
        _workspace.SetRoot(_root);
        _memory = new MemoryService(Path.Combine(_root, "memory.json"));
    }

    [Fact]
    public async Task Text_protocol_parses_action_lines_and_strips_final_answer()
    {
        var act = 0;
        _chat.Handler = _ => act++ switch
        {
            0 => Text("Thought: 先读取真实内容\nAction: read_file\nAction Input: {\"path\":\"a.txt\"}"),
            _ => Text("Final Answer: a.txt 的内容是 old")
        };

        var agent = Agent(AgentMode.Plan, textToolProtocol: true);
        var result = await agent.RunAsync(
            [ChatMessage.User("读取 a.txt")], null, null, CancellationToken.None);

        // 文本协议下不再发送 tools 字段，否则模板会返回空 tool_calls
        Assert.False(_chat.Calls[0].HasTools);
        Assert.Contains(_chat.Calls[0].Messages, message => message.Content.Contains("Action Input"));
        Assert.Contains(_chat.Calls, call => call.Messages.Any(message =>
            message.Role == "user" && message.Content.StartsWith("Observation:", StringComparison.Ordinal)));

        // 工具轮次的正文不进入最终回答；Final Answer 前缀被去掉
        Assert.DoesNotContain("Action:", result.Text);
        Assert.DoesNotContain("Final Answer", result.Text);
        Assert.Contains("old", result.Text);
        Assert.Contains(result.Activities, activity => activity.Name == "protocol");
        Assert.Contains(result.Activities, activity => activity.Name == "read_file");
        Assert.DoesNotContain(result.Trajectory!.Steps, step => step.Stage == AgentStage.Verify);
    }

    [Fact]
    public async Task Text_protocol_ignores_unknown_tool_names()
    {
        _chat.Handler = _ => Text("Action: delete_file\nAction Input: {\"path\":\"a.txt\"}");

        var agent = Agent(AgentMode.Plan, textToolProtocol: true);
        var result = await agent.RunAsync(
            [ChatMessage.User("删除 a.txt")], null, null, CancellationToken.None);

        // 未声明的工具不会被当作调用执行，文件保持不变
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.DoesNotContain(result.Activities, activity => activity.Name == "delete_file");
        Assert.Equal("Action: delete_file\nAction Input: {\"path\":\"a.txt\"}", result.Text);
    }

    [Fact]
    public async Task Agent_forwards_stream_deltas_to_callback()
    {
        _chat.Handler = call =>
        {
            call.Options?.OnDelta?.Invoke("流式");
            call.Options?.OnDelta?.Invoke("正文");
            return Text("流式正文");
        };

        var deltas = new List<string>();
        var agent = Agent(AgentMode.Chat);
        var result = await agent.RunAsync(
            [ChatMessage.User("打个招呼")], null, null, CancellationToken.None, onDelta: deltas.Add);

        Assert.Equal(["流式", "正文"], deltas);
        Assert.Contains("流式正文", result.Text);
    }

    [Fact]
    public async Task Verify_uses_json_schema_and_accepts_structured_result()
    {
        var act = 0;
        _chat.Handler = call =>
        {
            if (call.MaxTokens == 512) return Text("1. 读取 a.txt\n2. 修改 a.txt\n3. 验收");
            if (call.MaxTokens == 700) return Text("""{"pass": true, "summary": "已写入 a.txt"}""");

            return act++ switch
            {
                0 => Tool("read_file", """{"path":"a.txt"}"""),
                1 => Tool("replace_in_file", """{"path":"a.txt","old_text":"old","new_text":"new"}"""),
                _ => Text("完成：a.txt 已更新")
            };
        };

        var result = await Agent(AgentMode.Goal).RunAsync(
            [ChatMessage.User("把 a.txt 里的 old 改成 new")], null, null, CancellationToken.None);

        var verify = _chat.Calls.Single(call => call.MaxTokens == 700);
        Assert.True(verify.Options!.JsonMode);
        Assert.Equal(AgentProtocol.VerificationJsonSchema, verify.Options.JsonSchema);
        Assert.All(
            _chat.Calls.Where(call => call.MaxTokens == 2048),
            call => Assert.False(call.Options?.JsonMode ?? false));

        Assert.Equal("json", result.Verification!.Source);
        Assert.True(result.Verification.Passed);
    }

    [Fact]
    public async Task Non_json_verification_is_reasked_once_with_compact_schema()
    {
        var act = 0;
        var verifyCount = 0;
        _chat.Handler = call =>
        {
            if (call.MaxTokens == 512) return Text("1. 读取 a.txt\n2. 修改 a.txt\n3. 验收");
            if (call.MaxTokens == 700)
            {
                verifyCount++;
                return Text(verifyCount == 1
                    ? "未通过：没有看到 a.txt 的写入证据"
                    : """{"pass": true, "summary": "重问后确认完成"}""");
            }

            return act++ switch
            {
                0 => Tool("read_file", """{"path":"a.txt"}"""),
                1 => Tool("replace_in_file", """{"path":"a.txt","old_text":"old","new_text":"new"}"""),
                _ => Text("完成：a.txt 已更新")
            };
        };

        var result = await Agent(AgentMode.Goal).RunAsync(
            [ChatMessage.User("把 a.txt 里的 old 改成 new")], null, null, CancellationToken.None);

        Assert.Equal(2, verifyCount);
        var retry = _chat.Calls.Where(call => call.MaxTokens == 700).ElementAt(1);
        Assert.Equal(AgentProtocol.CompactVerificationJsonSchema, retry.Options!.JsonSchema);
        Assert.Contains(retry.Messages, message =>
            message.Content.Contains("上一次验收输出不是合法 JSON"));

        Assert.Equal("json", result.Verification!.Source);
        Assert.True(result.Verification.Passed);
        Assert.DoesNotContain(result.Activities, activity => activity.Name == "repair");
    }

    private WorkspaceAgent Agent(AgentMode mode, bool textToolProtocol = false) =>
        new(_chat, _workspace, _memory, mode, allowMemoryWrites: false,
            textToolProtocol: textToolProtocol);

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
            // 清理失败不影响测试
        }
    }

    private sealed class ScriptedChat : IChatCompletion
    {
        public List<Call> Calls { get; } = [];
        public Func<Call, ChatTurn> Handler { get; set; } = _ => new ChatTurn();

        public Task<ChatTurn> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokens,
            CancellationToken cancellationToken) =>
            Record(messages, tools, maxTokens, null);

        public Task<ChatTurn> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokens,
            ChatRequestOptions requestOptions,
            CancellationToken cancellationToken) =>
            Record(messages, tools, maxTokens, requestOptions);

        private Task<ChatTurn> Record(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokens,
            ChatRequestOptions? options)
        {
            var call = new Call(maxTokens, tools is { Count: > 0 }, [.. messages], options);
            Calls.Add(call);
            return Task.FromResult(Handler(call));
        }
    }

    private sealed record Call(
        int MaxTokens,
        bool HasTools,
        IReadOnlyList<ChatMessage> Messages,
        ChatRequestOptions? Options);
}
