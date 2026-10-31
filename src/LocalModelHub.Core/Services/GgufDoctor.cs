using System.Text;

namespace LocalModelHub.Core.Services;

/// <summary>GGUF 文件的结构化诊断结果；Problem 非空表示文件本身不可加载。</summary>
public sealed record GgufDiagnostics(
    uint Version,
    string? Architecture,
    int BlockCount,
    uint TensorCount,
    long FileSizeBytes,
    string? Problem)
{
    public bool IsUsable => Problem is null;

    public string Summary => IsUsable
        ? $"GGUF v{Version} · {Architecture ?? "未知架构"} · {BlockCount} 层 · {TensorCount} 个张量 · {FileSizeBytes / 1024.0 / 1024.0:F0} MB"
        : Problem!;
}

/// <summary>在启动前检查 GGUF 头、元数据与张量偏移，区分「文件损坏/版本过新」与「参数或后端问题」，避免无谓重试。</summary>
public static class GgufDoctor
{
    private const uint Magic = 0x46554747; // "GGUF"
    private const ulong MaxEntries = 4_000_000;

    public static GgufDiagnostics Inspect(string path)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            return new GgufDiagnostics(0, null, 0, 0, 0, $"无法读取模型文件：{ex.Message}");
        }

        if (length < 32)
        {
            return new GgufDiagnostics(0, null, 0, 0, length, "文件过小（不足 32 字节），下载可能未完成或文件为空");
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != Magic)
            {
                return new GgufDiagnostics(0, null, 0, 0, length, "文件头不是 GGUF，可能下载到了网页/其他格式，或被截断");
            }

            var version = reader.ReadUInt32();
            if (version is < 2 or > 3)
            {
                return new GgufDiagnostics(
                    version, null, 0, 0, length,
                    $"GGUF v{version} 不在当前推理引擎支持的版本范围（v2/v3），请更新推理引擎或换用兼容量化");
            }

            var tensorCount = reader.ReadUInt64();
            var metadataCount = reader.ReadUInt64();
            if (tensorCount == 0)
            {
                return new GgufDiagnostics(version, null, 0, 0, length, "GGUF 中没有张量，文件不完整");
            }

            if (tensorCount > MaxEntries || metadataCount > MaxEntries)
            {
                return new GgufDiagnostics(version, null, 0, (uint)Math.Min(tensorCount, uint.MaxValue), length,
                    "元数据条目数量异常，文件可能已损坏");
            }

            string? architecture = null;
            string? blockCountKey = null;
            var blockCount = 0;

            for (ulong i = 0; i < metadataCount; i++)
            {
                var key = ReadString(reader, length);
                var type = reader.ReadUInt32();
                if (type == 8)
                {
                    var value = ReadString(reader, length);
                    if (key == "general.architecture") architecture = value;
                    else if (key.EndsWith(".block_count", StringComparison.Ordinal))
                    {
                        blockCountKey = key;
                        blockCount = int.TryParse(value, out var parsed) ? parsed : 0;
                    }
                }
                else if (type is 4 or 5)
                {
                    var value = reader.ReadUInt32();
                    if (key.EndsWith(".block_count", StringComparison.Ordinal))
                    {
                        blockCountKey = key;
                        blockCount = (int)value;
                    }
                }
                else
                {
                    SkipValue(reader, type, length);
                }
            }

            if (blockCountKey is not null && architecture is not null &&
                !blockCountKey.StartsWith(architecture + ".", StringComparison.Ordinal))
            {
                blockCount = 0;
            }

            long maxOffset = 0;
            for (ulong i = 0; i < tensorCount; i++)
            {
                ReadString(reader, length);
                var dimensions = reader.ReadUInt32();
                if (dimensions > 8) throw new InvalidDataException("张量维度数量异常");
                for (var d = 0u; d < dimensions; d++) reader.ReadUInt64();
                reader.ReadUInt32();
                var offset = reader.ReadUInt64();
                if (offset > (ulong)long.MaxValue) throw new InvalidDataException("张量偏移异常");
                if ((long)offset > maxOffset) maxOffset = (long)offset;
            }

            var dataStart = stream.Position;
            if (dataStart + maxOffset > length)
            {
                return new GgufDiagnostics(
                    version, architecture, blockCount, (uint)tensorCount, length,
                    $"张量数据偏移越界（需要 {dataStart + maxOffset:N0} 字节，实际 {length:N0} 字节），文件不完整");
            }

            return new GgufDiagnostics(version, architecture, blockCount, (uint)tensorCount, length, null);
        }
        catch (EndOfStreamException)
        {
            return new GgufDiagnostics(0, null, 0, 0, length, "元数据在文件末尾处中断，下载不完整");
        }
        catch (Exception ex)
        {
            return new GgufDiagnostics(0, null, 0, 0, length, $"解析 GGUF 失败：{ex.Message}");
        }
    }

    private static string ReadString(BinaryReader reader, long length)
    {
        var size = reader.ReadUInt64();
        if (size > (ulong)length)
        {
            throw new InvalidDataException("字符串长度越界");
        }

        var bytes = reader.ReadBytes((int)size);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void SkipValue(BinaryReader reader, uint type, long length)
    {
        switch (type)
        {
            case 0 or 1 or 7: reader.ReadByte(); break;
            case 2 or 3: reader.ReadUInt16(); break;
            case 4 or 5 or 6: reader.ReadUInt32(); break;
            case 10 or 11 or 12: reader.ReadUInt64(); break;
            case 8: ReadString(reader, length); break;
            case 9:
                var elementType = reader.ReadUInt32();
                var count = reader.ReadUInt64();
                if (count > MaxEntries) throw new InvalidDataException("数组长度异常");
                for (ulong i = 0; i < count; i++) SkipValue(reader, elementType, length);
                break;
            default:
                throw new InvalidDataException($"未知元数据类型 {type}");
        }
    }
}
