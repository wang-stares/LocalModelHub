using System.IO;
using System.Text.RegularExpressions;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 全盘搜索本机已下载的 GGUF 模型：只读遍历固定磁盘，跳过系统目录与构建产物，
/// 用文件头（GGUF magic）校验真实模型，避免把同名的空文件 / 残片列出来。
/// </summary>
public sealed class LocalModelFinder
{
    public const int MaxResults = 300;

    /// <summary>小于 64 KB 的 .gguf 基本是占位文件或下载残片。</summary>
    public const long MinFileBytes = 64 * 1024;

    private const int MaxDepth = 10;
    private const int ReportEveryFiles = 500;

    private static readonly Regex ShardPattern = new(
        @"^(?<base>.+)-(?<index>\d{5})-of-(?<total>\d{5})\.gguf$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuantPattern = new(
        @"(?<quant>IQ\d+_[A-Z0-9_]+|Q\d+_K_[SML]|Q\d+_[01]|Q\d+|BF16|F16|F32)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "WinSxS", "$Recycle.Bin", "System Volume Information", "Program Files",
        "Program Files (x86)", "ProgramData", "node_modules", ".git", "obj", "bin",
        "packages", "__pycache__"
    };

    /// <summary>本机固定磁盘根目录（排除移动盘、光驱与网络盘，避免扫描时拔盘报错）。</summary>
    public static IReadOnlyList<string> FixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>遍历给定根目录，返回找到的模型；支持取消与进度上报。</summary>
    public IReadOnlyList<FoundLocalModel> Search(
        IEnumerable<string> roots,
        IProgress<FinderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<FoundLocalModel>();
        var scanned = 0;
        var reported = 0;

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (directory, depth) = pending.Pop();

                foreach (var file in SafeGgufFiles(directory))
                {
                    scanned++;
                    if (TryAccept(file, out var model))
                    {
                        results.Add(model);
                        if (results.Count >= MaxResults)
                        {
                            progress?.Report(new FinderProgress(scanned, results.Count, "已达到结果上限"));
                            return results;
                        }
                    }
                }

                if (depth < MaxDepth)
                {
                    foreach (var child in SafeDirectories(directory))
                    {
                        if (!Skip(child)) pending.Push((child, depth + 1));
                    }
                }

                if (scanned - reported >= ReportEveryFiles)
                {
                    reported = scanned;
                    progress?.Report(new FinderProgress(scanned, results.Count, directory));
                }
            }
        }

        progress?.Report(new FinderProgress(scanned, results.Count, "扫描完成"));
        return results;
    }

    private static bool TryAccept(string path, out FoundLocalModel model)
    {
        model = null!;
        if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return false;

        var name = Path.GetFileName(path);
        if (IsAuxiliary(name)) return false;

        var shard = ShardPattern.Match(name);
        if (shard.Success && int.Parse(shard.Groups["index"].Value) != 1) return false;

        if (SafeFileLength(path) < MinFileBytes) return false;
        if (!HasGgufHeader(path)) return false;

        var stem = shard.Success ? shard.Groups["base"].Value : Path.GetFileNameWithoutExtension(name);
        var quantMatch = QuantPattern.Match(stem);
        var quant = quantMatch.Success ? quantMatch.Groups["quant"].Value.ToUpperInvariant() : "";
        var display = quantMatch.Success ? stem[..quantMatch.Index].Trim('-', '_', '.', ' ') : stem;
        if (display.Length == 0) display = stem;

        model = new FoundLocalModel(
            Path.GetFullPath(path),
            display,
            quant,
            SafeFileLength(path),
            SafeModified(path));
        return true;
    }

    /// <summary>mmproj 是多模态投影，LoRA / adapter 是增量权重，都不作为可启动模型。</summary>
    private static bool IsAuxiliary(string name)
    {
        if (name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("adapter", StringComparison.OrdinalIgnoreCase)) return true;

        var lower = name.ToLowerInvariant();
        return lower.Contains("-lora-") ||
               lower.Contains("_lora_") ||
               lower.Contains("-lora.") ||
               lower.StartsWith("lora-");
    }

    private static bool HasGgufHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 4) return false;

            var header = new byte[4];
            stream.ReadExactly(header);
            return header[0] == 'G' && header[1] == 'G' && header[2] == 'U' && header[3] == 'F';
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Skip(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (SkippedNames.Contains(info.Name)) return true;
            return info.Attributes.HasFlag(FileAttributes.Hidden) ||
                   info.Attributes.HasFlag(FileAttributes.System);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static IEnumerable<string> SafeGgufFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.gguf", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive
            }).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true
            }).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static DateTime SafeModified(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTime(path) : default;
        }
        catch (IOException)
        {
            return default;
        }
    }
}

/// <summary>搜索到的一个本地模型（分片只列第一片）。</summary>
public sealed record FoundLocalModel(
    string Path,
    string Name,
    string Quant,
    long Bytes,
    DateTime ModifiedAt)
{
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";

    public string SizeText => Formatters.Bytes(Bytes);

    public string Detail => $"{(Quant.Length > 0 ? Quant : "未知量化")} · {SizeText} · {ModifiedAt:yyyy-MM-dd}";
}

/// <summary>扫描进度：已检查文件数、已找到模型数、当前目录。</summary>
public sealed record FinderProgress(int FilesScanned, int Found, string CurrentDirectory);
