using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class MultiAgentCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-multi-{Guid.NewGuid():N}");
    private readonly WorkspaceService _workspace = new();
    private readonly MemoryService _memory;
    private readonly FakeChatCompletion _chat = new();

    public MultiAgentCoordinatorTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "old\n");
        _workspace.SetRoot(_root);
        _memory = new MemoryService(Path.Combine(Path.GetTempPath(), $"lmh-memory-{Guid.NewGuid():N}.json"));
    }

    [Fact]
    public async Task Planner_coder_reviewer_produce_patch_and_review()
    {
        var act = 0;
        _chat.Handler = call =>
        {
            if (IsReviewer(call)) return Text("""{"pass": true, "summary": "补丁符合要求", "issues": [], "evidence": ["a.txt"]}""");
            if (call.MaxTokens == 512) return Text("1. 读取 a.txt\n2. 修改 a.txt\n3. 验收");
            if (!call.HasTools) return Text("""{"pass": true, "summary": "完成"}""");

            return act++ switch
            {
                0 => Tool("read_file", """{"path":"a.txt"}"""),
                1 => Tool("replace_in_file", """{"path":"a.txt","old_text":"old","new_text":"new"}"""),
                _ => Text("完成：a.txt 已更新")
            };
        };

        var coordinator = new MultiAgentCoordinator(
            _chat, _workspace, _memory, allowMemoryWrites: false);

        var result = await coordinator.RunAsync(
            [ChatMessage.User("把 a.txt 里的 old 改成 new")], null, null, CancellationToken.None);

        Assert.Equal("new\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.True(result.Plan.HasSteps);
        Assert.Equal(3, result.Plan.Steps.Count);
        Assert.NotNull(result.Review);
        Assert.True(result.Review!.Verified);
        Assert.True(result.Review.Passed);
        Assert.NotNull(result.Coder);
        Assert.Contains("--- a/a.txt", result.Patch);
        Assert.Contains("+new", result.Patch);
        Assert.Equal(["planner", "coder", "reviewer"], result.Roles.Select(role => role.Role).ToArray());
        Assert.Contains(result.Activities, activity => activity.Name == "planner");
        Assert.Contains(result.Activities, activity => activity.Name == "review");
        Assert.DoesNotContain("评审未通过", result.Text);
    }

    [Fact]
    public async Task Rejected_review_sends_specific_issues_back_for_repair()
    {
        var act = 0;
        var reviewCount = 0;
        _chat.Handler = call =>
        {
            if (IsReviewer(call))
            {
                reviewCount++;
                return Text(reviewCount == 1
                    ? """{"pass": false, "summary": "缺少 b.txt", "issues": ["请求要求新建 b.txt，但补丁里没有"], "evidence": []}"""
                    : """{"pass": true, "summary": "b.txt 已补充"}""");
            }

            if (call.MaxTokens == 512) return Text("1. 修改 a.txt\n2. 新建 b.txt\n3. 验收");
            if (!call.HasTools) return Text("""{"pass": true, "summary": "完成"}""");

            return act++ switch
            {
                0 => Tool("read_file", """{"path":"a.txt"}"""),
                1 => Tool("replace_in_file", """{"path":"a.txt","old_text":"old","new_text":"new"}"""),
                2 => Text("a.txt 已更新，但没有新建 b.txt"),
                3 => Tool("write_file", """{"path":"b.txt","content":"done"}"""),
                _ => Text("b.txt 已补充")
            };
        };

        var coordinator = new MultiAgentCoordinator(
            _chat, _workspace, _memory, allowMemoryWrites: false, maxRounds: 2);

        var result = await coordinator.RunAsync(
            [ChatMessage.User("把 a.txt 更新为 new，并新建 b.txt")], null, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_root, "b.txt")));
        Assert.Equal(2, reviewCount);
        Assert.True(result.Review!.Passed);
        Assert.Contains(result.Activities, activity => activity.Name == "repair");
        Assert.Contains(result.Roles, role => role.Role == "coder");
        Assert.Contains(result.Roles, role => role.Role == "reviewer");
        Assert.DoesNotContain("评审未通过", result.Text);
        Assert.Contains(_chat.Calls, call =>
            call.Messages.Any(message => message.Content.Contains(AgentProtocol.RepairMarker)));
    }

    [Fact]
    public async Task Unavailable_reviewer_is_reported_as_not_reviewed()
    {
        var act = 0;
        _chat.Handler = call =>
        {
            if (IsReviewer(call)) throw new InvalidOperationException("评审服务不可用");
            if (call.MaxTokens == 512) return Text("1. 读取 a.txt\n2. 修改 a.txt");
            if (!call.HasTools) return Text("""{"pass": true, "summary": "完成"}""");

            return act++ switch
            {
                0 => Tool("read_file", """{"path":"a.txt"}"""),
                1 => Tool("replace_in_file", """{"path":"a.txt","old_text":"old","new_text":"new"}"""),
                _ => Text("完成：a.txt 已更新")
            };
        };

        var coordinator = new MultiAgentCoordinator(
            _chat, _workspace, _memory, allowMemoryWrites: false);

        var result = await coordinator.RunAsync(
            [ChatMessage.User("把 a.txt 改成 new")], null, null, CancellationToken.None);

        Assert.NotNull(result.Review);
        Assert.False(result.Review!.Verified);
        Assert.Equal("跳过", result.Review.OutcomeLabel);
        Assert.Contains("评审未完成", result.Text);
        Assert.Contains(result.Activities, activity => activity.Name == "review");
    }

    [Fact]
    public async Task Rejected_review_at_round_limit_is_stated_in_text()
    {
        var act = 0;
        _chat.Handler = call =>
        {
            if (IsReviewer(call))
                return Text("""{"pass": false, "summary": "没有覆盖所有要求", "issues": ["缺少说明"]}""");
            if (call.MaxTokens == 512) return Text("1. 修改 a.txt");
            if (!call.HasTools) return Text("""{"pass": true, "summary": "完成"}""");

            return act++ switch
            {
                0 => Tool("read_file", """{"path":"a.txt"}"""),
                1 => Tool("replace_in_file", """{"path":"a.txt","old_text":"old","new_text":"new"}"""),
                _ => Text("完成：a.txt 已更新")
            };
        };

        var coordinator = new MultiAgentCoordinator(
            _chat, _workspace, _memory, allowMemoryWrites: false, maxRounds: 1);

        var result = await coordinator.RunAsync(
            [ChatMessage.User("把 a.txt 改成 new")], null, null, CancellationToken.None);

        Assert.False(result.Review!.Passed);
        Assert.Contains("评审未通过", result.Text);
        Assert.DoesNotContain(result.Activities, activity => activity.Name == "repair");
    }

    private static bool IsReviewer(Call call) =>
        call.Messages.Any(message => message.Content.Contains("独立评审角色"));

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
