using System.Text.Json;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

public static class AppPaths
{
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalModelHub");

    public static string ModelsDirectory => Path.Combine(RootDirectory, "models");
    public static string RuntimeDirectory => Path.Combine(RootDirectory, "runtime");
    public static string DownloadsDirectory => Path.Combine(RootDirectory, "downloads");
    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public static string SettingsFile => Path.Combine(RootDirectory, "settings.json");
    public static string CustomModelsFile => Path.Combine(RootDirectory, "custom-models.json");
    public static string RemoteCatalogFile => Path.Combine(RootDirectory, "catalog-remote.json");
    public static string MemoryFile => Path.Combine(RootDirectory, "memory.json");
    public static string SessionsFile => Path.Combine(RootDirectory, "sessions.json");
    public static string KnowledgeFile => Path.Combine(RootDirectory, "knowledge.json");
    public static string SkillsFile => Path.Combine(RootDirectory, "skills.json");
    public static string ModelProfilesFile => Path.Combine(RootDirectory, "model-profiles.json");

    /// <summary>本机实测的 CPU 算力 / 内存带宽（带硬件指纹），用于重启后保持同一评分口径。</summary>
    public static string MeasuredProfileFile => Path.Combine(RootDirectory, "measured-profile.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}

public sealed class AppSettings
{
    /// <summary>true = 自动挑选下载源；false = 使用 <see cref="DownloadProviders"/> 中选定的源。</summary>
    public bool DownloadAuto { get; set; } = true;
    /// <summary>手动模式下已勾选的下载源，按顺序尝试（可单选或多选）。</summary>
    public List<string> DownloadProviders { get; set; } = ["ModelScope", "HuggingFace", "Ollama"];
    /// <summary>当前下载源失败时，是否自动切换到其他源重试。</summary>
    public bool AutoSwitchSource { get; set; } = true;
    public bool UseHfMirror { get; set; } = true;
    public bool SyncHuggingFace { get; set; } = true;
    public bool SyncModelScope { get; set; } = true;
    public bool SyncOllama { get; set; } = true;
    public string ModelsDirectory { get; set; } = AppPaths.ModelsDirectory;
    public string? RuntimePath { get; set; }
    public string RuntimeVariant { get; set; } = "auto"; // auto | cuda | vulkan | cpu
    public string Theme { get; set; } = "system"; // system | light | dark
    /// <summary>左侧主导航是否折叠为图标栏。</summary>
    public bool NavCollapsed { get; set; }
    /// <summary>对话页的会话列表是否折叠，折叠后对话区占满宽度。</summary>
    public bool ChatSessionsCollapsed { get; set; }
    public string WorkspaceDirectory { get; set; } = "";
    public string AgentMode { get; set; } = "chat"; // chat | plan | goal
    /// <summary>允许工作区 Agent 运行白名单内的构建 / 测试命令（默认关闭，需用户显式开启）。</summary>
    public bool AgentAllowCommands { get; set; }
    /// <summary>目标模式下启用 Planner → Coder → Reviewer 多角色串行协作（默认关闭）。</summary>
    public bool AgentMultiAgent { get; set; }
    public bool MemoryEnabled { get; set; } = true;
    public bool MemoryAllowModelWrites { get; set; } = true;
    public bool KnowledgeEnabled { get; set; } = true;
    public int KnowledgeTopK { get; set; } = 4;
    /// <summary>知识库混合检索：BM25 + /v1/embeddings 向量（RRF 融合），默认关闭，失败自动回退。</summary>
    public bool KnowledgeHybridSearch { get; set; }
    /// <summary>从用户消息中自动提取偏好与事实写入长期记忆（规则版，不额外调用模型）。</summary>
    public bool AutoMemoryEnabled { get; set; } = true;
    /// <summary>任务完成后自动把执行过程总结为技能。</summary>
    public bool AutoSkillEnabled { get; set; } = true;
    /// <summary>对话时把相关技能注入上下文。</summary>
    public bool SkillsEnabled { get; set; } = true;
    public string SystemPrompt { get; set; } = "";
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 0.95;
    public int MaxTokens { get; set; } = 1024;
    public int ServerPort { get; set; } = 8090;
    public int ContextSize { get; set; } = 8192;
    public int Threads { get; set; } = 0;
    /// <summary>Flash Attention：auto | on | off。</summary>
    public string FlashAttention { get; set; } = "auto";
    /// <summary>KV cache 量化：f16 | q8_0 | q4_0。q8_0 在几乎无损的前提下大幅降低长上下文显存。</summary>
    public string KvCacheType { get; set; } = "q8_0";
    /// <summary>MoE 模型前 N 层专家权重留在 CPU（llama.cpp --n-cpu-moe），0 表示关闭。</summary>
    public int CpuMoeLayers { get; set; } = 0;
    /// <summary>并行槽位数；单用户场景 1 个槽位最省 KV 显存，0 表示交给 llama.cpp 自动。</summary>
    public int ParallelSlots { get; set; } = 1;
    /// <summary>模型装载模式：auto | mmap | mlock。</summary>
    public string LoadMode { get; set; } = "mmap";
    /// <summary>把 reasoning_content 单独返回并折叠展示；关闭时思考内容混在正文里。</summary>
    public bool ReasoningEnabled { get; set; } = true;
    /// <summary>启用 /metrics Prometheus 指标端点。</summary>
    public bool MetricsEnabled { get; set; }
    public bool AutoStartRuntime { get; set; } = true;
    public int IdleSleepMinutes { get; set; } = 10;
    public bool AutoWake { get; set; } = true;
    public bool ApiExposeLan { get; set; }
    public string ApiKey { get; set; } = "";
    /// <summary>外部 OpenAI 兼容 API 的地址与密钥，便于下次自动拉取模型列表。</summary>
    public string RemoteApiBaseUrl { get; set; } = "";
    public string RemoteApiKey { get; set; } = "";
    public string? LastModelPath { get; set; }

    // —— Agent 资源预算（P2-6）：默认值与旧常量一致，可在设置页「高级」调整 ——
    /// <summary>Plan 模式的工具轮次上限。</summary>
    public int AgentPlanRounds { get; set; } = AgentTuningLimits.DefaultPlanRounds;
    /// <summary>Goal 模式的工具轮次上限；轮次越多越耗 token 与时延。</summary>
    public int AgentGoalRounds { get; set; } = AgentTuningLimits.DefaultGoalRounds;
    /// <summary>read_file 默认返回行数。</summary>
    public int AgentReadLines { get; set; } = AgentTuningLimits.DefaultReadLines;
    /// <summary>read_file 单次返回的最大行数。</summary>
    public int AgentMaxReadWindow { get; set; } = AgentTuningLimits.DefaultMaxReadWindow;
    /// <summary>工具结果写入上下文前的裁剪字符数。</summary>
    public int AgentObservationChars { get; set; } = AgentTuningLimits.DefaultObservationChars;
    /// <summary>Agent 每一步回答的 max_tokens。</summary>
    public int AgentMaxTokens { get; set; } = AgentTuningLimits.DefaultMaxTokens;
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public AppSettings Settings { get; private set; } = new();

    public AppSettings Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            Settings = new AppSettings();
        }

        if (string.IsNullOrWhiteSpace(Settings.ModelsDirectory))
        {
            Settings.ModelsDirectory = AppPaths.ModelsDirectory;
        }

        if (Settings.DownloadProviders.Count == 0)
        {
            Settings.DownloadProviders = ["ModelScope", "HuggingFace", "Ollama"];
        }

        Directory.CreateDirectory(Settings.ModelsDirectory);
        return Settings;
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        Directory.CreateDirectory(Settings.ModelsDirectory);
        var temp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Settings, JsonOptions));
        File.Move(temp, AppPaths.SettingsFile, overwrite: true);
    }
}
