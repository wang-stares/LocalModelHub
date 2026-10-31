using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 模型能力探测：模型加载后用一条最小的工具调用请求验证 chat template 是否真的支持 tool calling，
/// 结果按「模型文件指纹 + 模板指纹」缓存到磁盘，只在换模型 / 换模板时重新探测。
/// 不支持的模型改走文本协议（Thought / Action / Action Input），而不是让用户面对空的 tool_calls。
/// </summary>
public static class ModelCapabilityProbe
{
    private const int ProbeMaxTokens = 64;
    private const int MaxCachedProfiles = 64;

    private static readonly object Sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly ToolDefinition ProbeTool = new(
        "probe_echo", "回显文本（能力探测用，调用后立即返回）。",
        System.Text.Json.Nodes.JsonNode.Parse(
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""")!.AsObject());

    /// <summary>模型文件指纹：大小 + 修改时间，避免每次加载都对数 GB 的 GGUF 做哈希。</summary>
    public static string ModelFingerprint(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return "";
        try
        {
            var info = new FileInfo(modelPath);
            return info.Exists ? $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>chat template 指纹：模板变了（换了模型版本 / 重新导出）就重新探测。</summary>
    public static string TemplateHash(string? template)
    {
        if (string.IsNullOrEmpty(template)) return "none";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(template));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>模板里出现这几类标记，说明它声明了工具调用消息（仅作探测失败时的兜底判断）。</summary>
    public static bool TemplateDeclaresTools(string? template)
    {
        if (string.IsNullOrEmpty(template)) return false;
        var lower = template.ToLowerInvariant();
        return lower.Contains("tool_calls", StringComparison.Ordinal) &&
               (lower.Contains("'tools'", StringComparison.Ordinal) ||
                lower.Contains("\"tools\"", StringComparison.Ordinal) ||
                lower.Contains("tools |", StringComparison.Ordinal) ||
                lower.Contains("tool_call", StringComparison.Ordinal));
    }

    /// <summary>
    /// 探测一次：发一条带 dummy tool 的请求，只有模型真的返回 tool_calls 才算原生支持。
    /// 请求异常（超时 / 服务未就绪）时退回模板特征判断，并且不写缓存，下次加载再试。
    /// </summary>
    public static async Task<ModelProfile> ProbeAsync(
        IChatCompletion server,
        string modelPath,
        int nCtx,
        string? chatTemplate,
        CancellationToken cancellationToken)
    {
        var fingerprint = ModelFingerprint(modelPath);
        var templateHash = TemplateHash(chatTemplate);

        var cached = FindCached(fingerprint, templateHash);
        if (cached is not null) return cached;

        var declaresTools = TemplateDeclaresTools(chatTemplate);
        var probedByRequest = false;
        var supportsTools = declaresTools;

        try
        {
            var turn = await server.ChatAsync(
                [
                    ChatMessage.System("你是工具调用自检。必须调用 probe_echo 工具，不要输出普通文本或解释。"),
                    ChatMessage.User("调用 probe_echo，参数 text 为 ok。")
                ],
                [ProbeTool],
                ProbeMaxTokens,
                new ChatRequestOptions(Temperature: 0, TopP: 0.8),
                cancellationToken);

            probedByRequest = true;
            supportsTools = turn.ToolCalls.Any(call =>
                call.Name.Equals(ProbeTool.Name, StringComparison.OrdinalIgnoreCase) ||
                call.Name.Contains("probe", StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 模型可能还在预热或调用超时：本次按模板推断，不写缓存
            Debug.WriteLine($"[ModelCapabilityProbe] 探测失败，按模板推断：{ex.Message}");
        }

        var profile = new ModelProfile(
            fingerprint,
            templateHash,
            supportsTools,
            true, // JSON 约束由 llama-server 的 response_format 实现，与模型无关
            nCtx > 0 ? nCtx : 8192,
            DateTime.Now)
        {
            ProbedByRequest = probedByRequest
        };

        if (probedByRequest) Store(profile);
        return profile;
    }

    public static ModelProfile? FindCached(string fingerprint, string templateHash)
    {
        if (fingerprint.Length == 0) return null;
        lock (Sync)
        {
            return LoadAll().FirstOrDefault(profile =>
                profile.ModelFingerprint.Equals(fingerprint, StringComparison.Ordinal) &&
                profile.TemplateHash.Equals(templateHash, StringComparison.Ordinal));
        }
    }

    public static void Store(ModelProfile profile)
    {
        lock (Sync)
        {
            try
            {
                var profiles = LoadAll();
                profiles.RemoveAll(existing => existing.ModelFingerprint == profile.ModelFingerprint);
                profiles.Add(profile);
                if (profiles.Count > MaxCachedProfiles)
                {
                    profiles = profiles.OrderByDescending(item => item.ProbedAt).Take(MaxCachedProfiles).ToList();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ModelProfilesFile)!);
                File.WriteAllText(
                    AppPaths.ModelProfilesFile,
                    JsonSerializer.Serialize(profiles, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Debug.WriteLine($"[ModelCapabilityProbe] 写入缓存失败：{ex.Message}");
            }
        }
    }

    private static List<ModelProfile> LoadAll()
    {
        try
        {
            if (!File.Exists(AppPaths.ModelProfilesFile)) return [];
            var json = File.ReadAllText(AppPaths.ModelProfilesFile);
            return JsonSerializer.Deserialize<List<ModelProfile>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[ModelCapabilityProbe] 读取缓存失败：{ex.Message}");
            return [];
        }
    }
}
