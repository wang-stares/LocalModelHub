using System.Security.Cryptography;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 下载文件的 SHA-256 校验：运行时安装包（GitHub Releases API 的 digest）与
/// GGUF 权重（HuggingFace lfs.oid / 魔搭 Sha256 / Ollama blob 摘要）共用。
/// </summary>
public static class FileIntegrity
{
    /// <summary>把 "sha256:…" 或裸十六进制统一成小写十六进制；空值或长度不符时返回 null。</summary>
    public static string? NormalizeSha256(string? digest)
    {
        var value = (digest ?? "").Trim();
        if (value.Length == 0) return null;

        var colon = value.IndexOf(':');
        if (colon >= 0)
        {
            if (!value[..colon].Equals("sha256", StringComparison.OrdinalIgnoreCase)) return null;
            value = value[(colon + 1)..].Trim();
        }

        if (value.Length != 64) return null;
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character)) return null;
        }

        return value.ToLowerInvariant();
    }

    /// <summary>流式计算 SHA-256（按 1 MB 缓冲，避免把数 GB 权重读进内存）。</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 校验文件 SHA-256；期望值为空或无法识别时返回 true（来源未提供摘要时不阻断下载）。
    /// </summary>
    public static async Task<bool> VerifyFileAsync(
        string path,
        string? expectedDigest,
        CancellationToken cancellationToken = default)
    {
        var expected = NormalizeSha256(expectedDigest);
        if (expected is null) return true;

        var actual = await ComputeSha256Async(path, cancellationToken);
        return string.Equals(expected, actual, StringComparison.Ordinal);
    }
}
