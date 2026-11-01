using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>模型自主记忆：提炼用户 / 项目信息、去重合并、按工作区作用域召回。</summary>
public sealed class MemoryDistillerTests
{
    private const string Workspace = @"C:\work\demo";

    [Fact]
    public async Task Model_output_is_written_with_kind_and_workspace_scope()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            var distiller = new MemoryDistiller(
                memory,
                (_, _, _) => Task.FromResult(
                    """{"facts":[{"text":"回答优先使用中文，代码注释用英文","kind":"user"},{"text":"项目使用 .NET 8 与 Avalonia 11，构建命令 dotnet build","kind":"project"}]}"""));

            var result = await distiller.DistillAsync(new MemoryDistillRequest(
                "我们这个项目是 .NET 8 + Avalonia，构建用 dotnet build；以后请用中文回答我。",
                "好的，我已经了解了项目结构和构建方式，后续改动都会遵循这些约定。",
                ["read_file：src/App.axaml"],
                Workspace), CancellationToken.None);

            Assert.Equal(2, result.Added);
            Assert.Equal("model", result.Source);
            Assert.Contains("2", result.StatusText);

            var user = Assert.Single(memory.Entries, entry => entry.Kind == MemoryEntry.KindUser);
            Assert.Equal("", user.Scope);

            var project = Assert.Single(memory.Entries, entry => entry.Kind == MemoryEntry.KindProject);
            Assert.Equal(Workspace, project.Scope);
            Assert.True(project.IsFromModel);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task Unparseable_model_output_falls_back_to_rule_extraction()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            var distiller = new MemoryDistiller(memory, (_, _, _) => Task.FromResult("抱歉，我无法完成这个请求。"));

            var result = await distiller.DistillAsync(new MemoryDistillRequest(
                "我更喜欢中文回答，代码注释也请用英文写。",
                "好的，之后我会一直按这个习惯回答，不再反复确认。"), CancellationToken.None);

            Assert.Equal("rules", result.Source);
            Assert.Equal(1, result.Added);
            Assert.Contains(memory.Entries, entry => entry.Text.Contains("中文回答"));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task Empty_facts_do_not_trigger_rule_fallback()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            var distiller = new MemoryDistiller(
                memory,
                (_, _, _) => Task.FromResult("""{"facts":[]}"""));

            var result = await distiller.DistillAsync(new MemoryDistillRequest(
                "我更喜欢中文回答，代码注释也请用英文写。",
                "好的，之后我会一直按这个习惯回答，不再反复确认。"), CancellationToken.None);

            Assert.Equal("model", result.Source);
            Assert.False(result.HasChanges);
            Assert.Empty(memory.Entries);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task Ephemeral_task_details_are_ignored()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            var distiller = new MemoryDistiller(
                memory,
                (_, _, _) => Task.FromResult(
                    """{"facts":[{"text":"本次修改了 src/App.axaml 的配色","kind":"project"},{"text":"项目使用 Avalonia 11 构建桌面界面","kind":"project"}]}"""));

            var result = await distiller.DistillAsync(new MemoryDistillRequest(
                "把配色改成深色，项目是 Avalonia 11 的桌面应用。",
                "已完成配色调整，并确认项目基于 Avalonia 11 桌面界面框架。",
                ["apply_patch：src/App.axaml"],
                Workspace), CancellationToken.None);

            Assert.Equal(1, result.Added);
            var entry = Assert.Single(memory.Entries);
            Assert.Contains("Avalonia", entry.Text);
            Assert.DoesNotContain("本次", entry.Text);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Project_memories_are_scoped_to_the_current_workspace()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            memory.Upsert("用户偏好中文回答", "manual");
            memory.Upsert("项目 A 使用 dotnet build 构建", "model", MemoryEntry.KindProject, @"C:\a");
            memory.Upsert("项目 B 使用 npm run build 构建", "model", MemoryEntry.KindProject, @"C:\b");

            var contextA = memory.BuildContext("构建", scope: @"C:\a");
            Assert.Contains("项目 A", contextA);
            Assert.Contains("用户偏好中文回答", contextA);
            Assert.DoesNotContain("项目 B", contextA);

            var contextB = memory.BuildContext("构建", scope: @"C:\b");
            Assert.Contains("项目 B", contextB);
            Assert.DoesNotContain("项目 A", contextB);

            // 未设置工作文件夹时只注入全局的偏好记忆。
            var contextGlobal = memory.BuildContext("构建", scope: "");
            Assert.DoesNotContain("项目 A", contextGlobal);
            Assert.DoesNotContain("项目 B", contextGlobal);
            Assert.Contains("用户偏好中文回答", contextGlobal);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Similar_project_memories_are_merged_instead_of_duplicated()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            var first = memory.Upsert("项目用 dotnet build 构建，测试用 dotnet test", "model", MemoryEntry.KindProject, Workspace);
            var second = memory.Upsert("项目用 dotnet build 构建，测试用 dotnet test 命令", "model", MemoryEntry.KindProject, Workspace);

            Assert.True(first.IsNew);
            Assert.False(second.IsNew);
            Assert.Equal(first.Entry.Id, second.Entry.Id);
            Assert.Single(memory.Entries);
            Assert.Equal("项目用 dotnet build 构建，测试用 dotnet test 命令", second.Entry.Text);

            // 同一个事实换到其它工作区时不应合并。
            var other = memory.Upsert("项目用 dotnet build 构建，测试用 dotnet test 命令", "model", MemoryEntry.KindProject, @"C:\other");
            Assert.True(other.IsNew);
            Assert.Equal(2, memory.Entries.Count);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Parser_accepts_kind_aliases_and_rejects_non_json()
    {
        Assert.True(MemoryDistiller.TryParse(
            """{"facts":[{"text":"用户偏好中文回答","kind":"项目"}]}""", out var facts));
        Assert.Equal(MemoryEntry.KindProject, facts[0].Kind);

        Assert.False(MemoryDistiller.TryParse("抱歉，我无法总结。", out var none));
        Assert.Empty(none);
    }

    [Fact]
    public async Task Secrets_and_transient_instructions_are_not_memorized()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            var distiller = new MemoryDistiller(
                memory,
                (_, _, _) => Task.FromResult(
                    """
                    {"facts":[
                      {"text":"用户的 API key 是 sk-abc123456789","kind":"user"},
                      {"text":"用户手机号 13800138000","kind":"user"},
                      {"text":"临时改用 yarn 而不是 npm","kind":"project"},
                      {"text":"项目使用 pnpm 管理依赖","kind":"project"}]}
                    """));

            var result = await distiller.DistillAsync(new MemoryDistillRequest(
                "项目依赖用 pnpm；我的手机号 13800138000，API key 是 sk-abc123456789。",
                "已了解依赖管理方式。这次先临时用 yarn，后面换回 pnpm。",
                Workspace: Workspace), CancellationToken.None);

            Assert.Equal(1, result.Added);
            Assert.Equal("项目使用 pnpm 管理依赖", Assert.Single(memory.Entries).Text);
        }
        finally
        {
            Delete(path);
        }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "lmh-distill-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
