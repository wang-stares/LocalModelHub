using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>llama-server 启动失败时的诊断路径（使用系统自带程序模拟，不依赖 Windows 运行时）。</summary>
public class ServerStartupTests
{
    [Fact]
    public async Task Reports_exit_code_instead_of_disposed_process_error()
    {
        if (OperatingSystem.IsWindows()) return; // 依赖 /bin/true 模拟秒退进程

        using var model = TempModelFile();
        using var service = new LlamaServerService();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(
            "/bin/true", model.Path, new LaunchOptions(59123, 2048, 0, 1, "test"), CancellationToken.None));

        Assert.Contains("退出码", error.Message);
        Assert.DoesNotContain("No process is associated", error.Message);
    }

    [Fact]
    public async Task Stop_during_startup_surfaces_as_cancellation()
    {
        if (OperatingSystem.IsWindows()) return; // 依赖 shell 脚本模拟长驻进程

        var scriptDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lmh-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scriptDirectory);
        var script = System.IO.Path.Combine(scriptDirectory, "fake-server.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 30\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            using var model = TempModelFile();
            using var service = new LlamaServerService();
            var startup = service.StartAsync(script, model.Path, new LaunchOptions(59124, 2048, 0, 1, "test"), CancellationToken.None);

            await WaitUntilAsync(() => service.IsRunning, TimeSpan.FromSeconds(5));
            await service.StopAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await startup.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            try { Directory.Delete(scriptDirectory, recursive: true); } catch { /* ignore */ }
        }
    }

    [Theory]
    [InlineData(-1073741819, "0xC0000005")]
    [InlineData(-1073740791, "0xC0000409")]
    [InlineData(-1073741571, "0xC00000FD")]
    [InlineData(-1073741675, "0xC0000095")]
    public void Explains_windows_crash_exit_codes(int exitCode, string code)
    {
        var hint = LlamaServerService.ExplainExitCode(exitCode);

        Assert.Contains(code, hint);
        Assert.Contains("建议", hint);
    }

    [Fact]
    public void Normal_exit_codes_have_no_crash_hint()
    {
        Assert.Equal("", LlamaServerService.ExplainExitCode(0));
        Assert.Equal("", LlamaServerService.ExplainExitCode(1));
        Assert.Equal("", LlamaServerService.ExplainExitCode(null));
    }

    private static TempFile TempModelFile()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lmh-model-{Guid.NewGuid():N}.gguf");
        File.WriteAllBytes(path, []);
        return new TempFile(path);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        throw new TimeoutException("等待条件超时");
    }

    private sealed class TempFile(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try { File.Delete(Path); } catch { /* ignore */ }
        }
    }
}
