using System;
using System.IO;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class RuntimeManagerTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090", true)]
    [InlineData("NVIDIA GeForce RTX 5060 Ti", true)]
    [InlineData("NVIDIA B200", true)]
    [InlineData("NVIDIA Blackwell", true)]
    [InlineData("NVIDIA GeForce RTX 4060", false)]
    [InlineData("NVIDIA GeForce RTX 4090", false)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsBlackwell_matches_rtx50_and_b100_b200(string? name, bool expected)
    {
        Assert.Equal(expected, RuntimeManager.IsBlackwell(name));
    }

    [Fact]
    public void SelectObsolete_keeps_current_and_newest_fallback()
    {
        var installed = new (string Directory, bool IsCurrent, DateTime ModifiedUtc)[]
        {
            ("b1-cpu", false, new DateTime(2026, 1, 1)),
            ("b2-cpu", false, new DateTime(2026, 3, 1)),
            ("b3-cuda", true, new DateTime(2026, 2, 1)),
            ("b4-vulkan", false, new DateTime(2026, 4, 1))
        };

        Assert.Equal(["b1-cpu", "b2-cpu"], RuntimeManager.SelectObsolete(installed));
    }

    [Fact]
    public void SelectObsolete_without_current_version_keeps_only_newest()
    {
        var installed = new (string Directory, bool IsCurrent, DateTime ModifiedUtc)[]
        {
            ("b1-cpu", false, new DateTime(2026, 1, 1)),
            ("b2-cpu", false, new DateTime(2026, 2, 1))
        };

        Assert.Equal(["b1-cpu"], RuntimeManager.SelectObsolete(installed));
    }

    [Fact]
    public void CudaCandidates_prefers_cuda13_for_blackwell_with_new_driver()
    {
        Assert.Equal(
            ["cuda-13.4", "cuda-12.4", "vulkan", "cpu"],
            RuntimeManager.CudaCandidates(true, new Version(572, 16)));
    }

    [Fact]
    public void CudaCandidates_keeps_cuda12_for_blackwell_with_old_driver()
    {
        Assert.Equal(
            ["cuda-12.4", "cuda-13.4", "vulkan", "cpu"],
            RuntimeManager.CudaCandidates(true, new Version(565, 90)));
    }

    [Fact]
    public void CudaCandidates_keeps_cuda12_for_older_gpus()
    {
        Assert.Equal(
            ["cuda-12.4", "cuda-13.4", "vulkan", "cpu"],
            RuntimeManager.CudaCandidates(false, new Version(590, 10)));
        Assert.Equal(
            ["cuda-12.4", "cuda-13.4", "vulkan", "cpu"],
            RuntimeManager.CudaCandidates(true, null));
    }

    [Fact]
    public void CudaCandidates_always_end_with_vulkan_then_cpu()
    {
        Assert.Equal(["vulkan", "cpu"], RuntimeManager.CudaCandidates(true, new Version(572, 16))[^2..]);
        Assert.Equal(["vulkan", "cpu"], RuntimeManager.CudaCandidates(false, null)[^2..]);
    }

    [Fact]
    public void VerifyServer_reports_failure_for_missing_binary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"llama-server-missing-{Guid.NewGuid():N}");

        Assert.False(RuntimeManager.VerifyServer(path, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void VerifyServer_rejects_unexecutable_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"llama-server-broken-{Guid.NewGuid():N}");
        File.WriteAllText(path, "");
        try
        {
            Assert.False(RuntimeManager.VerifyServer(path, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { /* 测试清理 */ }
        }
    }

    [Fact]
    public void ParseAssetDigests_reads_name_to_sha256()
    {
        const string json = """
        {
          "tag_name": "b11963",
          "assets": [
            { "name": "llama-b11963-bin-win-cuda-12.4-x64.zip", "digest": "sha256:70ba73e7554acd3319c74bb8f7d2cf01c8524cc4849b4e069b0a1e0426d9b2e4" },
            { "name": "cudart-llama-bin-win-cuda-13.4-x64.zip", "digest": "sha256:738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668" },
            { "name": "nightly-tag.txt" },
            { "name": "broken.zip", "digest": "md5:abc" }
          ]
        }
        """;

        var digests = RuntimeManager.ParseAssetDigests(json);

        Assert.Equal(2, digests.Count);
        Assert.Equal(
            "sha256:70ba73e7554acd3319c74bb8f7d2cf01c8524cc4849b4e069b0a1e0426d9b2e4",
            digests["llama-b11963-bin-win-cuda-12.4-x64.zip"]);
        Assert.False(digests.ContainsKey("nightly-tag.txt"));
        Assert.False(digests.ContainsKey("broken.zip"));
    }

    [Fact]
    public void ParseAssetDigests_returns_empty_for_invalid_json()
    {
        Assert.Empty(RuntimeManager.ParseAssetDigests("not json"));
        Assert.Empty(RuntimeManager.ParseAssetDigests("""{"assets":[]}"""));
    }

    [Fact]
    public void VerifyServer_accepts_runnable_binary()
    {
        // Windows 上需要真实的 llama-server.exe，这里只在 POSIX 上验证成功路径。
        if (OperatingSystem.IsWindows()) return;
        var shell = "/bin/bash";
        if (!File.Exists(shell)) return;

        Assert.True(RuntimeManager.VerifyServer(shell, out var error), error);
    }
}
