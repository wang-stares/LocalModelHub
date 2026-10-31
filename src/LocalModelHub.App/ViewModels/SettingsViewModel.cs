using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.App.Services;
using LocalModelHub.App.Views;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;

namespace LocalModelHub.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;

    /// <summary>影响适配评估的设置（上下文长度等）保存后通知主窗口重新打分。</summary>
    public event Action? RecommendationSettingsChanged;

    public SettingsViewModel()
    {
        Load();
        RefreshRuntimeStatus();
        _services.Server.StateChanged += (_, _) => Dispatcher.UIThread.Post(NotifyApiState);
    }

    /// <summary>常见 OpenAI 兼容服务商，选中后自动填写 API 地址，Key 仍需用户自己申请。</summary>
    public sealed record ApiProviderPreset(string Name, string BaseUrl, string Note);

    public static readonly ApiProviderPreset[] ApiPresets =
    [
        new("OpenRouter（聚合数百模型）", "https://openrouter.ai/api/v1", "在 openrouter.ai 申请 Key，模型目录无需 Key 即可拉取"),
        new("Gitee AI 模力方舟", "https://ai.gitee.com/v1", "Gitee 账号创建令牌，国内直连"),
        new("硅基流动 SiliconFlow", "https://api.siliconflow.cn/v1", "国内直连，注册后在控制台创建 API Key"),
        new("DeepSeek 官方", "https://api.deepseek.com/v1", "DeepSeek 开放平台 Key"),
        new("阿里云百炼（通义）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "DashScope API Key"),
        new("火山方舟（豆包）", "https://ark.cn-beijing.volces.com/api/v3", "火山引擎方舟 API Key"),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "智谱开放平台 Key"),
        new("月之暗面 Kimi", "https://api.moonshot.cn/v1", "Moonshot 开放平台 Key"),
        new("OpenAI 官方", "https://api.openai.com/v1", "需要海外网络与 OpenAI Key")
    ];

    public string[] DownloadModes { get; } = ["自动（多源）", "手动选择"];
    public string[] Variants { get; } = ["自动（按显卡选择）", "CUDA（NVIDIA）", "Vulkan（AMD / Intel）", "CPU"];
    public string[] ThemeOptions { get; } = ["跟随系统", "浅色", "深色"];

    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _downloadModeIndex;
    [ObservableProperty] private bool? _downloadModelScope;
    [ObservableProperty] private bool? _downloadHuggingFace;
    [ObservableProperty] private bool? _downloadOllama;
    [ObservableProperty] private bool? _autoSwitchSource;
    [ObservableProperty] private bool? _useHfMirror;
    [ObservableProperty] private bool? _syncHuggingFace;
    [ObservableProperty] private bool? _syncModelScope;
    [ObservableProperty] private bool? _syncOllama;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string _syncStatus = "";
    public string[] ApiPresetNames { get; } = ["自定义", .. ApiPresets.Select(preset => preset.Name)];
    [ObservableProperty] private int _apiPresetIndex;
    [ObservableProperty] private string _apiPresetHint = "填写任意 OpenAI 兼容地址（本地 Ollama、vLLM、One-API 也可以）。";
    [ObservableProperty] private string _remoteApiBaseUrl = "";
    [ObservableProperty] private string _remoteApiKey = "";
    [ObservableProperty] private bool _isApiProbing;
    [ObservableProperty] private string _apiProbeStatus = "";
    [ObservableProperty] private string _apiModelsEmptyText = "";
    [ObservableProperty] private string _modelsDirectory = "";
    [ObservableProperty] private string _workspaceDirectory = "";
    [ObservableProperty] private string _systemPrompt = "";
    [ObservableProperty] private decimal? _temperature;
    [ObservableProperty] private decimal? _topP;
    [ObservableProperty] private decimal? _maxTokens;
    [ObservableProperty] private decimal? _idleSleepMinutes;
    [ObservableProperty] private bool? _autoWake;
    [ObservableProperty] private string _runtimePathText = "";
    [ObservableProperty] private int _variantIndex;
    [ObservableProperty] private decimal? _port;
    [ObservableProperty] private decimal? _contextSize;
    [ObservableProperty] private decimal? _threads;
    public string[] FlashAttentionOptions { get; } = ["自动（推荐）", "强制开启", "强制关闭"];
    public string[] KvCacheOptions { get; } = ["f16（不量化，最稳）", "q8_0（推荐，近无损）", "q4_0（更省显存）"];
    public string[] LoadModeOptions { get; } = ["自动", "mmap（加载快）", "mlock（锁定内存）"];
    [ObservableProperty] private int _flashAttentionIndex;
    [ObservableProperty] private int _kvCacheIndex = 1;
    [ObservableProperty] private int _loadModeIndex = 1;
    [ObservableProperty] private decimal? _parallelSlots = 1;
    [ObservableProperty] private decimal? _cpuMoeLayers;
    [ObservableProperty] private bool? _reasoningEnabled = true;
    [ObservableProperty] private bool? _metricsEnabled;
    [ObservableProperty] private string _runtimeStatusText = "";
    [ObservableProperty] private bool _hasRuntimeUpdate;
    [ObservableProperty] private bool _isRuntimeBusy;
    [ObservableProperty] private string _runtimeUpdateText = "";
    [ObservableProperty] private bool? _apiExposeLan;
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _apiCopyStatus = "";
    [ObservableProperty] private string _saveStatus = "";
    [ObservableProperty] private bool _isReleaseArchiveOpen;

    private static readonly IReadOnlyList<ReleaseNote> Releases = ReleaseNotes.Load();

    public string VersionText => $"星坞 {AppInfo.Version}";
    public string LatestReleaseText => Releases.Count == 0 ? "" : $"{Releases[0].Version} · {Releases[0].Date}";
    public IReadOnlyList<ReleaseLine> LatestReleaseLines { get; } =
        Releases.Count == 0 ? [] : Releases[0].Flatten().ToList();

    /// <summary>「星河档案」：当前版本之外的往期公告，按时间倒序排列。</summary>
    public IReadOnlyList<ReleaseNote> PreviousReleases { get; } = Releases.Skip(1).ToList();
    public bool HasPreviousReleases => PreviousReleases.Count > 0;
    public string ReleaseArchiveCountText => $"{PreviousReleases.Count} 个往期版本";
    public string ReleaseArchiveToggleText => IsReleaseArchiveOpen ? "收起星河档案" : "展开星河档案";

    partial void OnIsReleaseArchiveOpenChanged(bool value) =>
        OnPropertyChanged(nameof(ReleaseArchiveToggleText));

    [RelayCommand]
    private void ToggleReleaseArchive() => IsReleaseArchiveOpen = !IsReleaseArchiveOpen;

    public string DataRootText => AppPaths.RootDirectory;
    public string CatalogText => _services.Catalog.RemoteCount > 0
        ? $"内置目录 v{_services.Catalog.Version} · 更新于 {_services.Catalog.UpdatedAt} · 社区模型同步于 {_services.Catalog.SyncedAt}"
        : $"内置目录 v{_services.Catalog.Version} · 更新于 {_services.Catalog.UpdatedAt}";
    public string CatalogCountText
    {
        get
        {
            var all = _services.Catalog.Models;
            var chinese = all.Count(m => m.IsChinese);
            var community = _services.Catalog.RemoteCount;
            return community > 0
                ? $"共 {all.Count} 个模型 · 国内 {chinese} 个 · 海外 {all.Count - chinese} 个 · 社区源 {community} 个"
                : $"共 {all.Count} 个模型 · 国内 {chinese} 个 · 海外 {all.Count - chinese} 个";
        }
    }

    public bool IsManualSource => DownloadModeIndex == 1;

    public string DownloadSourceHint => IsManualSource
        ? "勾选一个源即单源下载，勾选多个按「魔搭 → HuggingFace → Ollama」顺序依次尝试；开启换源后失败会继续尝试未勾选的源。"
        : "自动模式：优先使用模型的原生源，再按「魔搭 → HuggingFace → Ollama」依次尝试，失败时自动切换到其他源。";

    partial void OnDownloadModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsManualSource));
        OnPropertyChanged(nameof(DownloadSourceHint));
    }

    public string SyncSummaryText => _services.SourceStatuses.Count == 0
        ? "尚未同步；内置目录始终可用，社区源可随时手动同步。"
        : string.Join(" · ", _services.SourceStatuses.Select(status =>
            status.Error is null
                ? $"{status.Name} {status.Count} 个（{status.FetchedAt}）"
                : $"{status.Name} 失败：{status.Error}"));

    public bool HasSyncStatus => !string.IsNullOrWhiteSpace(SyncStatus);

    partial void OnSyncStatusChanged(string value) => OnPropertyChanged(nameof(HasSyncStatus));

    public string ThreadsHint => "0 表示自动使用物理核心数；建议保持 0。";
    public string RuntimePathHint => "留空时自动下载官方 llama.cpp Windows 预编译包（首次启动模型时）。";
    public string ParallelSlotsHint => "单用户建议 1：只保留一个槽位，KV 显存占用最小；多人并发再调高。";
    public string CpuMoeLayersHint => "MoE 模型（Qwen3-MoE、DeepSeek 等）可把前 N 层专家权重留在 CPU，显存不够时优先调这个；0 表示关闭。";
    public string KvCacheHint => "KV cache 量化对长上下文的显存影响最大：q8_0 基本无损，q4_0 更省但可能影响长文推理质量。";
    public bool CanUpdateRuntime => HasRuntimeUpdate && !IsRuntimeBusy;
    public string RuntimeUpdateButtonText => IsRuntimeBusy ? "处理中…" : HasRuntimeUpdate ? "更新引擎" : "已是最新";
    public bool HasRuntimeUpdateText => !string.IsNullOrWhiteSpace(RuntimeUpdateText);
    public bool CanReinstallRuntime => !IsRuntimeBusy && !IsSwitchingRuntime;
    public string ReinstallRuntimeText => IsSwitchingRuntime ? "正在切换…" : "重装后端";

    [ObservableProperty] private bool _isSwitchingRuntime;

    partial void OnIsSwitchingRuntimeChanged(bool value)
    {
        OnPropertyChanged(nameof(CanReinstallRuntime));
        OnPropertyChanged(nameof(ReinstallRuntimeText));
    }

    partial void OnRuntimeUpdateTextChanged(string value) =>
        OnPropertyChanged(nameof(HasRuntimeUpdateText));

    partial void OnHasRuntimeUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUpdateRuntime));
        OnPropertyChanged(nameof(RuntimeUpdateButtonText));
    }

    partial void OnIsRuntimeBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUpdateRuntime));
        OnPropertyChanged(nameof(RuntimeUpdateButtonText));
        OnPropertyChanged(nameof(CanReinstallRuntime));
    }

    private void RefreshRuntimeStatus()
    {
        var installed = _services.Runtime.GetInstalledTag();
        var variant = _services.Runtime.GetInstalledVariant();
        RuntimeStatusText = installed is null
            ? "尚未安装推理引擎；首次启动模型时会自动下载官方 llama.cpp 预编译包。"
            : $"已安装 llama.cpp {installed} · 后端 {variant}" +
              (string.IsNullOrWhiteSpace(_services.Settings.RuntimePath) ? "" : " · 使用自定义 llama-server 路径");
    }

    [RelayCommand]
    private async Task CheckRuntimeUpdateAsync()
    {
        if (IsRuntimeBusy) return;

        IsRuntimeBusy = true;
        RuntimeUpdateText = "正在查询 GitHub…";
        try
        {
            var latest = await _services.Runtime.TryGetLatestTagAsync(CancellationToken.None);
            if (latest is null)
            {
                HasRuntimeUpdate = false;
                RuntimeUpdateText = "查询失败：网络不可用或 GitHub 无法访问，稍后重试。";
                return;
            }

            var installed = _services.Runtime.GetInstalledTag();
            HasRuntimeUpdate = installed is not null && CompareTags(latest, installed) > 0;
            RuntimeUpdateText = installed is null
                ? $"上游最新 {latest}；本机尚未安装，首次启动模型时会自动下载。"
                : HasRuntimeUpdate
                    ? $"上游最新 {latest}，当前 {installed}，可更新。"
                    : $"已是最新（{installed}）。";
        }
        finally
        {
            IsRuntimeBusy = false;
            RefreshRuntimeStatus();
        }
    }

    [RelayCommand]
    private async Task UpdateRuntimeAsync()
    {
        if (IsRuntimeBusy || !HasRuntimeUpdate) return;

        var confirm = await DialogService.ConfirmAsync(
            "更新推理引擎",
            "将从 GitHub 下载最新的 llama.cpp Windows 预编译包（约 200–400 MB），完成后下次启动模型生效。",
            "开始更新");
        if (!confirm) return;

        IsRuntimeBusy = true;
        RuntimeUpdateText = "正在下载推理引擎…";
        try
        {
            var gpu = _services.Performance.BestGpu?.Gpu ?? _services.Hardware.Gpus.FirstOrDefault();
            var progress = new Progress<RuntimeProgress>(p => RuntimeUpdateText = p.Message);
            var install = await _services.Runtime.UpdateAsync(gpu, progress, CancellationToken.None);
            HasRuntimeUpdate = false;
            RuntimeUpdateText = $"已更新到 {install.Tag} · {install.Variant}；重启推理服务后生效。";
        }
        catch (Exception ex)
        {
            RuntimeUpdateText = $"更新失败：{ex.Message}";
        }
        finally
        {
            IsRuntimeBusy = false;
            RefreshRuntimeStatus();
        }
    }

    [RelayCommand]
    private async Task ReinstallRuntimeAsync()
    {
        if (IsRuntimeBusy || IsSwitchingRuntime) return;

        var variant = VariantIndex switch
        {
            1 => "cuda",
            2 => "vulkan",
            3 => "cpu",
            _ => "auto"
        };
        var name = Variants[Math.Clamp(VariantIndex, 0, Variants.Length - 1)];
        var confirm = await DialogService.ConfirmAsync(
            "切换推理后端",
            $"将按「{name}」重新下载 llama.cpp 预编译包（约 200–400 MB），完成后需重启推理服务。",
            "开始下载");
        if (!confirm) return;

        IsSwitchingRuntime = true;
        RuntimeUpdateText = $"正在下载 {name} 运行时…";
        try
        {
            _services.Settings.RuntimeVariant = variant;
            _services.SettingsService.Save();
            var gpu = _services.Performance.BestGpu?.Gpu ?? _services.Hardware.Gpus.FirstOrDefault();
            var progress = new Progress<RuntimeProgress>(p => RuntimeUpdateText = p.Message);
            var install = await _services.Runtime.UpdateAsync(gpu, progress, CancellationToken.None);
            RuntimeUpdateText = $"已安装 {install.Tag} · {install.Variant}；重启推理服务后生效。";
        }
        catch (Exception ex)
        {
            RuntimeUpdateText = $"切换失败：{ex.Message}";
        }
        finally
        {
            IsSwitchingRuntime = false;
            RefreshRuntimeStatus();
        }
    }

    private static int CompareTags(string left, string right)
    {
        static int Number(string tag) =>
            int.TryParse(tag.TrimStart('b').Split('-')[0], out var value) ? value : 0;
        return Number(left).CompareTo(Number(right));
    }

    public string ApiEndpointText
    {
        get
        {
            var port = _services.Server.IsRunning ? _services.Server.Port : _services.Settings.ServerPort;
            var host = ApiExposeLan == true ? NetworkHelper.GetLanAddress() : "127.0.0.1";
            return $"http://{host}:{port}/v1";
        }
    }

    public string ApiStateText => _services.Server.IsRunning
        ? "推理服务运行中；修改监听范围或密钥后需重启推理服务"
        : "启动模型后生效";

    public string ApiKeyHint => "留空表示不校验密钥；允许局域网访问时建议设置，客户端用 Bearer 方式携带。";
    public bool HasApiCopyStatus => !string.IsNullOrWhiteSpace(ApiCopyStatus);

    partial void OnApiCopyStatusChanged(string value) => OnPropertyChanged(nameof(HasApiCopyStatus));

    private void NotifyApiState()
    {
        OnPropertyChanged(nameof(ApiEndpointText));
        OnPropertyChanged(nameof(ApiStateText));
    }

    partial void OnApiExposeLanChanged(bool? value) => NotifyApiState();

    [RelayCommand]
    private void GenerateApiKey()
    {
        ApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        ApiCopyStatus = "已生成新密钥，保存并重启推理服务后生效";
    }

    [RelayCommand]
    private async Task CopyApiEndpointAsync()
    {
        var clipboard = App.MainWindow?.Clipboard;
        if (clipboard is null) return;

        await clipboard.SetTextAsync(ApiEndpointText);
        ApiCopyStatus = "已复制 API 地址";
    }

    public void RefreshCatalogInfo()
    {
        OnPropertyChanged(nameof(CatalogText));
        OnPropertyChanged(nameof(CatalogCountText));
        OnPropertyChanged(nameof(SyncSummaryText));
    }

    [RelayCommand]
    private async Task SyncSourcesAsync()
    {
        if (IsSyncing) return;

        IsSyncing = true;
        SyncStatus = "正在连接模型源…";
        try
        {
            var progress = new Progress<string>(message => SyncStatus = message);
            var summary = await _services.SyncCatalogAsync(progress, CancellationToken.None);
            SyncStatus = $"{summary.Describe()} · {summary.UpdatedAt}";
            RefreshCatalogInfo();
        }
        catch (Exception ex)
        {
            SyncStatus = $"同步失败：{ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    // ---------- 外部 API 模型 ----------

    public ObservableCollection<string> ApiModels { get; } = [];
    public bool HasApiModels => _apiModelIds.Count > 0;
    public bool HasNoApiModels => ApiModels.Count == 0;

    private IReadOnlyList<string> _apiModelIds = [];
    private CancellationTokenSource? _apiProbeCts;
    private bool _loading;
    private string _lastPopupSignature = "";

    partial void OnRemoteApiBaseUrlChanged(string value)
    {
        if (_loading) return;
        ScheduleApiProbe();
    }

    partial void OnApiPresetIndexChanged(int value)
    {
        if (value <= 0 || value > ApiPresets.Length)
        {
            ApiPresetHint = "填写任意 OpenAI 兼容地址（本地 Ollama、vLLM、One-API 也可以）。";
            return;
        }

        var preset = ApiPresets[value - 1];
        ApiPresetHint = preset.Note;
        if (_loading) return;
        RemoteApiBaseUrl = preset.BaseUrl;
    }

    private static int MatchApiPreset(string? baseUrl)
    {
        var normalized = RemoteApiService.NormalizeBaseUrl(baseUrl ?? "");
        if (normalized.Length == 0) return 0;
        var index = Array.FindIndex(ApiPresets, preset =>
            RemoteApiService.NormalizeBaseUrl(preset.BaseUrl) == normalized);
        return index + 1;
    }

    partial void OnRemoteApiKeyChanged(string value)
    {
        if (_loading) return;
        ScheduleApiProbe();
    }

    /// <summary>地址或密钥变化后延迟自动拉取模型列表，并弹出选择窗口。</summary>
    private void ScheduleApiProbe()
    {
        _apiProbeCts?.Cancel();
        var baseUrl = RemoteApiBaseUrl.Trim();
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _apiProbeCts = cts;
        _ = ProbeAfterDelayAsync(cts);
    }

    private async Task ProbeAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(900, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            if (!cts.IsCancellationRequested) await FetchApiModelsAsync(autoPopup: true, cts.Token);
        });
    }

    [RelayCommand]
    private async Task FetchApiModelsAsync() => await FetchApiModelsAsync(autoPopup: false, CancellationToken.None);

    private async Task FetchApiModelsAsync(bool autoPopup, CancellationToken cancellationToken)
    {
        if (IsApiProbing) return;
        var baseUrl = RemoteApiBaseUrl.Trim();
        if (baseUrl.Length == 0)
        {
            ApiProbeStatus = "请先填写 API 地址";
            return;
        }

        IsApiProbing = true;
        ApiProbeStatus = "正在获取模型列表…";
        try
        {
            var models = await _services.RemoteApi.ListModelsAsync(baseUrl, RemoteApiKey, cancellationToken);
            _apiModelIds = models;
            OnPropertyChanged(nameof(HasApiModels));
            ApiProbeStatus = $"获取到 {models.Count} 个模型，选择一个即可连接";

            var signature = RemoteApiService.NormalizeBaseUrl(baseUrl) + "|" + RemoteApiKey.Trim();
            if (autoPopup && signature != _lastPopupSignature)
            {
                _lastPopupSignature = signature;
                await ShowApiModelPickerAsync();
            }
        }
        catch (OperationCanceledException)
        {
            ApiProbeStatus = "";
        }
        catch (Exception ex)
        {
            _apiModelIds = [];
            OnPropertyChanged(nameof(HasApiModels));
            ApiProbeStatus = $"获取失败：{ex.Message}";
        }
        finally
        {
            IsApiProbing = false;
        }
    }

    [RelayCommand]
    private async Task ChooseApiModelAsync() => await ShowApiModelPickerAsync();

    private async Task ShowApiModelPickerAsync()
    {
        if (!HasApiModels) return;
        var owner = App.MainWindow;
        if (owner is null) return;

        var dialog = new ApiModelPickerWindow(_apiModelIds);
        var selected = await dialog.ShowDialog<string?>(owner);
        if (!string.IsNullOrWhiteSpace(selected)) UseApiModel(selected);
    }

    /// <summary>把选中的远程模型加入自定义模型并立即连接。</summary>
    public void UseApiModel(string modelId)
    {
        var baseUrl = RemoteApiService.NormalizeBaseUrl(RemoteApiBaseUrl);
        if (baseUrl.Length == 0) return;

        var apiKey = RemoteApiKey.Trim();
        var preset = ApiPresets.FirstOrDefault(item =>
            RemoteApiService.NormalizeBaseUrl(item.BaseUrl) == baseUrl);
        var existing = _services.CustomModels.Models.FirstOrDefault(model =>
            model.IsRemoteApi &&
            model.ApiModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase) &&
            model.ApiBaseUrl.Equals(baseUrl, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.ApiKey = apiKey;
            _services.CustomModels.Save();
        }
        else
        {
            existing = new ModelEntry
            {
                Name = modelId,
                DisplayName = modelId,
                Developer = preset?.Name ?? "外部 API",
                Country = "",
                Family = "API",
                ParametersB = 0,
                ActiveParametersB = 0,
                ContextLength = 0,
                ReleaseYear = DateTime.Now.Year,
                License = "外部 API",
                Description = preset is null
                    ? $"通过 {baseUrl} 调用的远程模型，无需下载权重。"
                    : $"通过 {preset.Name}（{baseUrl}）调用的远程模型，无需下载权重。",
                Tags = ["外部 API", "远程"],
                Capabilities = [ModelCapabilities.Chat],
                Quants = [],
                ApiBaseUrl = baseUrl,
                ApiKey = apiKey,
                ApiModelId = modelId,
                Origin = "API"
            };
            _services.AddCustomModel(existing);
        }

        _services.RemoteApi.Activate(existing.ApiBaseUrl, existing.ApiKey, existing.ApiModelId, existing.DisplayName);
        ApiProbeStatus = $"已连接「{existing.DisplayName}」，可在对话页直接使用";
    }

    /// <summary>刷新「API 接入」卡片中当前可被客户端调用的模型清单。</summary>
    public void RefreshApiModels()
    {
        ApiModels.Clear();
        var running = _services.Server.IsRunning ? _services.Server.LoadedModelName : null;
        if (!string.IsNullOrWhiteSpace(running))
        {
            ApiModels.Add($"{running}（运行中）");
        }

        foreach (var model in _services.InstalledModels
                     .Select(model => model.DisplayName)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (model.Equals(running, StringComparison.OrdinalIgnoreCase)) continue;
            ApiModels.Add(model);
        }

        ApiModelsEmptyText = ApiModels.Count == 0
            ? "暂无已下载模型；启动本地模型后即可被客户端调用。"
            : "";
        OnPropertyChanged(nameof(HasNoApiModels));
    }

    partial void OnThemeIndexChanged(int value) => App.ApplyTheme(value switch
    {
        1 => "light",
        2 => "dark",
        _ => "system"
    });

    [RelayCommand]
    private async Task BrowseModelsAsync()
    {
        var folder = await FileDialogService.PickFolderAsync("选择模型存储目录");
        if (folder is null) return;
        ModelsDirectory = folder;
        Save();
    }

    [RelayCommand]
    private async Task BrowseRuntimeAsync()
    {
        var file = await FileDialogService.PickFileAsync("选择 llama-server.exe", "*.exe");
        if (file is null) return;
        RuntimePathText = file;
        Save();
    }

    [RelayCommand]
    private async Task BrowseWorkspaceAsync()
    {
        var folder = await FileDialogService.PickFolderAsync("选择工作文件夹");
        if (folder is null) return;
        WorkspaceDirectory = folder;
        Save();
    }

    [RelayCommand]
    private void OpenDataFolder() => ModelLauncher.OpenFolder(AppPaths.RootDirectory);

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        var suggested = $"LocalModelHub-backup-{DateTime.Now:yyyyMMdd-HHmm}.json";
        var path = await FileDialogService.PickSaveFileAsync("导出备份", suggested, "JSON 备份", "*.json");
        if (path is null) return;

        try
        {
            _services.Backup.Export(path, AppInfo.Version);
            var size = new FileInfo(path).Length / 1024.0;
            await DialogService.AlertAsync("导出成功",
                $"已导出设置、自定义模型、记忆、会话与知识库：\n{path}\n（{size:0.#} KB）");
        }
        catch (Exception ex)
        {
            await DialogService.AlertAsync("导出失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportBackupAsync()
    {
        var path = await FileDialogService.PickFileAsync("导入备份", "*.json");
        if (path is null) return;

        var confirmed = await DialogService.ConfirmAsync("导入备份",
            "导入会覆盖当前的设置、自定义模型、记忆、全部会话与知识库，且无法撤销。\n导入完成后需要重启应用才会生效，是否继续？",
            "导入");
        if (!confirmed) return;

        try
        {
            var result = _services.Backup.Import(path);
            await DialogService.AlertAsync("导入成功",
                $"{Describe(result)}\n请重启应用以使用导入的数据。");
        }
        catch (Exception ex)
        {
            await DialogService.AlertAsync("导入失败", ex.Message);
        }
    }

    private static string Describe(BackupImportResult result)
    {
        var parts = new List<string>();
        if (result.SettingsImported) parts.Add("设置");
        if (result.CustomModels > 0) parts.Add($"{result.CustomModels} 个自定义模型");
        if (result.Memories > 0) parts.Add($"{result.Memories} 条记忆");
        if (result.Sessions > 0) parts.Add($"{result.Sessions} 个会话");
        if (result.KnowledgeDocuments > 0) parts.Add($"{result.KnowledgeDocuments} 份知识库文档");
        return parts.Count == 0 ? "（备份中没有可导入的数据）" : "已导入：" + string.Join("、", parts);
    }

    [RelayCommand]
    private void Save()
    {
        var settings = _services.Settings;
        settings.Theme = ThemeIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "system"
        };
        settings.DownloadAuto = DownloadModeIndex == 0;
        settings.AutoSwitchSource = AutoSwitchSource != false;
        var providers = new List<string>();
        if (DownloadModelScope != false) providers.Add("ModelScope");
        if (DownloadHuggingFace != false) providers.Add("HuggingFace");
        if (DownloadOllama != false) providers.Add("Ollama");

        if (!settings.DownloadAuto && providers.Count == 0)
        {
            SaveStatus = "请至少勾选一个下载源";
            return;
        }

        settings.DownloadProviders = providers.Count > 0 ? providers : ["ModelScope", "HuggingFace", "Ollama"];
        settings.UseHfMirror = UseHfMirror == true;
        settings.RemoteApiBaseUrl = RemoteApiBaseUrl.Trim();
        settings.RemoteApiKey = RemoteApiKey.Trim();
        settings.SyncHuggingFace = SyncHuggingFace != false;
        settings.SyncModelScope = SyncModelScope != false;
        settings.SyncOllama = SyncOllama != false;
        settings.ModelsDirectory = string.IsNullOrWhiteSpace(ModelsDirectory)
            ? AppPaths.ModelsDirectory
            : ModelsDirectory.Trim();
        settings.RuntimePath = string.IsNullOrWhiteSpace(RuntimePathText) ? null : RuntimePathText.Trim();
        settings.WorkspaceDirectory = string.IsNullOrWhiteSpace(WorkspaceDirectory)
            ? ""
            : WorkspaceDirectory.Trim();
        _services.Workspace.SetRoot(settings.WorkspaceDirectory);
        settings.SystemPrompt = SystemPrompt.Trim();
        settings.Temperature = (double)Math.Clamp(Temperature ?? 0.7m, 0m, 2m);
        settings.TopP = (double)Math.Clamp(TopP ?? 0.95m, 0m, 1m);
        settings.MaxTokens = (int)Math.Clamp(MaxTokens ?? 1024m, 64m, 8192m);

        Temperature = (decimal)settings.Temperature;
        TopP = (decimal)settings.TopP;
        MaxTokens = settings.MaxTokens;
        settings.IdleSleepMinutes = (int)Math.Clamp(IdleSleepMinutes ?? 10m, 0m, 240m);
        settings.AutoWake = AutoWake != false;
        IdleSleepMinutes = settings.IdleSleepMinutes;
        AutoWake = settings.AutoWake;
        _services.IdleSleep.Configure(settings.IdleSleepMinutes);
        settings.RuntimeVariant = VariantIndex switch
        {
            1 => "cuda",
            2 => "vulkan",
            3 => "cpu",
            _ => "auto"
        };
        settings.ServerPort = (int)Math.Clamp(Port ?? 8090, 1024, 65535);
        settings.ContextSize = (int)Math.Clamp(ContextSize ?? 8192, 1024, 131072);
        settings.Threads = (int)Math.Clamp(Threads ?? 0, 0, 256);

        var inferenceBefore = (settings.FlashAttention, settings.KvCacheType, settings.CpuMoeLayers,
            settings.ParallelSlots, settings.LoadMode, settings.ReasoningEnabled, settings.MetricsEnabled);
        settings.FlashAttention = FlashAttentionIndex switch { 1 => "on", 2 => "off", _ => "auto" };
        settings.KvCacheType = KvCacheIndex switch { 0 => "f16", 2 => "q4_0", _ => "q8_0" };
        settings.LoadMode = LoadModeIndex switch { 0 => "auto", 2 => "mlock", _ => "mmap" };
        settings.ParallelSlots = (int)Math.Clamp(ParallelSlots ?? 1, 0, 64);
        settings.CpuMoeLayers = (int)Math.Clamp(CpuMoeLayers ?? 0, 0, 999);
        settings.ReasoningEnabled = ReasoningEnabled != false;
        settings.MetricsEnabled = MetricsEnabled == true;
        var inferenceChanged = inferenceBefore != (settings.FlashAttention, settings.KvCacheType,
            settings.CpuMoeLayers, settings.ParallelSlots, settings.LoadMode, settings.ReasoningEnabled,
            settings.MetricsEnabled);

        var apiChanged = settings.ApiExposeLan != (ApiExposeLan == true) ||
                         settings.ApiKey != ApiKey.Trim();
        settings.ApiExposeLan = ApiExposeLan == true;
        settings.ApiKey = ApiKey.Trim();
        ApiKey = settings.ApiKey;

        Port = settings.ServerPort;
        ContextSize = settings.ContextSize;
        Threads = settings.Threads;
        ParallelSlots = settings.ParallelSlots;
        CpuMoeLayers = settings.CpuMoeLayers;

        _services.SettingsService.Save();
        RecommendationSettingsChanged?.Invoke();
        _services.RefreshLibrary();
        _services.RefreshMirrorPreference();
        NotifyApiState();
        RefreshCatalogInfo();
        RefreshApiModels();
        RefreshRuntimeStatus();
        SaveStatus = inferenceChanged && _services.Server.IsRunning
            ? $"已保存 · 推理参数将在重启推理服务后生效 · {DateTime.Now:HH:mm:ss}"
            : apiChanged && _services.Server.IsRunning
                ? $"已保存 · 重启推理服务后 API 设置生效 · {DateTime.Now:HH:mm:ss}"
                : $"已保存 · {DateTime.Now:HH:mm:ss}";
    }

    private void Load()
    {
        _loading = true;
        var settings = _services.Settings;
        ThemeIndex = settings.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };
        DownloadModeIndex = settings.DownloadAuto ? 0 : 1;
        DownloadModelScope = settings.DownloadProviders.Count == 0 ||
                             settings.DownloadProviders.Contains("ModelScope");
        DownloadHuggingFace = settings.DownloadProviders.Count == 0 ||
                              settings.DownloadProviders.Contains("HuggingFace");
        DownloadOllama = settings.DownloadProviders.Count == 0 ||
                         settings.DownloadProviders.Contains("Ollama");
        AutoSwitchSource = settings.AutoSwitchSource;
        UseHfMirror = settings.UseHfMirror;
        SyncHuggingFace = settings.SyncHuggingFace;
        SyncModelScope = settings.SyncModelScope;
        SyncOllama = settings.SyncOllama;
        ModelsDirectory = settings.ModelsDirectory;
        WorkspaceDirectory = settings.WorkspaceDirectory;
        SystemPrompt = settings.SystemPrompt;
        Temperature = (decimal)settings.Temperature;
        TopP = (decimal)settings.TopP;
        MaxTokens = settings.MaxTokens;
        IdleSleepMinutes = settings.IdleSleepMinutes;
        AutoWake = settings.AutoWake;
        RuntimePathText = settings.RuntimePath ?? "";
        VariantIndex = settings.RuntimeVariant switch
        {
            "cuda" => 1,
            "vulkan" => 2,
            "cpu" => 3,
            _ => 0
        };
        Port = settings.ServerPort;
        ContextSize = settings.ContextSize;
        Threads = settings.Threads;
        ParallelSlots = settings.ParallelSlots;
        CpuMoeLayers = settings.CpuMoeLayers;
        FlashAttentionIndex = settings.FlashAttention switch
        {
            "on" => 1,
            "off" => 2,
            _ => 0
        };
        KvCacheIndex = settings.KvCacheType switch
        {
            "f16" => 0,
            "q4_0" => 2,
            _ => 1
        };
        LoadModeIndex = settings.LoadMode switch
        {
            "auto" => 0,
            "mlock" => 2,
            _ => 1
        };
        ReasoningEnabled = settings.ReasoningEnabled;
        MetricsEnabled = settings.MetricsEnabled;
        ApiExposeLan = settings.ApiExposeLan;
        ApiKey = settings.ApiKey;
        RemoteApiBaseUrl = settings.RemoteApiBaseUrl;
        RemoteApiKey = settings.RemoteApiKey;
        ApiPresetIndex = MatchApiPreset(settings.RemoteApiBaseUrl);
        _loading = false;
        ApiProbeStatus = string.IsNullOrWhiteSpace(settings.RemoteApiBaseUrl)
            ? ""
            : "已保存 API 配置，点击「获取模型」可刷新列表";
        RefreshApiModels();
    }
}