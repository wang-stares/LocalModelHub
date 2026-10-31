using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalModelHub.App.Services;
using LocalModelHub.App.ViewModels;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(LocalModelHub.Ui.Tests.TestAppBuilder))]

namespace LocalModelHub.Ui.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<LocalModelHub.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class UiSmokeTests
{
    [AvaloniaFact]
    public void Renders_all_pages()
    {
        var services = AppServices.Current;
        services.Initialize();

        var viewModel = new MainWindowViewModel();
        var window = new LocalModelHub.App.MainWindow
        {
            DataContext = viewModel,
            Width = 1024,
            Height = 768
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var output = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(output);

        foreach (var (variant, tag) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            Dispatcher.UIThread.RunJobs();

            for (var index = 0; index < 6; index++)
            {
                viewModel.NavigateCommand.Execute(index.ToString());
                Dispatcher.UIThread.RunJobs();

                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame!.Save(Path.Combine(output, $"page{index}-{tag}.png"));
            }
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Renders_merged_knowledge_memory_tabs()
    {
        AppServices.Current.Initialize();
        var viewModel = new MainWindowViewModel();
        var window = new LocalModelHub.App.MainWindow
        {
            DataContext = viewModel,
            Width = 1024,
            Height = 768
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        viewModel.NavigateCommand.Execute("4");
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.KnowledgeMemory.IsKnowledgeTab);

        viewModel.KnowledgeMemory.TabIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.KnowledgeMemory.IsMemoryTab);

        var output = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(output);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(output, "state-knowledge-memory.png"));

        AppServices.Current.Skills.Add(
            "修改 Program.cs",
            "当用户要求调整启动逻辑时",
            "1. 读取 Program.cs\n2. 用 replace_in_file 修改启动代码\n3. 运行 dotnet build 验证");
        viewModel.KnowledgeMemory.TabIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.KnowledgeMemory.IsSkillsTab);

        var skillsFrame = window.CaptureRenderedFrame();
        Assert.NotNull(skillsFrame);
        skillsFrame!.Save(Path.Combine(output, "state-knowledge-skills.png"));

        window.Close();
    }

