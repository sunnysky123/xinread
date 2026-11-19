using System;
using System.Collections.Generic;
using System.Text;

namespace xinread.Rendering;

/// <summary>Markdown 的块级类型，决定渲染成什么样的原生控件。</summary>
internal enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    Quote,
    ListItem,
    CodeBlock,
    Separator,
    Image,
}

/// <summary>
/// 一个块级元素。<see cref="Text"/> 保留行内标记（<c>**粗体**</c> 等），
/// <see cref="PlainText"/> 是去掉标记后的纯文本，用于朗读。
/// </summary>
internal sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    string Text,
    string PlainText,
    int Level = 0,
    string Alt = "");

/// <summary>
/// 只覆盖服务端正文会用到的 Markdown 子集：标题、段落、引用、列表、代码块、分隔线、图片。
/// 手写而不引入第三方解析器，是为了 Trim 发布后不依赖反射。
/// </summary>
internal static class MarkdownParser
{
    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<string>();
        var quote = new List<string>();
        var code = new List<string>();
        bool inCode = false;

        foreach (string rawLine in markdown.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (inCode)
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    AddCodeBlock(blocks, code);
                    inCode = false;
                }
                else
                {
                    code.Add(line);
                }

                continue;
            }

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(blocks, paragraph);
                FlushQuote(blocks, quote);
                inCode = true;
                code.Clear();
                continue;
            }

            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                FlushParagraph(blocks, paragraph);
                FlushQuote(blocks, quote);
                continue;
            }

            if (IsSeparator(trimmed))
            {
                FlushParagraph(blocks, paragraph);
                FlushQuote(blocks, quote);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Separator, string.Empty, string.Empty));
                continue;
            }

            if (TryReadImage(trimmed, out string imageUrl, out string imageAlt))
            {
                FlushParagraph(blocks, paragraph);
                FlushQuote(blocks, quote);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Image, imageUrl, string.Empty, 0, imageAlt));
                continue;
            }

            if (TryReadHeading(trimmed, out int level, out string headingText))
            {
                FlushParagraph(blocks, paragraph);
                FlushQuote(blocks, quote);
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.Heading,
                    headingText,
                    StripInline(headingText),
                    level));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph(blocks, paragraph);
                string inner = trimmed[1..].TrimStart();
                if (inner.Length > 0)
                {
                    quote.Add(inner);
                }

                continue;
            }

            if (TryReadListItem(trimmed, out string marker, out string itemText))
            {
                FlushParagraph(blocks, paragraph);
                FlushQuote(blocks, quote);
                // 项目符号要显示在行首，但不进朗读文本，免得把“•”念出来。
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.ListItem,
                    marker + " " + itemText,
                    StripInline(itemText)));
                continue;
            }

            FlushQuote(blocks, quote);
            paragraph.Add(trimmed);
        }

        if (inCode)
        {
            AddCodeBlock(blocks, code);
        }

        FlushParagraph(blocks, paragraph);
        FlushQuote(blocks, quote);
        return blocks;
    }

    private static void AddCodeBlock(List<MarkdownBlock> blocks, List<string> lines)
    {
        while (lines.Count > 0 && lines[0].Trim().Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return;
        }

        string text = string.Join("\n", lines);
        blocks.Add(new MarkdownBlock(MarkdownBlockKind.CodeBlock, text, text));
    }

    private static void FlushParagraph(List<MarkdownBlock> blocks, List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        // 段内的软换行合成一行；只有源里的硬换行（转成 \n）才保留。
        string text = string.Join(" ", lines);
        lines.Clear();
        blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, text, StripInline(text)));
    }

    private static void FlushQuote(List<MarkdownBlock> blocks, List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        string text = string.Join(" ", lines);
        lines.Clear();
        blocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote, text, StripInline(text)));
    }

    /// <summary>解析独占一行的 <c>![说明](地址)</c>。地址后的 title 段会被忽略。</summary>
    private static bool TryReadImage(string trimmed, out string url, out string alt)
    {
        url = string.Empty;
        alt = string.Empty;

        if (!trimmed.StartsWith("![", StringComparison.Ordinal))
        {
            return false;
        }

        int altEnd = trimmed.IndexOf("](", StringComparison.Ordinal);
        if (altEnd < 0 || !trimmed.EndsWith(')'))
        {
            return false;
        }

        string target = trimmed[(altEnd + 2)..^1].Trim();
        int space = target.IndexOfAny([' ', '\t']);
        if (space > 0)
        {
            target = target[..space];
        }

        // 只认网络地址：txt 正文里偶尔出现的 “![x](y)” 是原文，不该被当成插图。
        if (!target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        alt = Unescape(trimmed[2..altEnd].Trim());
        url = Unescape(target);
        return url.Length > 0;
    }

    /// <summary>还原 <c>\\</c> <c>\*</c> <c>\`</c> <c>\#</c> <c>\-</c> <c>\&gt;</c> 这类转义。</summary>
    private static string Unescape(string text)
    {
        if (text.IndexOf('\\') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                builder.Append(text[i + 1]);
                i++;
                continue;
            }

            builder.Append(text[i]);
        }

        return builder.ToString();
    }

    /// <summary>转义后被当成普通字符的标记字符。</summary>
    private static bool IsEscapable(char c) => c is '*' or '`' or '\\' or '#' or '-' or '>' or '+';

    private static bool IsSeparator(string trimmed)
    {
        if (trimmed.Length < 3)
        {
            return false;
        }

        char first = trimmed[0];
        if (first is not ('-' or '*' or '_'))
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (c != first && !char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = string.Empty;

        int index = 0;
        while (index < trimmed.Length && trimmed[index] == '#')
        {
            index++;
        }

        if (index is 0 or > 6 || index >= trimmed.Length || trimmed[index] != ' ')
        {
            return false;
        }

        level = index;
        text = trimmed[(index + 1)..].Trim();
        return text.Length > 0;
    }

    private static bool TryReadListItem(string trimmed, out string marker, out string text)
    {
        marker = string.Empty;
        text = string.Empty;

        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
        {
            marker = "•";
            text = trimmed[2..].Trim();
            return text.Length > 0;
        }

        int digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits > 0
            && digits + 1 < trimmed.Length
            && trimmed[digits] is '.' or ')'
            && trimmed[digits + 1] == ' ')
        {
            marker = trimmed[..(digits + 1)];
            text = trimmed[(digits + 2)..].Trim();
            return text.Length > 0;
        }

        return false;
    }

    /// <summary>按 <c>**粗体**</c> / <c>*斜体*</c> / <c>`代码`</c> / <c>\</c> 转义切分行内片段。</summary>
    public static IReadOnlyList<MarkdownSpan> ParseInline(string text)
    {
        var spans = new List<MarkdownSpan>();
        var buffer = new StringBuilder(text.Length);
        bool bold = false;
        bool italic = false;
        bool code = false;

        void Flush()
        {
            if (buffer.Length > 0)
            {
                spans.Add(new MarkdownSpan(buffer.ToString(), bold, italic, code));
                buffer.Clear();
            }
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i++;
                continue;
            }

            if (c == '\n')
            {
                Flush();
                spans.Add(new MarkdownSpan("\n", false, false, false));
                continue;
            }

            if (c == '`')
            {
                Flush();
                code = !code;
                continue;
            }

            if (c == '*')
            {
                if (i + 1 < text.Length && text[i + 1] == '*')
                {
                    Flush();
                    bold = !bold;
                    i++;
                    continue;
                }

                // 单个星号只在“开标记后紧跟非空白 / 闭标记前紧跟非空白”时当斜体，
                // 避免中文里的乘号、注释符（如 a * b）被误判。
                bool opensOrCloses = italic
                    ? i > 0 && !char.IsWhiteSpace(text[i - 1])
                    : i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]);
                if (!opensOrCloses)
                {
                    buffer.Append(c);
                    continue;
                }

                Flush();
                italic = !italic;
                continue;
            }

            buffer.Append(c);
        }

        Flush();
        return spans;
    }

    /// <summary>去掉行内标记只留文字。</summary>
    public static string StripInline(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (MarkdownSpan span in ParseInline(text))
        {
            builder.Append(span.Text);
        }

        return builder.ToString().Trim();
    }
}

/// <summary>一段行内文本及其样式；<see cref="Text"/> 为 <c>\n</c> 时表示硬换行。</summary>
internal readonly record struct MarkdownSpan(string Text, bool Bold, bool Italic, bool Code);
