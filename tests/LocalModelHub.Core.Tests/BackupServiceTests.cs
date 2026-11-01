using System.Text.Json;
using System.Text.Json.Nodes;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class BackupServiceTests
{
    private const string SettingsJson = """
        {
          "ServerPort": 9099,
          "Theme": "dark",
          "SystemPrompt": "备份测试提示",
          "ApiExposeLan": true,
          "ApiKey": "test-api-key"
        }
        """;

    private const string CustomModelsJson = """
        [
          {
            "Id": "custom-abcd1234",
            "Name": "BackupModel",
            "DisplayName": "备份模型",
            "Country": "CN",
            "Quants": [
              { "Label": "Q4_K_M", "TotalBytes": 1024, "Files": [], "Sources": [] }
            ]
          }
        ]
        """;

    private const string MemoryJson = """
        [
          {
            "Id": "m1",
            "Text": "用户偏好 C# 与函数式写法",
            "Source": "manual",
            "CreatedAt": "2026-10-29T10:00:00",
            "UpdatedAt": "2026-10-29T10:00:00",
            "Pinned": true
          }
        ]
        """;

    private const string SessionsJson = """
        [
          {
            "Id": "s1",
            "Title": "备份会话",
            "ModelName": "Qwen3-8B",
            "CreatedAt": "2026-10-29T10:00:00",
            "UpdatedAt": "2026-10-29T11:00:00",
            "Messages": [ { "Role": "user", "Text": "你好" } ]
          }
        ]
        """;

    private const string KnowledgeJson = """
        [
          {
            "Id": "k1",
            "Name": "api.md",
            "SourcePath": "C:\\docs\\api.md",
            "AddedAt": "2026-10-29T10:00:00",
            "Bytes": 128,
            "Chunks": [ { "Id": "0", "Index": 0, "Text": "局域网 API 使用 Bearer 鉴权。" } ]
          }
        ]
        """;

    [Fact]
    public void Exports_and_imports_all_sections()
    {
        var root = TempDirectory();
        try
        {
            var service = new BackupService(root);
            File.WriteAllText(service.SettingsFile, SettingsJson);
            File.WriteAllText(service.CustomModelsFile, CustomModelsJson);
            File.WriteAllText(service.MemoryFile, MemoryJson);
            File.WriteAllText(service.SessionsFile, SessionsJson);
            File.WriteAllText(service.KnowledgeFile, KnowledgeJson);

            var backup = Path.Combine(root, "backup.json");
            service.Export(backup, "0.12.0");
            Assert.True(File.Exists(backup));

            var bundle = JsonNode.Parse(File.ReadAllText(backup))!.AsObject();
            Assert.Equal("LocalModelHub.Backup", bundle["format"]!.GetValue<string>());
            Assert.Equal(BackupService.FormatVersion, bundle["version"]!.GetValue<int>());
            Assert.Equal("0.12.0", bundle["appVersion"]!.GetValue<string>());

            // 模拟用户数据被修改后，再导入备份恢复
            File.WriteAllText(service.SettingsFile, """{ "ServerPort": 8090 }""");
            File.WriteAllText(service.CustomModelsFile, "[]");
            File.WriteAllText(service.MemoryFile, "[]");
            File.WriteAllText(service.SessionsFile, "[]");
            File.WriteAllText(service.KnowledgeFile, "[]");

            var result = service.Import(backup);

            Assert.True(result.SettingsImported);
            Assert.Equal(1, result.CustomModels);
            Assert.Equal(1, result.Memories);
            Assert.Equal(1, result.Sessions);
            Assert.Equal(1, result.KnowledgeDocuments);

            var reloadedSettings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(service.SettingsFile),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.Equal(9099, reloadedSettings.ServerPort);
            Assert.Equal("dark", reloadedSettings.Theme);
            Assert.True(reloadedSettings.ApiExposeLan);
            Assert.Equal("test-api-key", reloadedSettings.ApiKey);

            var models = new UserModelStore(service.CustomModelsFile);
            Assert.Equal("custom-abcd1234", Assert.Single(models.Load()).Id);
            Assert.Equal("用户偏好 C# 与函数式写法", Assert.Single(new MemoryService(service.MemoryFile).Load()).Text);
            Assert.Equal("备份会话", Assert.Single(new ChatSessionStore(service.SessionsFile).Load()).Title);
            Assert.Equal("api.md", Assert.Single(new KnowledgeService(service.KnowledgeFile).Load()).Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Export_skips_corrupted_section_and_import_leaves_absent_section_untouched()
    {
        var root = TempDirectory();
        try
        {
            var service = new BackupService(root);
            File.WriteAllText(service.SettingsFile, SettingsJson);
            File.WriteAllText(service.MemoryFile, "{ broken");

            var backup = Path.Combine(root, "backup.json");
            service.Export(backup);

            var bundle = JsonNode.Parse(File.ReadAllText(backup))!.AsObject();
            Assert.Null(bundle["memory"]);
            Assert.Null(bundle["customModels"]);
            Assert.NotNull(bundle["settings"]);

            File.Delete(service.SettingsFile);
            var result = service.Import(backup);

            Assert.True(result.SettingsImported);
            Assert.Equal(0, result.Memories);
            Assert.True(File.Exists(service.SettingsFile));
            Assert.False(File.Exists(service.CustomModelsFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "format": "Other.Backup", "version": 1, "settings": {} }""")]
    [InlineData("""{ "format": "LocalModelHub.Backup", "version": 99, "settings": {} }""")]
    [InlineData("""{ "format": "LocalModelHub.Backup", "version": 1, "settings": { } , "customModels": {} }""")]
    [InlineData("""{ "format": "LocalModelHub.Backup", "version": 1 }""")]
    public void Import_rejects_invalid_bundle_without_touching_files(string payload)
    {
        var root = TempDirectory();
        try
        {
            var service = new BackupService(root);
            File.WriteAllText(service.SettingsFile, SettingsJson);

            var backup = Path.Combine(root, "bad.json");
            File.WriteAllText(backup, payload);

            Assert.Throws<InvalidDataException>(() => service.Import(backup));
            Assert.Equal(SettingsJson, File.ReadAllText(service.SettingsFile));
            Assert.False(File.Exists(backup + ".tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cross_machine_import_drops_external_model_references()
    {
        var root = TempDirectory();
        try
        {
            var service = new BackupService(root);
            var backup = Path.Combine(root, "from-other-pc.json");
            File.WriteAllText(backup, $$"""
                {
                  "format": "LocalModelHub.Backup",
                  "version": {{BackupService.FormatVersion}},
                  "machineFingerprint": "OTHER-PC",
                  "settings": {
                    "ServerPort": 9099,
                    "ExternalModelPaths": [ "D:\\models\\Qwen3-8B-Q4_K_M.gguf" ]
                  }
                }
                """);

            var result = service.Import(backup, "THIS-PC");

            Assert.True(result.MachineBoundReset);
            var settings = ReloadSettings(service);
            Assert.Empty(settings.ExternalModelPaths);
            Assert.Equal(8090, settings.ServerPort);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Same_machine_import_keeps_external_model_references()
    {
        var root = TempDirectory();
        try
        {
            var service = new BackupService(root);
            var backup = Path.Combine(root, "same-pc.json");
            File.WriteAllText(backup, $$"""
                {
                  "format": "LocalModelHub.Backup",
                  "version": {{BackupService.FormatVersion}},
                  "machineFingerprint": "THIS-PC",
                  "settings": {
                    "ExternalModelPaths": [ "D:\\models\\Qwen3-8B-Q4_K_M.gguf" ]
                  }
                }
                """);

            var result = service.Import(backup, "THIS-PC");

            Assert.False(result.MachineBoundReset);
            Assert.Equal(
                new[] { "D:\\models\\Qwen3-8B-Q4_K_M.gguf" },
                ReloadSettings(service).ExternalModelPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AppSettings ReloadSettings(BackupService service) =>
        JsonSerializer.Deserialize<AppSettings>(
            File.ReadAllText(service.SettingsFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "lmh-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
