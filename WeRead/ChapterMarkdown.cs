using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace xinread.WeRead;

/// <summary>
/// 把服务端正文转成 Markdown 中间格式。EPUB 分片解码后是 XHTML，用标签扫描保留
/// 标题 / 引用 / 列表 / 代码块 / 图片等块级语义；<c>format=txt</c> 的书直接按行拆段。
/// 只输出段落级 Markdown 子集，交给 <c>MarkdownRenderer</c> 渲染成原生控件。
/// </summary>
public static class ChapterMarkdown
{
    /// <summary>
    /// 纯文本正文转 Markdown：每行一段。纯文本里的 <c>#</c>、<c>-</c>、<c>&gt;</c> 都是原文，
    /// 必须在行首补转义，否则会被块解析当成标题 / 分隔线 / 列表 / 引用。
    /// </summary>
    public static string FromPlainText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length + (text.Length / 8));
        foreach (string rawLine in text.Split('\n'))
        {
            // 原文用全角空格做首行缩进，缩进由渲染层统一处理。
            string line = rawLine.Trim('\r', ' ', '\t', '\u3000', '\u00A0');
            if (line.Length == 0)
            {
                continue;
            }

            AppendEscaped(builder, line, atLineStart: true);
            builder.Append('\n').Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>XHTML / HTML 正文转 Markdown。</summary>
    public static string FromHtml(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var walker = new HtmlMarkdownWalker();
        walker.Run(html);
        return walker.ToString();
    }

    /// <summary>
    /// 行内文本遇到 Markdown 标记字符时加反斜杠，避免被渲染层误判成强调或代码。
    /// <paramref name="atLineStart"/> 为 true 时连行首的 <c>#</c>、<c>-</c>、<c>&gt;</c> 一起转义，
    /// 这是因为块解析会按行首字符判断块类型。
    /// </summary>
    private static void AppendEscaped(StringBuilder builder, string text, bool atLineStart = false)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\\' or '*' or '`' or '#')
            {
                builder.Append('\\');
            }
            else if (atLineStart && i == 0 && c is '-' or '>' or '+')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }
    }

    /// <summary>块级标签：出现即代表段落边界。</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "p", "div", "section", "article", "aside", "header", "footer", "main", "nav", "figure",
        "figcaption", "dl", "dt", "dd", "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption",
    };

    /// <summary>整段内容都不可见的标签。</summary>
    private static readonly HashSet<string> SkippedTags = new(StringComparer.Ordinal)
    {
        "head", "script", "style", "title", "svg", "math", "noscript", "template",
    };

    /// <summary>没有结束标签的元素。</summary>
    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal)
    {
        "br", "img", "hr", "input", "meta", "link", "source", "wbr", "col", "area", "base", "embed", "param", "track",
    };

    private sealed class ListState(bool ordered)
    {
        public bool Ordered { get; } = ordered;

        public int Count { get; set; }
    }

    /// <summary>逐标签扫描 HTML，按块级边界把行内文本拼成 Markdown 段落。</summary>
    private sealed class HtmlMarkdownWalker
    {
        private const int MaxPreLength = 64 * 1024;

        private readonly StringBuilder _output = new();
        private readonly StringBuilder _inline = new();
        private readonly StringBuilder _pre = new();
        private readonly Stack<ListState> _lists = new();

        private int _quoteDepth;
        private int _headingLevel;
        private int _bold;
        private int _italic;
        private int _code;
        private bool _inPre;
        private string? _skipTag;
        private int _skipDepth;
        private string? _listMarker;

        public override string ToString() => _output.ToString();

        public void Run(string html)
        {
            int index = 0;
            while (index < html.Length)
            {
                int open = html.IndexOf('<', index);
                if (open < 0)
                {
                    AppendText(html[index..]);
                    break;
                }

                if (open > index)
                {
                    AppendText(html[index..open]);
                }

                if (StartsWith(html, open, "<!--"))
                {
                    int end = html.IndexOf("-->", open + 4, StringComparison.Ordinal);
                    index = end < 0 ? html.Length : end + 3;
                    continue;
                }

                if (StartsWith(html, open, "<!") || StartsWith(html, open, "<?"))
                {
                    int end = html.IndexOf('>', open);
                    index = end < 0 ? html.Length : end + 1;
                    continue;
                }

                int close = FindTagEnd(html, open);
                if (close < 0)
                {
                    AppendText(html[open..]);
                    break;
                }

                HandleTag(html, open, close);
                index = close + 1;
            }

            FlushInline();
        }

        private static bool StartsWith(string html, int index, string value)
            => index + value.Length <= html.Length
                && string.CompareOrdinal(html, index, value, 0, value.Length) == 0;

        /// <summary>找标签结束的 '&gt;'，跳过属性值里的引号内容。</summary>
        private static int FindTagEnd(string html, int start)
        {
            char quote = '\0';
            for (int i = start + 1; i < html.Length; i++)
            {
                char c = html[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c is '"' or '\'')
                {
                    quote = c;
                    continue;
                }

                if (c == '>')
                {
                    return i;
                }
            }

            return -1;
        }

        private void HandleTag(string html, int open, int close)
        {
            int cursor = open + 1;
            bool closing = cursor < close && html[cursor] == '/';
            if (closing)
            {
                cursor++;
            }

            int nameStart = cursor;
            while (cursor < close && (char.IsAsciiLetterOrDigit(html[cursor]) || html[cursor] is ':' or '-' or '_'))
            {
                cursor++;
            }

            if (cursor == nameStart)
            {
                return;
            }

            string name = html[nameStart..cursor].ToLowerInvariant();
            bool selfClosing = close > open + 1 && html[close - 1] == '/';

            if (_skipTag is not null)
            {
                if (!string.Equals(name, _skipTag, StringComparison.Ordinal))
                {
                    return;
                }

                if (closing)
                {
                    if (--_skipDepth <= 0)
                    {
                        _skipTag = null;
                    }
                }
                else if (!selfClosing)
                {
                    _skipDepth++;
                }

                return;
            }

            if (!closing && SkippedTags.Contains(name))
            {
                if (!selfClosing)
                {
                    _skipTag = name;
                    _skipDepth = 1;
                }

                return;
            }

            if (_inPre)
            {
                if (name == "br" && !closing)
                {
                    _pre.Append('\n');
                }
                else if (name == "pre" && closing)
                {
                    EmitCodeBlock();
                }

                return;
            }

            if (VoidTags.Contains(name))
            {
                switch (name)
                {
                    case "br":
                        // 块解析按行切分，正文里的 <br> 若保留换行会把一段拆成多块，
                        // 因此退化为一个空白；<pre> 里的换行另行保留。
                        _inline.Append(' ');
                        break;
                    case "hr":
                        FlushInline();
                        EmitSeparator();
                        break;
                    case "img":
                        EmitImage(html, cursor, close);
                        break;
                    default:
                        // input / meta 之类没有可读文字，直接忽略。
                        break;
                }

                return;
            }

            if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                FlushInline();
                _headingLevel = closing ? 0 : name[1] - '0';
                return;
            }

            if (BlockTags.Contains(name))
            {
                FlushInline();
                return;
            }

            switch (name)
            {
                case "strong":
                case "b":
                    if (closing)
                    {
                        if (_bold > 0)
                        {
                            _bold--;
                            _inline.Append("**");
                        }
                    }
                    else
                    {
                        _bold++;
                        _inline.Append("**");
                    }

                    break;

                case "em":
                case "i":
                    if (closing)
                    {
                        if (_italic > 0)
                        {
                            _italic--;
                            _inline.Append('*');
                        }
                    }
                    else
                    {
                        _italic++;
                        _inline.Append('*');
                    }

                    break;

                case "code":
                    if (closing)
                    {
                        if (_code > 0)
                        {
                            _code--;
                            _inline.Append('`');
                        }
                    }
                    else
                    {
                        _code++;
                        _inline.Append('`');
                    }

                    break;

                case "blockquote":
                    FlushInline();
                    if (closing)
                    {
                        _quoteDepth = Math.Max(0, _quoteDepth - 1);
                        if (_quoteDepth == 0)
                        {
                            // 引用块之后必须隔一个空行，否则紧跟的 - / # 会被当成引用的延续。
                            EnsureBlankLine();
                        }
                    }
                    else
                    {
                        _quoteDepth++;
                    }

                    break;

                case "ul":
                case "ol":
                    FlushInline();
                    if (closing)
                    {
                        if (_lists.Count > 0)
                        {
                            _lists.Pop();
                        }
                    }
                    else
                    {
                        _lists.Push(new ListState(name == "ol"));
                    }

                    break;

                case "li":
                    FlushInline();
                    if (!closing)
                    {
                        ListState? list = _lists.Count > 0 ? _lists.Peek() : null;
                        if (list is { Ordered: true })
                        {
                            list.Count++;
                            _listMarker = list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". ";
                        }
                        else
                        {
                            _listMarker = "- ";
                        }
                    }
                    else
                    {
                        // 空的 <li> 不会走到 FlushInline 消费标记，留到下一段就成了假列表项。
                        _listMarker = null;
                    }

                    break;

                case "pre":
                    FlushInline();
                    if (closing)
                    {
                        EmitCodeBlock();
                    }
                    else
                    {
                        _inPre = true;
                        _pre.Clear();
                    }

                    break;

                default:
                    break;
            }
        }

        private void AppendText(string raw)
        {
            if (_skipTag is not null || raw.Length == 0)
            {
                return;
            }

            string decoded = WebUtility.HtmlDecode(raw);
            if (_inPre)
            {
                if (_pre.Length < MaxPreLength)
                {
                    _pre.Append(decoded);
                }

                return;
            }

            AppendEscaped(_inline, decoded);
        }

        private void FlushInline()
        {
            string text = Collapse(_inline.ToString());
            _inline.Clear();
            _bold = 0;
            _italic = 0;
            _code = 0;

            if (text.Length == 0)
            {
                return;
            }

            // 渲染器没有嵌套引用的样式，多层引用压成单层：否则内层的 > 会当成正文显示出来。
            if (_quoteDepth > 0)
            {
                _output.Append("> ");
            }

            if (_quoteDepth == 0)
            {
                if (_headingLevel > 0)
                {
                    _output.Append('#', _headingLevel).Append(' ');
                }
                else if (_listMarker is not null)
                {
                    _output.Append(_listMarker);
                }
            }

            // 引用块内的标题/列表没有对应渲染样式，不输出 # 和 - 以免显示成字面量；
            // 但标记本身要消费掉，否则会泄漏到引用块之后的段落上。
            _listMarker = null;

            _output.Append(text);
            if (_quoteDepth > 0)
            {
                // 引用内的相邻段落只隔一个换行，让块解析把它们合并成同一个引用块。
                _output.Append('\n');
            }
            else
            {
                _output.Append('\n').Append('\n');
            }

            _headingLevel = 0;
        }

        private void EmitCodeBlock()
        {
            _inPre = false;
            string code = _pre.ToString().Replace("\r\n", "\n").Trim('\n');
            _pre.Clear();
            if (code.Trim().Length == 0)
            {
                return;
            }

            _output.Append("```\n").Append(code).Append("\n```\n\n");
        }

        private void EmitSeparator() => _output.Append("---\n\n");

        /// <summary>
        /// 把 <c>&lt;img&gt;</c> 转成独占一行的 Markdown 图片。缺 <c>src</c> 的（占位图、埋点图）
        /// 直接跳过；块级图片会先结束当前段落，避免和正文挤在同一段里。
        /// </summary>
        private void EmitImage(string html, int attributesStart, int tagEnd)
        {
            string? src = ReadAttribute(html, attributesStart, tagEnd, "src");
            if (string.IsNullOrWhiteSpace(src))
            {
                src = ReadAttribute(html, attributesStart, tagEnd, "data-src");
            }

            if (string.IsNullOrWhiteSpace(src))
            {
                return;
            }

            // 部分书用 data: 内联小图做分隔装饰，没有阅读价值，且会让正文里塞进巨型 Base64。
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string alt = ReadAttribute(html, attributesStart, tagEnd, "alt") ?? string.Empty;

            FlushInline();
            // <h1><img/></h1> 这类封面容器不能把标题标记泄漏到后面的段落上。
            _headingLevel = 0;
            _listMarker = null;

            _output.Append("![").Append(EscapeInline(alt)).Append("](").Append(EscapeInline(src)).Append(")\n\n");
        }

        /// <summary>从标签的属性区里读取指定属性的原文值，支持单双引号与无引号写法。</summary>
        private static string? ReadAttribute(string html, int start, int end, string name)
        {
            int index = start;
            while (index < end)
            {
                while (index < end && char.IsWhiteSpace(html[index]))
                {
                    index++;
                }

                int nameStart = index;
                while (index < end && !char.IsWhiteSpace(html[index]) && html[index] != '=')
                {
                    index++;
                }

                if (index == nameStart)
                {
                    break;
                }

                string attribute = html[nameStart..index];
                while (index < end && char.IsWhiteSpace(html[index]))
                {
                    index++;
                }

                if (index >= end || html[index] != '=')
                {
                    continue;
                }

                index++;
                while (index < end && char.IsWhiteSpace(html[index]))
                {
                    index++;
                }

                string value;
                if (index < end && html[index] is '"' or '\'')
                {
                    char quote = html[index++];
                    int valueStart = index;
                    while (index < end && html[index] != quote)
                    {
                        index++;
                    }

                    value = html[valueStart..Math.Min(index, end)];
                    index++;
                }
                else
                {
                    int valueStart = index;
                    while (index < end && !char.IsWhiteSpace(html[index]))
                    {
                        index++;
                    }

                    value = html[valueStart..index];
                }

                if (string.Equals(attribute, name, StringComparison.OrdinalIgnoreCase))
                {
                    return WebUtility.HtmlDecode(value).Trim();
                }
            }

            return null;
        }

        /// <summary>转义行内标记字符，避免图片说明或地址里的 * / ` 被当成强调或代码。</summary>
        private static string EscapeInline(string text)
        {
            var builder = new StringBuilder(text.Length + 8);
            AppendEscaped(builder, text);
            return builder.ToString();
        }

        /// <summary>补齐块之间的空行，避免上一个块的收尾换行不足时把下一块粘上来。</summary>
        private void EnsureBlankLine()
        {
            if (_output.Length == 0)
            {
                return;
            }

            int trailing = 0;
            for (int i = _output.Length - 1; i >= 0 && _output[i] == '\n'; i--)
            {
                trailing++;
            }

            // 输出块统一以 "\n\n" 收尾，只有引用块是单个 "\n"。
            for (int i = trailing; i < 2; i++)
            {
                _output.Append('\n');
            }
        }

        /// <summary>把 HTML 里的换行和多余空白压成单个空格，<c>&lt;br&gt;</c> 留下的 '\n' 保留。</summary>
        private static string Collapse(string text)
        {
            var builder = new StringBuilder(text.Length);
            bool pendingSpace = false;
            foreach (char c in text)
            {
                if (c == '\n')
                {
                    pendingSpace = false;
                    builder.Append('\n');
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(c);
            }

            return builder.ToString().Trim();
        }
    }
}
