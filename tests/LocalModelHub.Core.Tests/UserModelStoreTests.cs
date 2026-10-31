using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class UserModelStoreTests
{
    [Fact]
    public void Adds_persists_loads_and_removes_custom_models()
    {
        var path = Path.Combine(Path.GetTempPath(), "lmh-custom-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new UserModelStore(path);
            store.Add(BuildModel());

            var reloaded = new UserModelStore(path);
            var models = reloaded.Load();

            var model = Assert.Single(models);
            Assert.True(model.IsCustom);
            Assert.Equal("自定义测试模型", model.DisplayName);
            Assert.Equal("org/repo", model.PrimaryQuant.Sources[0].Repo);

            Assert.True(reloaded.Remove(model.Id));
            Assert.Empty(new UserModelStore(path).Load());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Ignores_corrupted_catalog_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "lmh-custom-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ broken");
            var store = new UserModelStore(path);
            Assert.Empty(store.Load());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static ModelEntry BuildModel() => new()
    {
        Id = "custom-test",
        Name = "自定义测试模型",
        DisplayName = "自定义测试模型",
        Developer = "测试",
        Country = "CN",
        Family = "自定义",
        ParametersB = 2,
        ActiveParametersB = 2,
        ContextLength = 32768,
        ReleaseYear = 2026,
        License = "自定义",
        Description = "测试",
        Tags = ["自定义"],
        Quants =
        [
            new ModelQuant
            {
                Label = "Q4_K_M",
                TotalBytes = 1024,
                Files = [new ModelFile { Path = "model.gguf", Bytes = 1024 }],
                Sources = [new ModelSource { Provider = "ModelScope", Repo = "org/repo" }]
            }
        ]
    };
}
