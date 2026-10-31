using System.Text;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class GgufDoctorTests
{
    [Fact]
    public void Reads_valid_gguf_header_and_metadata()
    {
        var path = WriteGguf(version: 3, architecture: "llama", blockCount: 32);
        try
        {
            var diagnosis = GgufDoctor.Inspect(path);

            Assert.True(diagnosis.IsUsable, diagnosis.Problem);
            Assert.Equal(3u, diagnosis.Version);
            Assert.Equal("llama", diagnosis.Architecture);
            Assert.Equal(32, diagnosis.BlockCount);
            Assert.Equal(1u, diagnosis.TensorCount);
            Assert.Contains("GGUF v3", diagnosis.Summary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rejects_non_gguf_files()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gguf-magic-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(new string('x', 64)));
        try
        {
            var diagnosis = GgufDoctor.Inspect(path);

            Assert.False(diagnosis.IsUsable);
            Assert.Contains("不是 GGUF", diagnosis.Problem);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rejects_unsupported_gguf_version()
    {
        var path = WriteGguf(version: 4, architecture: "qwen3", blockCount: 64);
        try
        {
            var diagnosis = GgufDoctor.Inspect(path);

            Assert.False(diagnosis.IsUsable);
            Assert.Contains("v4", diagnosis.Problem);
            Assert.Contains("更新推理引擎", diagnosis.Problem);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Detects_truncated_tensor_data()
    {
        var path = WriteGguf(version: 3, architecture: "qwen3", blockCount: 40, tensorOffset: 8 * 1024 * 1024);
        try
        {
            var diagnosis = GgufDoctor.Inspect(path);

            Assert.False(diagnosis.IsUsable);
            Assert.Contains("越界", diagnosis.Problem);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reports_tiny_or_missing_files()
    {
        var diagnostic = GgufDoctor.Inspect("/tmp/definitely-missing-model.gguf");
        Assert.False(diagnostic.IsUsable);
        Assert.Contains("无法读取", diagnostic.Problem);
    }

    private static string WriteGguf(
        uint version,
        string architecture,
        uint blockCount,
        ulong tensorOffset = 0)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gguf-{Guid.NewGuid():N}.gguf");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(0x46554747u); // "GGUF"
        writer.Write(version);
        writer.Write(1ul); // tensor count
        writer.Write(2ul); // metadata count

        WriteText(writer, "general.architecture");
        writer.Write(8u); // string
        WriteText(writer, architecture);

        WriteText(writer, architecture + ".block_count");
        writer.Write(4u); // uint32
        writer.Write(blockCount);

        WriteText(writer, "token_embd.weight");
        writer.Write(1u); // dimensions
        writer.Write(4ul);
        writer.Write(0u); // f32
        writer.Write(tensorOffset);

        if (tensorOffset == 0) writer.Write(new byte[64]);
        return path;
    }

    private static void WriteText(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }
}
