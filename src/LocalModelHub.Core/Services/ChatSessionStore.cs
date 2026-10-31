using System.Text.Json;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>对话会话存储：最多保留 100 个会话，每个会话最多 200 条消息。</summary>
public sealed class ChatSessionStore
{
    public const int MaxSessions = 100;
    public const int MaxMessagesPerSession = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string? _filePath;
    private readonly List<ChatSession> _sessions = [];

    public ChatSessionStore(string? filePath = null) => _filePath = filePath;

    public string FilePath => _filePath ?? AppPaths.SessionsFile;

    public IReadOnlyList<ChatSession> Sessions => _sessions;

    public IReadOnlyList<ChatSession> Load()
    {
        _sessions.Clear();
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<List<ChatSession>>(
                    File.ReadAllText(FilePath), JsonOptions) ?? [];

                foreach (var session in loaded
                             .Where(session => !string.IsNullOrWhiteSpace(session.Id))
                             .OrderByDescending(session => session.UpdatedAt)
                             .Take(MaxSessions))
                {
                    if (session.Messages.Count > MaxMessagesPerSession)
                    {
                        session.Messages = session.Messages.TakeLast(MaxMessagesPerSession).ToList();
                    }

                    _sessions.Add(session);
                }
            }
        }
        catch
        {
            // 会话文件损坏时按空列表处理
        }

        return _sessions;
    }

    public void Upsert(ChatSession session)
    {
        var index = _sessions.FindIndex(item =>
            item.Id.Equals(session.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _sessions.RemoveAt(index);

        if (session.Messages.Count > MaxMessagesPerSession)
        {
            session.Messages = session.Messages.TakeLast(MaxMessagesPerSession).ToList();
        }

        _sessions.Insert(0, session);
        while (_sessions.Count > MaxSessions) _sessions.RemoveAt(_sessions.Count - 1);
        Save();
    }

    public bool Remove(string sessionId)
    {
        var removed = _sessions.RemoveAll(session =>
            session.Id.Equals(sessionId, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Save();
        return removed;
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_sessions, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}
