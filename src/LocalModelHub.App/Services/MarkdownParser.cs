namespace LocalModelHub.App.Services;

public enum MarkdownBlockKind
{
    Paragraph,
    Heading1,
    Heading2,
    Heading3,
    Bullet,
    Code
}

public sealed record MarkdownBlock(MarkdownBlockKind Kind, string Text, string Language = "");

/// <summary>轻量 Markdown 解析：段落、标题、列表、围栏代码块。</summary>
public static class MarkdownParser
{
    public static List<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrEmpty(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd();

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                Flush(blocks, paragraph);
                var language = line[3..].Trim();
                var code = new List<string>();

                index++;
                while (index < lines.Length && !lines[index].TrimEnd().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[index]);
                    index++;
                }

                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, string.Join('\n', code), language));
                continue;
            }

            if (line.Length == 0)
            {
                Flush(blocks, paragraph);
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush(blocks, paragraph);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading3, line[4..].Trim()));
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush(blocks, paragraph);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading2, line[3..].Trim()));
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                Flush(blocks, paragraph);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading1, line[2..].Trim()));
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("* ", StringComparison.Ordinal) ||
                line.StartsWith("• ", StringComparison.Ordinal))
            {
                Flush(blocks, paragraph);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, line[2..].Trim()));
                continue;
            }

            var dot = line.IndexOf(". ", StringComparison.Ordinal);
            if (dot is > 0 and <= 2 && line[..dot].All(char.IsDigit))
            {
                Flush(blocks, paragraph);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, line[(dot + 2)..].Trim()));
                continue;
            }

            paragraph.Add(line);
        }

        Flush(blocks, paragraph);
        return blocks;
    }

    private static void Flush(List<MarkdownBlock> blocks, List<string> paragraph)
    {
        if (paragraph.Count == 0) return;
        blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, string.Join('\n', paragraph)));
        paragraph.Clear();
    }
}
