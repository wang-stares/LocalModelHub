using System.Text;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class WorkspaceServiceTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceService _service = new();

    public WorkspaceServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lmh-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _service.SetRoot(_root);
    }

    [Fact]
    public void Writes_reads_and_replaces_inside_workspace()
    {
        var write = _service.WriteFile("notes/todo.md", "# 计划\n- 写测试");
        Assert.True(write.Success);
        Assert.Equal("# 计划\n- 写测试", File.ReadAllText(Path.Combine(_root, "notes", "todo.md")));

        var read = _service.ReadFile("notes/todo.md");
        Assert.True(read.Success);
        Assert.Contains("写测试", read.Text);

        var replace = _service.ReplaceInFile("notes/todo.md", "写测试", "跑测试");
        Assert.True(replace.Success);
        Assert.Contains("跑测试", File.ReadAllText(Path.Combine(_root, "notes", "todo.md")));
    }

    [Fact]
    public void Rejects_parent_directory_traversal()
    {
        var result = _service.WriteFile("../escaped.txt", "nope");

        Assert.False(result.Success);
        Assert.Contains("超出工作文件夹", result.Text);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.txt")));
    }

    [Fact]
    public void Rejects_absolute_paths()
    {
        var target = Path.Combine(Path.GetTempPath(), "lmh-abs-" + Guid.NewGuid().ToString("N") + ".txt");

        var result = _service.WriteFile(target, "nope");

        Assert.False(result.Success);
        Assert.Contains("相对路径", result.Text);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Rejects_symlink_escape()
    {
        var outside = Path.Combine(Path.GetTempPath(), "lmh-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);
        }
        catch (Exception)
        {
            return; // 当前平台不允许创建符号链接时跳过
        }

        var result = _service.ReadFile("link/secret.txt");

        Assert.False(result.Success);
        Assert.Contains("符号链接", result.Text);

        Directory.Delete(outside, recursive: true);
    }

    [Fact]
    public void Lists_files_with_relative_paths_and_skips_outside()
    {
        _service.WriteFile("src/main.cs", "class A { }");

        var result = _service.ListFiles(".");

        Assert.True(result.Success);
        Assert.Contains("src/", result.Text);
        Assert.Contains("src/main.cs", result.Text);
    }

    [Fact]
    public void Reads_line_window_with_numbers_and_truncation_hint()
    {
        var content = string.Join('\n', Enumerable.Range(1, 300).Select(index => $"line {index}"));
        _service.WriteFile("big.txt", content);

        var first = _service.ReadFile("big.txt");
        Assert.True(first.Success);
        Assert.Contains("第 1-200 行 / 共 300 行", first.Text);
        Assert.Contains("1| line 1", first.Text);
        Assert.Contains("200| line 200", first.Text);
        Assert.DoesNotContain("line 300", first.Text);
        Assert.Contains("start_line", first.Text);

        var later = _service.ReadFile("big.txt", 250, 260);
        Assert.Contains("250| line 250", later.Text);
        Assert.Contains("260| line 260", later.Text);
        Assert.DoesNotContain("line 249", later.Text);
    }

    [Fact]
    public void ReadFile_window_size_is_configurable()
    {
        var content = string.Join('\n', Enumerable.Range(1, 300).Select(index => $"line {index}"));
        _service.WriteFile("big.txt", content);
        _service.DefaultReadLines = 50;
        _service.MaxReadWindow = 120;

        var first = _service.ReadFile("big.txt");
        Assert.Contains("第 1-50 行 / 共 300 行", first.Text);
        Assert.Contains("50| line 50", first.Text);
        Assert.DoesNotContain("51| line 51", first.Text);

        var wide = _service.ReadFile("big.txt", 1, 500);
        Assert.Contains("第 1-120 行 / 共 300 行", wide.Text);
        Assert.DoesNotContain("121| line 121", wide.Text);
        Assert.Contains("单次最多 120 行", wide.Text);
    }

    [Fact]
    public void Search_matches_lines_and_honors_include_filter()
    {
        _service.WriteFile("src/a.cs", "class Alpha { }\n// TODO fix alpha");
        _service.WriteFile("src/b.py", "def beta():\n    pass");

        var scoped = _service.Search("alpha", "*.cs");

        Assert.True(scoped.Success);
        Assert.Contains("src/a.cs:2", scoped.Text);
        Assert.DoesNotContain("b.py", scoped.Text);
    }

    [Fact]
    public void Search_skips_dependency_directories_and_handles_invalid_regex()
    {
        _service.WriteFile("node_modules/pkg/index.js", "needle");
        _service.WriteFile("x.txt", "foo[ bar");

        Assert.Equal("没有匹配结果", _service.Search("needle", null).Text);
        Assert.Contains("x.txt:1", _service.Search("foo[", null).Text);
    }

    [Fact]
    public void Search_expands_context_around_hits()
    {
        _service.WriteFile("src/a.cs", "line1\nline2\nTODO fix\nalpha\nline5\nline6");

        var result = _service.Search("TODO", null, 24, 1);

        Assert.True(result.Success);
        Assert.Contains("src/a.cs:3（第 2-4 行）", result.Text);
        Assert.Contains("2| line2", result.Text);
        Assert.Contains("3|>TODO fix", result.Text);
        Assert.Contains("4| alpha", result.Text);
        Assert.DoesNotContain("5| line5", result.Text);
    }

    [Fact]
    public void Search_with_context_caps_result_count()
    {
        var content = string.Join('\n', Enumerable.Range(1, 20).Select(index => $"TODO {index}"));
        _service.WriteFile("many.txt", content);

        var result = _service.Search("TODO", null, 40, 2);

        Assert.Contains("12 条上限", result.Text);
        Assert.Equal(12, result.Text.Split("many.txt:").Length - 1);
    }

    [Fact]
    public void ApplyPatch_applies_multiple_hunks()
    {
        _service.WriteFile("src/app.cs", "line1\nline2\nline3\nline4\nline5");

        var patch = """
            --- a/src/app.cs
            +++ b/src/app.cs
            @@ -1,3 +1,3 @@
             line1
            -line2
            +line2-changed
             line3
            @@ -4,2 +4,3 @@
             line4
             line5
            +line6
            """;

        var result = _service.ApplyPatch(patch);

        Assert.True(result.Success);
        Assert.Contains("src/app.cs", result.Text);
        Assert.Equal(
            "line1\nline2-changed\nline3\nline4\nline5\nline6",
            File.ReadAllText(Path.Combine(_root, "src", "app.cs")));
    }

    [Fact]
    public void FindSymbols_matches_definitions_not_call_sites()
    {
        _service.WriteFile(
            "src/symbols.cs",
            "public class AlphaService\n{\n    public void Initialize()\n    {\n        if (ready) { ReturnSomething(); }\n    }\n}\n");

        var classes = _service.FindSymbols("AlphaService");
        Assert.True(classes.Success);
        Assert.Contains("src/symbols.cs:1: public class AlphaService", classes.Text);

        var methods = _service.FindSymbols("initialize");
        Assert.Contains("src/symbols.cs:3: public void Initialize()", methods.Text);
        Assert.DoesNotContain("ReturnSomething", methods.Text);
    }

    [Fact]
    public void FindSymbols_reports_missing_definition()
    {
        _service.WriteFile("src/a.cs", "public class Alpha { }");

        var result = _service.FindSymbols("Beta");

        Assert.True(result.Success);
        Assert.Contains("没有找到", result.Text);
    }

    [Fact]
    public void PatchTargets_skips_dev_null()
    {
        var patch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -old
            +new
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,1 @@
            +hello
            """;

        var targets = WorkspaceService.PatchTargets(patch);

        Assert.Equal(["a.txt", "new.txt"], targets);
    }

    [Fact]
    public void ApplyPatch_rejects_context_mismatch_without_writing()
    {
        _service.WriteFile("a.txt", "alpha\nbeta");
        var before = File.ReadAllText(Path.Combine(_root, "a.txt"));

        var patch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,2 @@
             alpha
            -gamma
            +delta
            """;

        var result = _service.ApplyPatch(patch);

        Assert.False(result.Success);
        Assert.Contains("上下文", result.Text);
        Assert.Contains("第 1 行", result.Text);
        Assert.Contains("read_file", result.Text);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public void ApplyPatch_is_atomic_across_files()
    {
        _service.WriteFile("ok.txt", "one\ntwo");
        _service.WriteFile("bad.txt", "alpha\nbeta");

        var patch = """
            --- a/ok.txt
            +++ b/ok.txt
            @@ -1,2 +1,2 @@
             one
            -two
            +three
            --- a/bad.txt
            +++ b/bad.txt
            @@ -1,2 +1,2 @@
             alpha
            -gamma
            +delta
            """;

        var result = _service.ApplyPatch(patch);

        Assert.False(result.Success);
        Assert.Equal("one\ntwo", File.ReadAllText(Path.Combine(_root, "ok.txt")));
        Assert.Equal("alpha\nbeta", File.ReadAllText(Path.Combine(_root, "bad.txt")));
    }

    [Fact]
    public void ApplyPatch_writes_multiple_files_and_leaves_no_temp_files()
    {
        _service.WriteFile("a.txt", "one\ntwo");

        var patch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,2 @@
             one
            -two
            +three
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,1 @@
            +created
            """;

        var result = _service.ApplyPatch(patch);

        Assert.True(result.Success);
        Assert.Equal("one\nthree", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("created\n", File.ReadAllText(Path.Combine(_root, "new.txt")));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains(".lmh-", StringComparison.Ordinal)));
    }

    [Fact]
    public void ApplyPatch_creates_new_file_from_dev_null()
    {
        var patch = """
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,2 @@
            +hello
            +world
            """;

        var result = _service.ApplyPatch(patch);

        Assert.True(result.Success);
        Assert.Equal("hello\nworld\n", File.ReadAllText(Path.Combine(_root, "new.txt")));
    }

    [Fact]
    public void ApplyPatch_rejects_hunk_line_count_mismatch()
    {
        _service.WriteFile("a.txt", "alpha\nbeta\ngamma");

        var patch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,3 @@
             alpha
            -beta
            +BETA
            """;

        var result = _service.ApplyPatch(patch);

        Assert.False(result.Success);
        Assert.Contains("行数", result.Text);
        Assert.Equal("alpha\nbeta\ngamma", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public void ReplaceInFile_matches_lf_text_in_crlf_file_and_keeps_crlf()
    {
        var path = Path.Combine(_root, "crlf.cs");
        File.WriteAllText(path, "line1\r\nline2\r\nline3\r\nline4\r\n");

        var result = _service.ReplaceInFile("crlf.cs", "line2\nline3", "LINE2\nLINE3");

        Assert.True(result.Success);
        Assert.Equal("line1\r\nLINE2\r\nLINE3\r\nline4\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void ReplaceInFile_preserves_utf8_bom()
    {
        var path = Path.Combine(_root, "bom.txt");
        File.WriteAllText(path, "alpha\nbeta", new UTF8Encoding(true));

        var result = _service.ReplaceInFile("bom.txt", "alpha", "gamma");

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path).Take(3).ToArray());
        Assert.Equal("gamma\nbeta", File.ReadAllText(path));
    }

    [Fact]
    public void ReplaceInFile_reports_line_ending_style_when_text_is_missing()
    {
        var path = Path.Combine(_root, "crlf-miss.txt");
        File.WriteAllText(path, "line1\r\nline2\r\nline3\r\n");

        var result = _service.ReplaceInFile("crlf-miss.txt", "missing", "x");

        Assert.False(result.Success);
        Assert.Contains("CRLF", result.Text);
        Assert.Contains("old_text 归一化后有 1 行", result.Text);
    }

    [Fact]
    public void WriteFile_preserves_crlf_style_of_existing_file()
    {
        var path = Path.Combine(_root, "keep-crlf.txt");
        File.WriteAllText(path, "old1\r\nold2\r\n");

        var result = _service.WriteFile("keep-crlf.txt", "new1\nnew2");

        Assert.True(result.Success);
        Assert.Equal("new1\r\nnew2", File.ReadAllText(path));
    }

    [Fact]
    public void ApplyPatch_keeps_crlf_file_style()
    {
        var path = Path.Combine(_root, "crlf-patch.txt");
        File.WriteAllText(path, "one\r\ntwo\r\n");

        var patch = """
            --- a/crlf-patch.txt
            +++ b/crlf-patch.txt
            @@ -1,2 +1,2 @@
             one
            -two
            +three
            """;

        var result = _service.ApplyPatch(patch);

        Assert.True(result.Success);
        Assert.Equal("one\r\nthree\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Search_sees_external_changes_after_index_warmup()
    {
        _service.WriteFile("notes.txt", "alpha");
        Assert.Contains("notes.txt:1", _service.Search("alpha", null).Text);

        // 模拟其它进程（编辑器 / run_command）改写文件：大小与修改时间都会变化
        var path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "beta content, longer than the previous line");

        Assert.Equal("没有匹配结果", _service.Search("alpha", null).Text);
        Assert.Contains("notes.txt:1", _service.Search("beta", null).Text);
    }

    [Fact]
    public void Search_reflects_write_file_immediately_after_index_warmup()
    {
        _service.WriteFile("a.cs", "old");
        Assert.Contains("a.cs:1", _service.Search("old", null).Text);

        _service.WriteFile("a.cs", "new value");

        Assert.Contains("a.cs:1", _service.Search("new", null).Text);
        Assert.Equal("没有匹配结果", _service.Search("old", null).Text);
    }

    [Fact]
    public void Search_reports_scan_limit_when_workspace_exceeds_index_cap()
    {
        var folder = Path.Combine(_root, "many");
        Directory.CreateDirectory(folder);
        for (var index = 0; index <= 4000; index++)
        {
            File.WriteAllText(Path.Combine(folder, $"f{index}.txt"), "zzz");
        }

        var result = _service.Search("needle", null);

        Assert.True(result.Success);
        Assert.Contains("没有匹配结果", result.Text);
        Assert.Contains("已扫描 4000 个文件", result.Text);
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
