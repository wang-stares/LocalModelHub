namespace LocalModelHub.Core.Models;

/// <summary>一次对话会话，持久化在 sessions.json。</summary>
public sealed class ChatSession
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "新对话";
    public string ModelName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    /// <summary>最近一次闲时记忆整理的时间；有更新的会话（UpdatedAt 更晚）会被重新挑选。</summary>
    public DateTime? MemoryDigestedAt { get; set; }
    public List<StoredChatMessage> Messages { get; set; } = [];
}

public sealed class StoredChatMessage
{
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    /// <summary>assistant 消息的思考过程（reasoning_content），旧会话缺省为空。</summary>
    public string Reasoning { get; set; } = "";
    public bool SavedToMemory { get; set; }
}

/// <summary>普通对话的采样参数。</summary>
public sealed record ChatOptions(double Temperature, double TopP, int MaxTokens)
{
    public static ChatOptions Default { get; } = new(0.7, 0.95, 1024);
}
