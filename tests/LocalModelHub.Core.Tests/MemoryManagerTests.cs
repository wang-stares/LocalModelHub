using System.Text.Json;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>两层记忆：全局层常驻、项目层随工作区切换、层级隔离与过期清理。</summary>
public sealed class MemoryManagerTests
{
    private const string WorkspaceA = @"C:\work\alpha";
    private const string WorkspaceB = @"C:\work\beta";

    [Fact]
    public void Global_memory_survives_workspace_switches_while_project_memory_follows_the_workspace()
    {
        var directory = NewDirectory();
        try
        {
            var memory = new MemoryManager(directory);
            memory.Load();

            // 全局偏好：任何工作区都能读到。
            memory.Upsert("回答优先使用中文，代码注释用英文", "manual", MemoryEntry.KindUser, "");

            memory.UseWorkspace(WorkspaceA);
            memory.Upsert("项目使用 dotnet build 构建", "model", MemoryEntry.KindProject, memory.Workspace);
            Assert.Single(memory.ProjectEntries);

            memory.UseWorkspace(WorkspaceB);
            Assert.Empty(memory.ProjectEntries);
            Assert.Contains("回答优先使用中文", memory.BuildContext("构建", scope: WorkspaceB));
            Assert.DoesNotContain("dotnet build", memory.BuildContext("构建", scope: WorkspaceB));

            memory.UseWorkspace(WorkspaceA);
            Assert.Single(memory.ProjectEntries);
            var context = memory.BuildContext("构建", scope: WorkspaceA);
            Assert.Contains("【全局记忆", context);
            Assert.Contains("【项目记忆", context);
            Assert.Contains("dotnet build", context);
            Assert.Contains("回答优先使用中文", context);

            // 清空工作区后只保留全局记忆。
            memory.UseWorkspace(null);
            Assert.Empty(memory.ProjectEntries);
            Assert.Single(memory.GlobalEntries);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Project_memory_is_isolated_per_workspace_and_stored_in_separate_files()
    {
        var directory = NewDirectory();
        try
        {
            var memory = new MemoryManager(directory);
            memory.Load();

            memory.Upsert("用户偏好：回答用中文", "manual", MemoryEntry.KindUser, "");
            memory.UseWorkspace(WorkspaceA);
            memory.Upsert("项目约定：先写测试再实现", "model", MemoryEntry.KindProject, memory.Workspace);
            memory.UseWorkspace(WorkspaceB);
            memory.Upsert("项目约定：提交前跑 npm test", "model", MemoryEntry.KindProject, memory.Workspace);

            // 同一个工作区之外的项目记忆不会出现在当前上下文里。
            var contextB = memory.BuildContext("项目约定", scope: WorkspaceB);
            Assert.Contains("npm test", contextB);
            Assert.DoesNotContain("先写测试再实现", contextB);

            var otherScope = memory.BuildContext("项目约定", scope: @"C:\work\gamma");
            Assert.DoesNotContain("npm test", otherScope);
            Assert.DoesNotContain("先写测试再实现", otherScope);

            // 每个工作区一个独立的项目记忆文件，全局层单独一个文件。
            var projectFiles = Directory.GetFiles(Path.Combine(directory, "projects"), "*.json");
            Assert.Equal(2, projectFiles.Length);
            Assert.True(File.Exists(Path.Combine(directory, "global.json")));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Project_writes_require_an_active_workspace_and_cannot_target_another_workspace()
    {
        var directory = NewDirectory();
        try
        {
            var memory = new MemoryManager(directory);
            memory.Load();

            Assert.Throws<InvalidOperationException>(() =>
                memory.Upsert("项目事实", "model", MemoryEntry.KindProject, ""));

            memory.UseWorkspace(WorkspaceA);
            Assert.Throws<InvalidOperationException>(() =>
                memory.Upsert("项目事实", "model", MemoryEntry.KindProject, WorkspaceB));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Storage_layers_reject_cross_tier_writes()
    {
        var directory = NewDirectory();
        try
        {
            var global = new MemoryService(
                Path.Combine(directory, "global.json"), MemoryTier.Global);
            Assert.Throws<InvalidOperationException>(() =>
                global.Upsert("项目事实", "model", MemoryEntry.KindProject, WorkspaceA));

            var project = new MemoryService(
                Path.Combine(directory, "project.json"), MemoryTier.Project, WorkspaceA);
            Assert.Throws<InvalidOperationException>(() =>
                project.Upsert("用户偏好", "model", MemoryEntry.KindUser));
            Assert.Throws<InvalidOperationException>(() =>
                project.Upsert("项目事实", "model", MemoryEntry.KindProject, WorkspaceB));

            // 合法写入后，落盘内容也只有一个层级。
            global.Upsert("用户偏好中文回答", "model", MemoryEntry.KindUser, "");
            project.Upsert("项目使用 Avalonia 11", "model", MemoryEntry.KindProject, WorkspaceA);
            Assert.All(global.Entries, entry => Assert.False(entry.IsProject));
            Assert.All(project.Entries, entry => Assert.True(entry.IsProject));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Expired_project_memories_are_pruned_but_pinned_and_global_memories_survive()
    {
        var directory = NewDirectory();
        try
        {
            var memory = new MemoryManager(directory);
            memory.Load();
            memory.UseWorkspace(WorkspaceA);

            var pinned = memory.Upsert(
                "项目约定：先写测试再实现", "model", MemoryEntry.KindProject, memory.Workspace).Entry;
            memory.TogglePin(pinned.Id);
            var normal = memory.Upsert(
                "目录结构：src/Core 与 src/App", "model", MemoryEntry.KindProject, memory.Workspace).Entry;
            var global = memory.Upsert(
                "用户偏好：结论优先", "manual", MemoryEntry.KindUser, "").Entry;

            Assert.NotNull(pinned.ExpiresAt);
            Assert.NotNull(normal.ExpiresAt);
            Assert.Null(global.ExpiresAt);

            Assert.Equal(0, memory.PruneExpired(DateTime.Now.AddDays(MemoryManager.ProjectTtlDays - 5)));
            var removed = memory.PruneExpired(DateTime.Now.AddDays(MemoryManager.ProjectTtlDays + 5));

            Assert.Equal(1, removed);
            Assert.Equal(pinned.Id, Assert.Single(memory.ProjectEntries).Id);
            Assert.Equal(global.Id, Assert.Single(memory.GlobalEntries).Id);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Same_text_in_two_tiers_is_kept_separately_and_similar_project_notes_merge()
    {
        var directory = NewDirectory();
        try
        {
            var memory = new MemoryManager(directory);
            memory.Load();

            var global = memory.Upsert("构建前先更新依赖", "manual", MemoryEntry.KindUser, "").Entry;
            memory.UseWorkspace(WorkspaceA);
            var first = memory.Upsert("项目用 dotnet build 构建", "model", MemoryEntry.KindProject, memory.Workspace);
            var second = memory.Upsert("项目用 dotnet build 构建命令", "model", MemoryEntry.KindProject, memory.Workspace);

            Assert.True(first.IsNew);
            Assert.False(second.IsNew);
            Assert.Equal(first.Entry.Id, second.Entry.Id);
            Assert.Equal("项目用 dotnet build 构建命令", second.Entry.Text);
            Assert.Equal(2, memory.Entries.Count);
            Assert.NotEqual(global.Id, first.Entry.Id);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Query_update_and_delete_route_by_entry_id_across_tiers()
    {
        var directory = NewDirectory();
        try
        {
            var memory = new MemoryManager(directory);
            memory.Load();

            var global = memory.Upsert("用户偏好：中文回答", "manual", MemoryEntry.KindUser, "").Entry;
            memory.UseWorkspace(WorkspaceA);
            var project = memory.Upsert("项目：dotnet test 验证", "model", MemoryEntry.KindProject, memory.Workspace).Entry;

            Assert.Equal(MemoryTier.Global, memory.Get(global.Id)!.Tier);
            Assert.Equal(MemoryTier.Project, memory.Get(project.Id)!.Tier);
            Assert.Null(memory.Get("missing"));

            Assert.True(memory.UpdateText(project.Id, "项目：用 dotnet test 跑单元测试"));
            Assert.Equal("项目：用 dotnet test 跑单元测试", memory.Get(project.Id)!.Text);

            Assert.True(memory.Remove(project.Id));
            Assert.False(memory.Remove(project.Id));
            Assert.Single(memory.Entries);
            Assert.Equal(global.Text, Assert.Single(memory.Entries).Text);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Legacy_memory_file_is_migrated_into_global_and_project_layers()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var legacy = Path.Combine(directory, "memory.json");
            var entries = new[]
            {
                new MemoryEntry
                {
                    Id = "legacy-user",
                    Text = "用户偏好中文回答",
                    Source = "manual",
                    Kind = MemoryEntry.KindUser,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                    Pinned = true
                },
                new MemoryEntry
                {
                    Id = "legacy-project",
                    Text = "项目使用 Avalonia 11 桌面界面",
                    Source = "model",
                    Kind = MemoryEntry.KindProject,
                    Scope = WorkspaceA,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            };
            File.WriteAllText(legacy, JsonSerializer.Serialize(entries));

            var memory = new MemoryManager(directory);
            memory.Load();

            Assert.Equal("用户偏好中文回答", Assert.Single(memory.GlobalEntries).Text);
            Assert.True(memory.GlobalEntries[0].Pinned);
            Assert.False(File.Exists(legacy));
            Assert.True(File.Exists(legacy + ".migrated"));

            memory.UseWorkspace(WorkspaceA);
            Assert.Equal("项目使用 Avalonia 11 桌面界面", Assert.Single(memory.ProjectEntries).Text);
            Assert.Equal(MemoryTier.Project, memory.ProjectEntries[0].Tier);

            // 全局记忆不会因为工作区切换而丢失。
            Assert.Single(memory.GlobalEntries);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Query_context_keeps_manual_preferences_but_omits_unrelated_entries()
    {
        var directory = NewDirectory();
        try
        {
            var memory = new MemoryManager(directory);
            memory.Load();
            memory.Upsert("回答优先使用中文", "manual", MemoryEntry.KindUser, "");
            memory.UseWorkspace(WorkspaceA);
            memory.Upsert("项目使用 Avalonia 11 构建桌面界面", "model", MemoryEntry.KindProject, memory.Workspace);
            memory.Upsert("提交前运行 dotnet test 验证", "model", MemoryEntry.KindProject, memory.Workspace);

            // 无关问题：手写偏好常驻，无关项目事实不展开。
            var context = memory.BuildContext("主题颜色怎么改", scope: WorkspaceA);
            Assert.Contains("回答优先使用中文", context);
            Assert.DoesNotContain("Avalonia 11", context);
            Assert.DoesNotContain("dotnet test", context);

            // Agent 模式下提示还有多少条可按需查询，而不是全部塞进上下文。
            var withHint = memory.BuildContext(
                "主题颜色怎么改", scope: WorkspaceA, exposeRecallHint: true);
            Assert.Contains("recall_memory", withHint);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "lmh-memory-manager-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试
        }
    }
}
