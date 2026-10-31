using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.App.Services;
using LocalModelHub.Core.Models;

namespace LocalModelHub.App.ViewModels;

public sealed partial class SkillItemViewModel : ObservableObject
{
    private readonly SkillEntry _entry;
    private readonly Action<SkillItemViewModel> _toggle;
    private readonly Action<SkillItemViewModel> _delete;

    public SkillItemViewModel(
        SkillEntry entry,
        Action<SkillItemViewModel> toggle,
        Action<SkillItemViewModel> delete)
    {
        _entry = entry;
        _toggle = toggle;
        _delete = delete;
    }

    public string Id => _entry.Id;
    public string Name => _entry.Name;
    public string Trigger => _entry.Trigger;
    public string Steps => _entry.Steps;
    public bool IsEnabled => _entry.Enabled;
    public string EnabledText => _entry.Enabled ? "已启用" : "已停用";
    public string ToggleText => _entry.Enabled ? "停用" : "启用";
    public string SourceText => _entry.Source switch
    {
        "auto" => "自动沉淀",
        "model" => "模型总结",
        _ => "手动创建"
    };

    public string MetaText => $"{SourceText} · {_entry.CreatedAt:MM-dd HH:mm} · 已使用 {_entry.Uses} 次";

    [RelayCommand] private void Toggle() => _toggle(this);
    [RelayCommand] private void Delete() => _delete(this);
}

/// <summary>「技能」标签：任务完成后自动沉淀的可复用步骤，可启用/停用/删除。</summary>
public sealed partial class SkillsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public SkillsViewModel(AppServices services)
    {
        _services = services;
        AutoSkillEnabled = services.Settings.AutoSkillEnabled;
        SkillsEnabled = services.Settings.SkillsEnabled;
        Refresh();
    }

    public ObservableCollection<SkillItemViewModel> Items { get; } = [];

    [ObservableProperty] private bool _autoSkillEnabled;
    [ObservableProperty] private bool _skillsEnabled;
    [ObservableProperty] private string _status = "";

    public bool IsEmpty => Items.Count == 0;

    partial void OnAutoSkillEnabledChanged(bool value)
    {
        if (_services.Settings.AutoSkillEnabled == value) return;
        _services.Settings.AutoSkillEnabled = value;
        _services.SettingsService.Save();
    }

    partial void OnSkillsEnabledChanged(bool value)
    {
        if (_services.Settings.SkillsEnabled == value) return;
        _services.Settings.SkillsEnabled = value;
        _services.SettingsService.Save();
    }

    public void Refresh()
    {
        Items.Clear();
        foreach (var entry in _services.Skills.Entries)
        {
            Items.Add(new SkillItemViewModel(entry, Toggle, Delete));
        }

        var enabled = _services.Skills.Entries.Count(entry => entry.Enabled);
        Status = Items.Count == 0
            ? "还没有技能。在目标模式完成一次文件任务后会自动沉淀。"
            : $"共 {Items.Count} 条 · 启用 {enabled} 条 · 对话时按相关性注入最多 3 条";
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Toggle(SkillItemViewModel item)
    {
        _services.Skills.Toggle(item.Id);
        Refresh();
    }

    [RelayCommand]
    private void Delete(SkillItemViewModel item)
    {
        _services.Skills.Remove(item.Id);
        Refresh();
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Items.Count == 0) return;
        var confirmed = await DialogService.ConfirmAsync(
            "清空技能库",
            $"删除全部 {Items.Count} 条技能？此操作不可撤销。",
            "清空");
        if (!confirmed) return;

        _services.Skills.Clear();
        Refresh();
    }
}
