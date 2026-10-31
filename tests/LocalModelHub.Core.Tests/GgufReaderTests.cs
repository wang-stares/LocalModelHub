using System.Text;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class GgufReaderTests
{
    [Fact]
    public void ReadsArchitectureAndBlockCount()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gguf-test-{Guid.NewGuid():N}.gguf");
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Encoding.ASCII.GetBytes("GGUF"));
                writer.Write(3u);            // version
                writer.Write(0ul);           // tensor count
                writer.Write(3ul);           // kv count

                WriteString(writer, "general.architecture");
                writer.Write(8u);            // string
                WriteString(writer, "qwen3");

                WriteString(writer, "qwen3.block_count");
                writer.Write(4u);            // uint32
                writer.Write(36u);

                WriteString(writer, "general.name");
                writer.Write(8u);
                WriteString(writer, "Qwen3-8B");
            }

            var info = GgufReader.Read(path);

            Assert.Equal("qwen3", info.Architecture);
            Assert.Equal(36, info.BlockCount);
            Assert.Equal("Qwen3-8B", info.Name);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void InvalidFile_ReturnsUnknownWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gguf-bad-{Guid.NewGuid():N}.gguf");
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("NOTGGUF"));
            var info = GgufReader.Read(path);
            Assert.Equal(0, info.BlockCount);
            Assert.Equal(7, info.FileSizeBytes);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }
}
