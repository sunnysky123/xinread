using System;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace xinread.Rendering;

/// <summary>渲染结果：一个正文块对应的容器控件、它的纯文本，以及原块类型。</summary>
internal sealed record RenderedBlock(
    Border Container,
    string PlainText,
    MarkdownBlockKind Kind,
    int Level,
    TextBlock? Text,
    string ImageUrl = "",
    Image? Picture = null)
{
    /// <summary>
    /// 容器底色跟随的主题角色。引用块与代码块自带底色，主题切换时要重新取一次；
    /// 其余块用透明底色，保持为 null。
    /// </summary>
    public ReaderBrushRole? ContainerRole { get; init; }

    /// <summary>
    /// 真正承载 <see cref="ContainerRole"/> 底色的控件。多数块就是 <see cref="Container"/>，
    /// 但引用块的高亮换色发生在外层 wrapper 上，底色在内层 Border 上，两者必须分开。
    /// </summary>
    public Border? BackgroundHost { get; init; }

    /// <summary>引用块左侧竖线的主题角色，只有引用块有。</summary>
    public ReaderBrushRole? RuleRole { get; init; }

    /// <summary>引用块左侧竖线的控件，只有引用块有。</summary>
    public Border? Rule { get; init; }

    /// <summary>正文文字使用次要色的块（引用块），主题切换时要重新取一次。</summary>
    public bool UsesSecondaryText { get; init; }
}

/// <summary>
/// 把 <see cref="MarkdownBlock"/> 渲染成原生控件。整章只创建一次控件树，
/// 之后调字号只改进已有 <see cref="TextBlock.FontSize"/>，不重建、不丢滚动位置。
/// </summary>
internal static class MarkdownRenderer
{
    private const double LineHeightRatio = 1.9;
    private const double ImageMaxHeight = 620;
    private const double ImagePlaceholderHeight = 72;
    private const string MonospaceFamily = "Consolas, Cascadia Mono, Courier New";

    public static IReadOnlyList<RenderedBlock> Render(StackPanel host, IReadOnlyList<MarkdownBlock> blocks, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(blocks);

        host.Children.Clear();
        var rendered = new List<RenderedBlock>(blocks.Count);

        foreach (MarkdownBlock block in blocks)
        {
            RenderedBlock? item = block.Kind switch
            {
                MarkdownBlockKind.Separator => RenderSeparator(block),
                MarkdownBlockKind.CodeBlock => RenderCode(block, fontSize),
                MarkdownBlockKind.Image => RenderImage(block),
                _ => RenderText(block, fontSize),
            };

            if (item is null)
            {
                continue;
            }

            host.Children.Add(item.Container);
            rendered.Add(item);
        }

        return rendered;
    }

    /// <summary>字号变化时按块类型重算，标题与代码块保持相对比例。</summary>
    public static void ApplyFontSize(IReadOnlyList<RenderedBlock> blocks, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        foreach (RenderedBlock block in blocks)
        {
            if (block.Text is null)
            {
                continue;
            }

            double size = ScaleOf(block.Kind, block.Level, fontSize);
            block.Text.FontSize = size;
            block.Text.LineHeight = size * (block.Kind == MarkdownBlockKind.CodeBlock ? 1.6 : LineHeightRatio);
        }
    }

    /// <summary>
    /// 主题切换后重新给正文着色。只改画刷，不重建控件树，
    /// 因此滚动位置、选中内容和朗读高亮都能保住。
    /// </summary>
    /// <param name="theme">调用方已知的实际主题；为 null 时按应用级设置判断。</param>
    public static void ApplyTheme(IReadOnlyList<RenderedBlock> blocks, ElementTheme? theme = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        foreach (RenderedBlock block in blocks)
        {
            if (block.ContainerRole is { } containerRole)
            {
                Border host = block.BackgroundHost ?? block.Container;
                host.Background = ReaderTheme.Resolve(containerRole, theme);
            }

            if (block.Rule is not null && block.RuleRole is { } ruleRole)
            {
                block.Rule.Background = ReaderTheme.Resolve(ruleRole, theme);
            }

            if (block.UsesSecondaryText && block.Text is { } text)
            {
                text.Foreground = ReaderTheme.Resolve(ReaderBrushRole.SecondaryText, theme);
            }
        }
    }

