using System.Text.Json;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>用户自定义模型目录，持久化到 %LOCALAPPDATA%\LocalModelHub\custom-models.json。</summary>
public sealed class UserModelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly List<ModelEntry> _models = [];
    private readonly string? _filePath;

    public UserModelStore(string? filePath = null) => _filePath = filePath;

    public string FilePath => _filePath ?? AppPaths.CustomModelsFile;

    public IReadOnlyList<ModelEntry> Models => _models;

    public IReadOnlyList<ModelEntry> Load()
    {
        _models.Clear();
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<List<ModelEntry>>(json, JsonOptions) ?? [];
                foreach (var model in loaded.Where(model =>
                             !string.IsNullOrWhiteSpace(model.Id) &&
                             (model.Quants.Length > 0 || model.IsRemoteApi)))
                {
                    model.IsCustom = true;
                    _models.Add(model);
                }
            }
        }
        catch
        {
            // 目录损坏时按空目录处理，不影响内置模型
        }

        return _models;
    }

    public void Add(ModelEntry model)
    {
        model.IsCustom = true;
        _models.Add(model);
        Save();
    }

    public bool Remove(string modelId)
    {
        var removed = _models.RemoveAll(model =>
            model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Save();
        return removed;
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_models, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}
