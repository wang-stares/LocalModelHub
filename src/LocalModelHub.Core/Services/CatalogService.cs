using System.Reflection;
using System.Text.Json;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

public sealed class CatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ModelCatalog LoadEmbedded()
    {
        var assembly = typeof(CatalogService).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return new ModelCatalog();
        }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return new ModelCatalog();
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<ModelCatalog>(json, JsonOptions) ?? new ModelCatalog();
    }
}