    /// <summary>正文块的字号：标题按级别放大，代码块略小，其余跟随用户设置。</summary>
    public static double ScaleOf(MarkdownBlockKind kind, int level, double fontSize) => kind switch
    {
        MarkdownBlockKind.Heading => fontSize * (level switch
        {
            1 => 1.45,
            2 => 1.3,
            3 => 1.15,
            4 => 1.08,
            _ => 1.0,
        }),
        MarkdownBlockKind.CodeBlock => Math.Max(10, fontSize - 1),
        _ => fontSize,
    };

    private static RenderedBlock RenderText(MarkdownBlock block, double fontSize)
    {
        double size = ScaleOf(block.Kind, block.Level, fontSize);
        var text = new TextBlock
        {
            FontSize = size,
            LineHeight = size * LineHeightRatio,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading:
                text.FontWeight = FontWeights.SemiBold;
                text.Margin = new Thickness(0, block.Level == 1 ? 20 : 12, 0, 8);
                break;
            case MarkdownBlockKind.Quote:
                text.FontStyle = Windows.UI.Text.FontStyle.Italic;
                text.Foreground = ReaderTheme.Resolve(ReaderBrushRole.SecondaryText);
                text.Margin = new Thickness(12, 4, 0, 10);
                break;
            case MarkdownBlockKind.ListItem:
                text.Margin = new Thickness(16, 0, 0, 6);
                break;
            default:
                text.Margin = new Thickness(0, 0, 0, 10);
                break;
        }

        if (block.Kind is MarkdownBlockKind.Paragraph or MarkdownBlockKind.Quote)
        {
            // 中文正文首行缩进两字，与原 WebView 里的 text-indent 观感一致。
            text.Inlines.Add(new Run { Text = "\u3000\u3000" });
        }

        foreach (MarkdownSpan span in MarkdownParser.ParseInline(block.Text))
        {
            if (span.Code)
            {
                text.Inlines.Add(new Run
                {
                    Text = span.Text,
                    FontFamily = new FontFamily(MonospaceFamily),
                    FontSize = Math.Max(10, size - 1),
                });
                continue;
            }

            var run = new Run { Text = span.Text };
            if (span.Bold)
            {
                run.FontWeight = FontWeights.Bold;
            }

            if (span.Italic)
            {
                run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            }

            text.Inlines.Add(run);
        }

        if (block.Kind == MarkdownBlockKind.Quote)
        {
            return RenderQuote(block, text);
        }

        var container = new Border
        {
            // 左右各留 8px 内边距并配负外边距：高亮底色能略微超出文字，且不影响排版位置。
            Margin = new Thickness(-8, 0, -8, 0),
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(4),
            Background = TransparentBrush(),
            Child = text,
        };

        return new RenderedBlock(container, block.PlainText, block.Kind, block.Level, text);
    }

