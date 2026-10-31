namespace LocalModelHub.Core.Models;

/// <summary>可复用技能：一次完成过的任务被总结为「触发条件 + 步骤」，下次对话按需注入。</summary>
public sealed class SkillEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>什么时候该用这条技能。</summary>
    public string Trigger { get; set; } = "";
    /// <summary>按顺序排列的步骤要点。</summary>
    public string Steps { get; set; } = "";
    /// <summary>auto = 任务完成后自动沉淀；model = 模型总结；manual = 手动创建。</summary>
    public string Source { get; set; } = "auto";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool Enabled { get; set; } = true;
    public int Uses { get; set; }
}

/// <summary>规则总结出的技能草稿。</summary>
public sealed record SkillDraft(string Name, string Trigger, string Steps);
