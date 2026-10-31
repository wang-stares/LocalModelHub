using System.Text.Json.Serialization;

namespace LocalModelHub.Core.Models;

public sealed class ModelCatalog
{
    public int Version { get; set; } = 1;
    public string UpdatedAt { get; set; } = "";
    public List<ModelEntry> Models { get; set; } = [];

    /// <summary>最近一次从远程源同步的时间；为空表示仅使用内置目录。</summary>
    public string SyncedAt { get; set; } = "";

    /// <summary>从远程源同步得到的模型数量。</summary>
    public int RemoteCount { get; set; }
}

public sealed class ModelEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Developer { get; set; } = "";
    public string Country { get; set; } = "US";
    public string Family { get; set; } = "";
    public double ParametersB { get; set; }
    public double ActiveParametersB { get; set; }
    public int ContextLength { get; set; }
    public int ReleaseYear { get; set; }
    public string License { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public string[] Capabilities { get; set; } = [];
    public ModelQuant[] Quants { get; set; } = [];
    public string? HfRepo { get; set; }

    /// <summary>远程来源标识（HuggingFace / ModelScope / Ollama / API）；内置目录为空。</summary>
    public string Origin { get; set; } = "";

    /// <summary>OpenAI 兼容外部 API 的 Base URL（形如 https://host/v1）；为空表示本地 GGUF 模型。</summary>
    public string ApiBaseUrl { get; set; } = "";

    /// <summary>外部 API 的访问密钥，随自定义模型保存在本机。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>外部 API 中的模型 ID。</summary>
    public string ApiModelId { get; set; } = "";

    [JsonIgnore] public bool IsRemoteApi => !string.IsNullOrWhiteSpace(ApiBaseUrl);

    /// <summary>来源站点的累计下载量，用于远程模型的排序与展示。</summary>
    public long Downloads { get; set; }

    [JsonIgnore] public ModelQuant PrimaryQuant => Quants[0];
    [JsonIgnore] public bool IsChinese => Country.Equals("CN", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsCustom { get; set; }
    [JsonIgnore] public bool IsCommunity => Origin is "HuggingFace" or "ModelScope" or "Ollama";
    [JsonIgnore] public string OriginLabel => Origin switch
    {
        "HuggingFace" => "HuggingFace",
        "ModelScope" => "魔搭",
        "Ollama" => "Ollama",
        "API" => "远程 API",
        _ => "内置"
    };
    [JsonIgnore] public bool HasCapabilities => Capabilities.Length > 0;
    [JsonIgnore] public string CapabilityText => string.Join(" · ", Capabilities);
    [JsonIgnore] public bool IsMoe => ActiveParametersB > 0 && ParametersB > ActiveParametersB * 1.5;
    [JsonIgnore] public string CountryLabel => Country switch
    {
        "CN" => "国内",
        "US" => "美国",
        "FR" => "法国",
        _ => "海外"
    };
    [JsonIgnore] public string ParamsLabel => IsMoe
        ? $"{ParametersB:0.#}B / 激活 {ActiveParametersB:0.#}B"
        : $"{ParametersB:0.#}B";
}

public static class ModelCapabilities
{
    public const string All = "全部能力";
    public const string Coding = "编程";
    public const string Chat = "对话";
    public const string Reasoning = "推理";
    public const string LongContext = "长文本";
    public const string Tools = "工具调用";
    public const string Multilingual = "多语言";
    public const string Lightweight = "轻量";

    public static readonly string[] Filters =
        [All, Coding, Chat, Reasoning, LongContext, Tools, Multilingual, Lightweight];
}

public sealed class ModelQuant
{
    public string Label { get; set; } = "";
    public long TotalBytes { get; set; }
    public ModelFile[] Files { get; set; } = [];
    public ModelSource[] Sources { get; set; } = [];
    public string? Note { get; set; }

    [JsonIgnore] public double SizeGb => TotalBytes / Formatters.Gib;
}

public sealed class ModelFile
{
    public string Path { get; set; } = "";
    public long Bytes { get; set; }

    /// <summary>Ollama registry 的 blob 摘要（sha256:…）；其他来源为空。</summary>
    public string? Digest { get; set; }
}

public sealed class ModelSource
{
    public string Provider { get; set; } = "ModelScope";
    public string Repo { get; set; } = "";
    public string Revision { get; set; } = "master";
}
