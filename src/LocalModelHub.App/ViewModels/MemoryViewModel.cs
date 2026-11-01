using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.App.Services;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using LocalModelHub.Core.Services.Abstractions;

namespace LocalModelHub.App.ViewModels;

public sealed partial class MemoryItemViewModel : ObservableObject
{
    private readonly Action<MemoryItemViewModel> _remove;
    private readonly Action<MemoryItemViewModel> _togglePin;
    private readonly Action<MemoryItemViewModel> _saveEdit;

    public MemoryItemViewModel(
        MemoryEntry entry,
        Action<MemoryItemViewModel> remove,
        Action<MemoryItemViewModel> togglePin,
        Action<MemoryItemViewModel> saveEdit)
    {
        Entry = entry;
        _remove = remove;
        _togglePin = togglePin;
        _saveEdit = saveEdit;
    }

    public MemoryEntry Entry { get; }
    public string Text => Entry.Text;
    public bool IsModel => Entry.IsFromModel;
    public bool IsPinned => Entry.Pinned;
    public bool IsProject => Entry.IsProject;
    public string KindText => IsProject ? "项目事实" : "用户偏好";
    public string PinToolTip => IsPinned ? "取消固定" : "固定（每轮对话都会注入）";
    public string SourceText => Entry.Source switch
    {
        "model" => "模型自动记录",
        "chat" => "对话保存",
        _ => "手动添加"
    };
    public string MetaText => IsProject && !string.IsNullOrWhiteSpace(Entry.Scope)
        ? $"{Entry.CreatedAt:yyyy-MM-dd HH:mm} · {Entry.Scope}"
        : Entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = "";

    [RelayCommand]
    private void TogglePin() => _togglePin(this);

    [RelayCommand]
    private void BeginEdit()
    {
        EditText = Text;
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveEdit() => _saveEdit(this);

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void Remove() => _remove(this);
}

public sealed partial class MemoryViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;
    private readonly IMemoryStore _memory;
    private readonly bool _loaded;

    public MemoryViewModel(IMemoryStore? memory = null)
    {
        _memory = memory ?? _services.Memory;
        MemoryEnabled = _services.Settings.MemoryEnabled;
        AllowModelWrites = _services.Settings.MemoryAllowModelWrites;
        AutoDistillEnabled = _services.Settings.MemoryAutoDistill;
        _loaded = true;
        Refresh();
    }

    public ObservableCollection<MemoryItemViewModel> Items { get; } = [];

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _scopeText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasScope;
    [ObservableProperty] private string _emptyHint = "还没有长期记忆";
    [ObservableProperty] private bool? _memoryEnabled;
    [ObservableProperty] private bool? _allowModelWrites;
    [ObservableProperty] private bool? _autoDistillEnabled;

    private MemoryManager? Manager => _memory as MemoryManager;

    public bool CanAdd => !string.IsNullOrWhiteSpace(Input);
    public bool CanAddProject => CanAdd && HasScope;
    public string InputCounter => $"{Input.Length}/{MemoryService.MaxTextLength}";

    partial void OnInputChanged(string value)
    {
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CanAddProject));
        OnPropertyChanged(nameof(InputCounter));
    }

    partial void OnHasScopeChanged(bool value) => OnPropertyChanged(nameof(CanAddProject));

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnMemoryEnabledChanged(bool? value)
    {
        if (!_loaded) return;
        _services.Settings.MemoryEnabled = value == true;
        _services.SettingsService.Save();
    }

    partial void OnAllowModelWritesChanged(bool? value)
    {
        if (!_loaded) return;
        _services.Settings.MemoryAllowModelWrites = value == true;
        _services.SettingsService.Save();
    }

    partial void OnAutoDistillEnabledChanged(bool? value)
    {
        if (!_loaded) return;
        _services.Settings.MemoryAutoDistill = value == true;
        _services.SettingsService.Save();
    }

    public void Refresh()
    {
        var manager = Manager;
        HasScope = manager is { Workspace.Length: > 0 };
        ScopeText = HasScope
            ? $"当前项目记忆：{manager!.Workspace}（切换工作文件夹会自动切换）"
            : "未设置工作文件夹：只读取全局记忆，项目记忆不可读写";

        var query = SearchText.Trim();
        var entries = _memory.Entries
            .Where(entry => query.Length == 0 ||
                            entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Pinned)
            .ThenByDescending(entry => entry.CreatedAt)
            .ToArray();

        Items.Clear();
        foreach (var entry in entries)
        {
            Items.Add(new MemoryItemViewModel(entry, RemoveItem, PinItem, SaveEdit));
        }

        IsEmpty = Items.Count == 0;
        EmptyHint = query.Length > 0 ? "没有匹配的记忆" : "还没有长期记忆";
        var project = Items.Count(item => item.IsProject);
        var pinned = Items.Count(item => item.IsPinned);
        CountText = $"全局 {Items.Count - project} 条 · 项目 {project} 条 · 固定 {pinned} 条";
    }

    [RelayCommand]
    private void Add()
    {
        var text = Input.Trim();
        if (text.Length == 0) return;

        try
        {
            var result = _memory.Upsert(text, "manual", MemoryEntry.KindUser, "");
            Input = "";
            Status = result.IsNew ? "已添加到全局记忆" : "已合并更新全局记忆";
            Refresh();
        }
        catch (Exception ex)
        {
            Status = $"添加失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void AddToProject()
    {
        var text = Input.Trim();
        if (text.Length == 0 || Manager is not { Workspace.Length: > 0 } manager) return;

        try
        {
            var result = _memory.Upsert(text, "manual", MemoryEntry.KindProject, manager.Workspace);
            Input = "";
            Status = result.IsNew ? "已添加到当前项目记忆" : "已合并更新当前项目记忆";
            Refresh();
        }
        catch (Exception ex)
        {
            Status = $"添加失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Items.Count == 0) return;

        var confirmed = await DialogService.ConfirmAsync(
            "清空记忆",
            $"确定删除全部 {Items.Count} 条长期记忆（全局与当前项目）？此操作不可恢复。",
            "清空");
        if (!confirmed) return;

        _memory.Clear();
        Status = "已清空全局与当前项目记忆";
        Refresh();
    }

    private void RemoveItem(MemoryItemViewModel item)
    {
        _memory.Remove(item.Entry.Id);
        Status = "已删除一条记忆";
        Refresh();
    }

    private void PinItem(MemoryItemViewModel item)
    {
        Status = _memory.TogglePin(item.Entry.Id) ? "已固定这条记忆" : "已取消固定";
        Refresh();
    }

    private void SaveEdit(MemoryItemViewModel item)
    {
        if (!_memory.UpdateText(item.Entry.Id, item.EditText))
        {
            Status = "记忆内容不能为空";
            return;
        }

        Status = "已更新记忆内容";
        Refresh();
    }
}
