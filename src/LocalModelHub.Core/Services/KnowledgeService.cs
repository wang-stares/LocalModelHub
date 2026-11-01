using System.Text;
using System.Diagnostics;
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

    // 倒排索引：term → (chunk 序号, 词频)。文档增删改时整体重建（16 万 chunk < 1 s），查询变成 O(命中词)。
    private readonly object _indexLock = new();
    private readonly List<IndexEntry> _entries = [];
    private readonly Dictionary<string, List<Posting>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedQuery> _queryCache = new(StringComparer.Ordinal);
    private double _averageLength = 1;
    private int _dataVersion;
    private int _indexVersion = -1;

    // 混合检索（可选）：chunk 向量按数据版本缓存；不落盘，文档变化后自动失效。
    private readonly Dictionary<int, float[]> _vectors = new();
    private int _vectorsVersion = -1;
    private const int MaxVectorChunks = 512;
    private const int EmbeddingBatchSize = 32;
    private const int RrfK = 60;

    /// <summary>可选的向量提供者；为空时只做 BM25，行为与未开启混合检索一致。</summary>
    public IEmbeddingProvider? Embeddings { get; set; }

    /// <summary>同 query 的结果缓存命中次数（Agent 多轮常重复同一问题）；供诊断与测试。</summary>
    public long QueryCacheHits { get; private set; }

    /// <summary>索引重建次数；文档变化后第一次查询会 +1。</summary>
    public long IndexBuilds { get; private set; }

    private sealed record IndexEntry(KnowledgeDocument Document, KnowledgeChunk Chunk, int Length);
    private sealed record Posting(int Entry, int Frequency);
    private sealed record CachedQuery(DateTime ExpiresAt, IReadOnlyList<KnowledgeHit> Hits);

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

        MarkDirty();
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
        if (removed)
        {
            MarkDirty();
            Save();
        }

        return removed;
    }

    public void Clear()
    {
        _documents.Clear();
        MarkDirty();
        Save();
    }

    /// <summary>BM25 检索，英文词与中文二元组都参与打分；可选限定单个文档。首次查询建倒排索引，60 秒内同 query 走缓存。</summary>
    public IReadOnlyList<KnowledgeHit> Search(string? query, int limit = 4, string? documentId = null)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(query)) return [];

        var key = $"{documentId ?? "*"}|{limit}|{query.Trim()}";
        lock (_indexLock)
        {
            var now = DateTime.UtcNow;
            if (_queryCache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            {
                QueryCacheHits++;
                return cached.Hits;
            }

            EnsureIndex();
            if (_entries.Count == 0) return [];

            var terms = TokenizeCounts(query).Keys.Take(MaxQueryTerms).ToArray();
            if (terms.Length == 0) return [];

            const double k1 = 1.4;
            const double b = 0.72;
            var scores = new Dictionary<int, double>();
            var totalEntries = _entries.Count;

            foreach (var term in terms)
            {
                if (!_postings.TryGetValue(term, out var postings)) continue;
                if (!_documentFrequency.TryGetValue(term, out var df) || df <= 0) continue;

                var idf = Math.Log(1 + (totalEntries - df + 0.5) / (df + 0.5));
                foreach (var posting in postings)
                {
                    var entry = _entries[posting.Entry];
                    if (documentId is not null &&
                        !entry.Document.Id.Equals(documentId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var norm = posting.Frequency + k1 * (1 - b + b * entry.Length / _averageLength);
                    scores[posting.Entry] = scores.GetValueOrDefault(posting.Entry) +
                                            idf * (posting.Frequency * (k1 + 1)) / norm;
                }
            }

            var phrase = query.Trim();
            var hits = new List<KnowledgeHit>();
            foreach (var (entryIndex, score) in scores)
            {
                var entry = _entries[entryIndex];
                var total = score;
                if (phrase.Length >= 2 &&
                    entry.Chunk.Text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                {
                    total += 2;
                }

                if (total <= 0) continue;
                hits.Add(new KnowledgeHit(
                    entry.Document.Id, entry.Document.Name, entry.Chunk.Index, entry.Chunk.Text, total));
            }

            var result = hits
                .OrderByDescending(hit => hit.Score)
                .ThenBy(hit => hit.DocumentName, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(limit, 1, 10))
                .ToArray();

            if (_queryCache.Count >= 64) PruneQueryCache(now);
            _queryCache[key] = new CachedQuery(now.AddSeconds(60), result);
            return result;
        }
    }

    /// <summary>文档变化时让索引与查询缓存失效；下一次查询时重建。</summary>
    private void MarkDirty()
    {
        _dataVersion++;
        lock (_indexLock)
        {
            // 缓存必须随数据变化立即失效：否则删除文档后 60 秒内仍会命中旧结果。
            _queryCache.Clear();
        }
    }

    /// <summary>调用方需持有 <see cref="_indexLock"/>。</summary>
    private void EnsureIndex()
    {
        if (_indexVersion == _dataVersion) return;

        _entries.Clear();
        _postings.Clear();
        _documentFrequency.Clear();
        _queryCache.Clear();

        var totalLength = 0;
        foreach (var document in _documents)
        {
            foreach (var chunk in document.Chunks)
            {
                if (string.IsNullOrWhiteSpace(chunk.Text)) continue;

                var counts = TokenizeCounts(chunk.Text);
                var length = Math.Max(1, counts.Values.Sum());
                var entryIndex = _entries.Count;
                _entries.Add(new IndexEntry(document, chunk, length));
                totalLength += length;

                foreach (var (term, frequency) in counts)
                {
                    if (!_postings.TryGetValue(term, out var postings))
                    {
                        postings = [];
                        _postings[term] = postings;
                    }

                    postings.Add(new Posting(entryIndex, frequency));
                    _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
                }
            }
        }

        _averageLength = _entries.Count == 0 ? 1 : Math.Max(1, totalLength / (double)_entries.Count);
        _indexVersion = _dataVersion;
        IndexBuilds++;
    }

    private void PruneQueryCache(DateTime now)
    {
        foreach (var expired in _queryCache.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList())
        {
            _queryCache.Remove(expired);
        }

        if (_queryCache.Count < 64) return;
        _queryCache.Clear();
    }

    /// <summary>构造注入提示词的知识片段，带上来源编号，便于模型标注引用。</summary>
    public KnowledgeContext BuildContext(string? query, int limit = 4)
    {
        var hits = Search(query, limit);
        return FormatContext(hits);
    }

    /// <summary>
    /// 可选混合检索（P1-15）：BM25 与向量检索各出候选，用 RRF 融合排序。
    /// 未开启、没有向量提供者或调用失败时静默回退到纯 BM25，不阻断对话。
    /// </summary>
    public async Task<KnowledgeContext> BuildContextAsync(
        string? query,
        int limit = 4,
        bool hybrid = false,
        CancellationToken cancellationToken = default)
    {
        if (!hybrid || Embeddings is null || string.IsNullOrWhiteSpace(query))
        {
            return BuildContext(query, limit);
        }

        try
        {
            var hits = await HybridSearchAsync(query, limit, cancellationToken);
            if (hits.Count > 0) return FormatContext(hits);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 嵌入端点不可用（模型不支持 / 服务刚重启 / 请求被拒）时保持 BM25 结果。
            Debug.WriteLine($"[Knowledge] 混合检索失败，回退 BM25：{ex.Message}");
        }

        return BuildContext(query, limit);
    }

    private async Task<IReadOnlyList<KnowledgeHit>> HybridSearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        List<IndexEntry> entries;
        Dictionary<(string DocumentId, int ChunkIndex), int> lookup;
        lock (_indexLock)
        {
            EnsureIndex();
            if (_entries.Count == 0 || _entries.Count > MaxVectorChunks) return [];
            entries = [.. _entries];
            lookup = entries
                .Select((entry, index) => (entry, index))
                .ToDictionary(
                    item => (item.entry.Document.Id, item.entry.Chunk.Index),
                    item => item.index);
        }

        var bm25 = Search(query, 10);
        var vectors = await EnsureVectorsAsync(entries, cancellationToken);
        var queryVector = (await Embeddings!.EmbedAsync([query.Trim()], cancellationToken)).FirstOrDefault();
        if (queryVector is null) return [];

        var fused = new Dictionary<int, double>();
        for (var rank = 0; rank < bm25.Count; rank++)
        {
            var hit = bm25[rank];
            if (!lookup.TryGetValue((hit.DocumentId, hit.ChunkIndex), out var index)) continue;
            fused[index] = fused.GetValueOrDefault(index) + 1.0 / (RrfK + rank + 1);
        }

        var vectorRanks = vectors
            .Select(pair => (Index: pair.Key, Score: Cosine(queryVector, pair.Value)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(10)
            .ToList();

        for (var rank = 0; rank < vectorRanks.Count; rank++)
        {
            fused[vectorRanks[rank].Index] =
                fused.GetValueOrDefault(vectorRanks[rank].Index) + 1.0 / (RrfK + rank + 1);
        }

        return fused
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => entries[pair.Key].Document.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 10))
            .Select(pair => new KnowledgeHit(
                entries[pair.Key].Document.Id,
                entries[pair.Key].Document.Name,
                entries[pair.Key].Chunk.Index,
                entries[pair.Key].Chunk.Text,
                pair.Value))
            .ToArray();
    }

    /// <summary>按数据版本缓存 chunk 向量；只补算缺失项，分批请求嵌入接口。</summary>
    private async Task<Dictionary<int, float[]>> EnsureVectorsAsync(
        IReadOnlyList<IndexEntry> entries,
        CancellationToken cancellationToken)
    {
        List<int> missing;
        lock (_indexLock)
        {
            if (_vectorsVersion != _dataVersion)
            {
                _vectors.Clear();
                _vectorsVersion = _dataVersion;
            }

            missing = Enumerable.Range(0, entries.Count).Where(index => !_vectors.ContainsKey(index)).ToList();
        }

        if (missing.Count > 0)
        {
            var computed = new List<(int Index, float[] Vector)>();
            foreach (var batch in missing.Chunk(EmbeddingBatchSize))
            {
                var texts = batch.Select(index => entries[index].Chunk.Text).ToArray();
                var result = await Embeddings!.EmbedAsync(texts, cancellationToken);
                for (var offset = 0; offset < Math.Min(batch.Length, result.Count); offset++)
                {
                    computed.Add((batch[offset], result[offset]));
                }
            }

            lock (_indexLock)
            {
                // 计算期间文档可能变化：版本不一致时丢弃这批向量，下次重新计算。
                if (_vectorsVersion == _dataVersion)
                {
                    foreach (var (index, vector) in computed) _vectors[index] = vector;
                }
            }
        }

        lock (_indexLock)
        {
            return new Dictionary<int, float[]>(_vectors);
        }
    }

    private static double Cosine(float[] left, float[] right)
    {
        if (left.Length == 0 || left.Length != right.Length) return 0;
        var dot = 0.0;
        var leftNorm = 0.0;
        var rightNorm = 0.0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        if (leftNorm <= 0 || rightNorm <= 0) return 0;
        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    private static KnowledgeContext FormatContext(IReadOnlyList<KnowledgeHit> hits)
    {
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
        MarkDirty();
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
