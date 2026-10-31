using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;

namespace LocalModelHub.App.Services;

/// <summary>轻量服务定位器，避免引入 DI 容器。</summary>
public sealed class AppServices
{
    public static AppServices Current { get; } = new();

    public SettingsService SettingsService { get; } = new();
    public AppSettings Settings => SettingsService.Settings;
    public CatalogService CatalogService { get; } = new();
    public HardwareDetector HardwareDetector { get; } = new();
    public BenchmarkService Benchmark { get; } = new();
    public PerformanceScorer Scorer { get; } = new();
    public RecommendationEngine Recommender { get; } = new();
    public GpuMemoryProbe GpuMemory { get; } = new();
    public DownloadService DownloadService { get; } = new();
    public LlamaServerService Server { get; } = new();
    public RemoteApiService RemoteApi { get; } = new();
    public WorkspaceService Workspace { get; } = new();
    public UserModelStore CustomModels { get; } = new();
    public MemoryService Memory { get; } = new();
    public ChatSessionStore ChatSessions { get; } = new();
    public KnowledgeService Knowledge { get; } = new();

    /// <summary>可复用技能库：任务完成后自动沉淀，对话时按需注入。</summary>
    public SkillService Skills { get; } = new();
    public IdleSleepService IdleSleep { get; } = new();
    public BackupService Backup { get; } = new();
    public RemoteCatalogService RemoteCatalog { get; private set; } = null!;

    private RemoteCatalogCache _remoteCache = new();

    public bool IsCatalogSyncing { get; private set; }
    public IReadOnlyList<CatalogSourceStatus> SourceStatuses => _remoteCache.Sources;
    public string CatalogSyncedAt => _remoteCache.UpdatedAt;

    public DownloadedModel? LastLaunchedModel { get; set; }
    public bool ServerStoppedByUser { get; set; }

    /// <summary>最近一次 CPU 基准测试，用于让推荐引擎使用实测内存带宽。</summary>
    public BenchmarkResult? LastBenchmark { get; set; }

    private ModelProfile? _activeProfile;

    /// <summary>当前已加载模型的能力画像；为空表示还没探测或不支持探测。</summary>
    public ModelProfile? ActiveProfile
    {
        get => _activeProfile;
        set
        {
            if (_activeProfile == value) return;
            _activeProfile = value;
            ProfileChanged?.Invoke();
        }
    }

    /// <summary>能力画像变化（模型加载完成 / 失败回退）：供模型卡片刷新工具调用标注。</summary>
    public event Action? ProfileChanged;

    /// <summary>模型不支持原生 tool_calls 时，Agent 改走文本协议。</summary>
    public bool TextToolProtocol => ActiveProfile is { SupportsNativeTools: false };

    /// <summary>统一构造推荐参数：上下文跟随设置，带宽与算力有实测就用实测值，显存扣掉其它程序当前占用。</summary>
    public RecommendationOptions RecommendationOptions()
    {
        var gpu = Performance.BestGpu;
        var reserved = gpu is { IsIntegrated: false }
            ? GpuMemory.ReservedVramGb(gpu.Gpu.Name)
            : 0;

        var approvedKv = string.IsNullOrWhiteSpace(Settings.KvCacheType) ? "f16" : Settings.KvCacheType.Trim();
        return new RecommendationOptions
        {
            ContextTokens = Math.Max(2048, Settings.ContextSize),
            ReservedVramGb = reserved,
            CpuMemoryBandwidthGBps = LastBenchmark is { MemoryBandwidthGBps: > 10 } measured
                ? measured.MemoryBandwidthGBps
                : 51,
            CpuGflops = LastBenchmark is { CpuGflops: > 0 } compute ? compute.CpuGflops : 0,
            KvCacheTypeK = approvedKv,
            KvCacheTypeV = approvedKv,
            // llama.cpp 的 --flash-attn 只接受 on/off/auto；只有显式 off 才按不支持量化 V 处理。
            FlashAttention = !Settings.FlashAttention.Equals("off", StringComparison.OrdinalIgnoreCase),
            ParallelSlots = Math.Max(1, Settings.ParallelSlots),
            Backend = Settings.RuntimeVariant
        };
    }

    /// <summary>
    /// 下载前重新采样可用内存与显存占用：关掉浏览器等占用程序后立即生效，卡片标签不再沿用旧快照。
    /// 未做过硬件检测时不构造新硬件，避免在缺少显卡信息时误判。
    /// </summary>
    public void RefreshBudgetSnapshot()
    {
        var (total, available) = HardwareDetector.ReadMemoryNow();
        if (Hardware.DetectedAt != DateTime.MinValue && total > 0 && available > 0)
        {
            Hardware = Hardware with
            {
                TotalRamBytes = total,
                AvailableRamBytes = available,
                DetectedAt = DateTime.Now
            };
        }

        GpuMemory.Invalidate();
    }

    public ModelCatalog Catalog { get; private set; } = new();
    public ModelLibraryService Library { get; private set; } = null!;
    public ModelDownloadManager DownloadManager { get; private set; } = null!;
    public RuntimeManager Runtime { get; private set; } = null!;
    public DownloadCenter Downloads { get; private set; } = null!;
    public ModelLauncher Launcher { get; private set; } = null!;

