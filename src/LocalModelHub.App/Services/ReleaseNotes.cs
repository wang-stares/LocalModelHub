using System.Reflection;

namespace LocalModelHub.App.Services;

/// <summary>一个版本节点的更新公告，数据来自内嵌的 CHANGELOG.md。</summary>
public sealed class ReleaseNote
{
    public string Version { get; init; } = "";
    public string Date { get; init; } = "";
    public List<ReleaseSection> Sections { get; } = [];

    private IReadOnlyList<ReleaseLine>? _lines;

    /// <summary>展开后的标题与条目，供列表直接绑定。</summary>
    public IReadOnlyList<ReleaseLine> Lines => _lines ??= Flatten().ToList();

    public IEnumerable<ReleaseLine> Flatten()
    {
        foreach (var section in Sections)
        {
            yield return new ReleaseLine { Text = section.Title, IsHeading = true };
            foreach (var item in section.Items)
            {
                yield return new ReleaseLine { Text = $"• {item}" };
            }
        }
    }
}

public sealed class ReleaseSection
{
    public string Title { get; init; } = "";
    public List<string> Items { get; } = [];
}

public sealed class ReleaseLine
{
    public string Text { get; init; } = "";
    public bool IsHeading { get; init; }
}

public static class ReleaseNotes
{
    private const string ResourceName = "LocalModelHub.CHANGELOG.md";

    public static IReadOnlyList<ReleaseNote> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return [];

        var notes = new List<ReleaseNote>();
        ReleaseNote? note = null;
        ReleaseSection? section = null;

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## [", StringComparison.Ordinal))
            {
                var end = line.IndexOf(']');
                if (end < 0) continue;
                var version = line[4..end];
                var date = line[(end + 1)..].Trim().TrimStart('-').Trim();
                note = new ReleaseNote { Version = version, Date = date };
                section = null;
                notes.Add(note);
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal) && note is not null)
            {
                section = new ReleaseSection { Title = line[4..].Trim() };
                note.Sections.Add(section);
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) && section is not null)
            {
                section.Items.Add(line[2..].Trim());
            }
        }

        return notes;
    }
}
