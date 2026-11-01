using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 系统提示词库：内置提示词以嵌入资源形式随程序编译（Prompts/{chat,plan,goal,planner,verify,reviewer}.md），
/// 允许用户在配置目录覆盖，且可按模型再覆盖一层，便于热更与 A/B：
/// <list type="number">
/// <item>按模型覆盖：<c>{覆盖目录}/models/{模型名}/{名称}.md</item>
/// <item>全局覆盖：<c>{覆盖目录}/{名称}.md</item>
/// <item>嵌入资源（默认值）</item>
/// </list>
/// 占位符写成 <c>{{key}}</c>，由 <see cref="Render"/> 替换，未知占位符保持原样。
/// </summary>
public sealed class PromptLibrary
{
    public const string Chat = "chat";
    public const string Plan = "plan";
    public const string Goal = "goal";
    public const string Planner = "planner";
    public const string Verify = "verify";
    public const string Reviewer = "reviewer";

    /// <summary>随程序内置的提示词名称。</summary>
    public static readonly IReadOnlyList<string> KnownNames = [Chat, Plan, Goal, Planner, Verify, Reviewer];

    /// <summary>默认实例：覆盖目录为用户配置目录下的 prompts。</summary>
    public static PromptLibrary Default { get; } = new(Path.Combine(AppPaths.RootDirectory, "prompts"));

    private readonly ConcurrentDictionary<string, string> _embedded = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _overrideDirectory;

    public PromptLibrary(string? overrideDirectory = null) =>
        _overrideDirectory = string.IsNullOrWhiteSpace(overrideDirectory) ? null : overrideDirectory;

    public string? OverrideDirectory => _overrideDirectory;

    /// <summary>读取提示词正文：模型覆盖 → 全局覆盖 → 嵌入资源。缺失时返回空串并写 Debug 输出。</summary>
    public string Get(string name, string? model = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        foreach (var path in CandidatePaths(name, model))
        {
            var text = TryRead(path);
            if (text is not null) return text;
        }

        return Embedded(name);
    }

    /// <summary>读取并替换占位符；返回值已去除首尾空白。</summary>
    public string Render(string name, string? model, params (string Key, string Value)[] values)
    {
        var text = Get(name, model);
        foreach (var (key, value) in values)
        {
            text = text.Replace("{{" + key + "}}", value ?? "", StringComparison.Ordinal);
        }

        return text.Trim();
    }

    private IEnumerable<string> CandidatePaths(string name, string? model)
    {
        if (_overrideDirectory is null) yield break;

        if (!string.IsNullOrWhiteSpace(model))
        {
            var segment = SafeModelSegment(model);
            if (segment.Length > 0)
            {
                yield return Path.Combine(_overrideDirectory, "models", segment, name + ".md");
            }
        }

        yield return Path.Combine(_overrideDirectory, name + ".md");
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[PromptLibrary] 读取覆盖提示词失败 {path}：{ex.Message}");
            return null;
        }
    }

    private string Embedded(string name)
    {
        if (_embedded.TryGetValue(name, out var cached)) return cached;

        var text = "";
        var resourceName = $"{typeof(PromptLibrary).Assembly.GetName().Name}.Prompts.{name}.md";
        try
        {
            using var stream = typeof(PromptLibrary).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                Debug.WriteLine($"[PromptLibrary] 缺少嵌入资源：{resourceName}");
            }
            else
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd().Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Debug.WriteLine($"[PromptLibrary] 读取嵌入资源失败 {resourceName}：{ex.Message}");
        }

        _embedded[name] = text;
        return text;
    }

    /// <summary>模型名可能含路径分隔符或非法字符，转成安全的单级目录名。</summary>
    private static string SafeModelSegment(string model)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(model.Length);
        foreach (var ch in model.Trim())
        {
            builder.Append(invalid.Contains(ch) || ch == Path.DirectorySeparatorChar ? '_' : ch);
        }

        var text = builder.ToString().Trim();
        return text.Length <= 48 ? text : text[..48];
    }
}
