using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class WorkspaceRulesTests
{
    [Fact]
    public void Default_rules_cover_19_directories_and_25_binary_extensions()
    {
        Assert.Equal(19, WorkspaceRules.DefaultIgnoredDirectories.Count);
        Assert.Equal(25, WorkspaceRules.DefaultBinaryExtensions.Count);

        Assert.Contains("node_modules", WorkspaceRules.DefaultIgnoredDirectories);
        Assert.Contains(".git", WorkspaceRules.DefaultIgnoredDirectories);
        Assert.Contains(".gguf", WorkspaceRules.DefaultBinaryExtensions);

        Assert.True(WorkspaceRules.Default.IsIgnoredDirectory("NODE_MODULES"));
        Assert.True(WorkspaceRules.Default.IsBinaryFile("model.GGUF"));
        Assert.False(WorkspaceRules.Default.IsBinaryFile("Program.cs"));
        Assert.False(WorkspaceRules.Default.IsIgnoredDirectory("src"));
    }

    [Fact]
    public void Gitignore_parsing_only_keeps_plain_directory_patterns()
    {
        var lines = new[]
        {
            "# 注释",
            "",
            "dist/",
            "**/generated/",
            "/artifacts/",
            "*.log",
            "logs/**",
            "!keep/",
            "docs/guide/",
            "  build/  "
        };

        var parsed = WorkspaceRules.ParseGitIgnoreDirectories(lines);

        Assert.Equal(["dist", "generated", "artifacts", "build"], parsed);
    }

    [Fact]
    public void ForRoot_derives_directories_from_gitignore_and_refreshes_on_change()
    {
        var root = Path.Combine(Path.GetTempPath(), "lmh-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "tmp-cache/\n*.bin\n");

            var rules = WorkspaceRules.ForRoot(root);
            Assert.True(rules.IsIgnoredDirectory("tmp-cache"));
            Assert.True(rules.IsBinaryFile("payload.bin"));
            // 默认清单仍然生效
            Assert.True(rules.IsIgnoredDirectory("node_modules"));

            File.WriteAllText(Path.Combine(root, ".gitignore"), "tmp-cache/\nfresh-out/\n");
            var refreshed = WorkspaceRules.ForRoot(root);
            Assert.True(refreshed.IsIgnoredDirectory("fresh-out"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ForRoot_without_gitignore_falls_back_to_defaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "lmh-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var rules = WorkspaceRules.ForRoot(root);
            Assert.True(rules.IsIgnoredDirectory("bin"));
            Assert.False(rules.IsIgnoredDirectory("src"));
            Assert.Same(WorkspaceRules.Default, WorkspaceRules.ForRoot(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
