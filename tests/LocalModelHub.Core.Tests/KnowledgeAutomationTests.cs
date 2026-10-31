using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class AutoMemoryExtractorTests
{
    [Fact]
    public void Explicit_remember_phrases_are_extracted()
    {
        var facts = AutoMemoryExtractor.Extract("记住，回答一律用中文，代码注释用英文。");

        Assert.Contains("回答一律用中文", facts.Single());
    }

    [Theory]
    [InlineData("我更喜欢用暗色主题，界面字体可以大一点。", "我更喜欢用暗色主题，界面字体可以大一点")]
    [InlineData("我的电脑是 8GB 显存，跑 7B 模型比较合适。", "我的电脑是 8GB 显存，跑 7B 模型比较合适")]
    [InlineData("我们项目用 .NET 8 和 Avalonia。", "我们项目用 .NET 8 和 Avalonia")]
    public void Statements_about_preferences_or_environment_are_extracted(string text, string expected)
    {
        var facts = AutoMemoryExtractor.Extract(text);

        Assert.Contains(expected, facts);
    }

    [Theory]
    [InlineData("怎么读取文件？")]
    [InlineData("这个模型支持工具调用吗？")]
    [InlineData("如果显存不够会怎么样？")]
    public void Questions_and_hypotheticals_are_ignored(string text)
    {
        Assert.Empty(AutoMemoryExtractor.Extract(text));
    }

    [Fact]
    public void At_most_three_facts_per_message()
    {
        var facts = AutoMemoryExtractor.Extract(
            "我喜欢用中文交流。我是后端开发者。我的项目用 PostgreSQL。我们团队用 Git Flow。");

        Assert.Equal(3, facts.Count);
    }
}

public sealed class TaskSkillSummarizerTests
{
    [Fact]
    public void Summarize_builds_trigger_and_numbered_steps()
    {
        var draft = TaskSkillSummarizer.Summarize(
            "把 main.cs 里的启动逻辑拆成独立方法。",
            [("read_file", "读取 main.cs"), ("replace_in_file", "修改 main.cs")]);

        Assert.NotNull(draft);
        Assert.Contains("类似请求", draft!.Trigger);
        Assert.Contains("1. 读取 main.cs", draft.Steps);
        Assert.Contains("2. 修改 main.cs", draft.Steps);
    }

    [Fact]
    public void Remember_only_activities_do_not_create_skill()
    {
        var draft = TaskSkillSummarizer.Summarize("你好", [("remember", "偏好 · 用中文")]);

        Assert.Null(draft);
    }

    [Fact]
    public void Long_requests_are_shortened()
    {
        var request = new string('长', 80) + "，请修改代码";
        var draft = TaskSkillSummarizer.Summarize(request, [("replace_in_file", "修改 Program.cs")]);

        Assert.NotNull(draft);
        Assert.True(draft!.Name.Length <= 31, "技能名称应截断");
    }

    [Fact]
    public void Lifecycle_activities_do_not_become_skill_steps()
    {
        var draft = TaskSkillSummarizer.Summarize(
            "修改配置文件",
            [("plan", "3 步"), ("read_file", "读取 a.txt"), ("replace_in_file", "修改 a.txt"), ("verify", "通过"),
             ("repair", "按问题修复")]);

        Assert.NotNull(draft);
        Assert.DoesNotContain("plan", draft!.Steps);
        Assert.DoesNotContain("verify", draft.Steps);
        Assert.DoesNotContain("repair", draft.Steps);
        Assert.Contains("2. 修改 a.txt", draft.Steps);
    }
}

public sealed class SkillServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lmh-skills-{Guid.NewGuid():N}.json");

    private SkillService Create() => new(_path);

    [Fact]
    public void Add_updates_existing_same_name_instead_of_duplicating()
    {
        var service = Create();
        service.Load();

        service.Add("修改 Program.cs", "当用户要求修改启动代码", "1. 读取\n2. 写入");
        service.Add("修改 Program.cs", "当用户要求修改启动代码", "1. 读取\n2. 修改");

        Assert.Single(service.Entries);
        Assert.Contains("2. 修改", service.Entries[0].Steps);
    }

    [Fact]
    public void Disabled_skills_are_not_injected()
    {
        var service = Create();
        service.Load();
        var entry = service.Add("修改 Program.cs", "当用户要求修改启动代码", "1. 读取 main.cs");

        service.Toggle(entry.Id);

        var context = service.BuildContext("修改 main.cs 的启动逻辑");
        Assert.Equal("", context);
    }

    [Fact]
    public void Matching_query_puts_related_skill_first_and_counts_usage()
    {
        var service = Create();
        service.Load();
        service.Add("部署到服务器", "当用户要求发布", "1. 打包");
        var target = service.Add("修改 Program.cs", "当用户要求修改启动代码", "1. 读取 main.cs");

        var context = service.BuildContext("帮我修改 main.cs 的启动逻辑");

        Assert.Contains("修改 Program.cs", context);
        Assert.Contains(target.Id, service.Entries.Select(entry => entry.Id));
        Assert.Equal(1, service.Entries.Single(entry => entry.Id == target.Id).Uses);
    }

    [Fact]
    public void Entries_survive_reload()
    {
        var service = Create();
        service.Load();
        service.Add("构建项目", "当用户要求编译", "1. dotnet build");

        var reloaded = new SkillService(_path);
        reloaded.Load();

        Assert.Single(reloaded.Entries);
        Assert.Equal("构建项目", reloaded.Entries[0].Name);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
