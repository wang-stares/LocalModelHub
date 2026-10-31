using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.App.Services;
using LocalModelHub.App.Views;
using LocalModelHub.Core.Models;

namespace LocalModelHub.App.ViewModels;

public sealed partial class StoreViewModel : ObservableObject
{
    private const int PageSize = 48;

    private readonly AppServices _services = AppServices.Current;
    private IReadOnlyList<ModelCardViewModel> _all;
    private List<ModelCardViewModel> _filtered = [];

    public StoreViewModel(IReadOnlyList<ModelCardViewModel> cards)
    {
        _all = cards;
        _services.LibraryUpdated += () => Dispatcher.UIThread.Post(Apply);
        Apply();
    }

    public ObservableCollection<ModelCardViewModel> Cards { get; } = [];
    public string[] SourceFilters { get; } = ["全部来源", "国内模型", "海外模型", "社区模型"];
    public string[] OriginFilters { get; } = ["全部源", "内置模型", "HuggingFace", "魔搭", "Ollama", "远程 API"];
    public string[] CapabilityFilters { get; } = ModelCapabilities.Filters;

    [ObservableProperty] private int _sourceIndex;
    [ObservableProperty] private int _originIndex;
    [ObservableProperty] private int _capabilityIndex;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _importStatus = "";
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string _syncStatus = "";
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _activeFilterText = "";

    public string LoadMoreText => HasMore
        ? $"显示更多（还有 {_filtered.Count - Cards.Count} 个）"
        : "";

    public bool HasActiveFilters => SourceIndex != 0 || OriginIndex != 0 || CapabilityIndex != 0 || SearchText.Trim().Length > 0;

    partial void OnHasMoreChanged(bool value) => OnPropertyChanged(nameof(LoadMoreText));

    public string ProviderHint
    {
        get
        {
            var settings = _services.Settings;
            if (settings.DownloadAuto) return "下载源：自动（原生源优先，失败自动换源）";

            var names = settings.DownloadProviders.Select(SourceName).ToList();
            var text = names.Count == 0 ? "未选择" : string.Join(" → ", names);
            return settings.AutoSwitchSource
                ? $"下载源：{text}（失败自动换源）"
                : $"下载源：{text}（失败不换源）";
        }
    }

    private static string SourceName(string provider) => provider switch
    {
        "HuggingFace" => "HuggingFace",
        "Ollama" => "Ollama",
        _ => "魔搭"
    };

    public string SyncSummaryText => _services.Catalog.RemoteCount > 0
        ? $"社区模型 {_services.Catalog.RemoteCount} 个 · 上次同步 {_services.Catalog.SyncedAt}"
        : "尚未从源同步，当前仅显示内置模型";

    public bool HasSyncStatus => !string.IsNullOrWhiteSpace(SyncStatus);

    partial void OnSyncStatusChanged(string value) => OnPropertyChanged(nameof(HasSyncStatus));

