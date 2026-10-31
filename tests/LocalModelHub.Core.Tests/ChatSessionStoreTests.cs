using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class ChatSessionStoreTests
{
    [Fact]
    public void Upserts_persists_and_removes_sessions()
    {
        var path = TempPath();
        try
        {
            var store = new ChatSessionStore(path);
            var session = new ChatSession
            {
                Id = "s1",
                Title = "C# 环形缓冲",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            session.Messages.Add(new StoredChatMessage { Role = "user", Text = "写一个无锁队列" });
            session.Messages.Add(new StoredChatMessage { Role = "assistant", Text = "使用 Interlocked" });

            store.Upsert(session);

            var reloaded = new ChatSessionStore(path);
            var loaded = Assert.Single(reloaded.Load());
            Assert.Equal("C# 环形缓冲", loaded.Title);
            Assert.Equal(2, loaded.Messages.Count);

            session.Title = "重命名后";
            reloaded.Upsert(session);
            Assert.Equal("重命名后", Assert.Single(reloaded.Load()).Title);

            Assert.True(reloaded.Remove("s1"));
            Assert.Empty(new ChatSessionStore(path).Load());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Truncates_long_sessions_and_ignores_corrupted_file()
    {
        var path = TempPath();
        try
        {
            var store = new ChatSessionStore(path);
            var session = new ChatSession { Id = "s2", Title = "长会话" };
            for (var index = 0; index < ChatSessionStore.MaxMessagesPerSession + 20; index++)
            {
                session.Messages.Add(new StoredChatMessage { Role = "user", Text = $"消息 {index}" });
            }

            store.Upsert(session);
            var loaded = Assert.Single(new ChatSessionStore(path).Load());
            Assert.Equal(ChatSessionStore.MaxMessagesPerSession, loaded.Messages.Count);
            Assert.Equal("消息 219", loaded.Messages[^1].Text);

            File.WriteAllText(path, "{ broken");
            Assert.Empty(new ChatSessionStore(path).Load());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "lmh-sessions-" + Guid.NewGuid().ToString("N") + ".json");
}