    public HardwareProfile Hardware { get; set; } = HardwareProfile.Empty;
    public PerformanceScore Performance { get; set; } = new(0, 0, 0, 0, 0, "入门", "尚未检测", null);
    public IReadOnlyList<DownloadedModel> InstalledModels { get; private set; } = [];

    public event Action? LibraryUpdated;
    public event Action? CatalogChanged;

    /// <summary>评估输入（量化、上下文、KV、显卡占用）变化后广播，界面据此重算排序与结论。</summary>
    public event Action<string>? EvaluationInvalidated;

    /// <summary>预算快照已过期：重新采样内存 / 显存占用，再通知所有依赖评估的页面刷新。</summary>
    public void InvalidateEvaluation(string reason)
    {
        RefreshBudgetSnapshot();
        EvaluationInvalidated?.Invoke(reason);
    }

    private ModelCatalog _builtinCatalog = new();

    public bool Initialized { get; private set; }

    public void Initialize()
    {
        if (Initialized) return;
        Initialized = true;

        SettingsService.Load();
        _builtinCatalog = CatalogService.LoadEmbedded();
        RemoteCatalog = new RemoteCatalogService(preferHfMirror: Settings.UseHfMirror);
        _remoteCache = RemoteCatalog.LoadCache();
        ReloadCatalog();
        Workspace.SetRoot(Settings.WorkspaceDirectory);
        Memory.Load();
        Skills.Load();
        ChatSessions.Load();
        Knowledge.Load();
        Library = new ModelLibraryService(SettingsService);
        DownloadManager = new ModelDownloadManager(DownloadService, SettingsService, Library);
        Runtime = new RuntimeManager(DownloadService, SettingsService);
        Downloads = new DownloadCenter(DownloadManager, Library);
        Launcher = new ModelLauncher();
        Performance = Scorer.Score(Hardware);
        Library.Changed += RefreshLibrary;
        RefreshLibrary();
        IdleSleep.Configure(Settings.IdleSleepMinutes);
        LastLaunchedModel = string.IsNullOrWhiteSpace(Settings.LastModelPath)
            ? null
            : FindInstalledByPath(Settings.LastModelPath);
    }

    public void RefreshLibrary()
    {
        InstalledModels = Library.Scan(Catalog);
        LibraryUpdated?.Invoke();
    }

    public DownloadedModel? FindInstalled(string modelId, string quantLabel) =>
        InstalledModels.FirstOrDefault(m =>
            m.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase) &&
            m.QuantLabel.Equals(quantLabel, StringComparison.OrdinalIgnoreCase));

    public DownloadedModel? FindInstalledByPath(string path) =>
        InstalledModels.FirstOrDefault(m =>
            m.PrimaryPath.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>添加用户自定义模型并重建目录（内置 + 自定义）。</summary>
    public void AddCustomModel(ModelEntry model)
    {
        model.Id = "custom-" + Guid.NewGuid().ToString("N")[..10];
        model.IsCustom = true;
        CustomModels.Add(model);
        ReloadCatalog();
        CatalogChanged?.Invoke();
    }

    public void RemoveCustomModel(string modelId)
    {
        if (!CustomModels.Remove(modelId)) return;
        ReloadCatalog();
        CatalogChanged?.Invoke();
    }

    private void ReloadCatalog()
    {
        var customModels = CustomModels.Load();
        Catalog = CatalogMergeService.Merge(_builtinCatalog, customModels, _remoteCache);
    }

    /// <summary>按当前启用的模型源重新抓取社区模型；失败仅记录在源状态里，不影响内置目录。</summary>
    public async Task<CatalogSyncSummary> SyncCatalogAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!Initialized) throw new InvalidOperationException("应用尚未初始化");
        if (IsCatalogSyncing) return new CatalogSyncSummary(_remoteCache.Models.Count, _remoteCache.Sources, _remoteCache.UpdatedAt);

        var sourceIds = new List<string>();
        if (Settings.SyncHuggingFace) sourceIds.Add(RemoteCatalogService.HuggingFaceId);
        if (Settings.SyncModelScope) sourceIds.Add(RemoteCatalogService.ModelScopeId);
        if (Settings.SyncOllama) sourceIds.Add(RemoteCatalogService.OllamaId);
        if (sourceIds.Count == 0) throw new InvalidOperationException("请至少启用一个模型源");

        IsCatalogSyncing = true;
        RemoteCatalog.PreferHfMirror = Settings.UseHfMirror;
        try
        {
            _remoteCache = await RemoteCatalog.SyncAsync(sourceIds, progress, cancellationToken, _remoteCache);
            RemoteCatalog.SaveCache(_remoteCache);
            ReloadCatalog();
            RefreshLibrary();
            CatalogChanged?.Invoke();
            return new CatalogSyncSummary(_remoteCache.Models.Count, _remoteCache.Sources, _remoteCache.UpdatedAt);
        }
        finally
        {
            IsCatalogSyncing = false;
        }
    }

    public void RefreshMirrorPreference()
    {
        if (RemoteCatalog is null) return;
        RemoteCatalog.PreferHfMirror = Settings.UseHfMirror;
    }
}
