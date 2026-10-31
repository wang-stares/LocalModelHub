namespace LocalModelHub.Core.Models;

/// <summary>一条长期记忆（用户偏好、项目事实等），全部保存在本机。</summary>
public sealed class MemoryEntry
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Source { get; set; } = "manual"; // manual | model | chat
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool Pinned { get; set; }

    public bool IsFromModel => Source is "model" or "chat";
}
