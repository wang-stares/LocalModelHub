using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 把模型发出的工具调用限制在工作文件夹内执行，并按 Plan / Goal 模式约束工具权限。
/// 目标模式跑完整的生命周期：规划（Planner）→ 执行（Executor）→ 验收（Verifier）→ 按问题修复，
/// 每一步都记录 Thought–Action–Result 轨迹，交给评估器打分。
/// </summary>
public sealed class WorkspaceAgent
{
    private const int MaxTokens = 2048;
    private const int PlanTokens = 512;
    private const int VerifyTokens = 700;
    private const int MaxObservationChars = 400;

    private static readonly ToolDefinition ListTool = new(
        "list_files", "列出工作文件夹中的文件（最多 3 层、300 项）。",
        Schema("""{"type":"object","properties":{"path":{"type":"string","description":"相对目录，默认 ."}},"required":[]}"""));

    private static readonly ToolDefinition SearchTool = new(
        "search_code", "按正则或字面量搜索工作区代码，返回 路径:行号:片段（不区分大小写，跳过 .git / node_modules 等目录）。",
        Schema("""{"type":"object","properties":{"query":{"type":"string","description":"正则或字面量"},"include":{"type":"string","description":"可选文件过滤，如 *.cs"},"max_results":{"type":"integer","description":"默认 24，最大 40"},"context_lines":{"type":"integer","description":"命中行前后展开的行数，默认 0，最大 3；开启后结果上限 12"}},"required":["query"]}"""));

    private static readonly ToolDefinition FindSymbolTool = new(
        "find_symbols", "按类 / 接口 / 函数 / 方法名查找定义，返回 路径:行号:声明（比全文搜索少噪音）。",
        Schema("""{"type":"object","properties":{"query":{"type":"string","description":"符号名的字面量片段，不区分大小写"},"include":{"type":"string","description":"可选文件过滤，如 *.cs"},"max_results":{"type":"integer","description":"默认 20，最大 40"}},"required":["query"]}"""));

    private static readonly ToolDefinition ReadTool = new(
        "read_file", "按行窗口读取工作区文本文件，返回带行号的内容；默认 200 行，可用 start_line / end_line 继续读取。",
        Schema("""{"type":"object","properties":{"path":{"type":"string","description":"相对文件路径"},"start_line":{"type":"integer","description":"起始行，默认 1"},"end_line":{"type":"integer","description":"结束行，默认起始行 + 199"}},"required":["path"]}"""));

    private static readonly ToolDefinition WriteTool = new(
        "write_file", "在工作文件夹内创建或整体覆盖文本文件，最大 512 KB。",
        Schema("""{"type":"object","properties":{"path":{"type":"string","description":"相对文件路径"},"content":{"type":"string","description":"完整文件内容"}},"required":["path","content"]}"""));

    private static readonly ToolDefinition ReplaceTool = new(
        "replace_in_file", "把文件中的 old_text 全部替换为 new_text，需要精确匹配。",
        Schema("""{"type":"object","properties":{"path":{"type":"string","description":"相对文件路径"},"old_text":{"type":"string"},"new_text":{"type":"string"}},"required":["path","old_text","new_text"]}"""));

    private static readonly ToolDefinition ApplyTool = new(
        "apply_patch", "应用统一 diff（多个 hunk / 多个文件）；上下文行必须精确匹配，失败时不会写入任何文件。",
        Schema("""{"type":"object","properties":{"patch":{"type":"string","description":"统一 diff 文本，包含 --- a/...、+++ b/... 与 @@ -1,3 +1,3 @@ 头"}},"required":["patch"]}"""));

    private static readonly ToolDefinition RememberTool = new(
        "remember", "把用户偏好或重要事实写入本机长期记忆，之后的对话可以参考。",
        Schema("""{"type":"object","properties":{"text":{"type":"string","description":"要记住的事实或偏好，尽量简洁"}},"required":["text"]}"""));

