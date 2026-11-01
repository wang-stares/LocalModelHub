using System.Text.Json.Serialization;

namespace LocalModelHub.Core.Models;

/// <summary>记忆存储层级：全局记忆跨项目共享，项目记忆只属于当前工作文件夹。</summary>
public enum MemoryTier
{
    /// <summary>用户偏好、使用习惯等跨项目通用信息，所有场景可读写。</summary>
    Global = 0,

    /// <summary>当前工作文件夹的技术栈、目录约定、任务进度等专属信息，随工作区切换。</summary>
    Project = 1
}

/// <summary>一条长期记忆（用户偏好、项目事实等），全部保存在本机。</summary>
public sealed class MemoryEntry
{
    /// <summary>关于用户本人的稳定偏好 / 背景，任何项目都适用（全局层）。</summary>
    public const string KindUser = "user";

    /// <summary>关于某个工作文件夹的事实 / 约定，只在对应工作区召回（项目层）。</summary>
    public const string KindProject = "project";

    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Source { get; set; } = "manual"; // manual | model | chat

    /// <summary>记忆类型：user | project；旧数据没有该字段时按用户偏好处理。</summary>
    public string Kind { get; set; } = KindUser;

    /// <summary>项目记忆适用的工作文件夹路径；空表示全局（用户偏好）。</summary>
    public string Scope { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>过期时间；为空表示长期保留（全局记忆默认长期保留）。</summary>
    public DateTime? ExpiresAt { get; set; }

    public bool Pinned { get; set; }

    public bool IsFromModel => Source is "model" or "chat";
    public bool IsProject => Kind == KindProject;

    /// <summary>归属的存储层级，由 Kind 推导，不参与序列化。</summary>
    [JsonIgnore]
    public MemoryTier Tier => IsProject ? MemoryTier.Project : MemoryTier.Global;

    /// <summary>是否已过期：置顶条目不会被过期清理移除。</summary>
    public bool IsExpired(DateTime now) =>
        !Pinned && ExpiresAt is { } expires && expires <= now;
}

/// <summary>记忆写入结果：区分「新增」与「与已有条目合并」，供界面统计自动记忆条数。</summary>
public readonly record struct MemoryWriteResult(MemoryEntry Entry, bool IsNew);
