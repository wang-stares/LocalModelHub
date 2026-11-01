using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.App.Services;
using LocalModelHub.Core.Models;

namespace LocalModelHub.App.ViewModels;

/// <summary>模型商店 / 推荐列表共用的卡片视图模型。</summary>
public sealed partial class ModelCardViewModel : ObservableObject, IDisposable
{
    private static readonly ModelQuant ApiQuant = new() { Label = "API" };

    private readonly Func<ModelCardViewModel, Task> _launch;
    private readonly DownloadState _state;
    private DownloadedModel? _installed;
    private ModelRecommendation? _recommendation;
    private bool _suppressQuantSync;
    private int _recommendedQuantIndex = -1;
    private static DispatcherTimer? _invalidateTimer;

    public ModelCardViewModel(ModelEntry model, Func<ModelCardViewModel, Task> launch)
    {
        Model = model;
        _launch = launch;
        _state = AppServices.Current.Downloads.GetState(model.Id);
        _state.PropertyChanged += OnDownloadPropertyChanged;
        if (model.IsRemoteApi) AppServices.Current.RemoteApi.ActiveChanged += OnRemoteApiChanged;
        RefreshInstalled();
        SelectInstalledQuant();
        RefreshPartial();
    }

    public ModelEntry Model { get; }

    public string Title => Model.DisplayName;
    public string Developer => Model.Developer;
    public string Description => Model.Description;
    public string TagsText => string.Join(" · ", Model.Tags.Take(3));
    public bool HasCapability => Model.HasCapabilities;
    public string PrimaryCapability => Model.Capabilities.Length > 0 ? Model.Capabilities[0] : "";
    public string CapabilityText => Model.CapabilityText;
    public string Initial => Title.Length <= 2 ? Title : Title[..2];
    public string OriginText => Model.CountryLabel;
    public bool IsCommunity => Model.IsCommunity;
    public bool IsRemote => Model.IsRemoteApi;
    public bool ShowSourceBadge => IsCommunity || IsRemote;
    public string SourceBadgeText => Model.OriginLabel;
    public string MetaText => IsRemote
        ? $"远程 API · {Model.ApiBaseUrl}"
        : HasQuantChoices
            ? $"{Model.ParamsLabel} · {ContextText}{ContextAdvice}"
            : $"{Model.ParamsLabel} · {ContextText} · {SelectedQuant.Label} {Formatters.Bytes(SelectedQuant.TotalBytes)}{ContextAdvice}";

    /// <summary>表格行用的紧凑参数行：只保留参数量与体积，量化与适配细节放进状态点提示。</summary>
    public string TableMetaText => IsRemote
        ? $"远程 API · {Model.ApiBaseUrl}"
        : $"{Model.ParamsLabel} · {Formatters.Bytes(SelectedQuant.TotalBytes)}";

    /// <summary>显存/内存吃紧时把建议上下文放进信息行，避免「模型能跑但上下文开太大」。</summary>
    private string ContextAdvice
    {
        get
        {
            if (IsRemote || _recommendation is null) return "";
            return _recommendation.HasContextAdvice
                ? $" · 建议 {_recommendation.SuggestedContextLabel} 上下文"
                : "";
        }
    }

    private string ContextText => Model.ContextLength > 0
        ? $"{Model.ContextLength / 1024}K 上下文"
        : "上下文未知";
    public bool IsChinese => Model.IsChinese;
    public bool IsCustom => Model.IsCustom;

    /// <summary>
    /// 工具调用标注：只有「当前正在运行的模型」才显示，避免把上一个模型的探测结果套到别的卡片上。
    /// 原生 tool_calls 与文本协议（Thought / Action / Action Input）两种模式。
    /// </summary>
    public bool ShowToolModeBadge =>
        !IsRemote && _installed is not null &&
        AppServices.Current.Server.IsRunning &&
        AppServices.Current.Server.LoadedModelPath is { } loaded &&
        loaded.Equals(_installed.PrimaryPath, StringComparison.OrdinalIgnoreCase) &&
        AppServices.Current.ActiveProfile is not null;

    public string ToolModeBadgeText => AppServices.Current.ActiveProfile?.ToolModeText ?? "工具调用：未探测";

