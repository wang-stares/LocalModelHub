using CommunityToolkit.Mvvm.ComponentModel;

namespace LocalModelHub.App.ViewModels;

/// <summary>「知识与记忆」页面：对话上下文的两类来源，用标签区分文档检索与长期记忆。</summary>
public sealed partial class KnowledgeMemoryViewModel : ObservableObject
{
    public KnowledgeMemoryViewModel(
        KnowledgeViewModel knowledge,
        MemoryViewModel memory,
        SkillsViewModel skills)
    {
        Knowledge = knowledge;
        Memory = memory;
        Skills = skills;
    }

    public KnowledgeViewModel Knowledge { get; }
    public MemoryViewModel Memory { get; }
    public SkillsViewModel Skills { get; }

    [ObservableProperty] private int _tabIndex;

    public string[] Tabs { get; } = ["知识库", "记忆", "技能"];

    public bool IsKnowledgeTab => TabIndex == 0;
    public bool IsMemoryTab => TabIndex == 1;
    public bool IsSkillsTab => TabIndex == 2;

    public string TabHint => TabIndex switch
    {
        0 => "导入项目文档或代码后，对话会自动检索相关片段并标注来源。",
        1 => "记录长期偏好与事实；开启自动记忆后会从对话中提取，也可让模型用 remember 工具写入。",
        _ => "目标模式完成文件任务后自动把执行步骤沉淀为技能，下次遇到类似请求会自动注入。"
    };

    partial void OnTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsKnowledgeTab));
        OnPropertyChanged(nameof(IsMemoryTab));
        OnPropertyChanged(nameof(IsSkillsTab));
        OnPropertyChanged(nameof(TabHint));
        Refresh();
    }

    public void Refresh()
    {
        switch (TabIndex)
        {
            case 1: Memory.Refresh(); break;
            case 2: Skills.Refresh(); break;
            default: Knowledge.Refresh(); break;
        }
    }
}
