using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalModelHub.App.Services;

namespace LocalModelHub.App.ViewModels;

/// <summary>「我的模型」页面：同一条目下切换已下载模型与按适配度排序的推荐。</summary>
public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;

    public ModelsViewModel(LibraryViewModel library, RecommendViewModel recommend)
    {
        Library = library;
        Recommend = recommend;
        _services.LibraryUpdated += () => Dispatcher.UIThread.Post(RefreshCounts);
        _services.CatalogChanged += () => Dispatcher.UIThread.Post(RefreshCounts);
        RefreshCounts();
    }

    public LibraryViewModel Library { get; }
    public RecommendViewModel Recommend { get; }

    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string _installedTabText = "已下载";
    [ObservableProperty] private string _recommendTabText = "为你推荐";

    public string[] Tabs => [InstalledTabText, RecommendTabText];

    public bool IsInstalledTab => TabIndex == 0;
    public bool IsRecommendTab => TabIndex == 1;

    public string TabHint => TabIndex == 0
        ? "点击「启动模型」加载到推理服务；「停止服务」释放显存与内存。删除会移除磁盘上的模型文件。"
        : "按这台电脑的显存与内存排序；点击卡片上的按钮即可下载，下载完成后到「已下载」标签启动。";

    partial void OnTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsInstalledTab));
        OnPropertyChanged(nameof(IsRecommendTab));
        OnPropertyChanged(nameof(TabHint));
        if (value == 1) Recommend.RefreshCount();
        else Library.RefreshRunning();
    }

    public void RefreshCounts()
    {
        InstalledTabText = $"已下载 {Library.Items.Count}";
        RecommendTabText = $"为你推荐 {Recommend.SuitedCount}";
        OnPropertyChanged(nameof(Tabs));
    }
}
