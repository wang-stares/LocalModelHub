using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class AgentLocalizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-loc-{Guid.NewGuid():N}");

    public AgentLocalizerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(
            Path.Combine(_root, "src", "alpha.cs"),
            "class AlphaService { }\n// AlphaService init\n// AlphaService again");
        File.WriteAllText(Path.Combine(_root, "other.cs"), "// AlphaService once");
        File.WriteAllText(Path.Combine(_root, "notes.md"), "nothing here");
    }

    [Fact]
    public void Ranks_files_by_keyword_hits()
    {
        var localization = AgentLocalizer.Localize(_root, "修复 AlphaService 的初始化逻辑");

        Assert.True(localization.Files.Count >= 2);
        Assert.Equal("src/alpha.cs", localization.Files[0].Path);
        Assert.True(localization.Files[0].Hits > localization.Files[1].Hits);
        Assert.Contains("AlphaService", localization.Format());
    }

    [Fact]
    public void ExtractTokens_prefers_file_names_and_skips_noise()
    {
        var tokens = AgentLocalizer.ExtractTokens("修改 src/app.cs 里的 FooBar 函数");

        Assert.Equal("src/app.cs", tokens[0].Text);
        Assert.Equal(4, tokens[0].Weight);
        Assert.Contains(tokens, token => token.Text == "FooBar");
        Assert.DoesNotContain(tokens, token => token.Text == "app");
    }

    [Fact]
    public void Marks_test_files_without_changing_ranking()
    {
        File.WriteAllText(
            Path.Combine(_root, "src", "AlphaServiceTests.cs"),
            "// AlphaService test");

        var localization = AgentLocalizer.Localize(_root, "修复 AlphaService 的初始化逻辑");

        Assert.Equal("src/alpha.cs", localization.Files[0].Path);
        var testFile = localization.Files.Single(file => file.Path == "src/AlphaServiceTests.cs");
        Assert.True(testFile.IsTest);
        Assert.Contains("（测试文件）", localization.Format());
        Assert.True(AgentLocalizer.IsTestPath("test/alpha.cs"));
        Assert.True(AgentLocalizer.IsTestPath("alpha.spec.ts"));
        Assert.False(AgentLocalizer.IsTestPath("contest/alpha.cs"));
    }

    [Fact]
    public void Chinese_only_request_has_no_localization()
    {
        Assert.Empty(AgentLocalizer.ExtractTokens("修复登录按钮的点击问题"));
        Assert.Empty(AgentLocalizer.Localize(_root, "修复登录按钮的点击问题").Files);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