    /// <summary>
    /// 引用块：左侧竖线 + 浅色底。朗读高亮换的是内层 Border 的背景，
    /// 读完由 <see cref="ReaderPage"/> 还原成这里设置的底色。
    /// </summary>
    private static RenderedBlock RenderQuote(MarkdownBlock block, TextBlock text)
    {
        var content = new Border
        {
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(4),
            Background = ReaderTheme.Resolve(ReaderBrushRole.QuoteBackground),
            Child = text,
        };

        var rule = new Border
        {
            Width = 3,
            Margin = new Thickness(-8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ReaderTheme.Resolve(ReaderBrushRole.QuoteRule),
        };

        var grid = new Grid();
        grid.Children.Add(rule);
        grid.Children.Add(content);

        var wrapper = new Border { Margin = new Thickness(0, 4, 0, 12), Child = grid };
        return new RenderedBlock(wrapper, block.PlainText, block.Kind, block.Level, text)
        {
            BackgroundHost = content,
            ContainerRole = ReaderBrushRole.QuoteBackground,
            Rule = rule,
            RuleRole = ReaderBrushRole.QuoteRule,
            UsesSecondaryText = true,
        };
    }

    private static RenderedBlock RenderCode(MarkdownBlock block, double fontSize)
    {
        double size = ScaleOf(MarkdownBlockKind.CodeBlock, 0, fontSize);
        var text = new TextBlock
        {
            Text = block.PlainText,
            FontFamily = new FontFamily(MonospaceFamily),
            FontSize = size,
            LineHeight = size * 1.6,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        var container = new Border
        {
            Margin = new Thickness(0, 4, 0, 12),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(4),
            Background = ReaderTheme.Resolve(ReaderBrushRole.CodeBackground),
            Child = text,
        };

        return new RenderedBlock(container, block.PlainText, MarkdownBlockKind.CodeBlock, 0, text)
        {
            ContainerRole = ReaderBrushRole.CodeBackground,
        };
    }

    /// <summary>
    /// 插图块：先给一个最小高度占位，图片下载完再淡入；下载失败时整块折叠，不影响正文。
    /// 高度用 <c>MaxHeight</c> 兜底，超高的竖版封面不会把一屏全占满。
    /// </summary>
    private static RenderedBlock RenderImage(MarkdownBlock block)
    {
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxHeight = ImageMaxHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0,
        };

        if (!string.IsNullOrEmpty(block.Alt))
        {
            AutomationProperties.SetName(image, block.Alt);
        }

        var host = new Border
        {
            Margin = new Thickness(0, 8, 0, 16),
            MinHeight = ImagePlaceholderHeight,
            CornerRadius = new CornerRadius(4),
            Background = TransparentBrush(),
            Child = image,
        };

        var container = new Border
        {
            Margin = new Thickness(-8, 0, -8, 0),
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(4),
            Background = TransparentBrush(),
            Child = host,
        };

        return new RenderedBlock(
            container,
            string.Empty,
            MarkdownBlockKind.Image,
            0,
            null,
            block.Text,
            image);
    }

    /// <summary>
    /// 插图加载完成后的收尾：淡入显示；失败或为空则把整块折叠掉，不留空白。
    /// </summary>
    public static void ApplyImageState(RenderedBlock block, bool loaded)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (!loaded)
        {
            block.Container.Visibility = Visibility.Collapsed;
            return;
        }

        if (block.Picture is null)
        {
            return;
        }

        block.Picture.Opacity = 1;

        // 小配图（分隔图、装饰图）不该被拉满整行宽度：按解码后的像素宽度封顶。
        // 缩放比例取不到 1 时按 1 处理，避免出现 0 或负数的 MaxWidth。
        double scale = block.Picture.XamlRoot?.RasterizationScale ?? 1;
        if (scale <= 0)
        {
            scale = 1;
        }

        if (block.Picture.Source is BitmapImage bitmap && bitmap.PixelWidth > 0)
        {
            block.Picture.MaxWidth = bitmap.PixelWidth / scale;
        }

        // 下载成功后撤销占位高度，否则小图下方会留出一段空白。
        if (block.Container.Child is Border placeholder)
        {
            placeholder.MinHeight = 0;
        }
    }

    private static RenderedBlock RenderSeparator(MarkdownBlock block)
    {
        var container = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 12, 0, 16),
            Background = ReaderTheme.Resolve(ReaderBrushRole.Divider),
        };

        return new RenderedBlock(container, string.Empty, MarkdownBlockKind.Separator, 0, null)
        {
            ContainerRole = ReaderBrushRole.Divider,
        };
    }

    /// <summary>
    /// 切换某个块的朗读高亮。高亮总是画在「真正承载底色的那个控件」上，
    /// 取消高亮时按块自己的主题角色重新取色，因此中途切换明暗主题也不会留下旧颜色。
    /// </summary>
    public static void SetHighlight(RenderedBlock block, bool highlighted, ElementTheme? theme = null)
    {
        ArgumentNullException.ThrowIfNull(block);

        Border host = block.BackgroundHost ?? block.Container;
        host.Background = highlighted
            ? ReaderTheme.Resolve(ReaderBrushRole.Highlight, theme)
            : block.ContainerRole is { } role
                ? ReaderTheme.Resolve(role, theme)
                : TransparentBrush();
    }

    private static Brush TransparentBrush() => new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
