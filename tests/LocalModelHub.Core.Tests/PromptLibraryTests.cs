using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class PromptLibraryTests
{
    [Fact]
    public void Embedded_prompts_exist_for_every_known_name()
    {
        var library = new PromptLibrary();

        foreach (var name in PromptLibrary.KnownNames)
        {
            Assert.NotEmpty(library.Get(name));
        }

        Assert.Contains("工作文件夹", library.Get(PromptLibrary.Chat));
        Assert.Contains("目标", library.Get(PromptLibrary.Goal));
        Assert.Contains("计划", library.Get(PromptLibrary.Plan));
        Assert.Contains("Reviewer", library.Get(PromptLibrary.Reviewer));
        Assert.Contains("JSON", library.Get(PromptLibrary.Reviewer));
    }

    [Fact]
    public void Render_replaces_placeholders_and_trims()
    {
        var library = new PromptLibrary();

        var goal = library.Render(
            PromptLibrary.Goal,
            null,
            ("plan_marker", "【计划】"),
            ("verify_rule", "4. 运行测试；"));

        Assert.Contains("【计划】 计划", goal);
        Assert.Contains("4. 运行测试；", goal);
        Assert.DoesNotContain("{{", goal);
        Assert.DoesNotContain("\r", goal);
    }

    [Fact]
    public void File_override_wins_over_embedded_resource()
    {
        var root = TempDirectory("lmh-prompts-");
        try
        {
            File.WriteAllText(Path.Combine(root, "chat.md"), "自定义工作区提示：{{root}}");

            var library = new PromptLibrary(root);
            var text = library.Render(PromptLibrary.Chat, null, ("root", @"d:\demo"));

            Assert.Equal(@"自定义工作区提示：d:\demo", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Model_specific_override_wins_and_unknown_model_falls_back()
    {
        var root = TempDirectory("lmh-prompts-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "models", "qwen3-8b"));
            File.WriteAllText(Path.Combine(root, "models", "qwen3-8b", "plan.md"), "按 Qwen3 优化的计划提示");
            File.WriteAllText(Path.Combine(root, "plan.md"), "全局计划覆盖");

            var library = new PromptLibrary(root);

            Assert.Equal("按 Qwen3 优化的计划提示", library.Get(PromptLibrary.Plan, "qwen3-8b"));
            Assert.Equal("全局计划覆盖", library.Get(PromptLibrary.Plan, "other-model"));
            Assert.Equal("全局计划覆盖", library.Get(PromptLibrary.Plan));
            // 未覆盖的名称仍走嵌入资源
            Assert.Contains("目标", library.Get(PromptLibrary.Goal, "qwen3-8b"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
