using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>扫描模型目录，维护“我的模型”列表与本地导入。</summary>
public sealed class ModelLibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>分片命名：model-Q4_K_M-00001-of-00004.gguf。</summary>
    private static readonly Regex ShardPattern = new(
        @"^(?<base>.+)-(?<index>\d{5})-of-(?<total>\d{5})\.gguf$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly SettingsService _settings;
    private readonly object _sync = new();

    public ModelLibraryService(SettingsService settings)
    {
        _settings = settings;
    }

    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public IReadOnlyList<DownloadedModel> Scan(ModelCatalog catalog)
    {
        lock (_sync)
        {
            var result = new List<DownloadedModel>();
            var root = _settings.Settings.ModelsDirectory;
            if (!Directory.Exists(root)) return result;

            foreach (var modelDirectory in SafeDirectories(root))
            {
                var subDirectories = SafeDirectories(modelDirectory);
                var rootFiles = SafeGgufFiles(modelDirectory);
                if (rootFiles.Count > 0)
                {
                    TryAdd(result, catalog, modelDirectory, modelDirectory, rootFiles);
                }

                foreach (var quantDirectory in subDirectories)
                {
                    var files = SafeGgufFiles(quantDirectory);
                    if (files.Count > 0)
                    {
                        TryAdd(result, catalog, modelDirectory, quantDirectory, files);
                    }
                }
            }

            return result.OrderByDescending(m => m.AddedAt).ToList();
        }
    }

    private static void TryAdd(
        List<DownloadedModel> result,
        ModelCatalog catalog,
        string modelDirectory,
        string folder,
        List<string> files)
    {
        try
        {
            var manifest = ReadManifest(folder) ?? ReadManifest(modelDirectory);
            var modelId = manifest?.ModelId ?? Path.GetFileName(modelDirectory);
            var entry = catalog.Models.FirstOrDefault(m => m.Id == modelId);
            var quantLabel = manifest?.Quant;
            if (string.IsNullOrWhiteSpace(quantLabel))
            {
                quantLabel = entry is not null && entry.Quants.Any(q => q.Label.Equals(Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase))
                    ? Path.GetFileName(folder)
                    : "Q4_K_M";
            }

            // 同一目录里可能堆着多个量化，只记录真正属于本模型的文件，删除时才不会误伤同目录其它量化
            var owned = ResolveOwnedFiles(modelDirectory, folder, files, manifest, quantLabel!);
            // 主文件不能按文件名排序取第一个：mmproj / LoRA 会排在前面，分片也要从 00001 开始。
            var primary = SelectPrimary(owned, quantLabel!, out var selectionNote);
            var total = owned.Sum(SafeFileLength);
            var addedAt = manifest?.AddedAt ?? Directory.GetCreationTime(folder);

            result.Add(new DownloadedModel
            {
                ModelId = modelId,
                DisplayName = entry?.DisplayName ?? manifest?.DisplayName ?? Path.GetFileName(folder),
                QuantLabel = quantLabel!,
                FolderPath = folder,
                PrimaryPath = primary,
                SelectionNote = selectionNote,
                Files = owned,
                TotalBytes = total,
                AddedAt = addedAt,
                Developer = entry?.Developer ?? (manifest?.Imported == true ? "本地导入" : null),
                ParametersB = entry?.ParametersB ?? 0,
                ContextLength = entry?.ContextLength ?? 0,
                Entry = entry,
                Quant = entry?.Quants.FirstOrDefault(q => q.Label.Equals(quantLabel, StringComparison.OrdinalIgnoreCase)) ?? entry?.PrimaryQuant
            });
        }
        catch
        {
            // 单个模型目录异常不影响整体扫描
        }
    }

    public async Task<DownloadedModel> ImportAsync(
        string sourceFile,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourceFile)) throw new FileNotFoundException("未找到 GGUF 文件", sourceFile);
        if (!sourceFile.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择 .gguf 模型文件");

        var name = Path.GetFileNameWithoutExtension(sourceFile);
        var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var targetDirectory = Path.Combine(
            _settings.Settings.ModelsDirectory, "imports", $"{safeName}-{DateTime.Now:yyyyMMddHHmmss}");
        Directory.CreateDirectory(targetDirectory);

        var destination = Path.Combine(targetDirectory, Path.GetFileName(sourceFile));
        var sourceInfo = new FileInfo(sourceFile);
        var buffer = new byte[1024 * 1024];
        await using (var input = File.OpenRead(sourceFile))
        await using (var output = File.Create(destination))
        {
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                if (sourceInfo.Length > 0) progress?.Report(copied * 100d / sourceInfo.Length);
            }
        }

        var manifest = new Manifest
        {
            ModelId = $"local/{safeName}",
            DisplayName = name,
            Quant = "本地",
            Imported = true,
            AddedAt = DateTime.Now,
            Files = [new ManifestFile { RelativePath = Path.GetFileName(sourceFile), Bytes = sourceInfo.Length }]
        };
        await File.WriteAllTextAsync(
            Path.Combine(targetDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken);

        NotifyChanged();
        return new DownloadedModel
        {
            ModelId = manifest.ModelId,
            DisplayName = name,
            QuantLabel = "本地",
            FolderPath = targetDirectory,
            PrimaryPath = destination,
            Files = [destination],
            TotalBytes = sourceInfo.Length,
            AddedAt = manifest.AddedAt,
            Developer = "本地导入"
        };
    }

    public sealed record ModelDeleteResult(
        bool Success,
        string? Error,
        int DeletedFiles = 0,
        long ReleasedBytes = 0,
        bool UsedRecycleBin = false,
        int RemainingSiblingFiles = 0);

    /// <summary>删除前展示给用户的清单：会删哪些文件、共多大、同目录还剩多少。</summary>
    public sealed record ModelDeletePlan(
        IReadOnlyList<string> Files,
        long TotalBytes,
        int RemainingSiblingFiles);

    /// <summary>计算删除清单，供确认框展示；不修改磁盘。</summary>
    public ModelDeletePlan PlanDelete(DownloadedModel model)
    {
        var target = SafeFullPath(model.FolderPath);
        if (target is null) return new ModelDeletePlan([], 0, 0);

        var files = ResolveDeleteTargets(model, target);
        var remaining = Directory.Exists(target)
            ? SafeGgufFiles(target).Count(f => !files.Contains(f, StringComparer.OrdinalIgnoreCase))
            : 0;
        return new ModelDeletePlan(files, files.Sum(SafeFileLength), remaining);
    }

    /// <summary>
    /// 只删除属于该模型的文件，而不是整个目录；Windows 下默认移入回收站。
    /// 删除失败返回具体文件名，便于界面提示「文件仍占用」而不是让列表看起来像只做了 UI。
    /// </summary>
    public ModelDeleteResult Delete(DownloadedModel model, bool useRecycleBin = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(model.FolderPath))
            {
                return new ModelDeleteResult(false, "模型路径为空，未做任何删除");
            }

            var root = SafeFullPath(_settings.Settings.ModelsDirectory);
            var target = SafeFullPath(model.FolderPath);
            if (root is null || target is null)
            {
                return new ModelDeleteResult(false, "模型路径无效，未做任何删除");
            }

            if (!IsUnder(root, target))
            {
                return new ModelDeleteResult(false, "拒绝删除模型目录之外的路径");
            }

            var targets = ResolveDeleteTargets(model, target);
            if (targets.Count == 0)
            {
                return new ModelDeleteResult(false, "模型文件已不存在，列表已刷新");
            }

            var siblings = Directory.Exists(target)
                ? SafeGgufFiles(target)
                : [];
            var remaining = siblings.Count(f => !targets.Contains(f, StringComparer.OrdinalIgnoreCase));

            var deleted = 0;
            var released = 0L;
            var recycled = false;
            var failures = new List<(string Path, string Reason)>();

            foreach (var file in targets)
            {
                var size = SafeFileLength(file);
                try
                {
                    var movedToRecycleBin = useRecycleBin && WindowsNative.TryDeleteToRecycleBin(file);
                    if (movedToRecycleBin)
                    {
                        recycled = true;
                    }
                    else
                    {
                        File.Delete(file);
                    }

                    if (File.Exists(file))
                    {
                        failures.Add((file, "文件仍存在，可能被其它程序占用"));
                        continue;
                    }

                    deleted++;
                    released += size;
                }
                catch (Exception ex)
                {
                    failures.Add((file, ex.Message));
                }
            }

            // 只有目录为空时才移除，最多向上清理到模型根目录为止
            RemoveEmptyDirectories(
                Directory.Exists(target) ? target : Path.GetDirectoryName(target),
                root);

            if (failures.Count > 0)
            {
                var names = string.Join("、", failures.Take(3).Select(f => Path.GetFileName(f.Path)));
                return new ModelDeleteResult(
                    false,
                    $"仍有 {failures.Count} 个文件未删除（{names}）：{failures[0].Reason}",
                    deleted, released, recycled, remaining);
            }

            return new ModelDeleteResult(true, null, deleted, released, recycled, remaining);
        }
        catch (Exception ex)
        {
            return new ModelDeleteResult(false, $"删除失败：{ex.Message}");
        }
        finally
        {
            NotifyChanged();
        }
    }

    /// <summary>manifest 优先；没有清单时按量化标签匹配，匹配不到只取主文件，避免误删同目录其它量化。</summary>
    private static List<string> ResolveOwnedFiles(
        string modelDirectory,
        string folder,
        List<string> files,
        Manifest? manifest,
        string quantLabel)
    {
        var sorted = files.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();

        if (manifest?.Files is { Count: > 0 } declared)
        {
            var names = declared
                .Select(f => Path.GetFileName(f.RelativePath ?? ""))
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matched = sorted.Where(f => names.Contains(Path.GetFileName(f))).ToList();
            if (matched.Count > 0) return matched;
        }

        // 量化子目录里的 gguf 都属于该量化
        if (!string.Equals(folder, modelDirectory, StringComparison.OrdinalIgnoreCase)) return sorted;

        var byQuant = sorted
            .Where(f => Path.GetFileName(f).Contains(quantLabel, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byQuant.Count > 0) return byQuant;

        // 标签对不上文件名时按最大主文件兜底，并把同组分片一起纳入，保证能完整删除。
        var primary = SelectPrimary(sorted, quantLabel, out _);
        return ExpandShardGroup(sorted, primary);
    }

    /// <summary>
    /// 选择主模型文件：排除多模态投影 / LoRA / 适配器，量化标签优先，分片从 00001 开始；
    /// 仍无法判定时取体积最大的 GGUF，并返回「自动选择」提示。
    /// </summary>
    private static string SelectPrimary(List<string> owned, string quantLabel, out string? note)
    {
        note = null;
        var main = owned.Where(file => !IsAuxiliaryFile(file)).ToList();
        if (main.Count == 0) main = owned;

        if (quantLabel.Length > 0)
        {
            var byQuant = main
                .Where(file => Path.GetFileName(file).Contains(quantLabel, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byQuant.Count > 0) main = byQuant;
        }

        var firstShard = main.FirstOrDefault(IsFirstShard);
        if (firstShard is not null) return firstShard;

        if (main.Count == 1) return main[0];

        var largest = main
            .OrderByDescending(SafeFileLength)
            .ThenBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .First();
        note = "自动选择，可手动切换";
        return largest;
    }

    /// <summary>mmproj 是多模态投影，LoRA / adapter 是增量权重，都不应作为主模型启动。</summary>
    private static bool IsAuxiliaryFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("adapter", StringComparison.OrdinalIgnoreCase)) return true;

        var lower = name.ToLowerInvariant();
        return lower.Contains("-lora-") || lower.Contains("_lora_") || lower.StartsWith("lora-");
    }

    private static bool IsFirstShard(string path)
    {
        var match = ShardPattern.Match(Path.GetFileName(path));
        return match.Success && int.Parse(match.Groups["index"].Value) == 1;
    }

    /// <summary>把同组其它分片补进文件清单，删除 / 统计体积时按整个逻辑模型处理。</summary>
    private static List<string> ExpandShardGroup(List<string> files, string primary)
    {
        var match = ShardPattern.Match(Path.GetFileName(primary));
        if (!match.Success) return [primary];

        var stem = match.Groups["base"].Value;
        var total = match.Groups["total"].Value;
        var group = files
            .Where(file =>
            {
                var shard = ShardPattern.Match(Path.GetFileName(file));
                return shard.Success &&
                       shard.Groups["base"].Value.Equals(stem, StringComparison.OrdinalIgnoreCase) &&
                       shard.Groups["total"].Value == total;
            })
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return group.Count > 0 ? group : [primary];
    }

    private static List<string> ResolveDeleteTargets(DownloadedModel model, string target)
    {
        if (target.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return [];

        if (model.Files is { Count: > 0 } declared)
        {
            var allowed = declared
                .Select(SafeFullPath)
                .Where(path => path is not null && IsUnder(target, path) && File.Exists(path))
                .Select(path => path!)
                .ToList();
            if (allowed.Count > 0) return allowed;
        }

        if (File.Exists(target)) return [target];
        if (!Directory.Exists(target)) return [];

        var files = SafeGgufFiles(target);
        if (files.Count == 0) return [];

        var byQuant = files
            .Where(f => Path.GetFileName(f).Contains(model.QuantLabel, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byQuant.Count > 0) return byQuant;

        // 标签对不上文件名时只删主文件；单文件目录才允许整目录删除
        var primary = SafeFullPath(model.PrimaryPath);
        if (primary is not null && IsUnder(target, primary) && File.Exists(primary)) return [primary];
        return files.Count == 1 ? files : [];
    }

    private static void RemoveEmptyDirectories(string? start, string root)
    {
        var directory = start;
        while (directory is not null &&
               !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase) &&
               IsUnder(root, directory) &&
               Directory.Exists(directory))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any()) break;
                Directory.Delete(directory);
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    private static bool IsUnder(string parent, string child) =>
        child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static List<string> SafeDirectories(string path)
    {
        try { return Directory.GetDirectories(path).ToList(); }
        catch { return []; }
    }

    private static List<string> SafeGgufFiles(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*.gguf", SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static Manifest? ReadManifest(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed class Manifest
    {
        public string? ModelId { get; set; }
        public string? DisplayName { get; set; }
        public string? Quant { get; set; }
        public string? Provider { get; set; }
        public bool Imported { get; set; }
        public DateTime AddedAt { get; set; }

        /// <summary>下载/导入时写入的文件清单，删除与分片整组处理以它为准。</summary>
        public List<ManifestFile>? Files { get; set; }
    }

    private sealed class ManifestFile
    {
        public string? RelativePath { get; set; }
        public long Bytes { get; set; }
    }
}
