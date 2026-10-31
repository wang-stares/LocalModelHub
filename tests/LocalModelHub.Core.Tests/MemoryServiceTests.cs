using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class MemoryServiceTests
{
    [Fact]
    public void Adds_persists_searches_and_removes_memories()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            memory.Add("用户偏好中文回答，代码注释使用英文。");
            memory.Add("项目使用 .NET 8 + Avalonia 11。", "model");
            memory.Add("回答先给结论，再补充细节。");

            var reloaded = new MemoryService(path);
            var entries = reloaded.Load();
            Assert.Equal(3, entries.Count);
            Assert.Contains(entries, entry => entry.IsFromModel);

            var search = reloaded.Search("Avalonia");
            Assert.Single(search);
            Assert.Contains("Avalonia", search[0].Text);

            var context = reloaded.BuildContext("Avalonia 项目");
            Assert.Contains("长期记忆", context);
            Assert.Contains("Avalonia", context);

            Assert.True(reloaded.Remove(search[0].Id));
            Assert.Equal(2, new MemoryService(path).Load().Count);

            reloaded.Clear();
            Assert.Empty(new MemoryService(path).Load());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Rejects_empty_memory_text()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            Assert.Throws<ArgumentException>(() => memory.Add("   "));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Truncates_overlong_text_and_ignores_corrupted_file()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            var entry = memory.Add(new string('x', 900));
            Assert.Equal(MemoryService.MaxTextLength, entry.Text.Length);

            File.WriteAllText(path, "{ broken");
            Assert.Empty(new MemoryService(path).Load());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Duplicate_text_is_merged_instead_of_added_twice()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            var first = memory.Add("回答使用中文");
            var second = memory.Add("  回答使用中文  ");

            Assert.Equal(first.Id, second.Id);
            Assert.Single(memory.Entries);
            Assert.True(memory.Contains("回答使用中文"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Pinned_memories_are_always_injected()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            memory.Add("普通记忆：周末不工作");
            var pinned = memory.Add("变量命名使用英文，注释使用中文");
            Assert.True(memory.TogglePin(pinned.Id));

            var context = memory.BuildContext("完全无关的问题");
            Assert.Contains("变量命名使用英文", context);
            Assert.Contains("使用中文", pinned.Text);
            Assert.False(memory.TogglePin(pinned.Id));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Context_respects_character_budget()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            for (var index = 0; index < 6; index++)
            {
                memory.Add(new string((char)('a' + index), 300));
            }

            var context = memory.BuildContext(null, maxEntries: 6, maxChars: 500);
            Assert.True(context.Length < 700);
            Assert.Contains("长期记忆", context);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Memory_text_can_be_edited()
    {
        var path = TempPath();
        try
        {
            var memory = new MemoryService(path);
            var entry = memory.Add("旧内容");
            Assert.True(memory.UpdateText(entry.Id, "新内容"));
            Assert.Equal("新内容", memory.Entries[0].Text);
            Assert.False(memory.UpdateText(entry.Id, "  "));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "lmh-memory-" + Guid.NewGuid().ToString("N") + ".json");
}