    public string ToolModeBadgeTip => AppServices.Current.ActiveProfile is { } profile
        ? $"{profile.ToolModeText}（{profile.ProbeSourceText}）。" +
          (profile.SupportsNativeTools
              ? "模型可直接返回 tool_calls，Agent 按原生方式调用工具。"
              : "模型没有可用的原生工具调用，Agent 改用 Thought / Action / Action Input 文本协议，工具结果以 Observation 回填。")
        : "尚未完成能力探测。";

    public void RefreshProfileBadge()
    {
        OnPropertyChanged(nameof(ShowToolModeBadge));
        OnPropertyChanged(nameof(ToolModeBadgeText));
        OnPropertyChanged(nameof(ToolModeBadgeTip));
    }

    public bool HasQuantChoices => !IsRemote && Model.Quants.Length > 1;

    /// <summary>量化下拉项：每个量化单独评估，直接标注「体积 · 适配度」，避免把某一档的结论套到全部量化。</summary>
    public string[] QuantOptions
    {
        get
        {
            if (IsRemote || Model.Quants.Length == 0) return [];

            var plain = Model.Quants
                .Select(quant => $"{quant.Label} · {Formatters.Bytes(quant.TotalBytes)}")
                .ToArray();
            if (_recommendation is null) return plain;

            var services = AppServices.Current;
            return Model.Quants
                .Select(quant =>
                {
                    var fit = services.Recommender.Evaluate(
                        services.Hardware, services.Performance, Model, quant, services.RecommendationOptions());
                    return $"{quant.Label} · {ShortFit(fit.Fit)}";
                })
                .ToArray();
        }
    }

    /// <summary>下拉框空间有限，用短标签，避免被裁切成单个字符。</summary>
    private static string ShortFit(FitLevel fit) => fit switch
    {
        FitLevel.Perfect => "完整装入",
        FitLevel.Good => "部分卸载",
        FitLevel.Playable => "CPU",
        FitLevel.Tight => "勉强",
        _ => "不足"
    };

    [ObservableProperty] private int _quantIndex;

    public ModelQuant SelectedQuant => IsRemote || Model.Quants.Length == 0
        ? ApiQuant
        : Model.Quants[Math.Clamp(QuantIndex, 0, Model.Quants.Length - 1)];