    private static readonly ToolDefinition RunCommandTool = new(
        "run_command", "运行白名单内的构建 / 测试命令（dotnet test、npm test、pytest、go test、cargo test 等），工作目录为工作文件夹。",
        Schema("""{"type":"object","properties":{"command":{"type":"string","description":"完整命令，例如 dotnet test"}},"required":["command"]}"""));

    private static readonly ToolDefinition[] ReadOnlyTools = [ListTool, SearchTool, FindSymbolTool, ReadTool];
    private static readonly ToolDefinition[] FullTools =
        [ListTool, SearchTool, FindSymbolTool, ReadTool, WriteTool, ReplaceTool, ApplyTool];

    private readonly IChatCompletion _server;
    private readonly WorkspaceService _workspace;
    private readonly MemoryService _memory;
    private readonly AgentMode _mode;
    private readonly bool _allowMemoryWrites;
    private readonly bool _allowCommands;
    private readonly ICommandRunner _commands;
    private readonly bool _textToolProtocol;
    private readonly bool _selfVerify;

    /// <param name="textToolProtocol">模型不支持原生 tool_calls 时改走 Thought / Action / Action Input 文本协议。</param>
    /// <param name="selfVerify">是否在执行后自检；多 Agent 协作时由外层 Reviewer 负责，传 false 避免双重评审。</param>
    public WorkspaceAgent(
        IChatCompletion server,
        WorkspaceService workspace,
        MemoryService memory,
        AgentMode mode,
        bool allowMemoryWrites,
        bool allowCommands = false,
        ICommandRunner? commands = null,
        bool textToolProtocol = false,
        bool selfVerify = true)
    {
        _server = server;
        _workspace = workspace;
        _memory = memory;
        _mode = mode;
        _allowMemoryWrites = allowMemoryWrites;
        _allowCommands = allowCommands;
        _commands = commands ?? new CommandService();
        _textToolProtocol = textToolProtocol;
        _selfVerify = selfVerify;
    }

    /// <summary>各模式可用的工具名，便于测试与展示。</summary>
    public static IReadOnlyList<string> ToolNames(
        AgentMode mode,
        bool allowMemory = true,
        bool allowCommands = false) =>
        ToolsFor(mode, allowMemory, allowCommands).Select(tool => tool.Name).ToArray();

    public async Task<AgentResult> RunAsync(
        IReadOnlyList<ChatMessage> history,
        string? memoryContext,
        Action<ToolActivity>? onActivity,
        CancellationToken cancellationToken,
        AgentPlan? initialPlan = null,
        Action<string>? onDelta = null)
    {
        if (!_workspace.IsConfigured) throw new InvalidOperationException("请先设置工作文件夹。");

        var systemPrompt = BuildSystemPrompt(_workspace.Root!, _mode, _allowCommands);
        if (!string.IsNullOrWhiteSpace(memoryContext))
        {
            systemPrompt += "\n\n" + memoryContext;
        }

        if (_allowMemoryWrites && _mode != AgentMode.Chat)
        {
            systemPrompt += "\n如果用户表达了长期偏好或重要事实，可调用 remember 工具保存到长期记忆。";
        }

        var messages = new List<ChatMessage> { ChatMessage.System(systemPrompt) };
        messages.AddRange(history.Where(message =>
            message.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(message.Content)));

        var request = history
            .LastOrDefault(message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.Content))
            ?.Content.Trim() ?? "";

        var tools = ToolsFor(_mode, _allowMemoryWrites, _allowCommands);
        var toolNames = tools.Select(tool => tool.Name).ToArray();

        var contextTokens = _server.ContextTokens > 0 ? _server.ContextTokens : 8192;
        // 单条工具结果最多占上下文的 1/8（8k 上下文约 1000 token），超出时保留头尾并给出续读指令。
        var maxObservationTokens = Math.Max(400, contextTokens / 8);
        var answer = new StringBuilder();
        var activities = new List<ToolActivity>();
        var steps = new List<AgentStep>();