    partial void OnSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        Apply();
    }

    partial void OnOriginIndexChanged(int value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        Apply();
    }

    partial void OnCapabilityIndexChanged(int value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        Apply();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        Apply();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(ProviderHint));
        OnPropertyChanged(nameof(SyncSummaryText));
        Apply();
    }

    public void Reload(IReadOnlyList<ModelCardViewModel> cards)
    {
        _all = cards;
        OnPropertyChanged(nameof(SyncSummaryText));
        Apply();
    }

    [RelayCommand]
    private async Task SyncAsync()
    {
        if (IsSyncing) return;

        IsSyncing = true;
        SyncStatus = "正在连接模型源…";
        try
        {
            var progress = new Progress<string>(message => SyncStatus = message);
            var summary = await _services.SyncCatalogAsync(progress, CancellationToken.None);
            SyncStatus = $"{summary.Describe()} · {summary.UpdatedAt}";
            ImportStatus = "";
        }
        catch (Exception ex)
        {
            SyncStatus = $"同步失败：{ex.Message}";
        }
        finally
        {
            IsSyncing = false;
            OnPropertyChanged(nameof(SyncSummaryText));
            Apply();
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (IsImporting) return;
        var path = await FileDialogService.PickFileAsync("选择 GGUF 模型文件", "*.gguf");
        if (path is null) return;

        IsImporting = true;
        ImportStatus = "正在复制模型文件…";
        try
        {
            var progress = new Progress<double>(percent => ImportStatus = $"正在复制模型文件 {percent:0}%");
            var model = await _services.Library.ImportAsync(path, progress, CancellationToken.None);
            ImportStatus = $"已导入 {model.DisplayName}（{model.SizeText}）";
        }
        catch (Exception ex)
        {
            ImportStatus = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    [RelayCommand]
    private async Task AddCustomModelAsync()
    {
        var owner = App.MainWindow;
        if (owner is null) return;

        var dialog = new AddModelWindow();
        var added = await dialog.ShowDialog<bool>(owner);
        if (!added || dialog.CreatedModel is null) return;

        try
        {
            _services.AddCustomModel(dialog.CreatedModel);
            ImportStatus = $"已添加自定义模型「{dialog.CreatedModel.DisplayName}」";
        }
        catch (Exception ex)
        {
            ImportStatus = $"添加失败：{ex.Message}";
        }
    }

    private void Apply()
    {
        IEnumerable<ModelCardViewModel> query = SourceIndex switch
        {
            1 => _all.Where(c => c.IsChinese),
            2 => _all.Where(c => !c.IsChinese),
            3 => _all.Where(c => c.Model.IsCommunity),
            _ => _all
        };

        query = OriginIndex switch
        {
            1 => query.Where(c => !c.Model.IsCommunity && !c.Model.IsRemoteApi),
            2 => query.Where(c => c.Model.Origin.Equals("HuggingFace", StringComparison.OrdinalIgnoreCase)),
            3 => query.Where(c => c.Model.Origin.Equals("ModelScope", StringComparison.OrdinalIgnoreCase)),
            4 => query.Where(c => c.Model.Origin.Equals("Ollama", StringComparison.OrdinalIgnoreCase)),
            5 => query.Where(c => c.Model.IsRemoteApi),
            _ => query
        };

        var capability = CapabilityIndex > 0 && CapabilityIndex < CapabilityFilters.Length
            ? CapabilityFilters[CapabilityIndex]
            : ModelCapabilities.All;
        if (capability != ModelCapabilities.All)
        {
            query = query.Where(c => c.Model.Capabilities.Contains(capability));
        }

        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            query = query.Where(c =>
                c.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.Developer.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.Model.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.TagsText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.CapabilityText.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        _filtered = query.ToList();
        Cards.Clear();
        AppendPage();

        IsEmpty = _filtered.Count == 0;
        var downloaded = _all.Count(c => c.IsInstalled);
        CountText = Cards.Count < _filtered.Count
            ? $"共 {_filtered.Count} 个模型（当前显示 {Cards.Count} 个）· 已下载 {downloaded} 个"
            : $"共 {_filtered.Count} 个模型 · 已下载 {downloaded} 个";
        ActiveFilterText = BuildActiveFilterText();
    }

    private void AppendPage()
    {
        foreach (var card in _filtered.Skip(Cards.Count).Take(PageSize)) Cards.Add(card);
        HasMore = Cards.Count < _filtered.Count;
        OnPropertyChanged(nameof(LoadMoreText));
    }

    private string BuildActiveFilterText()
    {
        var parts = new List<string>();
        if (SourceIndex > 0 && SourceIndex < SourceFilters.Length) parts.Add(SourceFilters[SourceIndex]);
        if (OriginIndex > 0 && OriginIndex < OriginFilters.Length) parts.Add(OriginFilters[OriginIndex]);
        if (CapabilityIndex > 0 && CapabilityIndex < CapabilityFilters.Length) parts.Add(CapabilityFilters[CapabilityIndex]);
        var search = SearchText.Trim();
        if (search.Length > 0) parts.Add($"“{search}”");
        return parts.Count == 0 ? "" : "当前筛选：" + string.Join(" · ", parts);
    }

    [RelayCommand]
    private void LoadMore()
    {
        AppendPage();
        var downloaded = _all.Count(c => c.IsInstalled);
        CountText = Cards.Count < _filtered.Count
            ? $"共 {_filtered.Count} 个模型（当前显示 {Cards.Count} 个）· 已下载 {downloaded} 个"
            : $"共 {_filtered.Count} 个模型 · 已下载 {downloaded} 个";
    }

    [RelayCommand]
    private void ResetFilters()
    {
        SourceIndex = 0;
        OriginIndex = 0;
        CapabilityIndex = 0;
        SearchText = "";
    }
}