    partial void OnQuantIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedQuant));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(TableMetaText));
        OnPropertyChanged(nameof(QuantTip));
        OnPropertyChanged(nameof(HasRecommendedQuant));
        RefreshInstalled();
        RefreshPartial();
        UpdateDownloadStatus();

        if (_suppressQuantSync || IsRemote || Model.Quants.Length <= 1) return;
        ApplyCore(EvaluateSelected() ?? _recommendation);
        ScheduleEvaluationInvalidation();
    }

    /// <summary>
    /// 连点量化下拉时合并广播：150 ms 内的多次切换只重算一次推荐排序，避免反复 WMI 采样。
    /// </summary>
    private static void ScheduleEvaluationInvalidation()
    {
        if (_invalidateTimer is null)
        {
            _invalidateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _invalidateTimer.Tick += (_, _) =>
            {
                _invalidateTimer!.Stop();
                AppServices.Current.InvalidateEvaluation("quant");
            };
        }

        _invalidateTimer.Stop();
        _invalidateTimer.Start();
    }

    /// <summary>切换量化后，按当前量化重新评估适配度。</summary>
    private ModelRecommendation? EvaluateSelected()
    {
        if (IsRemote || Model.Quants.Length == 0) return null;
        var services = AppServices.Current;
        return services.Recommender.Evaluate(
            services.Hardware,
            services.Performance,
            Model,
            SelectedQuant,
            services.RecommendationOptions());
    }

    private int FindInstalledQuantIndex()
    {
        if (IsRemote || Model.Quants.Length == 0) return -1;
        var installed = AppServices.Current.InstalledModels
            .Where(model => model.ModelId.Equals(Model.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (installed.Count == 0) return -1;

        return Array.FindIndex(Model.Quants, quant =>
            installed.Any(model => model.QuantLabel.Equals(quant.Label, StringComparison.OrdinalIgnoreCase)));
    }

    private void SelectInstalledQuant()
    {
        if (IsRemote || Model.Quants.Length <= 1) return;
        var index = FindInstalledQuantIndex();
        // 第 0 档是合法结果：旧代码忽略它，已安装的首档模型会被错误地显示成另一个量化。
        if (index >= 0 && index != QuantIndex) SetQuantSilently(index);
    }

    private void SetQuantSilently(int index)
    {
        _suppressQuantSync = true;
        try
        {
            QuantIndex = index;
        }
        finally
        {
            _suppressQuantSync = false;
        }
    }

    public DownloadedModel? Installed => _installed;
    public bool IsInstalled => _installed is not null;

    public string FitLabel { get; private set; } = "未检测";
    public string FitDetail { get; private set; } = "运行“一键检测”后可获得这台电脑的适配结果";
    public string FitTip => _recommendation is null
        ? "尚未完成硬件检测：点「概览」里的“一键检测”后即可看到适配结果。"
        : $"当前量化 {_recommendation.Quant.Label}：{_recommendation.FitLabel}。{_recommendation.FitDescription} {_recommendation.ContextNote}"
          + (HasRecommendedQuant ? $"\n{RecommendedQuantText}（切换后按新量化重新评估，不会自动改你的选择）。" : "")
          + (_recommendation.HasReservedVram
              ? $"\n已扣除其它程序当前占用的显存约 {_recommendation.ReservedVramGb:0.#} GB。"
              : "");

    /// <summary>量化档位的通俗说明，用于下拉框提示。</summary>
    public string QuantTip
    {
        get
        {
            if (IsRemote) return $"远程 API 模型「{Model.ApiModelId}」，不需要量化档位。";
            var label = SelectedQuant.Label;
            return $"{label}：{DescribeQuant(label)}\n同一模型的量化档位数字越大，体积越大、质量越好、速度略慢。";
        }
    }

    public string SpeedTip => _recommendation is null
        ? "预计生成速度 tok/s：每秒大约生成多少个 token。1 个汉字约等于 1–2 个 token，低于 1 时基本不可用。"
        : $"预计生成 {(_recommendation.Speed?.Label ?? _recommendation.SpeedLabel)}；{_recommendation.PrefillLabel}（512 字提示）。1 个汉字约等于 1–2 个 token。";
    public string MetaTip => "依次是参数量、上下文窗口（能一次读入的文本长度）与模型体积；参数越多通常越聪明，但需要的内存也越多。「建议 XK」表示该硬件预算内可稳定运行的最大上下文。";
    public string DescriptionTip => "下载量来自模型社区，代表热度与成熟度，不代表它在这台电脑上的运行速度。";

    private static string DescribeQuant(string label)
    {
        var text = (label ?? "").ToUpperInvariant();
        if (text.Contains("F32")) return "32 位浮点，几乎无损，体积最大";
        if (text.Contains("BF16") || text.Contains("F16")) return "16 位浮点，接近原版质量，体积很大";
        if (text.StartsWith("Q8") || text.Contains("IQ8")) return "8-bit 量化，质量接近原版，体积约翻倍";
        if (text.StartsWith("Q6")) return "6-bit 量化，质量很高，体积偏大";
        if (text.StartsWith("Q5")) return "5-bit 量化，质量高，体积较大";
        if (text.Contains("IQ4")) return "4-bit 进阶量化，体积小、质量不错";
        if (text.StartsWith("Q4")) return "4-bit 量化，速度与质量最均衡（推荐）";
        if (text.Contains("IQ3")) return "3-bit 进阶量化，更省内存，质量有下降";
        if (text.StartsWith("Q3")) return "3-bit 量化，体积更小，质量下降可感知";
        if (text.Contains("IQ2")) return "2-bit 进阶量化，极限压缩，质量明显下降且更慢";
        if (text.StartsWith("Q2")) return "2-bit 量化，极限压缩，质量明显下降";
        if (text.StartsWith("IQ1") || text.StartsWith("Q1")) return "1-bit 极限量化，仅建议用来测试";
        return "该模型提供的量化版本，体积与质量介于 Q4 与 Q8 之间";
    }
    public int SortScore { get; private set; }
    public FitLevel Fit => _recommendation?.Fit ?? FitLevel.Insufficient;
    public bool HasRecommendation => _recommendation is not null;

    public bool IsFitPerfect => _recommendation?.Fit == FitLevel.Perfect;
    public bool IsFitGood => _recommendation?.Fit == FitLevel.Good;
    public bool IsFitPlayable => _recommendation?.Fit == FitLevel.Playable;
    public bool IsFitTight => _recommendation?.Fit == FitLevel.Tight;
    public bool IsFitBad => _recommendation?.Fit == FitLevel.Insufficient;
    public string SpeedText => _recommendation?.SpeedLabel ?? "";

    /// <summary>推荐置信度与结构化理由（P1-21）：让卡片能回答「这个结论有多可靠」。</summary>
    public string ConfidenceBadgeText => _recommendation is null
        ? ""
        : $"置信度 {_recommendation.ConfidenceText}";

    public string ConfidenceTip => _recommendation is null
        ? ""
        : $"{_recommendation.ConfidenceDetail}\n{string.Join('\n', _recommendation.Reasons)}"
          + (_recommendation.Scenarios.Count > 0 ? $"\n适合：{_recommendation.ScenariosText}" : "");

    /// <summary>同模型多量化并排对比（体积 / 速度 / 质量 / 结论）。</summary>
    public bool HasAlternatives => _recommendation?.HasAlternatives == true;

    public string AlternativesText => _recommendation is null
        ? ""
        : string.Join('\n', _recommendation.Alternatives.Take(5).Select(item =>
            $"{item.Label} · {item.SizeGb:0.#} GB · {item.SpeedText} · {item.QualityStars} · {item.FitText} · {item.Conclusion}"));

    /// <summary>可直接复制的启动参数；模型路径由启动器自行拼接。</summary>
    public bool HasLaunchCommand => _recommendation?.Launch is not null;

    public string LaunchCommand => _recommendation?.LaunchCommand ?? "";

    [RelayCommand]
    private async Task CopyLaunchCommandAsync()
    {
        if (_recommendation?.Launch is not { } launch) return;
        var clipboard = App.MainWindow?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(launch.CommandLine);
    }

    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _hasPartial;

    public bool ShowDownloadPanel => !IsRemote && (IsDownloading || HasError || HasPartial);
    public string ActionText => IsRemote
        ? (AppServices.Current.RemoteApi.IsActive && AppServices.Current.RemoteApi.ModelId == Model.ApiModelId
            ? "已连接"
            : "连接")
        : IsInstalled
            ? "启动模型"
            : IsDownloading
                ? "暂停下载"
                : HasError
                    ? "重试下载"
                    : HasPartial
                        ? "继续下载"
                        : "下载模型";
    public bool CanAct => !IsImporting;
    public bool IsImporting { get; private set; }

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        if (IsRemote)
        {
            await _launch(this);
            return;
        }

        if (IsDownloading)
        {
            AppServices.Current.Downloads.Pause(Model.Id);
            return;
        }

        if (_installed is not null)
        {
            await _launch(this);
            return;
        }

        if (_recommendation is not null)
        {
            // 下载前重新采样显存/内存占用：关掉浏览器等占用程序后，标签按当下状态重算。
            AppServices.Current.RefreshBudgetSnapshot();
            ApplyCore(EvaluateSelected() ?? _recommendation);
        }

        await AppServices.Current.Downloads.StartAsync(Model, SelectedQuant);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_installed is not null) ModelLauncher.OpenFolder(_installed.FolderPath);
    }

    [RelayCommand]
    private async Task DeleteCustomAsync()
    {
        if (!Model.IsCustom) return;

        var confirmed = await DialogService.ConfirmAsync(
            "移除自定义模型",
            $"从模型目录中移除「{Title}」？已下载到本地的模型文件不会被删除。",
            "移除");
        if (!confirmed) return;

        AppServices.Current.RemoveCustomModel(Model.Id);
    }

    public void Dispose()
    {
        _state.PropertyChanged -= OnDownloadPropertyChanged;
        if (IsRemote) AppServices.Current.RemoteApi.ActiveChanged -= OnRemoteApiChanged;
    }

    private void OnRemoteApiChanged(object? sender, EventArgs e)
    {
        if (!IsRemote) return;
        Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ActionText)));
    }

    /// <summary>推荐引擎给出的最佳量化档位，仅用于角标 / 提示，不改写用户当前选择。</summary>
    public int RecommendedQuantIndex => _recommendedQuantIndex;

    public string RecommendedQuantText => !IsRemote && _recommendedQuantIndex >= 0 &&
                                          _recommendedQuantIndex < Model.Quants.Length
        ? $"建议 {Model.Quants[_recommendedQuantIndex].Label}"
        : "";

    public bool HasRecommendedQuant => RecommendedQuantText.Length > 0 && _recommendedQuantIndex != QuantIndex;

    public void ApplyBestQuantHint(int index)
    {
        if (_recommendedQuantIndex == index) return;
        _recommendedQuantIndex = index;
        OnPropertyChanged(nameof(RecommendedQuantIndex));
        OnPropertyChanged(nameof(RecommendedQuantText));
        OnPropertyChanged(nameof(HasRecommendedQuant));
        OnPropertyChanged(nameof(FitTip));
    }

    /// <summary>按当前选中的量化重新评估；只读选择，不写回 QuantIndex，避免刷新时把用户选择弹回推荐档。</summary>
    public void Reevaluate() => ApplyCore(EvaluateSelected());

    private void ApplyCore(ModelRecommendation? recommendation)
    {
        _recommendation = recommendation;

        if (recommendation is null)
        {
            FitLabel = "未检测";
            FitDetail = "运行“一键检测”后可获得这台电脑的适配结果";
            SortScore = 0;
        }
        else
        {
            // 标签带上量化档位：不同量化体积差异可达数倍，只写「完美运行」会误导。
            FitLabel = $"{recommendation.Quant.Label} · {recommendation.FitLabel}";
            FitDetail = $"{recommendation.Reason}";
            SortScore = recommendation.SortScore;
        }

        OnPropertyChanged(nameof(QuantOptions));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(TableMetaText));
        OnPropertyChanged(nameof(FitLabel));
        OnPropertyChanged(nameof(FitDetail));
        OnPropertyChanged(nameof(FitTip));
        OnPropertyChanged(nameof(IsFitPerfect));
        OnPropertyChanged(nameof(IsFitGood));
        OnPropertyChanged(nameof(IsFitPlayable));
        OnPropertyChanged(nameof(IsFitTight));
        OnPropertyChanged(nameof(IsFitBad));
        OnPropertyChanged(nameof(Fit));
        OnPropertyChanged(nameof(HasRecommendation));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(SpeedTip));
        OnPropertyChanged(nameof(SortScore));
        OnPropertyChanged(nameof(ConfidenceBadgeText));
        OnPropertyChanged(nameof(ConfidenceTip));
        OnPropertyChanged(nameof(HasAlternatives));
        OnPropertyChanged(nameof(AlternativesText));
        OnPropertyChanged(nameof(HasLaunchCommand));
        OnPropertyChanged(nameof(LaunchCommand));
    }

    /// <summary>由主窗口在库变更后统一刷新，避免每张卡片各自订阅全局事件。</summary>
    public void RefreshLibraryState()
    {
        RefreshInstalled();
        RefreshPartial();
    }

    private void OnDownloadPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        IsDownloading = _state.IsDownloading;
        Progress = _state.Progress;
        HasError = _state.HasError;

        if (e.PropertyName is nameof(DownloadState.IsDownloading)
            or nameof(DownloadState.IsComplete)
            or nameof(DownloadState.Error)
            or nameof(DownloadState.IsPaused))
        {
            RefreshPartial();
        }

        UpdateDownloadStatus();
        OnPropertyChanged(nameof(ShowDownloadPanel));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CanAct));
    }

    private void RefreshPartial()
    {
        if (IsRemote)
        {
            HasPartial = false;
            UpdateDownloadStatus();
            OnPropertyChanged(nameof(ShowDownloadPanel));
            OnPropertyChanged(nameof(ActionText));
            return;
        }

        HasPartial = AppServices.Current.DownloadManager.GetPartialBytes(Model, SelectedQuant) > 0;
        UpdateDownloadStatus();
        OnPropertyChanged(nameof(ShowDownloadPanel));
        OnPropertyChanged(nameof(ActionText));
    }

    private void UpdateDownloadStatus()
    {
        if (_state.HasError)
        {
            DownloadStatus = _state.Error ?? "下载失败";
            return;
        }

        var parts = new[] { _state.Status, _state.SpeedText, _state.EtaText }
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        if (parts.Length > 0)
        {
            DownloadStatus = string.Join(" · ", parts);
        }
        else
        {
            DownloadStatus = HasPartial ? "存在未完成的下载，可从断点继续" : "";
        }
    }

    private void RefreshInstalled()
    {
        _installed = IsRemote ? null : AppServices.Current.FindInstalled(Model.Id, SelectedQuant.Label);
        OnPropertyChanged(nameof(Installed));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CanAct));
    }
}