        // 文本协议：告诉模型工具清单与调用格式，同时不再发送 tools 字段（模板不支持会返回空 tool_calls）。
        if (_textToolProtocol && tools.Count > 0)
        {
            // 替换已加入 messages 的 system 消息：ChatMessage 是 record 副本，只改局部字符串模型是收不到的。
            messages[0] = ChatMessage.System(
                systemPrompt + "\n\n" + AgentProtocol.BuildTextToolPrompt(tools));
            tools = [];
            var protocol = new ToolActivity("protocol", "文本协议 · 模型未启用原生工具调用");
            activities.Add(protocol);
            onActivity?.Invoke(protocol);
        }
        // 写操作强去重（整个运行期内不再重复执行）；只读调用单独缓存，写成功后失效，
        // 否则「写后复核」的 read_file 会被误判为重复调用。
        var seenWrites = new HashSet<string>(StringComparer.Ordinal);
        var seenReads = new HashSet<string>(StringComparer.Ordinal);
        // 已成功读取的文件：修改既有文件前必须命中，避免小模型不看原文直接覆盖。
        var readPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 0）关键词定位：先按请求在工作区找候选文件，把线索放进上下文，避免小模型盲搜。
        var localization = _mode is AgentMode.Plan or AgentMode.Goal && request.Length > 0
            ? await Task.Run(() => AgentLocalizer.Localize(_workspace.Root, request), cancellationToken)
            : AgentLocalization.Empty;
        if (localization.Files.Count > 0)
        {
            messages.Add(ChatMessage.System($"{AgentProtocol.ContextMarker}\n{localization.Format()}"));
            var activity = new ToolActivity(
                "localize", $"{localization.Files.Count} 个候选文件 · {localization.Summary()}");
            activities.Add(activity);
            onActivity?.Invoke(activity);
        }

        // 1）规划：目标模式先用一次短调用产出结构化计划，注入上下文后再执行。
        // 多 Agent 协作时 Planner 已经给出计划，直接复用，避免重复规划。
        var plan = initialPlan is { HasSteps: true }
            ? initialPlan
            : _mode == AgentMode.Goal
                ? await PlanAsync(messages, request, steps, activities, onActivity, cancellationToken)
                : AgentPlan.Empty;
        if (plan.HasSteps)
        {
            messages.Add(ChatMessage.System($"{AgentProtocol.PlanMarker}\n{AgentProtocol.FormatPlan(plan)}"));
        }

        // 2）执行：所有工具轮次共用一份预算，重复调用在执行层直接拦截。
        var actBudget = RoundsFor(_mode);
        var actUsed = 0;
        actUsed += await ActAsync();

        // 3）验收：只依据工具轨迹判断；未通过时给一次修复机会，再重新验收。
        // 多 Agent 协作时外层 Reviewer 负责评审，这里跳过自检，避免两套口径互相矛盾。
        AgentVerification? verification = null;
        if (_mode == AgentMode.Goal && actUsed > 0 && _selfVerify)
        {
            verification = await VerifyAsync(request, plan, steps, answer.ToString(), cancellationToken);
            if (!verification.Passed && verification.Verified && actBudget - actUsed > 0)
            {
                var repair = new ToolActivity("repair", "自检未通过 · 按问题继续修复");
                activities.Add(repair);
                onActivity?.Invoke(repair);

                messages.Add(ChatMessage.User(AgentProtocol.BuildRepairPrompt(verification)));
                actUsed += await ActAsync();
                verification = await VerifyAsync(request, plan, steps, answer.ToString(), cancellationToken);
            }
        }

        // 4）评估与轨迹：分数用于解释质量，不改变回答本身，除非验收未通过需要标注。
        var evaluation = AgentEvaluator.Evaluate(steps, verification);
        var text = answer.ToString().Trim();
        if (text.Length == 0) text = "（已执行工具调用，但模型没有返回文字总结）";

        // 计划模式不执行验收调用，只做结构完整性检查，缺项直接在界面标出。
        if (_mode == AgentMode.Plan && answer.Length > 0)
        {
            var missing = AgentProtocol.MissingPlanSections(answer.ToString());
            var activity = new ToolActivity(
                "verify",
                missing.Count == 0 ? "结构完整 · 目标 / 步骤 / 风险 / 验收" : $"缺少：{string.Join("、", missing)}");
            activities.Add(activity);
            onActivity?.Invoke(activity);
        }

