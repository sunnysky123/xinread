using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace xinread.WeRead;

/// <summary>官网首页上的一本书，只带点击所需的最少信息。</summary>
public sealed record HomeBook(string ReaderId, string Title, string Author, string Cover);

/// <summary>访客视角下的一本书：基础信息 + 完整目录。</summary>
public sealed record GuestBookDetail(
    string BookId,
    string Title,
    string Author,
    string Format,
    IReadOnlyList<ChapterInfo> Chapters);

/// <summary>
/// 未登录时的只读客户端。官网首页与阅读器页面本身是匿名可访问的 HTTP 页面：
/// 首页内嵌四本推荐书，阅读器页面内嵌 <c>bookInfo</c> / <c>chapterInfos</c> / <c>psvts</c>，
/// 章节正文仍走 e_0 / e_1 / e_3 分片，因此访客也能按同样的方式取前几章试看。
/// </summary>
public sealed class GuestClient : IDisposable
{
    private const string Origin = "https://weread.qq.com";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/131.0.0.0 Safari/537.36";

    private const int MaxBytes = 8 * 1024 * 1024;

    private static readonly Regex BookIdPattern = new(
        "\"reader\":\\{\"infoId\":\"[^\"]*\",\"bookId\":\"(\\d+)\"",
        RegexOptions.Compiled);

    private static readonly Regex CardPattern = new(
        "<a href=\"https://weread\\.qq\\.com/web/reader/([A-Za-z0-9]+)\"[^>]*class=\"wr_suggestion_card_wrapper\"",
        RegexOptions.Compiled);

    private static readonly Regex CardTitlePattern = new(
        "wr_suggestion_card_content_title[^>]*>([^<]*)<",
        RegexOptions.Compiled);

    private static readonly Regex CardAuthorPattern = new(
        "wr_suggestion_card_content_author[^>]*>([^<]*)<",
        RegexOptions.Compiled);

    private static readonly Regex CardCoverPattern = new("<img src=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    private IReadOnlyList<HomeBook>? _homeCache;
    private DateTimeOffset _homeCachedAt;

    public GuestClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = http is null;
    }

    /// <summary>官网首页推荐的前几本书；<paramref name="count"/> 默认 4 本。</summary>
    public async Task<IReadOnlyList<HomeBook>> GetHomeBooksAsync(int count = 4, CancellationToken cancellationToken = default)
    {
        // 首页内容变化不频繁，同一个客户端内缓存 10 分钟，来回切页不重复抓。
        if (_homeCache is { } cached && DateTimeOffset.UtcNow - _homeCachedAt < TimeSpan.FromMinutes(10))
        {
            return Trim(cached, count);
        }

        string html = await GetStringAsync("/", cancellationToken).ConfigureAwait(false);

        var books = new List<HomeBook>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match card in CardPattern.Matches(html))
        {
            string readerId = card.Groups[1].Value;
            if (!seen.Add(readerId))
            {
                continue;
            }

            int end = html.IndexOf("</a>", card.Index, StringComparison.Ordinal);
            string block = end > card.Index ? html[card.Index..end] : string.Empty;

            string title = Clean(CardTitlePattern.Match(block));
            if (title.Length == 0)
            {
                continue;
            }

            books.Add(new HomeBook(
                readerId,
                title,
                Clean(CardAuthorPattern.Match(block)),
                Clean(CardCoverPattern.Match(block))));

            if (books.Count >= Math.Max(count, 8))
            {
                break;
            }
        }

        if (books.Count > 0)
        {
            _homeCache = books;
            _homeCachedAt = DateTimeOffset.UtcNow;
        }

