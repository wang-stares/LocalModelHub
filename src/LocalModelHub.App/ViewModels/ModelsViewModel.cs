using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalModelHub.App.Services;

namespace LocalModelHub.App.ViewModels;

/// <summary>模型库：已下载 / 为你推荐 / 商店三段页内标签，共享同一套硬件评估结果。</summary>
public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;

    public ModelsViewModel(LibraryViewModel library, RecommendViewModel recommend, StoreViewModel store)
    {
        Library = library;
        Recommend = recommend;
        Store = store;
        _services.LibraryUpdated += () => Dispatcher.UIThread.Post(RefreshCounts);
        _services.CatalogChanged += () => Dispatcher.UIThread.Post(RefreshCounts);
        RefreshCounts();
    }

    public LibraryViewModel Library { get; }
    public RecommendViewModel Recommend { get; }
    public StoreViewModel Store { get; }

    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string _installedTabText = "已下载";
    [ObservableProperty] private string _recommendTabText = "为你推荐";
    [ObservableProperty] private string _storeTabText = "商店";
    public string[] Tabs => [InstalledTabText, RecommendTabText, StoreTabText];

    public bool IsInstalledTab => TabIndex == 0;
    public bool IsRecommendTab => TabIndex == 1;
    public bool IsStoreTab => TabIndex == 2;

    public string TabHint => TabIndex switch
    {
        0 => "启动推理服务、查看进度与磁盘占用。删除会移除磁盘上的模型文件。",
        1 => "按这台电脑的显存与内存排序，下载完成后回到「已下载」启动。",
        _ => "从 HuggingFace、魔搭与 Ollama 同步开源模型，一键下载到本机。"
    };

    partial void OnTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsInstalledTab));
        OnPropertyChanged(nameof(IsRecommendTab));
        OnPropertyChanged(nameof(IsStoreTab));
        OnPropertyChanged(nameof(TabHint));
        if (value == 1) Recommend.RefreshCount();
        else if (value == 2) Store.Refresh();
        else Library.RefreshRunning();
    }

    public void RefreshCounts()
    {
        InstalledTabText = $"已下载 {Library.Items.Count}";
        RecommendTabText = $"为你推荐 {Recommend.SuitedCount}";
        OnPropertyChanged(nameof(Tabs));
    }
}
