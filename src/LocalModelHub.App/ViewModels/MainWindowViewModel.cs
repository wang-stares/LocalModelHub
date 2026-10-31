using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.App.Services;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;

namespace LocalModelHub.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly string[] Titles =
        ["概览", "我的模型", "模型商店", "对话", "知识与记忆", "设置"];

    private static readonly string[] Subtitles =
    [
        "检测硬件性能，查看适合这台电脑的本地模型",
        "已下载模型与为你推荐的模型，启动推理或继续下载",
        "从 HuggingFace、魔搭与 Ollama 同步开源模型，一键下载到本机",
        "与本地模型对话，所有内容都留在本机",
        "知识库文档与长期记忆，都会在对话时自动引用",
        "下载源、存储目录与推理参数"
    ];

    private readonly AppServices _services = AppServices.Current;
    private List<ModelCardViewModel> _cards;

    public MainWindowViewModel()
    {
        _services.Initialize();
        _isNavCollapsed = _services.Settings.NavCollapsed;

        _cards = _services.Catalog.Models
            .Select(model => new ModelCardViewModel(model, LaunchCardAsync))
            .ToList();

        Dashboard = new DashboardViewModel();
        Library = new LibraryViewModel(LaunchModelAsync, () => PageIndex = 2);
        Recommend = new RecommendViewModel(_cards);
        Models = new ModelsViewModel(Library, Recommend);
        Store = new StoreViewModel(_cards);
        Chat = new ChatViewModel();
        KnowledgePage = new KnowledgeViewModel();
        SettingsPage = new SettingsViewModel();
        SettingsPage.RecommendationSettingsChanged += RefreshRecommendations;
        MemoryPage = new MemoryViewModel();
        SkillsPage = new SkillsViewModel(_services);
        KnowledgeMemory = new KnowledgeMemoryViewModel(KnowledgePage, MemoryPage, SkillsPage);
        Dashboard.OpenRecommendations = () =>
        {
            Models.TabIndex = 1;
            PageIndex = 1;
        };

        Dashboard.DetectionCompleted += (_, _) => RefreshRecommendations();
        _services.LibraryUpdated += () => Dispatcher.UIThread.Post(() =>
        {
            Recommend.RefreshCount();
            foreach (var card in _cards) card.RefreshLibraryState();
            Models.RefreshCounts();
        });
        _services.Server.StateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshServerState);
        _services.RemoteApi.ActiveChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RefreshServerState();
            Chat.RefreshServerState();
        });
        _services.CatalogChanged += ReloadCards;

        PageIndex = 0;
        CurrentPage = Dashboard;
        HeaderTitle = Titles[0];
        HeaderSubtitle = Subtitles[0];
        RefreshRecommendations();
        RefreshServerState();
        Dashboard.Initialize();
    }

    public DashboardViewModel Dashboard { get; }
    public ModelsViewModel Models { get; }
    public RecommendViewModel Recommend { get; }
    public StoreViewModel Store { get; }
    public LibraryViewModel Library { get; }
    public ChatViewModel Chat { get; }
    public KnowledgeMemoryViewModel KnowledgeMemory { get; }
    public KnowledgeViewModel KnowledgePage { get; }
    public SettingsViewModel SettingsPage { get; }
    public MemoryViewModel MemoryPage { get; }
    public SkillsViewModel SkillsPage { get; }

    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _headerTitle = Titles[0];
    [ObservableProperty] private string _headerSubtitle = Subtitles[0];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private bool _isServerRunning;
    [ObservableProperty] private bool _hasLoadedModel;
    [ObservableProperty] private bool _canStopServer;
    [ObservableProperty] private string _serverSummary = "推理服务未运行";
    [ObservableProperty] private string _loadedModelName = "未加载模型";

    public bool IsDashboardActive => PageIndex == 0;
    public bool IsModelsActive => PageIndex == 1;
    public bool IsStoreActive => PageIndex == 2;
    public bool IsChatActive => PageIndex == 3;
    public bool IsKnowledgeMemoryActive => PageIndex == 4;
    public bool IsSettingsActive => PageIndex == 5;

    /// <summary>导航折叠状态：折叠后只保留图标列，把宽度让给内容区。</summary>
    [ObservableProperty] private bool _isNavCollapsed;

    public GridLength NavColumnWidth => IsNavCollapsed ? new GridLength(76) : new GridLength(244);
    public Avalonia.Layout.HorizontalAlignment NavAlignment => IsNavCollapsed
        ? Avalonia.Layout.HorizontalAlignment.Center
        : Avalonia.Layout.HorizontalAlignment.Left;
    public string NavToggleText => IsNavCollapsed ? "›" : "‹";
    public string NavToggleTip => IsNavCollapsed ? "展开导航" : "收起导航";

    partial void OnIsNavCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(NavColumnWidth));
        OnPropertyChanged(nameof(NavAlignment));
        OnPropertyChanged(nameof(NavToggleText));
        OnPropertyChanged(nameof(NavToggleTip));
        _services.Settings.NavCollapsed = value;
        _services.SettingsService.Save();
    }

    [RelayCommand]
    private void ToggleNav() => IsNavCollapsed = !IsNavCollapsed;

    public string VersionText => _services.Catalog.RemoteCount > 0
        ? $"v{AppInfo.Version} · 内置目录 {_services.Catalog.UpdatedAt} · 社区同步 {_services.Catalog.SyncedAt}"
        : $"v{AppInfo.Version} · 模型目录 {_services.Catalog.UpdatedAt}";

    partial void OnPageIndexChanged(int value)
    {
        HeaderTitle = Titles[value];
        HeaderSubtitle = Subtitles[value];
        CurrentPage = value switch
        {
            0 => Dashboard,
            1 => Models,
            2 => Store,
            3 => Chat,
            4 => KnowledgeMemory,
            5 => SettingsPage,
            _ => Dashboard
        };

        OnPropertyChanged(nameof(IsDashboardActive));
        OnPropertyChanged(nameof(IsModelsActive));
        OnPropertyChanged(nameof(IsStoreActive));
        OnPropertyChanged(nameof(IsChatActive));
        OnPropertyChanged(nameof(IsKnowledgeMemoryActive));
        OnPropertyChanged(nameof(IsSettingsActive));

        switch (value)
        {
            case 1:
                Models.RefreshCounts();
                Library.RefreshRunning();
                break;
            case 2:
                Store.Refresh();
                break;
            case 3:
                Chat.RefreshWorkspaceState();
                Chat.RefreshServerState();
                break;
            case 4:
                KnowledgeMemory.Refresh();
                break;
            case 5:
                SettingsPage.RefreshApiModels();
                break;
        }
    }

    [RelayCommand]
    private void Navigate(string? index)
    {
        if (int.TryParse(index, out var value) && value is >= 0 and < 6)
        {
            PageIndex = value;
        }
    }

    [RelayCommand]
    private async Task StopServerAsync()
    {
        _services.RemoteApi.Deactivate();
        _services.ServerStoppedByUser = true;
        _services.IdleSleep.Stop();
        await _services.Server.StopAsync();
        RefreshServerState();
        Library.RefreshRunning();
    }

    [RelayCommand]
    private void OpenWebUi()
    {
        if (_services.Server.IsRunning) ModelLauncher.OpenUrl(_services.Server.WebUiUrl);
    }

    public async Task LaunchModelAsync(DownloadedModel model)
    {
        if (IsBusy) return;

        IsBusy = true;
        BusyText = "准备启动…";
        try
        {
            var runtimeProgress = new Progress<RuntimeProgress>(p => BusyText = p.Message);
            var status = new Progress<string>(message => BusyText = message);
            await _services.Launcher.LaunchAsync(model, runtimeProgress, status, CancellationToken.None);

            Chat.OnModelLaunched(model);
            PageIndex = 3;
            RefreshServerState();
        }
        catch (OperationCanceledException)
        {
            // 用户在加载过程中停止了服务，不按失败处理
            RefreshServerState();
        }
        catch (Exception ex)
        {
            await HandleLaunchFailureAsync(model, ex);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            Library.RefreshRunning();
        }
    }

    /// <summary>启动失败后先诊断 GGUF，再按「装载方式 → 省显存 → 纯 CPU → 换/升级运行时」逐级降级。</summary>
    private async Task HandleLaunchFailureAsync(DownloadedModel model, Exception error)
    {
        var diagnosis = await Task.Run(() => GgufDoctor.Inspect(model.PrimaryPath));
        if (!diagnosis.IsUsable)
        {
            await DialogService.AlertAsync(
                "模型文件无法加载",
                $"文件：{model.PrimaryPath}\n\n诊断：{diagnosis.Problem}\n\n这类问题与显存、后端、上下文无关，重试或降级都不会成功。建议删除该模型后重新下载；若文件来自第三方仓库，换用官方仓库的 Q4_K_M 量化。");
            return;
        }

        var acceptRepair = await DialogService.ConfirmAsync(
            "启动失败",
            $"{error.Message}\n\n文件诊断：{diagnosis.Summary}\n\n可以让星坞自动逐级降级，按顺序尝试：\n① 禁用 mmap 装载\n② 上下文减半 + 关闭 KV 量化\n③ 纯 CPU 推理\n④ 切换或升级推理引擎",
            "自动处理",
            "关闭");
        if (!acceptRepair) return;

        var configuredContext = _services.Settings.ContextSize;
        var reducedContext = Math.Max(2048, configuredContext / 2);
        var attempts = new List<(string Label, ModelLauncher.LaunchTuning Tuning)>
        {
            ("禁用 mmap 装载", new ModelLauncher.LaunchTuning(LoadMode: "none")),
            ("上下文减半 + 关闭 KV 量化", new ModelLauncher.LaunchTuning(
                ContextSize: reducedContext,
                CacheType: "f16",
                FlashAttention: "off",
                LoadMode: "none")),
            ("纯 CPU 推理", new ModelLauncher.LaunchTuning(
                ContextSize: Math.Min(reducedContext, 4096),
                GpuLayers: 0,
                CacheType: "f16",
                FlashAttention: "off",
                LoadMode: "none"))
        };

        // 已安装的其他运行时优先，避免一开始就下载
        var currentTag = _services.Runtime.GetInstalledTag();
        var otherTag = _services.Runtime.ListInstalledTags().FirstOrDefault(tag => tag != currentTag);
        if (otherTag is not null && _services.Runtime.FindServerForTag(otherTag) is { } otherServer)
        {
            attempts.Add(($"切换到运行时 {otherTag} · 纯 CPU", new ModelLauncher.LaunchTuning(
                ContextSize: Math.Min(reducedContext, 4096),
                GpuLayers: 0,
                CacheType: "f16",
                FlashAttention: "off",
                LoadMode: "none",
                ServerPath: otherServer)));
        }

        var failures = new List<string> { $"文件诊断：{diagnosis.Summary}" };
        foreach (var (label, tuning) in attempts)
        {
            if (await TryRepairAsync(model, label, tuning, failures)) return;
        }

        // 没有其他运行时可用时，下载上游最新版再试一次纯 CPU
        var gpu = _services.Performance.BestGpu?.Gpu ?? _services.Hardware.Gpus.FirstOrDefault();
        try
        {
            BusyText = "自动处理：检查上游最新推理引擎…";
            var latest = await _services.Runtime.TryGetLatestTagAsync(CancellationToken.None);
            if (latest is null)
            {
                failures.Add("升级推理引擎：无法访问 GitHub（网络不可用或被拦截）");
            }
            else if (latest == currentTag)
            {
                failures.Add($"升级推理引擎：已是上游最新（{latest}）");
            }
            else
            {
                BusyText = $"自动处理：下载推理引擎 {latest}…";
                var progress = new Progress<RuntimeProgress>(p => BusyText = $"自动处理：{p.Message}");
                var install = await _services.Runtime.UpdateAsync(gpu, progress, CancellationToken.None);
                var tuning = new ModelLauncher.LaunchTuning(
                    ContextSize: Math.Min(reducedContext, 4096),
                    GpuLayers: 0,
                    CacheType: "f16",
                    FlashAttention: "off",
                    LoadMode: "none",
                    ServerPath: install.ServerPath);
                if (await TryRepairAsync(model, $"升级到运行时 {install.Tag} · 纯 CPU", tuning, failures)) return;
            }
        }
        catch (Exception ex)
        {
            failures.Add($"升级推理引擎：{ShortMessage(ex.Message)}");
        }

        await DialogService.AlertAsync(
            "自动处理未成功",
            "已尝试以下方案：\n\n" +
            string.Join("\n", failures) +
            "\n\n纯 CPU + 禁用 mmap 仍失败时，问题基本可判定为模型文件与当前 llama.cpp 不兼容：请删除后重新下载，或改用官方仓库的 Q4_K_M 量化。");
    }

    private async Task<bool> TryRepairAsync(
        DownloadedModel model,
        string label,
        ModelLauncher.LaunchTuning tuning,
        List<string> failures)
    {
        BusyText = $"自动处理：{label}…";
        try
        {
            var runtimeProgress = new Progress<RuntimeProgress>(p => BusyText = $"自动处理：{p.Message}");
            var status = new Progress<string>(message => BusyText = $"自动处理：{message}");
            await _services.Launcher.LaunchAsync(model, runtimeProgress, status, CancellationToken.None, tuning);

            Chat.OnModelLaunched(model);
            PageIndex = 3;
            RefreshServerState();
            await DialogService.AlertAsync(
                "已自动处理",
                $"已用「{label}」启动成功。\n\n本次降级不会写回设置，下次启动仍按原参数；如果仍不稳定，可在“设置 → 推理引擎”更新推理引擎或切换运行时后端。");
            return true;
        }
        catch (Exception ex)
        {
            failures.Add($"{label}：{ShortMessage(ex.Message)}");
            return false;
        }
    }

    private static string ShortMessage(string message)
    {
        var line = message
            .Split('\n')
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))?.Trim();
        if (string.IsNullOrEmpty(line)) return "未知错误";
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    private async Task LaunchCardAsync(ModelCardViewModel card)
    {
        if (card.Model.IsRemoteApi)
        {
            if (_services.Server.IsRunning) await _services.Server.StopAsync();
            _services.RemoteApi.Activate(
                card.Model.ApiBaseUrl, card.Model.ApiKey, card.Model.ApiModelId, card.Model.DisplayName);
            Chat.OnRemoteActivated(card.Model.DisplayName);
            RefreshServerState();
            PageIndex = 3;
            return;
        }

        if (card.Installed is { } model)
        {
            await LaunchModelAsync(model);
        }
    }

    private void ReloadCards()
    {
        foreach (var card in _cards) card.Dispose();

        _cards = _services.Catalog.Models
            .Select(model => new ModelCardViewModel(model, LaunchCardAsync))
            .ToList();

        Recommend.Reload(_cards);
        Store.Reload(_cards);
        RefreshRecommendations();
        Library.Refresh();
        SettingsPage.RefreshCatalogInfo();
    }

    private void RefreshRecommendations()
    {
        var recommendations = _services.Recommender.Recommend(
            _services.Hardware,
            _services.Performance,
            _services.Catalog,
            _services.RecommendationOptions());
        var lookup = recommendations.ToDictionary(r => r.Model.Id);
        foreach (var card in _cards)
        {
            card.ApplyRecommendation(lookup.GetValueOrDefault(card.Model.Id));
        }

        var ordered = _cards
            .OrderByDescending(c => c.SortScore)
            .ThenBy(c => c.Model.ParametersB)
            .ToList();

        Dashboard.SetTopCards(ordered);
        Recommend.SetOrder(ordered);
        Store.Refresh();
    }

    private void RefreshServerState()
    {
        var local = _services.Server.IsRunning;
        var remote = _services.RemoteApi;
        IsServerRunning = local;
        CanStopServer = local || remote.IsActive;
        HasLoadedModel = local && !string.IsNullOrWhiteSpace(_services.Server.LoadedModelName);
        ServerSummary = local
            ? $"运行中 · 端口 {_services.Server.Port}"
            : remote.IsActive
                ? $"远程 API · {remote.DisplayName}"
                : "推理服务未运行";
        LoadedModelName = local
            ? _services.Server.LoadedModelName!
            : remote.IsActive
                ? remote.DisplayName
                : "未加载模型";
    }
}
