using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>能力探测的纯函数与「探测失败按模板推断」路径（成功路径会写用户缓存目录，不在测试里触发）。</summary>
public sealed class ModelCapabilityProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-probe-{Guid.NewGuid():N}");

    public ModelCapabilityProbeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ModelFingerprint_tracks_size_and_mtime()
    {
        var path = Path.Combine(_root, "model.gguf");
        File.WriteAllText(path, "abc");

        var bySize = ModelCapabilityProbe.ModelFingerprint(path);
        Assert.NotEmpty(bySize);

        // 大小不变、只改修改时间也要被视为不同版本（重新导出 / 替换文件）
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
        var byTime = ModelCapabilityProbe.ModelFingerprint(path);
        Assert.NotEqual(bySize, byTime);

        File.WriteAllText(path, "abcd");
        var byNewSize = ModelCapabilityProbe.ModelFingerprint(path);
        Assert.NotEqual(byTime, byNewSize);

        Assert.Equal("", ModelCapabilityProbe.ModelFingerprint(null));
        Assert.Equal("", ModelCapabilityProbe.ModelFingerprint(" "));
        Assert.Equal("", ModelCapabilityProbe.ModelFingerprint(Path.Combine(_root, "missing.gguf")));
    }

    [Fact]
    public void TemplateHash_is_stable_and_changes_with_template()
    {
        const string template = "{% for message in messages %}{{ message.role }}{% endfor %}";

        var first = ModelCapabilityProbe.TemplateHash(template);
        Assert.Equal(first, ModelCapabilityProbe.TemplateHash(template));
        Assert.Equal(16, first.Length);
        Assert.NotEqual(first, ModelCapabilityProbe.TemplateHash(template + "{# v2 #}"));
        Assert.Equal("none", ModelCapabilityProbe.TemplateHash(null));
        Assert.Equal("none", ModelCapabilityProbe.TemplateHash(""));
    }

    [Fact]
    public void TemplateDeclaresTools_requires_tool_call_markers()
    {
        Assert.True(ModelCapabilityProbe.TemplateDeclaresTools(
            "{% if tools %}{{ 'tools' }}{% endif %}{{ message.tool_calls }}"));
        Assert.False(ModelCapabilityProbe.TemplateDeclaresTools("{{ messages }}"));
        Assert.False(ModelCapabilityProbe.TemplateDeclaresTools(null));
        Assert.False(ModelCapabilityProbe.TemplateDeclaresTools(""));
    }

    [Fact]
    public async Task Failed_probe_falls_back_to_template_and_does_not_claim_measured()
    {
        var path = Path.Combine(_root, "model.gguf");
        File.WriteAllText(path, "gguf");

        var profile = await ModelCapabilityProbe.ProbeAsync(
            new ThrowingChat(), path, 4096,
            "{% if tools %}{{ tool_calls }}{% endif %}",
            CancellationToken.None);

        Assert.True(profile.SupportsNativeTools);
        Assert.False(profile.ProbedByRequest);
        Assert.Equal("模板推断", profile.ProbeSourceText);
        Assert.Equal("工具调用：原生", profile.ToolModeText);
        Assert.Equal(4096, profile.NCtx);
        Assert.Equal(ModelCapabilityProbe.TemplateHash("{% if tools %}{{ tool_calls }}{% endif %}"),
            profile.TemplateHash);
    }

    [Fact]
    public async Task Failed_probe_without_tool_markers_reports_text_protocol()
    {
        var path = Path.Combine(_root, "plain.gguf");
        File.WriteAllText(path, "gguf");

        var profile = await ModelCapabilityProbe.ProbeAsync(
            new ThrowingChat(), path, 0, "{{ messages }}", CancellationToken.None);

        Assert.False(profile.SupportsNativeTools);
        Assert.Equal("文本协议", profile.ToolModeLabel);
        Assert.Equal("工具调用：文本协议", profile.ToolModeText);
        // 未传上下文大小时给出可用的保守默认值
        Assert.Equal(8192, profile.NCtx);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试
        }
    }

    private sealed class ThrowingChat : IChatCompletion
    {
        public Task<ChatTurn> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokens,
            CancellationToken cancellationToken) =>
            Task.FromException<ChatTurn>(new InvalidOperationException("推理服务尚未就绪"));
    }
}
