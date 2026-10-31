using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.Core.Models;

namespace LocalModelHub.App.ViewModels;

public sealed partial class RecommendViewModel : ObservableObject
{
    private const int PageSize = 48;

    private IReadOnlyList<ModelCardViewModel> _all;
    private IReadOnlyList<ModelCardViewModel> _ordered;
    private List<ModelCardViewModel> _filtered = [];

    public RecommendViewModel(IReadOnlyList<ModelCardViewModel> cards)
    {
        _all = cards;
        _ordered = cards;
        UpdateSuitedCount();
        Apply();
    }

    public ObservableCollection<ModelCardViewModel> Cards { get; } = [];
    public string[] FitFilters { get; } = ["适合我的", "完美运行", "可运行", "内存不足", "全部模型"];
    public string[] CapabilityFilters { get; } = ModelCapabilities.Filters;

    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private int _capabilityIndex;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private int _suitedCount;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _activeFilterText = "";

    public string LoadMoreText => HasMore
        ? $"显示更多（还有 {_filtered.Count - Cards.Count} 个）"
        : "";

    public bool HasActiveFilters => FilterIndex != 0 || CapabilityIndex != 0 || SearchText.Trim().Length > 0;

    partial void OnHasMoreChanged(bool value) => OnPropertyChanged(nameof(LoadMoreText));

    public void Reload(IReadOnlyList<ModelCardViewModel> cards)
    {
        _all = cards;
        _ordered = cards;
        UpdateSuitedCount();
        Apply();
    }

    public void SetOrder(IReadOnlyList<ModelCardViewModel> ordered)
    {
        _ordered = ordered;
        UpdateSuitedCount();
        Apply();
    }

    public void RefreshCount() => Apply();

    [RelayCommand]
    private void ResetFilters()
    {
        FilterIndex = 0;
        CapabilityIndex = 0;
        SearchText = "";
    }

    partial void OnFilterIndexChanged(int value)
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

    private void UpdateSuitedCount() =>
        SuitedCount = _ordered.Count(card => card.Fit <= FitLevel.Playable);

    private void Apply()
    {
        IEnumerable<ModelCardViewModel> query = FilterIndex switch
        {
            1 => _ordered.Where(c => c.Fit == FitLevel.Perfect),
            2 => _ordered.Where(c => c.Fit is FitLevel.Good or FitLevel.Playable),
            3 => _ordered.Where(c => c.Fit is FitLevel.Tight or FitLevel.Insufficient),
            // 默认「适合我的」：只展示这台电脑能顺畅运行的模型。
            4 => _ordered,
            _ => _ordered.Where(c => c.Fit <= FitLevel.Playable)
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
        CountText = $"适合这台电脑 {SuitedCount} 个 · 共 {_ordered.Count} 个模型 · 已下载 {_all.Count(c => c.IsInstalled)} 个";
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
        var parts = new List<string>
        {
            FitFilters[Math.Clamp(FilterIndex, 0, FitFilters.Length - 1)]
        };
        if (CapabilityIndex > 0 && CapabilityIndex < CapabilityFilters.Length)
        {
            parts.Add(CapabilityFilters[CapabilityIndex]);
        }

        var search = SearchText.Trim();
        if (search.Length > 0) parts.Add($"“{search}”");
        return "当前筛选：" + string.Join(" · ", parts);
    }

    [RelayCommand]
    private void LoadMore() => AppendPage();
}
