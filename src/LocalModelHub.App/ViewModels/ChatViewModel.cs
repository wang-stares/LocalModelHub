using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.App.Services;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;

namespace LocalModelHub.App.ViewModels;

public sealed partial class ChatMessageViewModel : ObservableObject
{
    private ChatMessageViewModel(string role, string text, string reasoning = "")
    {
        Role = role;
        _text = text;
        _reasoning = reasoning;
    }

    public static ChatMessageViewModel UserMessage(string text) => new("user", text);

    public static ChatMessageViewModel AssistantMessage(string text) => new("assistant", text);

    public static ChatMessageViewModel ToolMessage(string text) => new("tool", text);

    public static ChatMessageViewModel Stored(StoredChatMessage message) =>
        new(message.Role, message.Text, message.Reasoning) { SavedToMemory = message.SavedToMemory };

    public string Role { get; }
    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";
    public bool IsTool => Role == "tool";
    public string RoleLabel => IsUser ? "我" : IsTool ? "文件操作" : "本地模型";

    [ObservableProperty] private string _text;
    /// <summary>推理模型的思考过程，单独折叠展示，不混入正文。</summary>
    [ObservableProperty] private string _reasoning;
    [ObservableProperty] private bool _isReasoningExpanded;
    [ObservableProperty] private bool _savedToMemory;

    public bool HasReasoning => !string.IsNullOrEmpty(Reasoning);
    public string ReasoningHeaderText => IsStreamingReasoning
        ? $"思考中 · {Reasoning.Length} 字"
        : $"思考过程 · {Reasoning.Length} 字";
    public bool IsStreamingReasoning => IsEmpty && HasReasoning;
    public bool IsEmpty => string.IsNullOrEmpty(Text);
    public string DisplayText => IsEmpty
        ? HasReasoning ? "已思考，正在整理回答…" : "正在生成…"
        : Text;
    public bool CanSaveToMemory => IsAssistant && !IsEmpty;
    public bool CanSaveAgain => CanSaveToMemory && !SavedToMemory;
    public string SaveMemoryText => SavedToMemory ? "已保存" : "保存到记忆";
    public bool CanRegenerate => IsAssistant;
    public bool CanEdit => IsUser;

    public Action<ChatMessageViewModel>? RegenerateRequested { get; set; }
    public Action<ChatMessageViewModel>? EditRequested { get; set; }

    [RelayCommand]
    private void Regenerate() => RegenerateRequested?.Invoke(this);

    [RelayCommand]
    private void Edit() => EditRequested?.Invoke(this);

    public void Append(string chunk)
    {
        Text += chunk;
        NotifyDerived();
    }

    public void AppendReasoning(string chunk)
    {
        Reasoning += chunk;
        if (!IsReasoningExpanded) IsReasoningExpanded = true;
        NotifyReasoning();
        NotifyDerived();
    }

    public void SetText(string text)
    {
        Text = text;
        NotifyDerived();
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(CanSaveToMemory));
        OnPropertyChanged(nameof(CanSaveAgain));
        OnPropertyChanged(nameof(IsStreamingReasoning));
        OnPropertyChanged(nameof(ReasoningHeaderText));
    }

    private void NotifyReasoning()
    {
        OnPropertyChanged(nameof(HasReasoning));
        OnPropertyChanged(nameof(ReasoningHeaderText));
        OnPropertyChanged(nameof(IsStreamingReasoning));
    }

    partial void OnReasoningChanged(string value) => NotifyReasoning();

    partial void OnSavedToMemoryChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSaveAgain));
        OnPropertyChanged(nameof(SaveMemoryText));
    }

    [RelayCommand]
    private void SaveToMemory()
    {
        if (!CanSaveAgain) return;

        try
        {
            AppServices.Current.Memory.Add(Text, "chat");
            SavedToMemory = true;
        }
        catch
        {
            // 保存失败时保持按钮可再次点击
        }
    }
}

public sealed partial class ChatSessionItemViewModel : ObservableObject
{
    private readonly Action<ChatSessionItemViewModel> _delete;

