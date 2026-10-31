using System.Text.Json;

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

            var sorted = files.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
            var total = files.Sum(f => new FileInfo(f).Length);
            var addedAt = manifest?.AddedAt ?? Directory.GetCreationTime(folder);

            result.Add(new DownloadedModel
            {
                ModelId = modelId,
                DisplayName = entry?.DisplayName ?? manifest?.DisplayName ?? Path.GetFileName(folder),
                QuantLabel = quantLabel!,
                FolderPath = folder,
                PrimaryPath = sorted[0],
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
            AddedAt = DateTime.Now
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
            TotalBytes = sourceInfo.Length,
            AddedAt = manifest.AddedAt,
            Developer = "本地导入"
        };
    }

    public sealed record ModelDeleteResult(bool Success, string? Error);

    /// <summary>
    /// 从磁盘删除模型目录。只允许删除模型根目录内的路径，删除失败返回原因而不是静默吞掉，
    /// 便于界面提示「文件仍占用」而不是让列表看起来像只做了 UI。
    /// </summary>
    public ModelDeleteResult Delete(DownloadedModel model)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(model.FolderPath))
            {
                return new ModelDeleteResult(false, "模型路径为空，未做任何删除");
            }

            var root = Path.GetFullPath(_settings.Settings.ModelsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var target = Path.GetFullPath(model.FolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelDeleteResult(false, "拒绝删除模型目录之外的路径");
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
            else
            {
                return new ModelDeleteResult(false, "模型文件已不存在，列表已刷新");
            }

            // 若父目录已空且不是模型根目录，一并清理
            var parent = Path.GetDirectoryName(target);
            if (parent is not null && parent != root && Directory.Exists(parent) &&
                !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }

            return new ModelDeleteResult(true, null);
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
    }
}
