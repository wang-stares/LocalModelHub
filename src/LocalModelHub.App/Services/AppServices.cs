using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using LocalModelHub.Core.Services.Abstractions;

namespace LocalModelHub.App.Services;

/// <summary>轻量服务定位器，避免引入 DI 容器。</summary>
public sealed class AppServices
{
    public static AppServices Current { get; } = new();

    /// <summary>显式服务注册表（接口化 DI）；强类型属性保留为过渡门面。</summary>
    public ServiceRegistry Services { get; } = new();

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
    /// <summary>两层长期记忆：全局层常驻，项目层随工作文件夹切换。</summary>
    public MemoryManager Memory { get; } = new();

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

    /// <summary>最近一次实测的采样时间（本地时间）；没有实测时为空。</summary>
    public DateTime? BenchmarkMeasuredAt { get; private set; }

    /// <summary>实测档案的硬件指纹与当前机器不一致（换内存 / 换显卡）：提示重新检测。</summary>
    public bool HardwareChanged { get; private set; }

    public string HardwareChangeNote =>
        HardwareChanged ? "配置已变化：与上次实测的硬件指纹不一致，建议重新检测。" : "";

    /// <summary>当前硬件与本机实测档案的指纹；用于实测结果复用与配置导入时判断是否换了设备。</summary>
    public string HardwareFingerprint(HardwareProfile? hardware = null)
    {
        var hw = hardware ?? Hardware;
        // 尚未检测时返回空指纹：既不误判为「换了设备」，也不会把空配置写进实测档案。
        if (hw.DetectedAt == DateTime.MinValue) return "";
        return MeasuredProfileStore.Fingerprint(hw, GpuDatabase.GetBestGpu(hw));
    }

    /// <summary>
    /// 检测到硬件后先尝试复用本机实测结果：同一台机器重启不再退回默认带宽，
    /// 分数与上次一致；没有档案或指纹不匹配时才按估算口径评分。
    /// </summary>
    public bool LoadMeasuredProfile(HardwareProfile hardware)
    {
        var fingerprint = HardwareFingerprint(hardware);
        var profile = MeasuredProfileStore.TryLoad(fingerprint);
        if (profile is null)
        {
            // 档案存在但硬件指纹不同 → 换了硬件；同一硬件只是版本 / 过期 → 不算配置变化。
            var previous = MeasuredProfileStore.ReadLatest();
            var hardwareKey = MeasuredProfileStore.HardwareFingerprint(hardware, GpuDatabase.GetBestGpu(hardware));
            HardwareChanged = previous is { HardwareFingerprint.Length: > 0 } &&
                              !previous.HardwareFingerprint.Equals(hardwareKey, StringComparison.Ordinal);
            LastBenchmark = null;
            BenchmarkMeasuredAt = null;
            return false;
        }

        HardwareChanged = false;
        LastBenchmark = new BenchmarkResult(
            profile.CpuGflops, profile.MemoryBandwidthGBps, TimeSpan.Zero, profile.StdevRatio);
        BenchmarkMeasuredAt = profile.MeasuredAt.Kind == DateTimeKind.Utc
            ? profile.MeasuredAt.ToLocalTime()
            : profile.MeasuredAt;
        return true;
    }

    /// <summary>用户点「检测」跑完基准后落盘，换设备 / 跨大版本时自动失效。</summary>
    public void SaveMeasuredProfile(HardwareProfile hardware, BenchmarkResult benchmark)
    {
        var measuredAt = DateTime.UtcNow;
        var hardwareKey = MeasuredProfileStore.HardwareFingerprint(hardware, GpuDatabase.GetBestGpu(hardware));
        MeasuredProfileStore.Save(new MeasuredProfile(
            HardwareFingerprint(hardware),
            benchmark.CpuGflops,
            benchmark.MemoryBandwidthGBps,
            benchmark.StdevRatio,
            measuredAt,
            MeasuredProfileStore.AppMajorVersion)
        {
            HardwareFingerprint = hardwareKey
        });
        BenchmarkMeasuredAt = measuredAt.ToLocalTime();
        HardwareChanged = false;
    }

    /// <summary>按稳定输入评分，并把采样时间写进结果，界面据此显示「实测于 X」而不是「刚刚」。</summary>
    public PerformanceScore ScoreHardware(
        HardwareProfile hardware,
        BenchmarkResult? benchmark,
        DateTime? measuredAt = null)
    {
        var score = Scorer.Score(hardware, benchmark);
        return score with { ScoredAt = measuredAt ?? hardware.DetectedAt };
    }

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
            CpuMoeLayers = Math.Max(0, Settings.CpuMoeLayers),
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
    public AgentRunner AgentRunner { get; private set; } = null!;

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
        var tuning = AgentTuningLimits.Normalize(Settings);
        Workspace.DefaultReadLines = tuning.ReadLines;
        Workspace.MaxReadWindow = tuning.MaxReadWindow;
        Memory.Load();
        Memory.UseWorkspace(Workspace.Root);
        // 启动时整理一次：清理过期，并把高度相似的历史条目合并，避免记忆越攒越散。
        Memory.Consolidate();
        // 挂机空闲时再整理一次，完成后再停止模型进入休眠；与启动整理逻辑相同，重复执行无副作用。
        IdleSleep.BeforeSleep += () => Memory.Consolidate();
        // 切换工作文件夹时自动切换项目记忆上下文；全局记忆不受影响。
        Workspace.RootChanged += root => Memory.UseWorkspace(root);
        Skills.Load();
        ChatSessions.Load();
        Knowledge.Load();
        // 混合检索只在设置开启时生效；地址实时读取，服务重启后无需重建客户端。
        Knowledge.Embeddings = new LlamaEmbeddingClient(
            () => Server.IsRunning ? $"http://127.0.0.1:{Server.Port}" : null,
            () => Settings.ApiKey,
            () => Server.LoadedModelName);
        Library = new ModelLibraryService(SettingsService);
        DownloadManager = new ModelDownloadManager(DownloadService, SettingsService, Library);
        Runtime = new RuntimeManager(DownloadService, SettingsService);
        Downloads = new DownloadCenter(DownloadManager, Library);
        Launcher = new ModelLauncher();
        AgentRunner = new AgentRunner(
            Server, Workspace, Memory, tuning: () => AgentTuning.FromSettings(Settings));
        Services.Register<IChatCompletion>(Server);
        Services.Register<IWorkspace>(Workspace);
        Services.Register<IMemoryStore>(Memory);
        Services.Register<IAgentRunner>(AgentRunner);
        Performance = Scorer.Score(Hardware);
        Library.Changed += RefreshLibrary;
        RefreshLibrary();
        IdleSleep.Configure(Settings.IdleSleepMinutes);
        LastLaunchedModel = string.IsNullOrWhiteSpace(Settings.LastModelPath)
            ? null
            : FindInstalledByPath(Settings.LastModelPath);
        LogRuntimeStorage();
    }

    /// <summary>启动时统计推理引擎占用，旧版本堆积时在日志里给出可清理的提示。</summary>
    private void LogRuntimeStorage()
    {
        try
        {
            var report = Runtime.ScanStorage();
            var obsolete = report.Obsolete;
            AppLog.Info("runtime",
                $"运行时占用 {Formatters.Bytes(report.TotalBytes)}（{report.Entries.Count} 个版本）" +
                (obsolete.Count == 0 ? "" : $" · 可清理 {obsolete.Count} 个，约 {Formatters.Bytes(report.ObsoleteBytes)}"));
        }
        catch (Exception ex)
        {
            AppLog.Error("runtime", "统计运行时占用失败", ex);
        }
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
