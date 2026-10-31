using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.App.Services;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;

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
    public string PinToolTip => IsPinned ? "取消固定" : "固定（每轮对话都会注入）";
    public string SourceText => Entry.Source switch
    {
        "model" => "模型写入",
        "chat" => "对话保存",
        _ => "手动添加"
    };
    public string MetaText => Entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");

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
    private readonly MemoryService _memory;
    private readonly bool _loaded;

    public MemoryViewModel(MemoryService? memory = null)
    {
        _memory = memory ?? _services.Memory;
        MemoryEnabled = _services.Settings.MemoryEnabled;
        AllowModelWrites = _services.Settings.MemoryAllowModelWrites;
        _loaded = true;
        Refresh();
    }

    public ObservableCollection<MemoryItemViewModel> Items { get; } = [];

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyHint = "还没有长期记忆";
    [ObservableProperty] private bool? _memoryEnabled;
    [ObservableProperty] private bool? _allowModelWrites;

    public bool CanAdd => !string.IsNullOrWhiteSpace(Input);
    public string InputCounter => $"{Input.Length}/{MemoryService.MaxTextLength}";

    partial void OnInputChanged(string value)
    {
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(InputCounter));
    }

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

    public void Refresh()
    {
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
        CountText = $"共 {Items.Count} 条 · 固定 {Items.Count(item => item.IsPinned)} 条";
    }

    [RelayCommand]
    private void Add()
    {
        var text = Input.Trim();
        if (text.Length == 0) return;

        try
        {
            var existed = _memory.Contains(text);
            _memory.Add(text, "manual");
            Input = "";
            Status = existed ? "已存在相同记忆，已更新时间并置顶" : "已添加一条长期记忆";
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
            $"确定删除全部 {Items.Count} 条长期记忆？此操作不可恢复。",
            "清空");
        if (!confirmed) return;

        _memory.Clear();
        Status = "已清空全部记忆";
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