        return Trim(books, count);
    }

    /// <summary>读取匿名阅读器页面里的书籍信息与完整目录。</summary>
    public async Task<GuestBookDetail> GetBookAsync(string readerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(readerId);

        string html = await GetStringAsync($"/web/reader/{readerId}", cancellationToken).ConfigureAwait(false);

        Match bookIdMatch = BookIdPattern.Match(html);
        if (!bookIdMatch.Success)
        {
            throw new WeReadException("阅读器页面里没有书籍信息，这本书可能已下架。", "/web/reader");
        }

        string bookId = bookIdMatch.Groups[1].Value;

        string title = bookId;
        string author = string.Empty;
        string format = string.Empty;

        if (ExtractJson(html, "\"bookInfo\":", out string bookInfoJson))
        {
            using JsonDocument document = JsonDocument.Parse(bookInfoJson);
            JsonElement root = document.RootElement;
            title = ReadString(root, "title") ?? title;
            author = ReadString(root, "author") ?? author;
            format = ReadString(root, "format") ?? format;
        }

        var chapters = new List<ChapterInfo>();
        if (ExtractJson(html, "\"chapterInfos\":", out string chapterJson))
        {
            using JsonDocument document = JsonDocument.Parse(chapterJson);
            foreach (JsonElement row in document.RootElement.EnumerateArray())
            {
                chapters.Add(new ChapterInfo
                {
                    ChapterUid = ReadLong(row, "chapterUid"),
                    Title = ReadString(row, "title") ?? "(无标题)",
                });
            }
        }

        if (chapters.Count == 0)
        {
            throw new WeReadException("这本书没有可试看的章节。", "/web/reader");
        }

        return new GuestBookDetail(bookId, title, author, format, chapters);
    }

    /// <summary>匿名拉取并解码一章节正文。</summary>
    public async Task<ChapterContent> GetChapterAsync(
        string bookId,
        long chapterUid,
        string? format,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bookId);

        string readerId = ReaderIdEncoder.Encode(bookId);
        string page = await GetStringAsync($"/web/reader/{readerId}", cancellationToken).ConfigureAwait(false);

        string? ps = ChapterRequest.ReadPsvts(page);
        if (string.IsNullOrEmpty(ps))
        {
            throw new ChapterFormatException("阅读器页面缺少 psvts 时间戳。", "/web/reader");
        }

        string encodedChapter = ReaderIdEncoder.Encode(chapterUid);
        string pc = ReaderIdEncoder.Encode(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var parts = new List<string>(ChapterRequest.PartIndexes.Length);
        foreach (int index in ChapterRequest.PartIndexes)
        {
            Dictionary<string, object?> payload = ChapterRequest.CreatePayload(readerId, encodedChapter, ps, pc);
            string path = $"/web/book/chapter/e_{index}";
            string text = await PostJsonAsync(path, payload, cancellationToken).ConfigureAwait(false);

            if (text.StartsWith('{'))
            {
                // 付费章节未登录时服务端会回错误 JSON，而不是加密分片。
                throw new ChapterFormatException("该章节需要登录后才能阅读。", path);
            }

            parts.Add(text.StartsWith('"') ? JsonDom.Unquote(text) : text);
        }

        string html = ChapterDecoder.Decode(parts);

        // 匿名链路拿到的一定是 XHTML 分片；节选与否按是否含标签判断，与登录态保持一致。
        return new ChapterContent(
            bookId,
            chapterUid,
            ChapterMarkdown.FromHtml(html),
            IsFullText: WebReaderClient.LooksLikeMarkup(html),
            Format: format ?? string.Empty);
    }

    private async Task<string> GetStringAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin + path);
        ApplyHeaders(request);

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new WeReadException($"官网接口 {path} 返回 HTTP {(int)response.StatusCode}", path, (int)response.StatusCode);
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaxBytes)
        {
            throw new WeReadException($"官网接口 {path} 响应过大。", path, (int)response.StatusCode);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<string> PostJsonAsync(
        string path,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Origin + path)
        {
            Content = new StringContent(
                JsonDom.FromValues(payload).ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };
        ApplyHeaders(request, isPost: true);

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new WeReadException($"章节接口 {path} 返回 HTTP {(int)response.StatusCode}", path, (int)response.StatusCode);
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaxBytes)
        {
            throw new WeReadException($"章节接口 {path} 响应过大。", path, (int)response.StatusCode);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static void ApplyHeaders(HttpRequestMessage request, bool isPost = false)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/json");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("Referer", Origin + "/");

        if (isPost)
        {
            request.Headers.TryAddWithoutValidation("x-wrpa-0", "2097d7b063d6f8b5");
        }
    }

    /// <summary>
    /// 把 <c>"key":{...}</c> / <c>"key":[...]</c> 这段 JSON 原样截出来。
    /// 页面里还有大量其他结构，所以按括号配对扫描，避免正则贪婪匹配。
    /// </summary>
    private static bool ExtractJson(string html, string marker, out string json)
    {
        json = string.Empty;

        int start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        int index = start + marker.Length;
        while (index < html.Length && html[index] is ' ' or '\t' or '\r' or '\n')
        {
            index++;
        }

        if (index >= html.Length || html[index] is not ('{' or '['))
        {
            return false;
        }

        char open = html[index];
        char close = open == '{' ? '}' : ']';
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = index; i < html.Length; i++)
        {
            char c = html[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == open)
            {
                depth++;
            }
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    json = html[index..(i + 1)];
                    return true;
                }
            }
        }

        return false;
    }

    private static string Clean(Match match)
        => match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : string.Empty;

    private static IReadOnlyList<HomeBook> Trim(IReadOnlyList<HomeBook> books, int count)
    {
        if (count <= 0 || books.Count <= count)
        {
            return books;
        }

        var trimmed = new List<HomeBook>(count);
        for (int i = 0; i < count; i++)
        {
            trimmed.Add(books[i]);
        }

        return trimmed;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out long number) ? number : 0,
            JsonValueKind.String => long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0,
            _ => 0,
        };
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}