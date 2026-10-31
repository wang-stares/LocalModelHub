using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using LocalModelHub.App.Services;

namespace LocalModelHub.App.Views.Controls;

/// <summary>聊天消息的 Markdown 渲染：段落 / 标题 / 列表 / 代码块（带复制按钮）。</summary>
public sealed class MarkdownView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, monospace");

    private readonly StackPanel _panel = new() { Spacing = 7 };

    public MarkdownView() => Content = _panel;

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty) Rebuild();
    }

    private void Rebuild()
    {
        _panel.Children.Clear();
        foreach (var block in MarkdownParser.Parse(Markdown))
        {
            _panel.Children.Add(CreateBlock(block));
        }
    }

    private Control CreateBlock(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading1 => CreateTextBlock(block.Text, 17, FontWeight.SemiBold, 24),
        MarkdownBlockKind.Heading2 => CreateTextBlock(block.Text, 15.5, FontWeight.SemiBold, 22),
        MarkdownBlockKind.Heading3 => CreateTextBlock(block.Text, 14, FontWeight.SemiBold, 21),
        MarkdownBlockKind.Bullet => CreateTextBlock("•  " + block.Text, 13, FontWeight.Normal, 20, margin: new Thickness(4, 0, 0, 0)),
        MarkdownBlockKind.Code => CreateCodeBlock(block),
        _ => CreateTextBlock(block.Text, 13, FontWeight.Normal, 21)
    };

    private static TextBlock CreateTextBlock(
        string text, double fontSize, FontWeight weight, double lineHeight, Thickness? margin = null)
    {
        var textBlock = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = lineHeight,
            Margin = margin ?? default
        };

        AddInlines(textBlock.Inlines, text);
        return textBlock;
    }

    private static void AddInlines(InlineCollection inlines, string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var bold = text.IndexOf("**", index, StringComparison.Ordinal);
            var code = text.IndexOf('`', index);

            var next = NextMarker(bold, code);
            if (next < 0)
            {
                inlines.Add(new Run(text[index..]));
                return;
            }

            if (next > index) inlines.Add(new Run(text[index..next]));

            if (next == code)
            {
                var close = text.IndexOf('`', next + 1);
                if (close < 0)
                {
                    inlines.Add(new Run(text[next..]));
                    return;
                }

                inlines.Add(new Run(text[(next + 1)..close]) { FontFamily = MonoFont, FontSize = 12 });
                index = close + 1;
            }
            else
            {
                var close = text.IndexOf("**", next + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    inlines.Add(new Run(text[next..]));
                    return;
                }

                inlines.Add(new Run(text[(next + 2)..close]) { FontWeight = FontWeight.SemiBold });
                index = close + 2;
            }
        }
    }

    private static int NextMarker(int bold, int code)
    {
        if (bold < 0) return code;
        if (code < 0) return bold;
        return Math.Min(bold, code);
    }

    private Control CreateCodeBlock(MarkdownBlock block)
    {
        var border = new Border { Classes = { "codeBlock" } };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 6)
        };

        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(block.Language) ? "代码" : block.Language,
            Classes = { "caption" },
            VerticalAlignment = VerticalAlignment.Center
        });

        var copy = new Button
        {
            Content = "复制",
            FontSize = 10.5,
            Padding = new Thickness(8, 2),
            MinHeight = 0,
            Classes = { "ghost" }
        };
        Grid.SetColumn(copy, 1);
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;

            await clipboard.SetTextAsync(block.Text);
            copy.Content = "已复制";
        };
        header.Children.Add(copy);

        var code = new SelectableTextBlock
        {
            Text = block.Text,
            FontFamily = MonoFont,
            FontSize = 11.5,
            TextWrapping = TextWrapping.NoWrap
        };

        var scroller = new ScrollViewer
        {
            Content = code,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(header);
        panel.Children.Add(scroller);
        border.Child = panel;
        return border;
    }
}