    public ChatSessionItemViewModel(ChatSession session, Action<ChatSessionItemViewModel> delete)
    {
        Session = session;
        _delete = delete;
    }

    public ChatSession Session { get; }
    public string Title => Session.Title;
    public string TimeText => Session.UpdatedAt.ToString("MM-dd HH:mm");
    public bool IsEmpty => Session.Messages.Count == 0;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Rename(string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return;

        Session.Title = trimmed.Length <= 40 ? trimmed : trimmed[..40];
        Refresh();
    }

    [RelayCommand]
    private void Delete() => _delete(this);
}

public sealed partial class ChatViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;
    private readonly ChatSessionStore _sessions = AppServices.Current.ChatSessions;
    private CancellationTokenSource? _cancellation;
    private bool _suppressSessionChange;
    private bool _isWaking;

    public ChatViewModel()
    {
        _isSessionsCollapsed = _services.Settings.ChatSessionsCollapsed;
        _allowCommands = _services.Settings.AgentAllowCommands;
        MultiAgentEnabled = _services.Settings.AgentMultiAgent;
        _services.Server.StateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshServerState);
        _services.IdleSleep.Slept += OnIdleSlept;
        RefreshServerState();
        RefreshWorkspaceState();
        LoadSessions();
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<ChatSessionItemViewModel> Sessions { get; } = [];
    public string[] Modes { get; } = ["对话", "计划", "目标"];

    public event Action? ScrollRequested;

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _hasMessages;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private string _statusText = "推理服务未运行";
    [ObservableProperty] private string _modelName = "未加载模型";
    [ObservableProperty] private string _workspaceLabel = "未设置工作文件夹，模型无法读写文件";
    [ObservableProperty] private bool _hasWorkspace;
    [ObservableProperty] private string _knowledgeStatus = "";
    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private ChatSessionItemViewModel? _selectedSession;

    /// <summary>会话列表折叠状态：折叠后对话区占满宽度，状态会持久化。</summary>
    [ObservableProperty] private bool _isSessionsCollapsed;

    /// <summary>是否允许 Agent 运行白名单内的构建 / 测试命令，状态会持久化。</summary>
    [ObservableProperty] private bool _allowCommands;

    partial void OnAllowCommandsChanged(bool value)
    {
        if (_services.Settings.AgentAllowCommands == value) return;
        _services.Settings.AgentAllowCommands = value;
        _services.SettingsService.Save();
    }

    /// <summary>目标模式下的多 Agent 协作开关：Planner 规划、Coder 执行、Reviewer 独立评审。</summary>
    [ObservableProperty] private bool _multiAgentEnabled;

    partial void OnMultiAgentEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeHint));
        if (_services.Settings.AgentMultiAgent == value) return;
        _services.Settings.AgentMultiAgent = value;
        _services.SettingsService.Save();
    }

    public GridLength SessionsWidth => IsSessionsCollapsed ? new GridLength(0) : new GridLength(196);
    public GridLength SessionsGap => IsSessionsCollapsed ? new GridLength(0) : new GridLength(16);
    public string SessionsToggleTip => IsSessionsCollapsed ? "显示会话列表" : "隐藏会话列表";

    partial void OnIsSessionsCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(SessionsWidth));
        OnPropertyChanged(nameof(SessionsGap));
        OnPropertyChanged(nameof(SessionsToggleTip));
        _services.Settings.ChatSessionsCollapsed = value;
        _services.SettingsService.Save();
    }

    [RelayCommand]
    private void ToggleSessions() => IsSessionsCollapsed = !IsSessionsCollapsed;

    public AgentMode CurrentMode => (AgentMode)Math.Clamp(ModeIndex, 0, 2);

    public string ModeHint => ModeIndex switch
    {
        1 => HasWorkspace ? "Plan：只读分析工作文件夹，输出实施计划，不改动任何文件" : "Plan 需要先选择工作文件夹",
        2 => HasWorkspace
            ? MultiAgentEnabled
                ? "Goal · 多 Agent：Planner 规划 → Coder 执行 → Reviewer 独立评审，未通过时带问题返工"
                : "Goal：先规划，再读写工作文件夹，完成后自检并按问题修复"
            : "Goal 需要先选择工作文件夹",
        _ => HasWorkspace
            ? "普通对话；提到文件或代码时会自动调用工作区工具"
            : "普通对话，不使用文件工具（选择工作文件夹后可读写文件）"
    };

    public bool CanSend => !IsStreaming && !string.IsNullOrWhiteSpace(Input);
    public bool HasKnowledgeStatus => !string.IsNullOrWhiteSpace(KnowledgeStatus);

    partial void OnKnowledgeStatusChanged(string value) => OnPropertyChanged(nameof(HasKnowledgeStatus));

    partial void OnInputChanged(string value)
    {
        OnPropertyChanged(nameof(CanSend));
        if (string.IsNullOrWhiteSpace(value)) return;
        if (_services.RemoteApi.IsActive) return;

        if (_services.Server.IsRunning)
        {
            _services.IdleSleep.Poke();
            return;
        }

        if (_services.Settings.AutoWake &&
            _services.LastLaunchedModel is not null &&
            !_services.ServerStoppedByUser)
        {
            _ = WakeAsync();
        }
    }

    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(CanSend));

    public void OnModelLaunched(DownloadedModel model)
    {
        _services.RemoteApi.Deactivate();
        ModelName = model.DisplayName;
        _services.LastLaunchedModel = model;
        _services.Settings.LastModelPath = model.PrimaryPath;
        _services.SettingsService.Save();
        _services.IdleSleep.Poke();
        _services.ServerStoppedByUser = false;

        if (SelectedSession is { } item)
        {
            item.Session.ModelName = model.DisplayName;
            item.Refresh();
        }

        RefreshServerState();
    }

    private void LoadSessions()
    {
        foreach (var session in _sessions.Load())
        {
            Sessions.Add(new ChatSessionItemViewModel(session, DeleteSession));
        }

        if (Sessions.Count == 0) CreateSession();
        else SelectSession(Sessions[0]);
    }

    private void CreateSession()
    {
        var now = DateTime.Now;
        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Title = "新对话",
            CreatedAt = now,
            UpdatedAt = now
        };

        var item = new ChatSessionItemViewModel(session, DeleteSession);
        Sessions.Insert(0, item);
        SelectSession(item);
    }

    private void SelectSession(ChatSessionItemViewModel item)
    {
        _suppressSessionChange = true;
        SelectedSession = item;
        _suppressSessionChange = false;
        LoadMessages(item.Session);
    }

    partial void OnSelectedSessionChanged(ChatSessionItemViewModel? value)
    {
        if (_suppressSessionChange || value is null || IsStreaming) return;
        LoadMessages(value.Session);
    }

    private void LoadMessages(ChatSession session)
    {
        Messages.Clear();
        KnowledgeStatus = "";
        foreach (var message in session.Messages)
        {
            var viewModel = ChatMessageViewModel.Stored(message);
            AttachActions(viewModel);
            Messages.Add(viewModel);
        }

        HasMessages = Messages.Count > 0;
        if (!string.IsNullOrWhiteSpace(session.ModelName)) ModelName = session.ModelName;
        ScrollRequested?.Invoke();
    }

    /// <summary>把当前消息写回会话并落盘；首条用户消息自动生成标题。</summary>
    public void SaveCurrentSession() => SyncCurrentSession();

    private void SyncCurrentSession()
    {
        if (SelectedSession is not { } item) return;
        if (Messages.Count == 0) return; // 空会话不写盘，避免启动时产生空记录

        var session = item.Session;
        session.Messages = Messages
            .Select(message => new StoredChatMessage
            {
                Role = message.Role,
                Text = message.Text,
                Reasoning = message.Reasoning,
                SavedToMemory = message.SavedToMemory
            })
            .ToList();
        session.UpdatedAt = DateTime.Now;

        if (session.Title == "新对话")
        {
            var firstUser = session.Messages.FirstOrDefault(message =>
                message.Role == "user" && !string.IsNullOrWhiteSpace(message.Text));
            if (firstUser is not null) item.Rename(Truncate(firstUser.Text, 18));
        }

        _sessions.Upsert(session);
        item.Refresh();

        var index = Sessions.IndexOf(item);
        if (index > 0) Sessions.Move(index, 0);
    }

    private static string Truncate(string text, int maxLength)
    {
        var singleLine = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength] + "…";
    }

    private void DeleteSession(ChatSessionItemViewModel item)
    {
        if (IsStreaming) return;

        _sessions.Remove(item.Session.Id);
        var index = Sessions.IndexOf(item);
        Sessions.Remove(item);

        if (!ReferenceEquals(SelectedSession, item)) return;

        if (Sessions.Count > 0)
        {
            SelectSession(Sessions[Math.Clamp(index, 0, Sessions.Count - 1)]);
        }
        else
        {
            CreateSession();
        }
    }

    [RelayCommand]
    private void NewSession() => CreateSession();

    [RelayCommand]
    private void DeleteCurrentSession()
    {
        if (SelectedSession is { } item) DeleteSession(item);
    }

    /// <summary>连接外部 API 模型后更新对话页状态。</summary>
    public void OnRemoteActivated(string displayName)
    {
        ModelName = displayName;
        _services.ServerStoppedByUser = false;
        if (SelectedSession is { } item)
        {
            item.Session.ModelName = displayName;
            item.Refresh();
        }

        RefreshServerState();
    }

    public void RefreshServerState()
    {
        var remote = _services.RemoteApi;
        var running = _services.Server.IsRunning;
        IsReady = running || remote.IsActive;
        var sleeping = !running && !remote.IsActive && _services.IdleSleep.IsSleeping;
        StatusText = running
            ? $"已连接 127.0.0.1:{_services.Server.Port}"
            : remote.IsActive
                ? $"已连接远程 API · {remote.DisplayName}"
                : sleeping
                    ? "已休眠 · 输入消息自动唤醒"
                    : "推理服务未运行";

        if (running && !string.IsNullOrWhiteSpace(_services.Server.LoadedModelName))
        {
            ModelName = _services.Server.LoadedModelName!;
        }
        else if (remote.IsActive)
        {
            ModelName = remote.DisplayName;
        }
    }

    /// <summary>空闲休眠触发：停止 llama-server，输入时再唤醒。</summary>
    private void OnIdleSlept()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_services.Server.IsRunning) await _services.Server.StopAsync();
            }
            catch
            {
                // 休眠失败时保持当前状态
            }

            RefreshServerState();
        });
    }

    /// <summary>用最近一次启动的模型重新拉起推理服务。</summary>
    public async Task WakeAsync()
    {
        if (_isWaking || _services.Server.IsRunning) return;
        if (_services.LastLaunchedModel is not { } model) return;

        _isWaking = true;
        try
        {
            StatusText = "正在唤醒模型…";
            var runtimeProgress = new Progress<RuntimeProgress>(progress => StatusText = progress.Message);
            var status = new Progress<string>(text => StatusText = text);
            await _services.Launcher.LaunchAsync(model, runtimeProgress, status, CancellationToken.None);

            ModelName = model.DisplayName;
            _services.IdleSleep.Poke();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消唤醒";
            return;
        }
        catch (Exception ex)
        {
            StatusText = $"唤醒失败：{ex.Message}";
            return;
        }
        finally
        {
            _isWaking = false;
        }

        _services.ServerStoppedByUser = false;
        RefreshServerState();
    }

    private void AttachActions(ChatMessageViewModel message)
    {
        message.RegenerateRequested = RegenerateMessage;
        message.EditRequested = EditMessage;
    }

    private List<ChatMessage> BuildHistory() => Messages
        .Where(message => !message.IsTool && !message.IsEmpty)
        .Select(message => new ChatMessage(message.Role, message.Text))
        .ToList();

    private (string Text, int KnowledgeHits) BuildContextAdditions(string query)
    {
        var memoryContext = _services.Settings.MemoryEnabled
            ? _services.Memory.BuildContext(query)
            : "";
        var knowledgeContext = _services.Settings.KnowledgeEnabled
            ? _services.Knowledge.BuildContext(query, Math.Clamp(_services.Settings.KnowledgeTopK, 1, 8))
            : KnowledgeContext.Empty;
        var skillContext = _services.Settings.SkillsEnabled ? _services.Skills.BuildContext(query) : "";
        var customPrompt = _services.Settings.SystemPrompt.Trim();
        var text = string.Join("\n\n", new[] { customPrompt, memoryContext, skillContext, knowledgeContext.Text }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        return (text, knowledgeContext.Hits.Count);
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0 || IsStreaming) return;

        var wantsFiles = FileRequestDetector.Matches(text);
        if (ModeIndex == 0 && !HasWorkspace && wantsFiles)
        {
            var choose = await DialogService.ConfirmAsync(
                "需要工作文件夹",
                "这条消息涉及文件读写。选择一个工作文件夹后，模型就能读取并修改其中的文件。",
                "选择文件夹");
            if (!choose) return;

            await ChooseWorkspaceAsync();
            if (!HasWorkspace) return;
        }

        var fileRequest = ModeIndex == 0 && HasWorkspace && wantsFiles;
        AgentMode? agentMode = ModeIndex > 0 ? CurrentMode : fileRequest ? AgentMode.Goal : null;

        var remote = _services.RemoteApi;
        if (remote.IsActive && agentMode is not null)
        {
            await DialogService.AlertAsync(
                "远程模型不支持文件工具",
                fileRequest
                    ? "这条消息需要读写工作文件夹，请先启动本地模型后再发送。"
                    : "请切换到「对话」模式后再发送，或启动本地模型使用计划 / 目标模式。");
            return;
        }

        if (!remote.IsActive &&
            !_services.Server.IsRunning &&
            _services.Settings.AutoWake &&
            !_services.ServerStoppedByUser &&
            _services.LastLaunchedModel is not null)
        {
            await WakeAsync();
        }

        if (!remote.IsActive && !_services.Server.IsRunning)
        {
            await DialogService.AlertAsync("推理服务未运行", "请先在“我的模型”页面启动一个已下载的模型，或在设置中连接外部 API 模型。");
            return;
        }

        if (agentMode is not null && !HasWorkspace)
        {
            await DialogService.AlertAsync("需要工作文件夹",
                "文件读写需要先选择工作文件夹，可在下方工具栏或设置页选择。");
            return;
        }

        Input = "";
        var remembered = _services.Settings.AutoMemoryEnabled ? RememberFacts(text) : 0;
        var user = ChatMessageViewModel.UserMessage(text);
        AttachActions(user);
        Messages.Add(user);
        if (fileRequest)
        {
            Messages.Add(ChatMessageViewModel.ToolMessage("已识别为文件操作请求 · 自动启用工作区工具"));
        }

        HasMessages = true;
        SyncCurrentSession();

        var history = BuildHistory();
        var (contextAdditions, knowledgeHits) = BuildContextAdditions(text);
        KnowledgeStatus = knowledgeHits > 0 ? $"本次回答引用知识库 {knowledgeHits} 个片段" : "";
        if (remembered > 0)
        {
            KnowledgeStatus = KnowledgeStatus.Length > 0
                ? $"已自动记忆 {remembered} 条 · {KnowledgeStatus}"
                : $"已自动记忆 {remembered} 条偏好";
        }

        if (ModeIndex == 0 && contextAdditions.Length > 0)
        {
            history.Insert(0, ChatMessage.System(contextAdditions));
        }

        var assistant = ChatMessageViewModel.AssistantMessage("");
        AttachActions(assistant);
        Messages.Add(assistant);
        ScrollRequested?.Invoke();

        await RunAssistantAsync(history, text, assistant, Messages.Count - 1, contextAdditions, agentMode);
    }

    private int RememberFacts(string text)
    {
        var count = 0;
        foreach (var fact in AutoMemoryExtractor.Extract(text))
        {
            if (_services.Memory.Contains(fact)) continue;
            _services.Memory.Add(fact, "chat");
            count++;
        }

        return count;
    }

    /// <summary>把生命周期阶段的内部名称换成用户能看懂的标签。</summary>
    private static string ActivityLabel(string name) => name switch
    {
        "plan" => "计划",
        "planner" => "规划",
        "verify" => "自检",
        "review" => "评审",
        "repair" => "修复",
        "localize" => "定位",
        "search_code" => "搜索",
        "find_symbols" => "符号",
        "apply_patch" => "补丁",
        "run_command" => "运行",
        _ => name
    };

    /// <summary>任务完成后把「请求 + 工具步骤」沉淀成技能；没有实际动作时不写入。</summary>
    private void MaybeSaveSkill(string request, IEnumerable<(string Name, string Summary)> activities, int insertIndex)
    {
        if (!_services.Settings.AutoSkillEnabled) return;

        var draft = TaskSkillSummarizer.Summarize(request, activities);
        if (draft is null) return;

        var entry = _services.Skills.Add(draft, "auto");
        Messages.Insert(insertIndex, ChatMessageViewModel.ToolMessage($"已沉淀技能 · {entry.Name}"));
        HasMessages = true;
        ScrollRequested?.Invoke();
    }

    private async Task RunAssistantAsync(
        IReadOnlyList<ChatMessage> history,
        string request,
        ChatMessageViewModel assistant,
        int assistantIndex,
        string contextAdditions,
        AgentMode? agentMode = null)
    {
        _services.IdleSleep.Stop();
        IsStreaming = true;
        _cancellation = new CancellationTokenSource();
        try
        {
            if (agentMode is not null)
            {
                void OnActivity(ToolActivity activity)
                {
                    Messages.Insert(assistantIndex, ChatMessageViewModel.ToolMessage(
                        $"{ActivityLabel(activity.Name)} · {activity.Summary}"));
                    assistantIndex++;
                    HasMessages = true;
                    ScrollRequested?.Invoke();
                }

                string resultText;
                IReadOnlyList<ToolActivity> resultActivities;
                if (agentMode.Value == AgentMode.Goal && MultiAgentEnabled)
                {
                    var coordinator = new MultiAgentCoordinator(
                        _services.Server,
                        _services.Workspace,
                        _services.Memory,
                        _services.Settings.MemoryAllowModelWrites,
                        _services.Settings.AgentAllowCommands);
                    var multi = await coordinator.RunAsync(
                        history, contextAdditions, OnActivity, _cancellation.Token);
                    resultText = multi.Text;
                    resultActivities = multi.Coder?.Activities ?? [];
                    if (resultActivities.Count == 0)
                    {
                        Messages.Insert(assistantIndex, ChatMessageViewModel.ToolMessage(
                            "协作已完成 · Coder 没有调用文件工具，若结果涉及改动请检查工作文件夹"));
                        assistantIndex++;
                    }
                }
                else
                {
                    var agent = new WorkspaceAgent(
                        _services.Server,
                        _services.Workspace,
                        _services.Memory,
                        agentMode.Value,
                        _services.Settings.MemoryAllowModelWrites,
                        _services.Settings.AgentAllowCommands);
                    var result = await agent.RunAsync(history, contextAdditions, OnActivity, _cancellation.Token);
                    resultText = result.Text;
                    resultActivities = result.Activities;

                    if (result.Activities.Count == 0)
                    {
                        Messages.Insert(assistantIndex, ChatMessageViewModel.ToolMessage(
                            "本次没有调用文件工具 · 若回答里没有真实读写文件，可能是模型太小或该模型不支持工具调用"));
                        assistantIndex++;
                    }
                }

                assistant.SetText(string.IsNullOrWhiteSpace(resultText) ? "（模型没有返回内容）" : resultText);
                MaybeSaveSkill(request, resultActivities.Select(item => (item.Name, item.Summary)), assistantIndex);
            }
            else
            {
                var options = new ChatOptions(
                    Math.Clamp(_services.Settings.Temperature, 0, 2),
                    Math.Clamp(_services.Settings.TopP, 0, 1),
                    Math.Clamp(_services.Settings.MaxTokens, 64, 8192));

                await foreach (var chunk in _services.RemoteApi.IsActive
                                   ? _services.RemoteApi.ChatStreamAsync(history, options, _cancellation.Token)
                                   : _services.Server.ChatStreamAsync(history, options, _cancellation.Token))
                {
                    if (!string.IsNullOrEmpty(chunk.Reasoning)) assistant.AppendReasoning(chunk.Reasoning);
                    if (!string.IsNullOrEmpty(chunk.Content)) assistant.Append(chunk.Content);
                    ScrollRequested?.Invoke();
                }

                if (assistant.IsEmpty) assistant.SetText("（模型没有返回内容）");
            }
        }
        catch (OperationCanceledException)
        {
            if (assistant.IsEmpty) assistant.SetText("（已停止生成）");
            else assistant.Append("\n\n（已停止生成）");
        }
        catch (Exception ex)
        {
            assistant.SetText($"生成失败：{ex.Message}");
        }
        finally
        {
            IsStreaming = false;
            _cancellation.Dispose();
            _cancellation = null;
            SyncCurrentSession();
            if (_services.Server.IsRunning) _services.IdleSleep.Poke();
            ScrollRequested?.Invoke();
        }
    }

    private async void RegenerateMessage(ChatMessageViewModel message) => await RegenerateAsync(message);

    private async Task RegenerateAsync(ChatMessageViewModel message)
    {
        if (IsStreaming || !message.IsAssistant) return;

        var index = Messages.IndexOf(message);
        if (index <= 0) return;

        // 跳过工具过程消息，找到这条回答对应的用户请求。
        var userIndex = index - 1;
        while (userIndex >= 0 && Messages[userIndex].IsTool) userIndex--;
        if (userIndex < 0 || !Messages[userIndex].IsUser) return;

        while (Messages.Count > index) Messages.RemoveAt(Messages.Count - 1);

        var query = Messages[userIndex].Text;
        var fileRequest = ModeIndex == 0 && HasWorkspace && FileRequestDetector.Matches(query);
        AgentMode? agentMode = ModeIndex > 0 ? CurrentMode : fileRequest ? AgentMode.Goal : null;
        if (agentMode is not null && _services.RemoteApi.IsActive)
        {
            await DialogService.AlertAsync("远程模型不支持文件工具", "请先启动本地模型后再重新生成这条消息。");
            return;
        }

        var history = BuildHistory();
        var (contextAdditions, knowledgeHits) = BuildContextAdditions(query);
        KnowledgeStatus = knowledgeHits > 0 ? $"本次回答引用知识库 {knowledgeHits} 个片段" : "";
        if (ModeIndex == 0 && contextAdditions.Length > 0)
        {
            history.Insert(0, ChatMessage.System(contextAdditions));
        }

        var assistant = ChatMessageViewModel.AssistantMessage("");
        AttachActions(assistant);
        Messages.Add(assistant);
        ScrollRequested?.Invoke();

        await RunAssistantAsync(history, query, assistant, Messages.Count - 1, contextAdditions, agentMode);
    }

    private void EditMessage(ChatMessageViewModel message)
    {
        if (IsStreaming || !message.IsUser) return;

        var index = Messages.IndexOf(message);
        if (index < 0) return;

        Input = message.Text;
        while (Messages.Count > index) Messages.RemoveAt(Messages.Count - 1);
        HasMessages = Messages.Count > 0;

        if (Messages.Count == 0 && SelectedSession is { } item)
        {
            item.Session.Messages.Clear();
            item.Session.UpdatedAt = DateTime.Now;
            _sessions.Upsert(item.Session);
            item.Refresh();
        }
        else
        {
            SyncCurrentSession();
        }

        ScrollRequested?.Invoke();
    }

    [RelayCommand]
    private void Stop()
    {
        _cancellation?.Cancel();
    }

    [RelayCommand]
    private void Clear()
    {
        Messages.Clear();
        HasMessages = false;
        KnowledgeStatus = "";

        if (SelectedSession is { } item)
        {
            item.Session.Messages.Clear();
            item.Session.UpdatedAt = DateTime.Now;
            _sessions.Upsert(item.Session);
            item.Refresh();
        }
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        if (Messages.Count == 0)
        {
            await DialogService.AlertAsync("导出对话", "当前会话还没有消息。");
            return;
        }

        SyncCurrentSession();
        var title = SelectedSession?.Session.Title ?? "对话";
        var safeName = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = await FileDialogService.PickSaveFileAsync("导出对话", $"{safeName}.md", "Markdown 文件", "*.md");
        if (path is null) return;

        try
        {
            await File.WriteAllTextAsync(path, BuildMarkdown());
            await DialogService.AlertAsync("导出成功", $"当前会话已导出到：\n{path}");
        }
        catch (Exception ex)
        {
            await DialogService.AlertAsync("导出失败", ex.Message);
        }
    }

    private string BuildMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {SelectedSession?.Session.Title ?? "对话"}").AppendLine();
        builder.AppendLine($"- 模型：{ModelName}");
        builder.AppendLine($"- 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine();

        foreach (var message in Messages)
        {
            builder.AppendLine(message.IsTool ? "## 文件操作" : message.IsUser ? "## 我" : "## 本地模型");
            builder.AppendLine();
            if (!string.IsNullOrEmpty(message.Reasoning))
            {
                builder.AppendLine("<details>");
                builder.AppendLine("<summary>思考过程</summary>");
                builder.AppendLine();
                builder.AppendLine(message.Reasoning);
                builder.AppendLine();
                builder.AppendLine("</details>");
                builder.AppendLine();
            }

            builder.AppendLine(message.Text);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    [RelayCommand]
    private async Task StopServerAsync()
    {
        _services.RemoteApi.Deactivate();
        _services.ServerStoppedByUser = true;
        _services.IdleSleep.Stop();
        await _services.Server.StopAsync();
        RefreshServerState();
    }

    [RelayCommand]
    private void OpenWebUi()
    {
        if (_services.Server.IsRunning)
        {
            ModelLauncher.OpenUrl(_services.Server.WebUiUrl);
        }
    }

    [RelayCommand]
    private async Task ChooseWorkspaceAsync()
    {
        var folder = await FileDialogService.PickFolderAsync("选择工作文件夹");
        if (folder is null) return;

        _services.Settings.WorkspaceDirectory = folder;
        _services.SettingsService.Save();
        _services.Workspace.SetRoot(folder);
        RefreshWorkspaceState();
    }

    [RelayCommand]
    private void OpenWorkspace()
    {
        if (_services.Workspace.Root is { } root && Directory.Exists(root))
        {
            ModelLauncher.OpenFolder(root);
        }
    }

    public void RefreshWorkspaceState()
    {
        HasWorkspace = _services.Workspace.IsConfigured;
        WorkspaceLabel = HasWorkspace
            ? $"工作文件夹：{_services.Workspace.Root}"
            : "未设置工作文件夹，模型无法读写文件";
        ModeIndex = (int)ParseMode(_services.Settings.AgentMode);
    }

    partial void OnModeIndexChanged(int value)
    {
        var key = ModeKey(CurrentMode);
        if (_services.Settings.AgentMode != key)
        {
            _services.Settings.AgentMode = key;
            _services.SettingsService.Save();
        }

        OnPropertyChanged(nameof(CurrentMode));
        OnPropertyChanged(nameof(ModeHint));
    }

    partial void OnHasWorkspaceChanged(bool value) => OnPropertyChanged(nameof(ModeHint));

    private static AgentMode ParseMode(string? value) => value switch
    {
        "plan" => AgentMode.Plan,
        // 旧版本保存过 "space"，合并后按「目标」模式恢复。
        "space" or "goal" => AgentMode.Goal,
        _ => AgentMode.Chat
    };

    private static string ModeKey(AgentMode mode) => mode switch
    {
        AgentMode.Plan => "plan",
        AgentMode.Goal => "goal",
        _ => "chat"
    };
}
