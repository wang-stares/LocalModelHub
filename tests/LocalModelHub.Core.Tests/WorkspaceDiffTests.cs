using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class WorkspaceDiffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-diff-{Guid.NewGuid():N}");

    public WorkspaceDiffTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Modified_file_patch_applies_to_baseline_copy()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        var before = WorkspaceDiff.Capture(_root);

        File.WriteAllText(path, "one\nTWO\nthree\n");
        var after = WorkspaceDiff.Capture(_root);

        var patch = WorkspaceDiff.BuildPatch(before, after);
        Assert.Equal(["a.txt"], patch.Modified);
        Assert.Contains("-two", patch.Text);
        Assert.Contains("+TWO", patch.Text);
        Assert.Equal(1, patch.AddedLines);
        Assert.Equal(1, patch.RemovedLines);

        var copy = Path.Combine(Path.GetTempPath(), $"lmh-diff-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(copy);
        try
        {
            File.WriteAllText(Path.Combine(copy, "a.txt"), "one\ntwo\nthree\n");
            var workspace = new WorkspaceService();
            workspace.SetRoot(copy);
            var result = workspace.ApplyPatch(patch.Text);

            Assert.True(result.Success, result.Text);
            Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(Path.Combine(copy, "a.txt")));
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }

    [Fact]
    public void Added_and_removed_files_are_reported()
    {
        File.WriteAllText(Path.Combine(_root, "gone.txt"), "bye\nold\n");
        var before = WorkspaceDiff.Capture(_root);

        File.Delete(Path.Combine(_root, "gone.txt"));
        File.WriteAllText(Path.Combine(_root, "fresh.txt"), "hello\nworld\n");
        var after = WorkspaceDiff.Capture(_root);

        var patch = WorkspaceDiff.BuildPatch(before, after);
        Assert.Equal(["fresh.txt"], patch.Added);
        Assert.Equal(["gone.txt"], patch.Removed);
        Assert.Contains("--- /dev/null", patch.Text);
        Assert.Contains("+++ b/fresh.txt", patch.Text);
        Assert.Contains("+++ /dev/null", patch.Text);
        Assert.Equal(2, patch.RemovedLines);
    }

    [Fact]
    public void Empty_files_are_skipped_because_patch_cannot_represent_them()
    {
        File.WriteAllText(Path.Combine(_root, "empty.txt"), "");
        var before = WorkspaceDiff.Capture(_root);

        File.WriteAllText(Path.Combine(_root, "empty.txt"), "content\n");
        var after = WorkspaceDiff.Capture(_root);

        var patch = WorkspaceDiff.BuildPatch(before, after);
        Assert.Equal(["empty.txt"], patch.Modified);
        Assert.Contains("+content", patch.Text);

        File.WriteAllText(Path.Combine(_root, "blank.txt"), "");
        var afterBlank = WorkspaceDiff.Capture(_root);
        var blankPatch = WorkspaceDiff.BuildPatch(after, afterBlank);

        Assert.Empty(blankPatch.Text);
        Assert.Equal(0, blankPatch.ChangedFiles);
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
}
