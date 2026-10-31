using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class RemoteCatalogTests
{
    private const string HfSearchJson = """
    [
      {
        "id": "unsloth/Qwen3.8-27B-GGUF",
        "downloads": 6042413,
        "likes": 5398,
        "tags": ["gguf", "license:apache-2.0", "conversational", "qwen3_5"],
        "createdAt": "2026-08-13T08:28:40.000Z"
      },
      {
        "id": "someone/readme-only",
        "downloads": 1,
        "tags": ["gguf"],
        "createdAt": "2026-01-01T00:00:00.000Z"
      }
    ]
    """;

    private const string HfTreeJson = """
    [
      {"type": "file", "path": ".gitattributes", "size": 1798},
      {"type": "file", "path": "LICENSE", "size": 11544},
      {"type": "file", "path": "Qwen3.8-27B-Q4_K_M.gguf", "size": 16056478688,
       "lfs": {"size": 16056478688}},
      {"type": "file", "path": "Qwen3.8-27B-Q8_0.gguf", "size": 29047086048,
       "lfs": {"size": 29047086048}},
      {"type": "file", "path": "mmproj-F16.gguf", "size": 1073741824},
      {"type": "file", "path": "BF16/Qwen3.8-27B-BF16-00001-of-00002.gguf", "size": 49986159616},
      {"type": "file", "path": "BF16/Qwen3.8-27B-BF16-00002-of-00002.gguf", "size": 4671576000},
      {"type": "file", "path": "MTP/mtp-Qwen3.8-27B-Q4_0.gguf", "size": 1369590656}
    ]
    """;

    [Fact]
    public void Builds_huggingface_entry_with_quant_groups()
    {
        var repos = RemoteCatalogService.ParseHuggingFaceSearch(HfSearchJson);
        var files = RemoteCatalogService.ParseHuggingFaceTree(HfTreeJson);

        Assert.Equal(2, repos.Count);
        var built = RemoteCatalogService.BuildHuggingFaceEntry(repos[0], files);

        Assert.NotNull(built);
        var entry = built!;
        Assert.StartsWith("hf-", entry.Id);
        Assert.Equal("Qwen3.8-27B", entry.DisplayName);
        Assert.Equal("unsloth", entry.Developer);
        Assert.Equal("HuggingFace", entry.Origin);
        Assert.True(entry.IsCommunity);
        Assert.Equal(27, entry.ParametersB);
        Assert.Equal("apache-2.0", entry.License);
        Assert.Equal(6042413, entry.Downloads);
        Assert.Contains(ModelCapabilities.Chat, entry.Capabilities);

        var labels = entry.Quants.Select(quant => quant.Label).ToList();
        Assert.Contains("Q4_K_M", labels);
        Assert.Contains("Q8_0", labels);
        Assert.Contains("BF16", labels);
        Assert.DoesNotContain("F16", labels);
        Assert.DoesNotContain("Q4_0", labels);

        var bf16 = entry.Quants.Single(quant => quant.Label == "BF16");
        Assert.Equal(2, bf16.Files.Length);
        Assert.Equal(49986159616 + 4671576000, bf16.TotalBytes);
        Assert.Equal("HuggingFace", bf16.Sources[0].Provider);
        Assert.Equal("unsloth/Qwen3.8-27B-GGUF", bf16.Sources[0].Repo);

        Assert.NotNull(entry.HfRepo);
        Assert.Null(RemoteCatalogService.BuildHuggingFaceEntry(repos[1], Array.Empty<HfFileInfo>()));
    }

    [Fact]
    public void Builds_modelscope_entry_from_openapi_and_files()
    {
        const string searchJson = """
        {
          "success": true,
          "data": {
            "models": [
              {
                "id": "unsloth/Qwen3.8-27B-GGUF",
                "display_name": "Qwen3.8-27B-GGUF",
                "downloads": 469323,
                "license": "Apache License 2.0",
                "tasks": ["text-generation"],
                "created_at": "2025-05-03T00:00:00Z",
                "params": 27320697856,
                "file_size": 472072189494
              }
            ],
            "total_count": 1
          }
        }
        """;

        const string filesJson = """
        {
          "Code": 200,
          "Data": {
            "Files": [
              {"Path": ".gitattributes", "Size": 2075, "Type": "blob"},
              {"Path": "BF16", "Size": 0, "Type": "tree"},
              {"Path": "Qwen3.8-27B-Q4_0.gguf", "Size": 16056478688, "Type": "blob"},
              {"Path": "Qwen3.8-27B-Q8_0.gguf", "Size": 29047086048, "Type": "blob"}
            ]
          }
        }
        """;

        var repos = RemoteCatalogService.ParseModelScopeSearch(searchJson);
        var files = RemoteCatalogService.ParseModelScopeFiles(filesJson);
        var built = RemoteCatalogService.BuildModelScopeEntry(repos[0], files);

        Assert.NotNull(built);
        var entry = built!;
        Assert.StartsWith("ms-", entry.Id);
        Assert.Equal("ModelScope", entry.Origin);
        Assert.Equal(27.3, entry.ParametersB);
        Assert.Equal("Apache-2.0", entry.License);
        Assert.Equal(2, entry.Quants.Length);
        Assert.Equal("ModelScope", entry.Quants[0].Sources[0].Provider);
        Assert.Equal("master", entry.Quants[0].Sources[0].Revision);
        Assert.Equal("unsloth/Qwen3.8-27B-GGUF", entry.HfRepo);
    }

    [Fact]
    public void Parses_ollama_tags_manifest_and_split()
    {
        Assert.Equal(("30b-a3b", "Q4_K_M"), RemoteCatalogService.SplitOllamaTag("30b-a3b-q4_K_M"));
        Assert.Equal(("8b", "FP16"), RemoteCatalogService.SplitOllamaTag("8b-fp16"));
        Assert.Null(RemoteCatalogService.SplitOllamaTag("latest"));
        Assert.Null(RemoteCatalogService.SplitOllamaTag("8b"));

        const string html = """
        <a href="/library/qwen3:latest">latest</a>
        <a href="/library/qwen3:8b">8b</a>
        <a href="/library/qwen3:8b-q4_K_M">8b q4</a>
        <a href="/library/qwen3:8b-q4_K_M">dup</a>
        """;
        var tags = RemoteCatalogService.ParseOllamaTags("qwen3", html);
        Assert.Equal(["latest", "8b", "8b-q4_K_M"], tags);

        const string manifest = """
        {
          "schemaVersion": 2,
          "layers": [
            {"mediaType": "application/vnd.ollama.image.model",
             "digest": "sha256:a3de86cd1c132c822487ededd47a324c50491393e6565cd14bafa40d0b8e686f",
             "size": 5225374496},
            {"mediaType": "application/vnd.ollama.image.template",
             "digest": "sha256:ae370d884f108d16e7cc8fd5259ebc5773a0afa6e078b11f4ed7e39a27e0dfc4",
             "size": 1723}
          ]
        }
        """;
        var layer = RemoteCatalogService.ParseOllamaManifest(manifest);
        Assert.NotNull(layer);
        Assert.Equal("sha256:a3de86cd1c132c822487ededd47a324c50491393e6565cd14bafa40d0b8e686f", layer!.Digest);
        Assert.Equal(5225374496, layer.Size);
    }

    [Fact]
    public void Extracts_quant_labels()
    {
        Assert.Equal("Q4_K_M", RemoteCatalogService.ExtractQuantLabel("Qwen3-8B-Q4_K_M.gguf"));
        Assert.Equal("UD-Q4_K_XL", RemoteCatalogService.ExtractQuantLabel("Qwen3.8-27B-UD-Q4_K_XL.gguf"));
        Assert.Equal("FP16", RemoteCatalogService.ExtractQuantLabel("model-fp16.gguf"));
        Assert.Equal("Q8_0", RemoteCatalogService.ExtractQuantLabel("model-00001-of-00003-q8_0.gguf"));
        Assert.Null(RemoteCatalogService.ExtractQuantLabel("README.md"));
    }

    [Fact]
    public void Merge_skips_duplicates_and_keeps_all_ollama_variants()
    {
        var builtin = new ModelCatalog
        {
            Version = 4,
            UpdatedAt = "2026-10-29",
            Models =
            [
                new ModelEntry
                {
                    Id = "qwen3-8b",
                    DisplayName = "Qwen3-8B",
                    HfRepo = "Qwen/Qwen3-8B-GGUF",
                    Quants =
                    [
                        new ModelQuant
                        {
                            Label = "Q4_K_M",
                            Files = [new ModelFile { Path = "a.gguf", Bytes = 1 }],
                            Sources = [new ModelSource { Provider = "ModelScope", Repo = "Qwen/Qwen3-8B-GGUF" }]
                        }
                    ]
                }
            ]
        };

        var custom = new ModelEntry
        {
            Id = "custom-1",
            DisplayName = "本地模型",
            Quants = [new ModelQuant { Label = "本地", Files = [new ModelFile { Path = "b.gguf", Bytes = 1 }] }]
        };

        var remote = new RemoteCatalogCache
        {
            UpdatedAt = "2026-10-29 12:00",
            Models =
            [
                new ModelEntry
                {
                    Id = "hf-dup",
                    HfRepo = "Qwen/Qwen3-8B-GGUF",
                    Quants = [new ModelQuant { Label = "Q4_K_M", Files = [new ModelFile { Path = "c.gguf", Bytes = 1 }] }]
                },
                new ModelEntry
                {
                    Id = "ollama-qwen3-8b-1",
                    Origin = "Ollama",
                    Quants =
                    [
                        new ModelQuant
                        {
                            Label = "Q4_K_M",
                            Files = [new ModelFile { Path = "q.gguf", Bytes = 1, Digest = "sha256:1" }],
                            Sources = [new ModelSource { Provider = "Ollama", Repo = "library/qwen3", Revision = "8b-q4_K_M" }]
                        }
                    ]
                },
                new ModelEntry
                {
                    Id = "ollama-qwen3-8b-2",
                    Origin = "Ollama",
                    Quants =
                    [
                        new ModelQuant
                        {
                            Label = "Q8_0",
                            Files = [new ModelFile { Path = "q8.gguf", Bytes = 1, Digest = "sha256:2" }],
                            Sources = [new ModelSource { Provider = "Ollama", Repo = "library/qwen3", Revision = "8b-q8_0" }]
                        }
                    ]
                }
            ]
        };

        var merged = CatalogMergeService.Merge(builtin, [custom], remote);

        Assert.Equal(4, merged.Models.Count);
        Assert.Equal(2, merged.RemoteCount);
        Assert.Equal("2026-10-29 12:00", merged.SyncedAt);
        Assert.DoesNotContain(merged.Models, model => model.Id == "hf-dup");
        Assert.Contains(merged.Models, model => model.Id == "custom-1");
        Assert.Contains(merged.Models, model => model.Id == "ollama-qwen3-8b-2");
    }

    [Fact]
    public void Builds_urls_for_sources_and_mirrors()
    {
        var settings = new AppSettings { UseHfMirror = true };
        var hf = new ModelSource { Provider = "HuggingFace", Repo = "unsloth/Qwen3.8-27B-GGUF", Revision = "main" };
        var file = new ModelFile { Path = "Qwen3.8-27B-Q4_K_M.gguf", Bytes = 1 };

        Assert.Equal(
            "https://hf-mirror.com/unsloth/Qwen3.8-27B-GGUF/resolve/main/Qwen3.8-27B-Q4_K_M.gguf",
            DownloadUrlBuilder.Build(hf, file.Path, settings));

        settings.UseHfMirror = false;
        Assert.StartsWith("https://huggingface.co/", DownloadUrlBuilder.Build(hf, file.Path, settings));

        var ms = new ModelSource { Provider = "ModelScope", Repo = "unsloth/Qwen3.8-27B-GGUF", Revision = "master" };
        Assert.Contains("modelscope.cn/api/v1/models/unsloth/Qwen3.8-27B-GGUF/repo", DownloadUrlBuilder.Build(ms, file.Path, settings));

        var ollama = new ModelSource { Provider = "Ollama", Repo = "library/qwen3", Revision = "8b-q4_K_M" };
        var ollamaFile = new ModelFile { Path = "qwen3-8b-Q4_K_M.gguf", Bytes = 1, Digest = "sha256:abc" };
        Assert.Equal(
            "https://registry.ollama.ai/v2/library/qwen3/blobs/sha256:abc",
            DownloadUrlBuilder.Build(ollama, ollamaFile, settings));

        Assert.Throws<InvalidOperationException>(() =>
            DownloadUrlBuilder.Build(ollama, new ModelFile { Path = "x.gguf", Bytes = 1 }, settings));
    }

    [Fact]
    public void Cache_roundtrip_preserves_remote_models()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lmh-catalog-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "catalog-remote.json");
        try
        {
            var cache = new RemoteCatalogCache
            {
                UpdatedAt = "2026-10-29 12:00",
                Sources = [new CatalogSourceStatus("ollama", "Ollama 官方库", 2, null, "2026-10-29 12:00")],
                Models =
                [
                    new ModelEntry
                    {
                        Id = "ollama-qwen3-8b",
                        DisplayName = "qwen3 8b",
                        Origin = "Ollama",
                        Quants =
                        [
                            new ModelQuant
                            {
                                Label = "Q4_K_M",
                                Files = [new ModelFile { Path = "q.gguf", Bytes = 5, Digest = "sha256:1" }],
                                Sources = [new ModelSource { Provider = "Ollama", Repo = "library/qwen3", Revision = "8b-q4_K_M" }]
                            }
                        ]
                    }
                ]
            };

            RemoteCatalogService.SaveCacheTo(path, cache);
            var loaded = RemoteCatalogService.LoadCacheFrom(path);

            Assert.Equal("2026-10-29 12:00", loaded.UpdatedAt);
            Assert.Single(loaded.Models);
            Assert.Equal("sha256:1", loaded.Models[0].Quants[0].Files[0].Digest);
            Assert.Single(loaded.Sources);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