        if (verification is { Passed: false })
        {
            text += $"\n\n（自检未通过：{verification.Summary}）";
        }

        if (verification is not null)
        {
            steps.Add(new AgentStep(
                AgentStage.Verify, 0, "", "verify", "", Truncate(verification.Summary, MaxObservationChars),
                verification.Passed));
            var issues = !verification.Passed && verification.Issues.Count > 0
                ? $" · {verification.Issues.Count} 个问题"
                : "";
            var activity = new ToolActivity(
                "verify",
                $"{verification.OutcomeLabel} · 质量分 {evaluation.Score}{issues}");
            activities.Add(activity);
            onActivity?.Invoke(activity);
        }

        var trajectory = new AgentTrajectory(
            request, _mode, plan, steps, verification, evaluation.Score, evaluation.Detail);
        return new AgentResult(text, activities, verification, trajectory);

        async Task<int> ActAsync()
        {
            var used = 0;
            var roundOffset = actUsed;
            var overflowRetries = 0;
            var timeoutRetries = 0;
            while (used < actBudget - actUsed)
            {
                ContextBudget.Compact(messages, contextTokens, MaxTokens);

                ChatTurn turn;
                try
                {
                    // 流式：工具轮次的正文也会实时回传，最终回答由调用方覆盖为完整文本。
                    turn = await _server.ChatAsync(
                        messages, tools, MaxTokens, new ChatRequestOptions(OnDelta: onDelta), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    switch (AgentFailure.Classify(ex))
                    {
                        case FailureKind.ContextOverflow when overflowRetries < 2:
                            overflowRetries++;
                            ContextBudget.Compact(messages, contextTokens, MaxTokens, aggressive: true);
                            Report("budget", $"上下文超限 · 已压缩历史后重试（{overflowRetries}/2）");
                            continue; // 不消耗轮次
                        case FailureKind.Timeout when timeoutRetries < 1:
                            timeoutRetries++;
                            Report("retry", "推理超时 · 重试一次");
                            continue; // 不消耗轮次
                        case FailureKind.ServerDown:
                            throw new InvalidOperationException("推理服务无响应，请重新启动模型后重试。", ex);
                        default:
                            throw;
                    }
                }

                // 文本协议：模型把工具调用写在正文里，解析成 ToolCall 后按同一执行路径处理；
                // 工具轮次的正文不进入最终回答，避免 "Action: ..." 混进给用户看的结论。
                if (_textToolProtocol && turn.ToolCalls.Count == 0 && turn.Content.Length > 0)
                {
                    turn.ToolCalls.AddRange(AgentProtocol.ParseTextToolCalls(turn.Content, toolNames));
                }

                var isFinalRound = turn.ToolCalls.Count == 0;
                if (!string.IsNullOrWhiteSpace(turn.Content) && (isFinalRound || !_textToolProtocol))
                {
                    var text = _textToolProtocol ? StripFinalAnswerMarker(turn.Content) : turn.Content.Trim();
                    if (text.Length > 0)
                    {
                        if (answer.Length > 0) answer.AppendLine();
                        answer.Append(text);
                    }
                }

                if (isFinalRound) break;

                messages.Add(_textToolProtocol
                    ? new ChatMessage("assistant", turn.Content)
                    : new ChatMessage("assistant", turn.Content) { ToolCalls = turn.ToolCalls });
                var thought = FirstLine(turn.Content, 120);

                foreach (var call in turn.ToolCalls)
                {
                    var (result, summary, success) = await ExecuteAsync(
                        call, seenWrites, seenReads, readPaths, cancellationToken);
                    var (readPath, readStartLine) = ReadWindow(call);
                    var observation = ContextBudget.TrimObservation(
                        result, maxObservationTokens, readPath, readStartLine);
                    messages.Add(_textToolProtocol
                        ? ChatMessage.User($"Observation: {observation}")
                        : new ChatMessage("tool", observation) { ToolCallId = call.Id });

                    // 参数保留完整 JSON：评估器要靠 path 判断「读了再改」，截断会让证据核对失效。
                    steps.Add(new AgentStep(
                        AgentStage.Act, roundOffset + used + 1, thought, call.Name, call.Arguments ?? "{}",
                        Truncate(result, MaxObservationChars), success));

                    Report(call.Name, summary);
                }

                used++;
            }

            return used;
        }

        void Report(string name, string summary)
        {
            var activity = new ToolActivity(name, summary);
            activities.Add(activity);
            onActivity?.Invoke(activity);
        }
    }

