using System;
using System.IO;
using System.Threading.Tasks;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class FileIntegrityTests
{
    private const string SampleDigest = "sha256:74e5d8d3f4e1e2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b";

    [Fact]
    public void Normalize_accepts_prefixed_and_bare_hex()
    {
        var hex = "74E5D8D3F4E1E2B3C4D5E6F708192A3B4C5D6E7F8091A2B3C4D5E6F708192A3B";

        Assert.Equal(hex.ToLowerInvariant(), FileIntegrity.NormalizeSha256($"sha256:{hex}"));
        Assert.Equal(hex.ToLowerInvariant(), FileIntegrity.NormalizeSha256(hex));
        Assert.Equal(hex.ToLowerInvariant(), FileIntegrity.NormalizeSha256($"  SHA256:{hex}  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("md5:abc")]
    [InlineData("sha256:1234")]
    [InlineData("sha256:zzzz")]
    public void Normalize_rejects_invalid_values(string? digest)
    {
        Assert.Null(FileIntegrity.NormalizeSha256(digest));
    }

    [Fact]
    public async Task Verify_returns_true_when_source_provides_no_digest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lms-integrity-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "data");
        try
        {
            Assert.True(await FileIntegrity.VerifyFileAsync(path, null));
            Assert.True(await FileIntegrity.VerifyFileAsync(path, "not-a-digest"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Verify_matches_actual_sha256()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lms-integrity-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "LocalModelHub");
        try
        {
            var actual = await FileIntegrity.ComputeSha256Async(path);

            Assert.True(await FileIntegrity.VerifyFileAsync(path, $"sha256:{actual}"));
            Assert.False(await FileIntegrity.VerifyFileAsync(path, SampleDigest));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
