namespace LocalModelHub.Core.Models;

/// <summary>知识库中的一份文档，正文已切分为片段并保存在本机。</summary>
public sealed class KnowledgeDocument
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public long Bytes { get; set; }
    public List<KnowledgeChunk> Chunks { get; set; } = [];
}

/// <summary>文档的一个可检索片段。</summary>
public sealed class KnowledgeChunk
{
    public string Id { get; set; } = "";
    public int Index { get; set; }
    public string Text { get; set; } = "";
}