    [AvaloniaFact]
    public void Renders_installed_downloading_and_chat_states()
    {
        var services = AppServices.Current;
        services.Initialize();

        var sessionsPath = AppPaths.SessionsFile;
        var sessionsBackup = File.Exists(sessionsPath) ? File.ReadAllText(sessionsPath) : null;
        var originalDirectory = services.Settings.ModelsDirectory;
        var root = Path.Combine(Path.GetTempPath(), "lmh-ui-state");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

        var quantDirectory = Path.Combine(root, "qwen3-8b", "Q4_K_M");
        Directory.CreateDirectory(quantDirectory);
        File.WriteAllBytes(Path.Combine(quantDirectory, "Qwen3-8B-Q4_K_M.gguf"), new byte[512]);
        File.WriteAllText(
            Path.Combine(quantDirectory, "manifest.json"),
            $$"""{"modelId":"qwen3-8b","quant":"Q4_K_M","addedAt":"{{DateTime.Now:O}}"}""");

        services.Settings.ModelsDirectory = root;
        services.RefreshLibrary();

        // 工作文件夹：沙箱目录 + 已启用文件编辑
        var workspace = Path.Combine(Path.GetTempPath(), "lmh-ui-workspace");
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "main.cs"), "class A { }");
        services.Settings.WorkspaceDirectory = workspace;
        services.Settings.AgentMode = "goal";
        services.Workspace.SetRoot(workspace);

        if (File.Exists(sessionsPath)) File.Delete(sessionsPath);

        var viewModel = new MainWindowViewModel();
        viewModel.Chat.RefreshWorkspaceState();
        viewModel.Chat.ModeIndex = 2;
        var window = new LocalModelHub.App.MainWindow
        {
            DataContext = viewModel,
            Width = 1024,
            Height = 768
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var output = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(output);

        // 商店：已下载模型的卡片显示“启动模型”与目录按钮
        viewModel.NavigateCommand.Execute("2");
        viewModel.Store.SearchText = "Qwen3-8B";
        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(output, "state-store-installed.png"));

        // 商店：按能力筛选「编程」
        viewModel.Store.SearchText = "";
        viewModel.Store.CapabilityIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(viewModel.Store.Cards);
        Assert.All(viewModel.Store.Cards, card =>
            Assert.Contains(ModelCapabilities.Coding, card.Model.Capabilities));
        Capture(window, Path.Combine(output, "state-store-coding.png"));
        viewModel.Store.CapabilityIndex = 0;

        // 商店：下载中的卡片显示进度条与状态
        viewModel.Store.SearchText = "DeepSeek-R1 蒸馏 7B";
        var download = services.Downloads.GetState("deepseek-r1-distill-qwen-7b");
        download.IsDownloading = true;
        download.Progress = 37.4;
        download.Status = "正在下载 DeepSeek-R1-Distill-Qwen-7B-Q4_K_M.gguf";
        download.SpeedText = "7.2 MB/s";
        download.EtaText = "剩余 02:41";
        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(output, "state-store-downloading.png"));

        // 商店：暂停后保留断点，主按钮变为“继续下载”
        var partialDirectory = Path.Combine(root, "deepseek-r1-distill-qwen-7b", "Q4_K_M");
        Directory.CreateDirectory(partialDirectory);
        File.WriteAllBytes(
            Path.Combine(partialDirectory, "DeepSeek-R1-Distill-Qwen-7B-Q4_K_M.gguf.part"), new byte[4096]);
        download.IsDownloading = false;
        download.IsPaused = true;
        download.Status = "已暂停 · 断点已保留，点击“继续下载”";
        download.SpeedText = "";
        download.EtaText = "";
        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(output, "state-store-paused.png"));

        // 我的模型：已下载行 + 推荐标签
        viewModel.NavigateCommand.Execute("1");
        Dispatcher.UIThread.RunJobs();
        Assert.Single(viewModel.Library.Items);
        Assert.True(viewModel.Models.IsInstalledTab);
        Capture(window, Path.Combine(output, "state-library.png"));

        viewModel.Models.TabIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.Models.IsRecommendTab);
        Assert.NotEmpty(viewModel.Recommend.Cards);
        Capture(window, Path.Combine(output, "state-models-recommend.png"));
        viewModel.Models.TabIndex = 0;

        // 对话：用户 / 助手气泡（Markdown + 代码块）
        viewModel.NavigateCommand.Execute("3");
        viewModel.Chat.ModelName = "Qwen3-8B";
        viewModel.Chat.Sessions[0].Rename("C# 环形缓冲实现");
        viewModel.Chat.Messages.Add(ChatMessageViewModel.UserMessage("用 C# 写一个无锁的环形缓冲队列，并说明内存屏障的作用。"));
        viewModel.Chat.Messages.Add(ChatMessageViewModel.ToolMessage(
            "replace_in_file · main.cs · 已在 main.cs 中替换 1 处"));
        viewModel.Chat.Messages.Add(ChatMessageViewModel.AssistantMessage(
            "无锁环形缓冲的关键是维护 head / tail 两个位置，并通过 Volatile.Read/Volatile.Write 保证可见性。\n\n" +
            "- 生产者只写 tail，消费者只写 head，避免共享写；\n" +
            "- 使用 Interlocked.Increment 获取槽位，防止并发覆盖；\n" +
            "- 写入数据后发布 tail 位置（Release 语义），读取 head 前使用 Acquire 语义。\n\n" +
            "```csharp\n" +
            "public sealed class RingBuffer<T>\n" +
            "{\n" +
            "    private readonly T[] _items;\n" +
            "    private int _head;\n" +
            "    private int _tail;\n" +
            "}\n" +
            "```"));
        var answer = viewModel.Chat.Messages[^1];
        answer.AppendReasoning(
            "先确认并发模型：单生产者单消费者，用 head / tail 两个游标即可无锁；" +
            "容量取 2 的幂，用位运算代替取模；写入后发布 tail，读取前获取 head。");
        Assert.True(answer.HasReasoning);
        Assert.True(answer.IsReasoningExpanded);
        viewModel.Chat.HasMessages = true;
        viewModel.Chat.SaveCurrentSession();

        viewModel.Chat.NewSessionCommand.Execute(null);
        viewModel.Chat.NewSessionCommand.Execute(null);
        viewModel.Chat.Sessions[0].Rename("Space 方案草稿");
        viewModel.Chat.Sessions[1].Rename("模型参数调优");
        viewModel.Chat.SelectedSession = viewModel.Chat.Sessions[2];
        Dispatcher.UIThread.RunJobs();
        foreach (var message in viewModel.Chat.Messages.Where(message => message.HasReasoning))
        {
            message.IsReasoningExpanded = true;
        }

        Dispatcher.UIThread.RunJobs();
        for (var tick = 0; tick < 12; tick++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        var reasoningExpander = window.GetVisualDescendants().OfType<Expander>().FirstOrDefault(expander => expander.IsVisible);
        Assert.NotNull(reasoningExpander);
        Assert.True(reasoningExpander!.IsExpanded);
        Capture(window, Path.Combine(output, "state-chat.png"));

        // 设置页：版本更新公告
        viewModel.NavigateCommand.Execute("5");
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(output, "state-settings-release-notes.png"));

        // 设置页：展开「星河档案」查看往期公告
        Assert.True(viewModel.SettingsPage.PreviousReleases.Count > 0);
        viewModel.SettingsPage.ToggleReleaseArchiveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.SettingsPage.IsReleaseArchiveOpen);

        var archiveTitle = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == "星河档案");
        archiveTitle?.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var archiveScroll = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (archiveScroll is not null)
        {
            archiveScroll.Offset = new Vector(0, Math.Max(0, archiveScroll.Offset.Y - 32));
        }

        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(output, "state-settings-release-archive.png"));
        viewModel.SettingsPage.IsReleaseArchiveOpen = false;
        Dispatcher.UIThread.RunJobs();

        // 设置页：推理性能（KV 量化 / MoE / 并行槽位）
        var inferenceTitle = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == "推理性能");
        Assert.NotNull(inferenceTitle);
        inferenceTitle!.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var inferenceScroll = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (inferenceScroll is not null)
        {
            inferenceScroll.Offset = new Vector(0, Math.Max(0, inferenceScroll.Offset.Y - 32));
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, viewModel.SettingsPage.KvCacheIndex);
        Assert.Equal(1m, viewModel.SettingsPage.ParallelSlots);
        Capture(window, Path.Combine(output, "state-settings-inference.png"));

        // 设置页：API 接入卡片
        var apiTitle = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == "API 接入");
        apiTitle?.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var settingsScroll = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (settingsScroll is not null)
        {
            settingsScroll.Offset = new Vector(0, Math.Max(0, settingsScroll.Offset.Y - 32));
        }

        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(output, "state-settings-api.png"));

        // 深色主题下重复关键状态，验证主题字典切换
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        viewModel.NavigateCommand.Execute("2");
        viewModel.Store.SearchText = "Qwen3-8B";
        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(output, "state-store-installed-dark.png"));

        viewModel.NavigateCommand.Execute("1");
        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(output, "state-library-dark.png"));

        viewModel.NavigateCommand.Execute("3");
        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(output, "state-chat-dark.png"));

        window.Close();

        services.Settings.ModelsDirectory = originalDirectory;
        services.RefreshLibrary();
        services.Settings.WorkspaceDirectory = "";
        services.Settings.AgentMode = "chat";
        services.Workspace.SetRoot(null);
        try { Directory.Delete(root, recursive: true); } catch { }
        try { Directory.Delete(workspace, recursive: true); } catch { }
        try
        {
            if (sessionsBackup is null)
            {
                if (File.Exists(sessionsPath)) File.Delete(sessionsPath);
            }
            else
            {
                File.WriteAllText(sessionsPath, sessionsBackup);
            }
        }
        catch
        {
            // ignore
        }
    }

    [AvaloniaFact]
    public void Store_paginates_and_recommend_defaults_to_runnable_models()
    {
        var services = AppServices.Current;
        services.Initialize();

        var viewModel = new MainWindowViewModel();
        var window = new LocalModelHub.App.MainWindow
        {
            DataContext = viewModel,
            Width = 1024,
            Height = 768
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 商店：分页渲染，首批不超过一页，且页脚「显示更多」与 HasMore 一致
        viewModel.NavigateCommand.Execute("2");
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.Store.Cards.Count <= 48);
        Assert.Equal(viewModel.Store.HasMore, viewModel.Store.LoadMoreText.Length > 0);
        var legend = window.GetVisualDescendants()
            .OfType<LocalModelHub.App.Views.ModelLegendView>()
            .FirstOrDefault();
        Assert.NotNull(legend);

        // 图例展开后可截图检查文案
        var output = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(output);
        var expander = legend!.GetVisualDescendants().OfType<Expander>().FirstOrDefault();
        Assert.NotNull(expander);
        expander!.IsExpanded = true;
        for (var tick = 0; tick < 12; tick++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        var legendTexts = legend.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").ToList();
        Assert.Contains(legendTexts, text => text.Contains("量化"));
        Assert.Contains(legendTexts, text => text.Contains("tok/s"));
        Capture(window, Path.Combine(output, "state-store-legend.png"));
        expander.IsExpanded = false;
        Dispatcher.UIThread.RunJobs();

        if (viewModel.Store.HasMore)
        {
            var before = viewModel.Store.Cards.Count;
            viewModel.Store.LoadMoreCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.Store.Cards.Count > before);
        }

        // 商店：空结果时显示当前筛选，并可一键重置
        viewModel.Store.SearchText = "zzz-no-such-model";
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.Store.IsEmpty);
        Assert.Contains("当前筛选", viewModel.Store.ActiveFilterText);

        viewModel.Store.ResetFiltersCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.Store.IsEmpty);
        Assert.Empty(viewModel.Store.ActiveFilterText);

        // 推荐页：默认「适合我的」只包含本机能运行的模型
        viewModel.NavigateCommand.Execute("1");
        viewModel.Models.TabIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, viewModel.Recommend.FilterIndex);
        Assert.True(viewModel.Recommend.SuitedCount > 0);
        Assert.All(viewModel.Recommend.Cards, card => Assert.True(card.Fit <= FitLevel.Playable));
        Assert.True(viewModel.Recommend.Cards.Count <= 48);
        Assert.Equal(viewModel.Recommend.HasMore, viewModel.Recommend.LoadMoreText.Length > 0);
        Assert.NotNull(window.GetVisualDescendants()
            .OfType<LocalModelHub.App.Views.ModelLegendView>()
            .FirstOrDefault());

        window.Close();
    }

    [AvaloniaFact]
    public void Renders_knowledge_page_with_documents_and_search_results()
    {
        var services = AppServices.Current;
        services.Initialize();

        var knowledgeFile = AppPaths.KnowledgeFile;
        var backup = File.Exists(knowledgeFile) ? File.ReadAllText(knowledgeFile) : null;
        var root = Path.Combine(Path.GetTempPath(), "lmh-kb-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            services.Knowledge.Clear();
            var api = Path.Combine(root, "api.md");
            File.WriteAllText(api,
                "Local Model Hub 支持局域网 API 接入。开启 0.0.0.0 监听后，其他设备可以通过 Bearer API Key 调用 /v1/chat/completions。");
            var chat = Path.Combine(root, "chat.md");
            File.WriteAllText(chat,
                "对话页支持 Markdown 渲染、代码块复制与导出 Markdown；知识库检索会在系统提示中注入相关片段。");
            Task.Run(async () =>
            {
                await services.Knowledge.ImportAsync(api, CancellationToken.None);
                await services.Knowledge.ImportAsync(chat, CancellationToken.None);
            }).GetAwaiter().GetResult();

            var viewModel = new KnowledgeViewModel();
            viewModel.SearchText = "局域网 API 怎么调用";
            viewModel.SearchCommand.Execute(null);

            var window = new Window
            {
                Content = new LocalModelHub.App.Views.KnowledgeView { DataContext = viewModel },
                Width = 1024,
                Height = 768,
                Background = Avalonia.Media.Brushes.White
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();

            var output = Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(output);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(output, "state-knowledge.png"));

            window.Close();
        }
        finally
        {
            services.Knowledge.Clear();
            if (backup is null)
            {
                if (File.Exists(knowledgeFile)) File.Delete(knowledgeFile);
            }
            else
            {
                File.WriteAllText(knowledgeFile, backup);
            }

            services.Knowledge.Load();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public void Renders_add_model_dialog()
    {
        AppServices.Current.Initialize();

        var window = new LocalModelHub.App.Views.AddModelWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var output = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(output);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(output, "window-add-model.png"));

        window.Close();
    }

    [AvaloniaFact]
    public void Renders_memory_page_with_entries()
    {
        AppServices.Current.Initialize();

        var path = Path.Combine(Path.GetTempPath(), "lmh-memory-ui-" + Guid.NewGuid().ToString("N") + ".json");
        var memory = new MemoryService(path);
        memory.Add("用户偏好中文回答，代码注释使用英文。", "manual");
        var pinned = memory.Add("当前项目使用 .NET 8 + Avalonia 11，窗口保留 Windows 原生边框。", "model");
        memory.TogglePin(pinned.Id);
        memory.Add("这段回答来自对话，可保存为记忆：优先使用 replace_in_file 做局部修改。", "chat");
        memory.Add("回答先给结论，再补充必要的技术细节。", "manual");

        var viewModel = new MemoryViewModel(memory);
        var window = new Window
        {
            Content = new LocalModelHub.App.Views.MemoryView { DataContext = viewModel },
            Width = 1024,
            Height = 768,
            Background = Avalonia.Media.Brushes.White
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var output = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(output);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(output, "state-memory.png"));

        window.Close();
        File.Delete(path);
    }

    [AvaloniaFact]
    public void Renders_failure_dialog_with_wrapped_log()
    {
        AppServices.Current.Initialize();

        var longLine = "[参数] -m \"D:\\myModels\\hf-jonathancoletti-qwen3.8-27b-uncensored-Q4_0.gguf\" --host 127.0.0.1 --port 8000 -c 8192 -ngl 999 --jinja -ctk q8_0 -ctv q8_0 -np 1 --reasoning-format deepseek -t 16 --alias Qwen3.8";
        var message = "推理进程启动失败（退出码 -1073741819）。\n\nWindows 访问冲突：多为显存不足、显卡驱动与 CUDA 运行时不匹配。建议切换后端或降低上下文重试。\n\n" +
                      string.Join("\n", Enumerable.Repeat(longLine, 12));

        var window = DialogService.CreateWindow("启动失败", message, out var buttons);
        Assert.Equal(504d, window.Width);
        buttons[0].Content = "自动处理";
        buttons[1].Content = "关闭";

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var output = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(output);

        var text = window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == message);
        Assert.True(text.Bounds.Width <= 460.5, $"消息宽度 {text.Bounds.Width} 超出对话框可用宽度");

        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(scroller.Bounds.Height <= 340.5, $"滚动区域高度 {scroller.Bounds.Height} 超出上限");
        Assert.True(text.DesiredSize.Height > scroller.Bounds.Height, "长文本应触发区域内滚动而不是撑高窗口");

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(output, "state-dialog-failure.png"));

        window.Close();
    }

    [AvaloniaFact]
    public void Collapses_navigation_and_sessions_for_more_chat_room()
    {
        var services = AppServices.Current;
        services.Initialize();

        var viewModel = new MainWindowViewModel();
        var window = new LocalModelHub.App.MainWindow
        {
            DataContext = viewModel,
            Width = 1024,
            Height = 768
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var output = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(output);

        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        viewModel.NavigateCommand.Execute("3");
        viewModel.ToggleNavCommand.Execute(null);
        viewModel.Chat.ToggleSessionsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.IsNavCollapsed);
        Assert.Equal(76, viewModel.NavColumnWidth.Value);
        Assert.True(viewModel.Chat.IsSessionsCollapsed);
        Assert.Equal(0, viewModel.Chat.SessionsWidth.Value);
        Assert.Equal(0, viewModel.Chat.SessionsGap.Value);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(output, "state-chat-collapsed.png"));

        viewModel.IsNavCollapsed = false;
        viewModel.Chat.IsSessionsCollapsed = false;
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(LocalModelHub.App.MainWindow window, string path)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(path);
    }
}
