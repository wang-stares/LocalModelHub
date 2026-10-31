using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

public sealed record KnowledgeImportProgress(int Current, int Total, string FileName);

public sealed record KnowledgeImportResult(int Imported, int Skipped, int Failed);

/// <summary>一次检索命中的片段，用于界面展示引用来源。</summary>
public sealed record KnowledgeHit(string DocumentId, string DocumentName, int ChunkIndex, string Text, double Score);

/// <summary>注入对话的知识库上下文与对应命中片段。</summary>
public sealed record KnowledgeContext(string Text, IReadOnlyList<KnowledgeHit> Hits)
{
    public static KnowledgeContext Empty { get; } = new("", []);
}

/// <summary>
/// 本地知识库：导入文本文件、切分片段，并用 BM25（英文词 + 中文二元组）做检索。
/// 全部数据保存在本机 knowledge.json，不依赖额外模型或网络。
/// </summary>
public sealed class KnowledgeService
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int ChunkSize = 900;
    public const int ChunkOverlap = 120;
    public const int MaxChunksPerDocument = 800;
    public const int MaxDocuments = 200;
    public const int MaxFolderFiles = 120;
    public const int MaxQueryTerms = 64;

    public static readonly IReadOnlyList<string> SupportedExtensions =
    [
        ".md", ".markdown", ".txt", ".log",
        ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".go", ".rs", ".c", ".cpp", ".h", ".hpp",
        ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".html", ".css", ".scss", ".sql", ".sh", ".ps1", ".bat", ".csproj", ".sln", ".axaml", ".xaml"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Regex LatinToken = new("[a-z0-9_.#+-]{2,}", RegexOptions.Compiled);
    private static readonly Regex ParagraphBreak = new(@"\n\s*\n+", RegexOptions.Compiled);

    private readonly string? _filePath;
    private readonly List<KnowledgeDocument> _documents = [];

    public KnowledgeService(string? filePath = null) => _filePath = filePath;

    public string FilePath => _filePath ?? AppPaths.KnowledgeFile;

    public IReadOnlyList<KnowledgeDocument> Documents => _documents;

    public int ChunkCount => _documents.Sum(document => document.Chunks.Count);

    public static bool IsSupportedFile(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string[] SupportedPatterns() => SupportedExtensions.Select(extension => "*" + extension).ToArray();

    public IReadOnlyList<KnowledgeDocument> Load()
    {
        _documents.Clear();
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<List<KnowledgeDocument>>(
                    File.ReadAllText(FilePath), JsonOptions) ?? [];

                foreach (var document in loaded
                             .Where(document => !string.IsNullOrWhiteSpace(document.Id) &&
                                                !string.IsNullOrWhiteSpace(document.Name))
                             .OrderByDescending(document => document.AddedAt)
                             .Take(MaxDocuments))
                {
                    document.Chunks = document.Chunks
                        .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
                        .Take(MaxChunksPerDocument)
                        .ToList();
                    if (document.Chunks.Count > 0) _documents.Add(document);
                }
            }
        }
        catch
        {
            // 索引文件损坏时按空知识库处理
        }

        return _documents;
    }

    public async Task<KnowledgeDocument> ImportAsync(string path, CancellationToken cancellationToken)
    {
        var document = await ImportCoreAsync(path, cancellationToken);
        Save();
        return document;
    }

    /// <summary>递归导入文件夹中的受支持文本文件，最多 MaxFolderFiles 个，单文件超过 2 MB 会跳过。</summary>
    public async Task<KnowledgeImportResult> ImportFolderAsync(
        string folder,
        IProgress<KnowledgeImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"文件夹不存在：{folder}");

        var files = EnumerateTextFiles(folder)
            .Where(IsSupportedFile)
            .Select(path => new FileInfo(path))
            .Where(info => info.Length is > 0 and <= MaxFileBytes)
            .Take(MaxFolderFiles)
            .ToList();

        if (files.Count == 0) return new KnowledgeImportResult(0, 0, 0);

        var imported = 0;
        var skipped = 0;
        var failed = 0;
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            progress?.Report(new KnowledgeImportProgress(index + 1, files.Count, file.Name));

            try
            {
                var document = await ImportCoreAsync(file.FullName, cancellationToken);
                if (document.Chunks.Count == 0)
                {
                    skipped++;
                    continue;
                }

                imported++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failed++;
            }
        }

        if (imported > 0) Save();
        return new KnowledgeImportResult(imported, skipped, failed);
    }

    public bool Delete(string documentId)
    {
        var removed = _documents.RemoveAll(document =>
            document.Id.Equals(documentId, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Save();
        return removed;
    }

    public void Clear()
    {
        _documents.Clear();
        Save();
    }

    /// <summary>BM25 检索，英文词与中文二元组都参与打分；可选限定单个文档。</summary>
    public IReadOnlyList<KnowledgeHit> Search(string? query, int limit = 4, string? documentId = null)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(query)) return [];

        var terms = TokenizeCounts(query).Keys.Take(MaxQueryTerms).ToArray();
        if (terms.Length == 0) return [];

        var candidates = new List<(KnowledgeDocument Document, KnowledgeChunk Chunk)>();
        foreach (var document in _documents)
        {
            if (documentId is not null &&
                !document.Id.Equals(documentId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var chunk in document.Chunks)
            {
                if (!string.IsNullOrWhiteSpace(chunk.Text)) candidates.Add((document, chunk));
            }
        }

        if (candidates.Count == 0) return [];

        var termCounts = new List<Dictionary<string, int>>(candidates.Count);
        var lengths = new List<int>(candidates.Count);
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (_, chunk) in candidates)
        {
            var counts = TokenizeCounts(chunk.Text);
            termCounts.Add(counts);
            lengths.Add(counts.Values.Sum());

            foreach (var term in counts.Keys)
            {
                documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
            }
        }

        const double k1 = 1.4;
        const double b = 0.72;
        var averageLength = Math.Max(1, lengths.Average());
        var phrase = query.Trim();
        var hits = new List<KnowledgeHit>();

        for (var index = 0; index < candidates.Count; index++)
        {
            var counts = termCounts[index];
            var length = lengths[index];
            double score = 0;

            foreach (var term in terms)
            {
                if (!counts.TryGetValue(term, out var frequency)) continue;

                var df = documentFrequency[term];
                var idf = Math.Log(1 + (candidates.Count - df + 0.5) / (df + 0.5));
                score += idf * (frequency * (k1 + 1)) /
                         (frequency + k1 * (1 - b + b * length / averageLength));
            }

            if (phrase.Length >= 2 &&
                candidates[index].Chunk.Text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (score <= 0) continue;

            var (document, chunk) = candidates[index];
            hits.Add(new KnowledgeHit(document.Id, document.Name, chunk.Index, chunk.Text, score));
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.DocumentName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 10))
            .ToArray();
    }

    /// <summary>构造注入提示词的知识片段，带上来源编号，便于模型标注引用。</summary>
    public KnowledgeContext BuildContext(string? query, int limit = 4)
    {
        var hits = Search(query, limit);
        if (hits.Count == 0) return KnowledgeContext.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("以下是本机知识库中与问题相关的片段。仅在确实相关时引用，并在回答中用 [编号] 标注来源：");
        for (var index = 0; index < hits.Count; index++)
        {
            var hit = hits[index];
            builder.AppendLine($"[{index + 1}] {hit.DocumentName} · 第 {hit.ChunkIndex + 1} 段");
            builder.AppendLine(hit.Text);
            builder.AppendLine();
        }

        return new KnowledgeContext(builder.ToString().TrimEnd(), hits);
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_documents, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    private async Task<KnowledgeDocument> ImportCoreAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("文件不存在", path);
        if (!IsSupportedFile(path)) throw new InvalidDataException("仅支持 Markdown、文本与常见代码文件。");

        var info = new FileInfo(path);
        if (info.Length == 0) throw new InvalidDataException("文件内容为空。");
        if (info.Length > MaxFileBytes)
        {
            throw new InvalidDataException(
                $"文件超过 {Formatters.Bytes(MaxFileBytes)}，请拆分后再导入。");
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        if (text.Contains('\0')) throw new InvalidDataException("文件看起来是二进制内容，无法索引。");

        var chunks = SplitText(text).Take(MaxChunksPerDocument).ToList();
        if (chunks.Count == 0) throw new InvalidDataException("文件没有可索引的文本。");

        var fullPath = Path.GetFullPath(path);
        _documents.RemoveAll(document =>
            document.SourcePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));

        var document = new KnowledgeDocument
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Name = Path.GetFileName(path),
            SourcePath = fullPath,
            AddedAt = DateTime.Now,
            Bytes = info.Length,
            Chunks = chunks.Select((chunk, index) => new KnowledgeChunk
            {
                Id = $"{index}",
                Index = index,
                Text = chunk
            }).ToList()
        };

        _documents.Insert(0, document);
        while (_documents.Count > MaxDocuments) _documents.RemoveAt(_documents.Count - 1);
        return document;
    }

    /// <summary>按空行分段并把段落打包到约 900 字符；超长段落按 120 字符重叠切分。</summary>
    public static IReadOnlyList<string> SplitText(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var chunks = new List<string>();
        var buffer = new StringBuilder();

        void Flush()
        {
            var value = buffer.ToString().Trim();
            if (value.Length > 0) chunks.Add(value);
            buffer.Clear();
        }

        foreach (var paragraph in ParagraphBreak.Split(normalized))
        {
            var clean = paragraph.Trim();
            if (clean.Length == 0) continue;

            if (clean.Length > ChunkSize)
            {
                Flush();
                for (var start = 0; start < clean.Length; start += ChunkSize - ChunkOverlap)
                {
                    var take = Math.Min(ChunkSize, clean.Length - start);
                    var piece = clean.Substring(start, take).Trim();
                    if (piece.Length > 0) chunks.Add(piece);
                    if (start + take >= clean.Length) break;
                }

                continue;
            }

            if (buffer.Length > 0 && buffer.Length + clean.Length + 1 > ChunkSize) Flush();
            if (buffer.Length > 0) buffer.Append('\n');
            buffer.Append(clean);
        }

        Flush();
        return chunks;
    }

    private static IEnumerable<string> EnumerateTextFiles(string folder)
    {
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((folder, 0));
        var emitted = 0;
        var limit = MaxFolderFiles * 5;

        while (queue.Count > 0 && emitted < limit)
        {
            var (directory, depth) = queue.Dequeue();
            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch { files = []; }

            foreach (var file in files)
            {
                yield return file;
                if (++emitted >= limit) yield break;
            }

            if (depth >= 4) continue;

            string[] directories;
            try { directories = Directory.GetDirectories(directory); }
            catch { continue; }

            foreach (var child in directories)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch
                {
                    continue;
                }

                queue.Enqueue((child, depth + 1));
            }
        }
    }

    private static Dictionary<string, int> TokenizeCounts(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(string term)
        {
            if (term.Length == 0) return;
            counts[term] = counts.GetValueOrDefault(term) + 1;
        }

        foreach (Match match in LatinToken.Matches(text.ToLowerInvariant()))
        {
            Add(match.Value);
        }

        var run = new StringBuilder();
        void FlushRun()
        {
            if (run.Length == 1) Add(run.ToString());
            for (var index = 0; index + 1 < run.Length; index++)
            {
                Add(run.ToString(index, 2));
            }

            run.Clear();
        }

        foreach (var character in text)
        {
            if (IsCjk(character)) run.Append(character);
            else FlushRun();
        }

        FlushRun();
        return counts;
    }

    private static bool IsCjk(char value) =>
        value is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF';
}
