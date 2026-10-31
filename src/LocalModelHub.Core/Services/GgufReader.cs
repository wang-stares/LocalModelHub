using System.Text;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

public sealed record GgufInfo(string? Architecture, string? Name, int BlockCount, long FileSizeBytes)
{
    public static GgufInfo Unknown(long fileSize) => new(null, null, 0, fileSize);
}

/// <summary>极简 GGUF 头部解析：只读取架构、名称与 block_count，用于计算 GPU 卸载层数。</summary>
public static class GgufReader
{
    private const uint Magic = 0x46554747; // "GGUF"

    public static GgufInfo Read(string path)
    {
        long fileSize = 0;
        try
        {
            fileSize = new FileInfo(path).Length;
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            if (reader.ReadUInt32() != Magic) return GgufInfo.Unknown(fileSize);
            var version = reader.ReadUInt32();
            if (version is < 2 or > 3) return GgufInfo.Unknown(fileSize);

            _ = reader.ReadUInt64(); // tensor count
            var kvCount = reader.ReadUInt64();
            if (kvCount > 1_000_000) return GgufInfo.Unknown(fileSize);

            string? architecture = null;
            string? name = null;
            var blockCount = 0;

            for (ulong i = 0; i < kvCount; i++)
            {
                var key = ReadString(reader);
                var type = reader.ReadUInt32();

                if (key == "general.architecture" && type == 8)
                {
                    architecture = ReadString(reader);
                }
                else if (key == "general.name" && type == 8)
                {
                    name = ReadString(reader);
                }
                else if (key.EndsWith(".block_count", StringComparison.Ordinal) && IsInteger(type))
                {
                    blockCount = (int)ReadInteger(reader, type);
                    if (architecture is null && key.Length > ".block_count".Length)
                    {
                        architecture = key[..^".block_count".Length];
                    }
                }
                else
                {
                    SkipValue(reader, type, 0);
                }
            }

            return new GgufInfo(architecture, name, blockCount, fileSize);
        }
        catch
        {
            return GgufInfo.Unknown(fileSize);
        }
    }

    private static bool IsInteger(uint type) => type is 0 or 1 or 2 or 3 or 4 or 5 or 10 or 11;

    private static long ReadInteger(BinaryReader reader, uint type) => type switch
    {
        0 => reader.ReadByte(),
        1 => reader.ReadSByte(),
        2 => reader.ReadUInt16(),
        3 => reader.ReadInt16(),
        4 => reader.ReadUInt32(),
        5 => reader.ReadInt32(),
        10 => (long)reader.ReadUInt64(),
        11 => reader.ReadInt64(),
        _ => 0
    };

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > 32 * 1024 * 1024) throw new InvalidDataException("GGUF 字符串过长");
        var bytes = reader.ReadBytes((int)length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void SkipString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        SkipBytes(reader, length);
    }

    private static void SkipBytes(BinaryReader reader, ulong count)
    {
        var remaining = (long)Math.Min(count, long.MaxValue);
        Span<byte> buffer = stackalloc byte[8192];
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            var read = reader.Read(buffer[..chunk]);
            if (read <= 0) throw new EndOfStreamException();
            remaining -= read;
        }
    }

    private static void SkipValue(BinaryReader reader, uint type, int depth)
    {
        if (depth > 8) throw new InvalidDataException("GGUF 嵌套过深");
        switch (type)
        {
            case 0 or 1 or 7:
                SkipBytes(reader, 1);
                break;
            case 2 or 3:
                SkipBytes(reader, 2);
                break;
            case 4 or 5 or 6:
                SkipBytes(reader, 4);
                break;
            case 10 or 11 or 12:
                SkipBytes(reader, 8);
                break;
            case 8:
                SkipString(reader);
                break;
            case 9:
                var elementType = reader.ReadUInt32();
                var count = reader.ReadUInt64();
                var fixedSize = elementType switch
                {
                    0 or 1 or 7 => 1,
                    2 or 3 => 2,
                    4 or 5 or 6 => 4,
                    10 or 11 or 12 => 8,
                    _ => 0
                };
                if (fixedSize > 0)
                {
                    SkipBytes(reader, count * (ulong)fixedSize);
                }
                else
                {
                    if (count > 10_000_000) throw new InvalidDataException("GGUF 数组过大");
                    for (ulong i = 0; i < count; i++) SkipValue(reader, elementType, depth + 1);
                }

                break;
            default:
                throw new InvalidDataException($"未知 GGUF 类型 {type}");
        }
    }
}
