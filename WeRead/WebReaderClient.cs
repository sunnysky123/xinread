using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace xinread.WeRead;

/// <summary>一章节的解析结果。正文已经转成 Markdown，由界面层解析成原生控件。</summary>
public sealed record ChapterContent(
    string BookId,
    long ChapterUid,
    string Markdown,
    bool IsFullText,
    string Format);

/// <summary>
/// 网页阅读接口（weread.qq.com）的 C# 移植。对应 SDK 的 <c>WebReader</c>：
/// 先用 vid/accessToken 建立网页会话，取到 psvts 后向 e_0 / e_1 / e_3 请求加密片段，
/// 再由 <see cref="ChapterDecoder"/> 还原成 XHTML。
/// </summary>
public sealed class WebReaderClient
{
    private const string Origin = "https://weread.qq.com";
    private const int MaxBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IAccessTokenProvider _tokens;

    public WebReaderClient(IAccessTokenProvider tokens, HttpClient? http = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        _ownsHttp = http is null;
    }

    /// <summary>拉取并解码一章节正文。</summary>
    public async Task<ChapterContent> GetChapterAsync(
        string bookId,
        long chapterUid,
        string? format = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bookId);

        AccessToken token = await _tokens.GetAsync(false, cancellationToken).ConfigureAwait(false);
        string cookie = await InitSessionAsync(token, cancellationToken).ConfigureAwait(false);

        string encodedBook = ReaderIdEncoder.Encode(bookId);
        string encodedChapter = ReaderIdEncoder.Encode(chapterUid);

        string readerPage = await SendAsync(
            HttpMethod.Get,
            $"/web/reader/{encodedBook}k{encodedChapter}",
            payload: null,
            cookie,
            cancellationToken).ConfigureAwait(false);

        string? ps = ChapterRequest.ReadPsvts(readerPage);
        if (string.IsNullOrEmpty(ps))
        {
            throw new ChapterFormatException("阅读器页面缺少 psvts 时间戳。", "/web/reader");
        }

        string pc = ReaderIdEncoder.Encode(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var parts = new List<string>(ChapterRequest.PartIndexes.Length);
        foreach (int index in ChapterRequest.PartIndexes)
        {
            Dictionary<string, object?> payload = ChapterRequest.CreatePayload(encodedBook, encodedChapter, ps, pc);
            string path = $"/web/book/chapter/e_{index}";
            string part = await SendAsync(HttpMethod.Post, path, payload, cookie, cancellationToken)
                .ConfigureAwait(false);

            if (part.StartsWith('{'))
            {
                // format=txt 的书不走加密分片，服务端直接回明文 JSON；
                // 只有拿不到 content 时才是真正的错误响应。
                if (ChapterRequest.TryReadPlainText(part, out string plainText, out bool plainIsExcerpt))
                {
                    // 明文正文已经完整，无需再请求 e_1 / e_3。
                    return new ChapterContent(
                        bookId,
                        chapterUid,
                        ChapterMarkdown.FromPlainText(plainText),
                        IsFullText: !plainIsExcerpt,
                        Format: "txt");
                }

                throw ChapterRequest.ToChapterError(part, path);
            }

            parts.Add(part.StartsWith('"') ? JsonDom.Unquote(part) : part);
        }

        string html = ChapterDecoder.Decode(parts);

        // 加密分片只有 EPUB 书会走，format 为空时不能原样返回空串，否则后续按格式分支会走错。
        return new ChapterContent(
            bookId,
            chapterUid,
            ChapterMarkdown.FromHtml(html),
            LooksLikeMarkup(html),
            string.IsNullOrEmpty(format) ? "epub" : format);
    }

    private async Task<string> InitSessionAsync(AccessToken token, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["vid"] = token.Vid,
            ["skey"] = token.AccessTokenValue,
            ["rt"] = token.RefreshToken,
            ["pf"] = 2,
            ["ql"] = false,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Origin + "/web/login/session/init")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyWebHeaders(request, cookie: null, isPost: true);

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new WeReadAuthException("网页会话初始化被拒绝。", "/web/login/session/init");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new WeReadException($"网页会话初始化失败：HTTP {(int)response.StatusCode}", "/web/login/session/init", (int)response.StatusCode);
        }

        JsonObject? body = ParseObject(text);
        int? code = ReadCode(body);
        if (code is -2010 or -2012 or -2013)
        {
            throw new WeReadAuthException($"网页会话初始化时令牌失效（{code}）。", "/web/login/session/init");
        }

        if (code is not null and not 0)
        {
            throw new WeReadException($"网页会话初始化返回 {code}。", "/web/login/session/init", (int)response.StatusCode, code);
        }

        long success = MobileClient.ReadLong(body, "success");
        if (success != 1)
        {
            throw new WeReadAuthException("网页会话初始化未返回 success=1。", "/web/login/session/init");
        }

        string cookie = BuildCookie(response);
        if (!cookie.Contains("wr_skey=", StringComparison.Ordinal))
        {
            throw new WeReadAuthException("网页会话 cookie 缺少 wr_skey。", "/web/login/session/init");
        }

        return cookie;
    }

    private async Task<string> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, object?>? payload,
        string cookie,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, Origin + path);

        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonDom.FromValues(payload).ToJsonString(),
                Encoding.UTF8,
                "application/json");
        }

        ApplyWebHeaders(request, cookie, method == HttpMethod.Post);

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new WeReadAuthException($"网页接口 {path} 拒绝认证。", path);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new WeReadException($"网页接口 {path} 失败：HTTP {(int)response.StatusCode}", path, (int)response.StatusCode);
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaxBytes)
        {
            throw new WeReadException($"网页接口 {path} 响应超过 8MiB。", path, (int)response.StatusCode);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static void ApplyWebHeaders(HttpRequestMessage request, string? cookie, bool isPost)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("Referer", Origin + "/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        if (isPost)
        {
            request.Headers.TryAddWithoutValidation("x-wrpa-0", "2097d7b063d6f8b5");
        }
    }

    private static string BuildCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? raw) || raw is null)
        {
            return string.Empty;
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal) { "wr_vid", "wr_skey", "wr_rt", "wr_pf", "wr_ql" };
        var pairs = new List<string>();
        foreach (string entry in raw)
        {
            string pair = entry.Split(';', 2)[0].Trim();
            int separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (allowed.Contains(pair[..separator]))
            {
                pairs.Add(pair);
            }
        }

        return string.Join("; ", pairs);
    }

    private static JsonObject? ParseObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{'))
        {
            return null;
        }

        return JsonDom.ParseObject(text);
    }

    private static int? ReadCode(JsonObject? body) => JsonDom.ReadErrorCode(body, includeGenericCode: true);

    /// <summary>完整正文是 XHTML；付费书的试读节选是没有任何标签的纯文本。</summary>
    public static bool LooksLikeMarkup(string html)
    {
        for (int i = 0; i < html.Length; i++)
        {
            if (html[i] != '<')
            {
                continue;
            }

            int end = html.IndexOf('>', i + 1);
            if (end > i + 1)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
