using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>闲时自动记忆：挑选未整理的会话、提炼全局偏好、敏感信息过滤与规则兜底。</summary>
public sealed class IdleMemoryDigestTests
{
    [Fact]
    public void Selects_unprocessed_sessions_newest_first()
    {
        var now = DateTime.Now;
        var fresh = Session("fresh", now.AddMinutes(-2), null, ("user", "我更喜欢中文回答"));
        var stale = Session("stale", now.AddHours(-3), now.AddHours(-1), ("user", "旧会话"));
        var updated = Session("updated", now.AddHours(-2), now.AddHours(-2).AddMinutes(-10), ("user", "有新消息"));
        var empty = Session("empty", now, null);

        var pending = IdleMemoryDigest.SelectPending([stale, updated, fresh, empty], limit: 2);

        Assert.Equal(["fresh", "updated"], pending.Select(session => session.Id));
    }

    [Fact]
    public async Task Model_output_is_written_as_global_preferences_only()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            var digest = new IdleMemoryDigest(
                memory,
                (_, _, _) => Task.FromResult(
                    """
                    {"facts":[
                      {"text":"用户习惯中文回答，代码注释用英文","kind":"user"},
                      {"text":"用户的 API key 是 sk-abc123456","kind":"user"},
                      {"text":"项目使用 dotnet build 构建","kind":"project"}]}
                    """));

            var result = await digest.DigestAsync(
                [Session("s1", DateTime.Now, null, ("user", "以后用中文回答"), ("assistant", "好的"))],
                CancellationToken.None);

            Assert.Equal(1, result.Added);
            var entry = Assert.Single(memory.Entries);
            Assert.Equal("用户习惯中文回答，代码注释用英文", entry.Text);
            Assert.False(entry.IsProject);
            Assert.Equal("model", result.Source);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task Falls_back_to_rules_when_no_model_is_available()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            var digest = new IdleMemoryDigest(memory);

            var result = await digest.DigestAsync(
                [Session("s1", DateTime.Now, null, ("user", "我更喜欢中文回答，代码注释用英文。"))],
                CancellationToken.None);

            Assert.Equal("rules", result.Source);
            Assert.Equal(1, result.Added);
            Assert.Contains(memory.Entries, entry => entry.Text.Contains("中文回答"));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task Transcript_is_capped_and_empty_sessions_are_ignored()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            var digest = new IdleMemoryDigest(memory, (_, _, _) => Task.FromResult("""{"facts":[]}"""));

            var longSession = Session(
                "long",
                DateTime.Now,
                null,
                ("user", new string('问', 500)),
                ("assistant", new string('答', 500)));
            var result = await digest.DigestAsync([longSession], CancellationToken.None);

            Assert.False(result.HasChanges);
            Assert.Equal(1, result.Sessions);
            Assert.Empty(memory.Entries);
        }
        finally
        {
            Delete(path);
        }
    }

    private static ChatSession Session(
        string id,
        DateTime updatedAt,
        DateTime? digestedAt,
        params (string Role, string Text)[] messages)
    {
        var session = new ChatSession
        {
            Id = id,
            Title = id,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            MemoryDigestedAt = digestedAt
        };

        foreach (var (role, text) in messages)
        {
            session.Messages.Add(new StoredChatMessage { Role = role, Text = text });
        }

        return session;
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "lmh-idle-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
