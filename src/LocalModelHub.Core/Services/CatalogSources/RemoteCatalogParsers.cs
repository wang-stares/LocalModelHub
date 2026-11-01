using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Security.Cryptography;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>目录源的解析、模型条目构造与命名推断；供各 CatalogSource 与 RemoteCatalogService 复用。</summary>
internal static class RemoteCatalogParsers
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal static readonly Regex QuantRegex = new(
        @"(?<q>(?:UD[-_])?(?:IQ\d+_(?:XXS|XS|S|M|L)|IQ\d+_[A-Z0-9]+|Q\d+_K(?:_[A-Z0-9]+)?|Q\d+_\d+|Q\d+|MXFP4|BF16|FP16|F16|F32))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static readonly Regex ParamsRegex = new(@"(\d+(?:\.\d+)?)\s*[bB](?![a-zA-Z])", RegexOptions.Compiled);
    internal static readonly Regex ActiveParamsRegex = new(@"A(\d+(?:\.\d+)?)\s*[bB](?![a-zA-Z])", RegexOptions.Compiled);
    internal static readonly Regex ContextRegex = new(@"(\d+)\s*[kK](?![a-zA-Z])", RegexOptions.Compiled);
    internal static readonly Regex SplitRegex = new(@"-\d{5}-of-\d{5}", RegexOptions.Compiled);
    internal static readonly Regex OllamaTagRegex = new(@"[^a-zA-Z0-9._-]", RegexOptions.Compiled);

    internal static readonly string[] QuantPreference =
    [
        "Q4_K_M", "Q4_K_S", "Q5_K_M", "Q5_K_S", "Q8_0", "Q4_0", "Q6_K", "Q5_0",
        "Q3_K_M", "Q3_K_S", "Q2_K", "MXFP4", "F16", "BF16", "F32"
    ];

    internal static readonly string[] NonTextMarkers =
    [
        "text-to-image", "text-to-video", "text-to-audio", "image-to-image", "image-segmentation",
        "image-classification", "object-detection", "depth-estimation", "image-feature-extraction",
        "automatic-speech-recognition", "text-to-speech", "audio-classification", "voice-activity"
    ];

    internal static readonly string[] ChineseOrgs =
    [
        "qwen", "alibaba", "deepseek", "deepseek-ai", "zhipu", "zhipuai", "thudm", "baichuan", "01-ai",
        "internlm", "openbmb", "tencent", "bytedance", "moonshot", "moonshotai", "stepfun", "minimax",
        "modelbest", "shanghai_ai_laboratory", "iic", "xiaomi", "huawei", "baidu", "sensetime",
        "meituan", "antgroup", "iflytek", "01ai", "teleai", "gientech", "unisound", "wenge"
    ];

    internal static IReadOnlyList<HfModelInfo> ParseHuggingFaceSearch(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<HfModelInfo>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<HfFileInfo> ParseHuggingFaceTree(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<HfFileInfo>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static HfModelInfo? ParseHuggingFaceModel(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<HfModelInfo>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析仓库链接或 owner/name 形式的仓库 ID；无法识别时返回 null。</summary>
    internal static (string Provider, string Repo)? ParseRepoReference(string input, string defaultProvider)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var text = input.Trim();
        var provider = defaultProvider;
        string path;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if (host.Contains("modelscope")) provider = "ModelScope";
            else if (host.Contains("huggingface") || host.Contains("hf-mirror")) provider = "HuggingFace";
            path = uri.AbsolutePath;
        }
        else
        {
            path = text;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !segment.Equals("models", StringComparison.OrdinalIgnoreCase) &&
                              !segment.Equals("datasets", StringComparison.OrdinalIgnoreCase) &&
                              !segment.Equals("tree", StringComparison.OrdinalIgnoreCase) &&
                              !segment.Equals("files", StringComparison.OrdinalIgnoreCase) &&
                              !segment.Equals("resolve", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (segments.Count > 0)
        {
            var first = segments[0].ToLowerInvariant();
            if (first.Contains("huggingface") || first.Contains("hf-mirror") || first.Contains("modelscope"))
            {
                if (first.Contains("modelscope")) provider = "ModelScope";
                else if (first.Contains("huggingface") || first.Contains("hf-mirror")) provider = "HuggingFace";
                segments.RemoveAt(0);
            }
        }

        if (segments.Count < 2) return null;
        return (provider, $"{segments[0]}/{segments[1]}");
    }

    /// <summary>从仓库文件列表中筛出可单独使用的 GGUF 主权重（过滤附属文件与分片）。</summary>
    internal static IReadOnlyList<RepoFileOption> BuildRepoFileOptions(IEnumerable<ModelFile> files)
    {
        return files
            .Where(file => file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Where(file => !IsAuxiliaryFile(file.Path))
            .Where(file => !SplitRegex.IsMatch(file.Path))
            .Select(file => new RepoFileOption(
                file.Path, ExtractQuantLabel(file.Path) ?? "GGUF", file.Bytes, file.Digest))
            .OrderBy(option => QuantRank(option.Label))
            .ThenByDescending(option => option.Bytes)
            .ToList();
    }

    internal static (double ParametersB, double ActiveB) GuessParametersFromName(string display) =>
        GuessParameters(display);

    internal static int GuessContextFromName(string display) => GuessContext(display);

    internal static ModelEntry? BuildHuggingFaceEntry(HfModelInfo repo, IReadOnlyList<HfFileInfo> files)
    {
        var modelFiles = files
            .Where(file => file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Select(file => new ModelFile { Path = file.Path, Bytes = file.ActualSize, Digest = file.Digest })
            .ToList();

        var quants = BuildQuantList(modelFiles, "HuggingFace", repo.Id, "main");
        if (quants.Length == 0) return null;

        var repoName = repo.Id.Split('/').Last();
        var developer = repo.Id.Contains('/') ? repo.Id.Split('/')[0] : "社区";
        var display = CleanDisplayName(repoName);
        var (parameters, active) = GuessParameters(display);
        if (parameters <= 0)
        {
            parameters = EstimateParameters(quants[0].TotalBytes);
            active = parameters;
        }

        var license = repo.Tags.FirstOrDefault(tag =>
            tag.StartsWith("license:", StringComparison.OrdinalIgnoreCase))?[8..] ?? "";
        var caps = InferCapabilities(display, repo.Tags, parameters);
        var releaseYear = ParseYear(repo.CreatedAt);

        return new ModelEntry
        {
            Id = BuildId("hf", repo.Id),
            Name = display,
            DisplayName = display,
            Developer = developer,
            Country = GuessCountry(developer),
            Family = display.Split(' ')[0],
            ParametersB = parameters,
            ActiveParametersB = Math.Min(active > 0 ? active : parameters, parameters),
            ContextLength = GuessContext(display),
            ReleaseYear = releaseYear,
            License = license,
            Description = BuildDescription("HuggingFace 社区", repo.Downloads, license, repo.Id),
            Tags = ["社区", "GGUF", repo.Id],
            Capabilities = caps,
            Quants = quants,
            HfRepo = repo.Id,
            Origin = "HuggingFace",
            Downloads = repo.Downloads
        };
    }

    internal static IReadOnlyList<MsModelInfo> ParseModelScopeSearch(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("models", out var models))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<MsModelInfo>>(models.GetRawText(), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<MsFileInfo> ParseModelScopeFiles(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Data", out var data) ||
                !data.TryGetProperty("Files", out var files))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<MsFileInfo>>(files.GetRawText(), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static ModelEntry? BuildModelScopeEntry(MsModelInfo repo, IReadOnlyList<MsFileInfo> files)
    {
        var modelFiles = files
            .Where(file => file.Type.Equals("blob", StringComparison.OrdinalIgnoreCase))
            .Where(file => file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Select(file => new ModelFile
            {
                Path = file.Path,
                Bytes = file.Size,
                Digest = string.IsNullOrWhiteSpace(file.Sha256) ? null : $"sha256:{file.Sha256}"
            })
            .ToList();

        var quants = BuildQuantList(modelFiles, "ModelScope", repo.Id, "master");
        if (quants.Length == 0) return null;

        var repoName = repo.Id.Split('/').Last();
        var developer = repo.Id.Contains('/') ? repo.Id.Split('/')[0] : "社区";
        var display = CleanDisplayName(string.IsNullOrWhiteSpace(repo.DisplayName) ? repoName : repo.DisplayName);
        var (parameters, active) = GuessParameters(display);
        if (repo.Params > 0)
        {
            parameters = Math.Round(repo.Params / 1_000_000_000d, 1);
        }

        if (parameters <= 0)
        {
            parameters = EstimateParameters(quants[0].TotalBytes);
        }

        if (active <= 0 || active > parameters) active = parameters;

        var license = NormalizeLicense(repo.License);
        var tags = repo.Tasks.Concat(repo.Tags).ToArray();
        var caps = InferCapabilities(display, tags, parameters);

        return new ModelEntry
        {
            Id = BuildId("ms", repo.Id),
            Name = display,
            DisplayName = display,
            Developer = developer,
            Country = GuessCountry(developer),
            Family = display.Split(' ')[0],
            ParametersB = parameters,
            ActiveParametersB = active,
            ContextLength = GuessContext(display),
            ReleaseYear = ParseYear(repo.CreatedAt),
            License = license,
            Description = BuildDescription("魔搭 ModelScope", repo.Downloads, license, repo.Id),
            Tags = ["社区", "GGUF", repo.Id],
            Capabilities = caps,
            Quants = quants,
            HfRepo = repo.Id,
            Origin = "ModelScope",
            Downloads = repo.Downloads
        };
    }

    internal static IReadOnlyList<string> ParseOllamaLibrary(string html)
    {
        var names = new List<string>();
        foreach (Match match in Regex.Matches(html, @"href=""/library/([a-zA-Z0-9._-]+)"""))
        {
            var name = match.Groups[1].Value;
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }

        return names;
    }

    internal static IReadOnlyList<string> ParseOllamaTags(string modelName, string html)
    {
        var prefix = $@"href=""/library/{Regex.Escape(modelName)}:";
        var tags = new List<string>();
        foreach (Match match in Regex.Matches(html, prefix + @"([^""]+)"""))
        {
            var tag = match.Groups[1].Value;
            if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
        }

        return tags;
    }

    internal static OllamaLayer? ParseOllamaManifest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("layers", out var layers)) return null;

            foreach (var layer in layers.EnumerateArray())
            {
                var mediaType = layer.TryGetProperty("mediaType", out var type) ? type.GetString() : null;
                if (!string.Equals(mediaType, "application/vnd.ollama.image.model", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var digest = layer.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
                var size = layer.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
                if (string.IsNullOrWhiteSpace(digest) || size <= 0) return null;
                return new OllamaLayer(digest, size);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>把 qwen3:30b-a3b-q4_K_M 拆成 (30b-a3b, Q4_K_M)；没有量化后缀的标签返回 null。</summary>
    internal static (string Variant, string Quant)? SplitOllamaTag(string tag)
    {
        var index = tag.LastIndexOf('-');
        if (index <= 0 || index >= tag.Length - 1) return null;

        var quant = tag[(index + 1)..];
        if (ExtractQuantLabel(quant + ".gguf") is null) return null;

        var variant = tag[..index];
        if (variant.Length == 0) return null;
        return (variant, quant.Replace('-', '_').ToUpperInvariant());
    }

    internal static string? ExtractQuantLabel(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (SplitRegex.IsMatch(name)) name = SplitRegex.Replace(name, "");

        Match? last = null;
        foreach (Match match in QuantRegex.Matches(name))
        {
            last = match;
        }

        if (last is null) return null;
        var label = last.Groups["q"].Value.Replace('_', '_').ToUpperInvariant();
        return label.Replace("UD_", "UD-");
    }

    /// <summary>把 GGUF 文件按量化标签分组，过滤 mmproj / imatrix / LoRA 等附属文件。</summary>
    internal static ModelQuant[] BuildQuantList(
        IReadOnlyList<ModelFile> files,
        string provider,
        string repo,
        string revision)
    {
        var eligible = files
            .Where(file => !IsAuxiliaryFile(file.Path))
            .Select(file => (File: file, Label: ExtractQuantLabel(file.Path)))
            .Where(item => item.Label is not null)
            .ToList();

        var quants = new List<ModelQuant>();
        foreach (var group in eligible.GroupBy(item => item.Label!, StringComparer.OrdinalIgnoreCase))
        {
            var byDirectory = group
                .GroupBy(item => Path.GetDirectoryName(item.File.Path) ?? "")
                .OrderBy(dir => dir.Key.Count(c => c == '/'))
                .ThenByDescending(dir => dir.Count())
                .First();

            var filesInGroup = byDirectory
                .Select(item => new ModelFile { Path = item.File.Path, Bytes = item.File.Bytes, Digest = item.File.Digest })
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            quants.Add(new ModelQuant
            {
                Label = group.Key,
                TotalBytes = filesInGroup.Sum(file => file.Bytes),
                Files = filesInGroup,
                Sources = [new ModelSource { Provider = provider, Repo = repo, Revision = revision }]
            });
        }

        return quants
            .OrderBy(quant => QuantRank(quant.Label))
            .ThenBy(quant => quant.TotalBytes)
            .Take(5)
            .ToArray();
    }

    internal static bool IsAuxiliaryFile(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.Contains("mmproj")
               || lower.Contains("mtp/")
               || lower.Contains("mtp-")
               || lower.Contains("-mtp")
               || lower.Contains("imatrix")
               || lower.Contains("lora")
               || lower.Contains("adapter")
               || lower.Contains("tokenizer");
    }

    internal static bool IsGgufRepo(string repoId)
    {
        var lower = repoId.ToLowerInvariant();
        return lower.Contains("gguf")
               && !lower.Contains("lora")
               && !lower.Contains("adapter")
               && !lower.Contains("imatrix");
    }

    /// <summary>来源标注为图像/语音/视频等任务时跳过，避免非文本模型混入模型库。</summary>
    internal static bool IsNonTextRepo(IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            var lower = label.ToLowerInvariant();
            foreach (var marker in NonTextMarkers)
            {
                if (lower.Contains(marker)) return true;
            }
        }

        return false;
    }

    internal static int QuantRank(string label)
    {
        var normalized = label.Replace('-', '_');
        if (normalized.StartsWith("UD_", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[3..];
        }

        var index = Array.FindIndex(QuantPreference, item =>
            item.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 99 : index;
    }

    internal static (double Parameters, double Active) GuessParameters(string text)
    {
        var activeMatch = ActiveParamsRegex.Match(text);
        var active = activeMatch.Success && double.TryParse(activeMatch.Groups[1].Value, out var a) ? a : 0;

        foreach (Match match in ParamsRegex.Matches(text))
        {
            if (!double.TryParse(match.Groups[1].Value, out var value)) continue;
            if (active > 0) return (value, active);
            return (value, value);
        }

        return (0, 0);
    }

    internal static double EstimateParameters(long bytes)
    {
        if (bytes <= 0) return 0;
        return Math.Round(bytes / Formatters.Gib / 0.62, 1);
    }

    internal static int GuessContext(string name)
    {
        var match = ContextRegex.Match(name);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var k))
        {
            return Math.Clamp(k * 1024, 2048, 1_048_576);
        }

        return 32768;
    }

    internal static string GuessCountry(string developer)
    {
        var lower = developer.ToLowerInvariant();
        return ChineseOrgs.Any(org => lower.Contains(org)) ? "CN" : "";
    }

    internal static string[] InferCapabilities(string name, string[] tags, double parametersB)
    {
        var haystack = (name + " " + string.Join(' ', tags)).ToLowerInvariant();
        var capabilities = new List<string>();

        if (haystack.Contains("coder") || haystack.Contains("code")) capabilities.Add(ModelCapabilities.Coding);
        if (haystack.Contains("thinking") || haystack.Contains("reason") || haystack.Contains("r1"))
        {
            capabilities.Add(ModelCapabilities.Reasoning);
        }

        if (haystack.Contains("instruct") || haystack.Contains("chat") || haystack.Contains("conversational"))
        {
            capabilities.Add(ModelCapabilities.Chat);
        }

        if (haystack.Contains("tool") || haystack.Contains("function-call")) capabilities.Add(ModelCapabilities.Tools);
        if (parametersB > 0 && parametersB < 2) capabilities.Add(ModelCapabilities.Lightweight);
        if (haystack.Contains("multilingual") || haystack.Contains("qwen") || haystack.Contains("glm") ||
            haystack.Contains("internlm") || haystack.Contains("minimax"))
        {
            capabilities.Add(ModelCapabilities.Multilingual);
        }

        if (capabilities.Count == 0) capabilities.Add(ModelCapabilities.Chat);
        return capabilities.Distinct().ToArray();
    }

    internal static string CleanDisplayName(string name)
    {
        var cleaned = Regex.Replace(name, @"[-_ ]?gguf$", "", RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? name : cleaned;
    }

    internal static string NormalizeLicense(string license)
    {
        if (string.IsNullOrWhiteSpace(license)) return "";
        return license.Trim() switch
        {
            "Apache License 2.0" => "Apache-2.0",
            "MIT License" => "MIT",
            "Apache-2.0" => "Apache-2.0",
            _ => license.Trim()
        };
    }

    internal static string BuildDescription(string sourceName, long downloads, string license, string repo)
    {
        var parts = new List<string> { sourceName };
        if (downloads > 0) parts.Add(FormatDownloads(downloads));
        if (!string.IsNullOrWhiteSpace(license)) parts.Add(license);
        parts.Add(repo);
        return string.Join(" · ", parts);
    }

    internal static string FormatDownloads(long downloads) => downloads switch
    {
        >= 10_000 => $"{downloads / 10_000d:0.#} 万次下载",
        _ => $"{downloads} 次下载"
    };

    internal static int ParseYear(string value) =>
        DateTime.TryParse(value, out var date) ? date.Year : DateTime.Now.Year;

    internal static string BuildId(string prefix, string rawKey)
    {
        var slug = Regex.Replace(rawKey.ToLowerInvariant(), "[^a-z0-9.]+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)))[..6].ToLowerInvariant();
        return string.IsNullOrWhiteSpace(slug) ? $"{prefix}-{hash}" : $"{prefix}-{slug}-{hash}";
    }

    internal static string SanitizeFileName(string value) =>
        OllamaTagRegex.Replace(value, "-").Trim('-');

    internal static string EscapeRepo(string repo) =>
        string.Join('/', repo.Split('/').Select(Uri.EscapeDataString));
}