    /// <summary>规划阶段失败不应拖垮任务：解析不出计划时按直接执行处理。</summary>
    private async Task<AgentPlan> PlanAsync(
        List<ChatMessage> messages,
        string request,
        List<AgentStep> steps,
        List<ToolActivity> activities,
        Action<ToolActivity>? onActivity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request)) return AgentPlan.Empty;

        try
        {
            var planMessages = new List<ChatMessage>(messages)
            {
                ChatMessage.System(AgentProtocol.BuildPlanSystemPrompt()),
                ChatMessage.User(AgentProtocol.BuildPlanRequest(request))
            };
            ContextBudget.Compact(planMessages, _server.ContextTokens, PlanTokens);
            var turn = await _server.ChatAsync(planMessages, null, PlanTokens, cancellationToken);
            var plan = AgentProtocol.ParsePlan(turn.Content);

            steps.Add(new AgentStep(
                AgentStage.Plan, 0, FirstLine(turn.Content, 120), "plan", "",
                Truncate(plan.Raw, MaxObservationChars), plan.HasSteps));
            var activity = new ToolActivity(
                "plan",
                plan.HasSteps
                    ? $"{plan.Steps.Count} 步 · {FirstLine(plan.Steps[0], 70)}"
                    : "未给出结构化计划 · 按直接执行处理");
            activities.Add(activity);
            onActivity?.Invoke(activity);
            return plan;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var activity = new ToolActivity("plan", $"已跳过 · {FirstLine(ex.Message, 60)}");
            activities.Add(activity);
            onActivity?.Invoke(activity);
            return AgentPlan.Empty;
        }
    }

    /// <summary>
    /// 验收阶段使用独立上下文：只带请求、计划、工具轨迹与回答摘要，
    /// 不复制完整的工具结果，避免把大文件内容再塞进一次上下文。
    /// </summary>
    private async Task<AgentVerification> VerifyAsync(
        string request,
        AgentPlan plan,
        IReadOnlyList<AgentStep> steps,
        string answer,
        CancellationToken cancellationToken)
    {
        try
        {
            var verifyMessages = new List<ChatMessage>
            {
                ChatMessage.System($"{AgentProtocol.VerifyMarker} 你是验收角色，只依据工具轨迹与证据判断，不补充臆测内容。"),
                ChatMessage.User(AgentProtocol.BuildVerifyPrompt(request, plan, steps) +
                                 $"\n\n当前回答：\n{Truncate(answer, 2000)}")
            };
            // 约束解码：要求后端只输出 JSON 对象，减少「找第一个 { 到最后一个 }」的兜底解析。
            var turn = await _server.ChatAsync(
                verifyMessages, null, VerifyTokens,
                new ChatRequestOptions(JsonMode: true, JsonSchema: AgentProtocol.VerificationJsonSchema),
                cancellationToken);
            var verification = AgentProtocol.ParseVerification(turn.Content);
            if (verification.Source != "text") return verification;

            // 后端不支持 json_schema（或模型不守约束）时，用缩小版 schema 重问一次；
            // 仍解析不出 JSON 就保留文本兜底结论，不额外增加第三次调用。
            var retry = await _server.ChatAsync(
                [
                    ChatMessage.System($"{AgentProtocol.VerifyMarker} 只输出 JSON 对象，不要输出解释或代码块。"),
                    ChatMessage.User(AgentProtocol.BuildCompactVerifyPrompt(request, verification))
                ],
                null, VerifyTokens,
                new ChatRequestOptions(
                    JsonMode: true,
                    JsonSchema: AgentProtocol.CompactVerificationJsonSchema,
                    Temperature: 0.1),
                cancellationToken);
            var retried = AgentProtocol.ParseVerification(retry.Content);
            return retried.Source == "json" ? retried : verification;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AgentVerification.Skipped($"自检调用失败：{FirstLine(ex.Message, 80)}");
        }
    }

    private static IReadOnlyList<ToolDefinition> ToolsFor(
        AgentMode mode,
        bool allowMemory,
        bool allowCommands = false)
    {
        var baseTools = mode switch
        {
            AgentMode.Plan => ReadOnlyTools,
            AgentMode.Chat => [],
            _ => FullTools
        };

        var tools = new List<ToolDefinition>(baseTools);
        if (allowMemory && mode != AgentMode.Chat) tools.Add(RememberTool);
        if (allowCommands && mode == AgentMode.Goal) tools.Add(RunCommandTool);
        return tools;
    }

    private static int RoundsFor(AgentMode mode) => mode switch
    {
        AgentMode.Plan => 4,
        AgentMode.Goal => 10,
        _ => 4
    };

    private async Task<(string Result, string Summary, bool Success)> ExecuteAsync(
        ToolCall call,
        HashSet<string> seenWrites,
        HashSet<string> seenReads,
        HashSet<string> readPaths,
        CancellationToken cancellationToken)
    {
        var path = ".";
        try
        {
            var key = CallKey(call);
            if (IsWriteTool(call.Name))
            {
                if (!seenWrites.Add(key))
                {
                    return ("错误：该写操作（相同参数）本轮已执行过，未重复写入。" +
                            "若需继续修改，请先 read_file 获取最新内容再生成新的补丁。",
                        "重复写操作已拦截", false);
                }
            }
            else if (!seenReads.Add(key))
            {
                return ("错误：相同只读调用本轮已执行过（结果未变化）。" +
                        "请改用不同的 path / start_line / include 参数，或先完成写入后再复核。",
                    "重复调用已拦截", false);
            }

            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            var root = document.RootElement;
            path = GetString(root, "path") ?? ".";

            if (call.Name == "remember")
            {
                if (!_allowMemoryWrites)
                {
                    return ("错误：记忆写入已在设置中关闭。", "长期记忆 · 已关闭", false);
                }

                var remembered = Remember(GetString(root, "text"));
                return (remembered.Success ? remembered.Text : $"错误：{remembered.Text}",
                    $"长期记忆 · {FirstLine(remembered.Text)}", remembered.Success);
            }

            if (call.Name == "run_command")
            {
                if (!_allowCommands)
                {
                    return ("错误：运行命令已在设置中关闭。", "命令 · 已关闭", false);
                }

                var command = GetString(root, "command");
                if (string.IsNullOrWhiteSpace(command))
                {
                    return ("错误：缺少 command 参数", "命令 · 参数缺失", false);
                }

                var run = await _commands.RunAsync(command, _workspace.Root!, cancellationToken);
                // 命令可能改动工作区（格式化 / 代码生成），让只读缓存失效
                seenReads.Clear();
                var commandSummary = $"{FirstLine(command, 50)} · 退出码 {run.ExitCode}";
                return run.Success
                    ? (run.Output, commandSummary, true)
                    : ($"错误：命令退出码 {run.ExitCode}\n{run.Output}", commandSummary, false);
            }

            var unread = FindUnreadTargets(call.Name, root, path, readPaths);
            if (unread.Count > 0)
            {
                return ($"错误：请先 read_file 再修改 {string.Join("、", unread)}（禁止未读先改）。",
                    "未读先改 · 已拦截", false);
            }

            var result = call.Name switch
            {
                "list_files" => _workspace.ListFiles(path),
                "read_file" => _workspace.ReadFile(path, GetInt(root, "start_line"), GetInt(root, "end_line")),
                "search_code" => await Task.Run(
                    () => _workspace.Search(
                        GetString(root, "query"), GetString(root, "include"),
                        GetInt(root, "max_results") ?? 24, GetInt(root, "context_lines") ?? 0),
                    cancellationToken),
                "find_symbols" => await Task.Run(
                    () => _workspace.FindSymbols(
                        GetString(root, "query"), GetString(root, "include"),
                        GetInt(root, "max_results") ?? 20),
                    cancellationToken),
                "write_file" => _workspace.WriteFile(path, GetString(root, "content")),
                "replace_in_file" => _workspace.ReplaceInFile(
                    path, GetString(root, "old_text"), GetString(root, "new_text")),
                "apply_patch" => _workspace.ApplyPatch(GetString(root, "patch")),
                _ => new WorkspaceResult(false, $"未知工具：{call.Name}")
            };

            if (result.Success)
            {
                TrackReadState(call.Name, root, path, readPaths);
                if (IsFileWriteTool(call.Name)) seenReads.Clear();
            }

            var summary = call.Name switch
            {
                "search_code" => $"{GetString(root, "query") ?? ""} · {FirstLine(result.Text, 70)}",
                "find_symbols" => $"{GetString(root, "query") ?? ""} · {FirstLine(result.Text, 70)}",
                "apply_patch" => $"补丁 · {FirstLine(result.Text, 70)}",
                _ => $"{path} · {FirstLine(result.Text)}"
            };
            return (result.Success ? result.Text : $"错误：{result.Text}", summary, result.Success);
        }
        catch (JsonException)
        {
            return ("错误：工具参数不是合法 JSON", $"{path} · 参数解析失败", false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ($"错误：{ex.Message}", $"{path} · 执行失败", false);
        }
    }

    /// <summary>
    /// 写前读取校验（Agentless / SWE-agent 的纪律）：覆盖或修改既有文件前，必须先成功 read_file；
    /// 新建文件不受限制。补丁会按 +++ 目标逐个核对。
    /// </summary>
    private IReadOnlyList<string> FindUnreadTargets(
        string name,
        JsonElement root,
        string path,
        HashSet<string> readPaths)
    {
        if (name is not ("write_file" or "replace_in_file" or "apply_patch"))
        {
            return Array.Empty<string>();
        }

        var targets = new List<string>();
        if (name == "apply_patch")
        {
            targets.AddRange(WorkspaceService.PatchTargets(GetString(root, "patch")));
        }
        else if (!string.IsNullOrWhiteSpace(path) && path != ".")
        {
            targets.Add(path);
        }

        var unread = new List<string>();
        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_workspace.TryResolve(target, out var full, out _)) continue;
            if (File.Exists(full) && !readPaths.Contains(full)) unread.Add(target);
        }

        return unread;
    }

    /// <summary>读取成功后登记；写入成功后注销，强制下一次修改前重新读取最新内容。</summary>
    private void TrackReadState(string name, JsonElement root, string path, HashSet<string> readPaths)
    {
        if (name == "read_file")
        {
            if (_workspace.TryResolve(path, out var readFull, out _)) readPaths.Add(readFull);
            return;
        }

        if (name is not ("write_file" or "replace_in_file" or "apply_patch")) return;

        var targets = new List<string>();
        if (name == "apply_patch")
        {
            targets.AddRange(WorkspaceService.PatchTargets(GetString(root, "patch")));
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            targets.Add(path);
        }

        foreach (var target in targets)
        {
            if (_workspace.TryResolve(target, out var written, out _)) readPaths.Remove(written);
        }
    }

    private static bool IsWriteTool(string name) =>
        name is "write_file" or "replace_in_file" or "apply_patch" or "run_command" or "remember";

    private static bool IsFileWriteTool(string name) =>
        name is "write_file" or "replace_in_file" or "apply_patch";

    /// <summary>规范化调用参数，用于拦截小模型原地打转的重复调用。</summary>
    private static string CallKey(ToolCall call)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            return $"{call.Name}|{JsonSerializer.Serialize(node)}";
        }
        catch (JsonException)
        {
            return $"{call.Name}|{(call.Arguments ?? "").Trim()}";
        }
    }

    private static string BuildSystemPrompt(string root, AgentMode mode, bool allowCommands)
    {
        var common = $"""
            工作文件夹：{root}
            代码定位：已知类 / 函数名时优先 find_symbols 查定义；再用 search_code 搜索文件名 / 标识符，可用 context_lines（最多 3）在命中行前后展开；读取大文件用 start_line / end_line 分窗口，不要一次要求整个文件。
            规则：path 必须是工作文件夹内的相对路径；绝对路径、.. 与符号链接都会被拒绝。
            修改既有文件前必须先 read_file（新建文件除外），否则会被拦截。
            用户要求查看或修改文件时，必须先调用工具读取真实内容；没有调用工具时，禁止声称已经读取或修改。
            工具返回“错误”时不要重复同样的调用，应修正参数或向用户说明。
            """;

        var verifyRule = allowCommands
            ? "4. 修改后若项目有构建 / 测试命令，用 run_command 运行一次；失败时依据真实输出修复，禁止口称通过；"
            : "4. 当前未开启命令执行：无法验证构建 / 测试时，在回答里明确说明「未验证」，不要声称测试通过；";

        return mode switch
        {
            AgentMode.Plan => $"""
                {common}

                你是「计划」模式助手：只能使用 list_files、find_symbols、search_code 与 read_file，禁止修改任何文件。
                流程：
                1. 先用 find_symbols / search_code / list_files 了解工作区结构与关键文件；
                2. 输出中文实施计划，包含：目标理解、范围、按顺序的步骤、涉及文件、风险与回滚、验收标准；
                3. 不要粘贴大段代码，也不要尝试写文件。
                """,
            AgentMode.Goal => $"""
                {common}

                你是「目标」模式助手：直接在工作文件夹里读取和修改文件，完成用户的目标。
                流程：
                1. 若存在 PLAN.md / SPEC.md，先读取并遵循；
                2. 修改前先用 search_code / read_file 定位：局部改动优先 apply_patch（统一 diff，--- / +++ 与 @@ 头，上下文行必须与原文一致）；其次 replace_in_file；整文件重写才用 write_file；
                3. 若上下文里有 {AgentProtocol.PlanMarker} 计划，按计划执行；偏离计划时在最终回答里说明原因；
                {verifyRule}
                5. 每完成一步可用 read_file 复核；
                6. 无法完成时说明原因并停止，不要虚构结果；
                7. 完成后用中文列出改动文件与验收结果，不要粘贴完整文件内容。
                """,
            _ => common
        };
    }

    private WorkspaceResult Remember(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(false, "记忆内容不能为空");

        try
        {
            var entry = _memory.Add(text, "model");
            return new(true, $"已写入长期记忆：{entry.Text}");
        }
        catch (Exception ex)
        {
            return new(false, $"写入记忆失败：{ex.Message}");
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element)) return null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)) return value;
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed)) return parsed;
        return null;
    }

    /// <summary>read_file 的续读位置；其它工具返回的文本没有行号语义。</summary>
    private static (string? Path, int StartLine) ReadWindow(ToolCall call)
    {
        if (call.Name != "read_file") return (null, 0);
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            return (GetString(document.RootElement, "path"), GetInt(document.RootElement, "start_line") ?? 1);
        }
        catch (JsonException)
        {
            return (null, 0);
        }
    }

    private static string FirstLine(string? text, int limit = 90)
    {
        var line = (text ?? "").Split('\n', 2)[0].Trim();
        return line.Length <= limit ? line : line[..limit] + "…";
    }

    /// <summary>文本协议最终回答可能带 "Final Answer:" 前缀，去掉后才是给用户看的正文。</summary>
    private static string StripFinalAnswerMarker(string content)
    {
        var trimmed = content.Trim();
        var markers = new[] { "Final Answer:", "Final Answer：", "最终回答:", "最终回答：" };
        foreach (var marker in markers)
        {
            var index = trimmed.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var tail = trimmed[(index + marker.Length)..].Trim();
            if (tail.Length > 0) return tail;
        }

        return trimmed;
    }

    private static string Truncate(string? text, int limit)
    {
        var value = (text ?? "").Trim();
        return value.Length <= limit ? value : value[..limit] + "…";
    }

    private static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();
}
