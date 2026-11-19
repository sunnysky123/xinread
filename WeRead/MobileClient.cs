using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace xinread.WeRead;

public sealed class ShelfBook
{
    public string BookId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    public string Cover { get; init; } = string.Empty;

    public long ReadUpdateTime { get; init; }

    public bool Finished { get; init; }
}

public sealed class ChapterInfo
{
    public long ChapterUid { get; init; }

    public string Title { get; init; } = string.Empty;
}

public sealed class BookInfo
{
    public string BookId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    public string Format { get; init; } = string.Empty;
}

/// <summary>
/// 移动端接口（i.weread.qq.com）的 C# 移植。对应 SDK 的 <c>MobileClient</c>：
/// 统一挂载 E-Ink 版本头与 vid/accessToken，遇到会话过期（-2012 / HTTP 401）刷新令牌后重放一次。
/// </summary>
public sealed class MobileClient : IDisposable
{
    private const string BaseUrl = "https://i.weread.qq.com";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IAccessTokenProvider _tokens;

    public MobileClient(IAccessTokenProvider tokens, HttpClient? http = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = http is null;
    }

    public async Task<IReadOnlyList<ShelfBook>> GetShelfAsync(CancellationToken cancellationToken = default)
    {
        JsonObject body = await CallAsync(HttpMethod.Get, "/shelf/sync", query: null, payload: null, cancellationToken)
            .ConfigureAwait(false);

        if (body["books"] is not JsonArray books)
        {
            throw new WeReadException("书架响应缺少 books 字段。", "/shelf/sync");
        }

        var result = new List<ShelfBook>(books.Count);
        foreach (JsonNode? node in books)
        {
            if (node is not JsonObject row)
            {
                continue;
            }

            string? bookId = ReadString(row, "bookId");
            if (string.IsNullOrEmpty(bookId))
            {
                continue;
            }

            result.Add(new ShelfBook
            {
                BookId = bookId,
                Title = ReadString(row, "title") ?? "(无标题)",
                Author = ReadString(row, "author") ?? string.Empty,
                Cover = ReadString(row, "cover") ?? string.Empty,
                ReadUpdateTime = ReadLong(row, "readUpdateTime"),
                Finished = ReadLong(row, "finishReading") == 1,
            });
        }

        return result;
    }

    public async Task<BookInfo> GetBookInfoAsync(string bookId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bookId);
        JsonObject body = await CallAsync(
            HttpMethod.Get,
            "/book/info",
            new Dictionary<string, string> { ["bookId"] = bookId },
            payload: null,
            cancellationToken).ConfigureAwait(false);

        return new BookInfo
        {
            BookId = ReadString(body, "bookId") ?? bookId,
            Title = ReadString(body, "title") ?? "(无标题)",
            Author = ReadString(body, "author") ?? string.Empty,
            Format = ReadString(body, "format") ?? string.Empty,
        };
    }

    public async Task<IReadOnlyList<ChapterInfo>> GetChaptersAsync(string bookId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bookId);

        var payload = new JsonObject
        {
            ["bookIds"] = new JsonArray(bookId),
            ["synckeys"] = new JsonArray(0),
            ["updateTimes"] = new JsonArray(0),
            ["maxfreeIdx"] = new JsonArray(0),
        };

        JsonObject body = await CallAsync(HttpMethod.Post, "/book/chapterInfos", query: null, payload, cancellationToken)
            .ConfigureAwait(false);

        if (body["data"] is not JsonArray data)
        {
            throw new WeReadException("目录响应缺少 data 字段。", "/book/chapterInfos");
        }

        var chapters = new List<ChapterInfo>();
        if (data.Count > 0 && data[0] is JsonObject entry && entry["updated"] is JsonArray updated)
        {
            foreach (JsonNode? node in updated)
            {
                if (node is not JsonObject row)
                {
                    continue;
                }

                chapters.Add(new ChapterInfo
                {
                    ChapterUid = ReadLong(row, "chapterUid"),
                    Title = ReadString(row, "title") ?? "(无标题)",
                });
            }
        }

        return chapters;
    }

    /// <summary>读取上次阅读位置对应的章节与进度（0~1）；未读过时返回 (null, 0)。</summary>
    public async Task<(long? ChapterUid, double Progress)> GetProgressAsync(string bookId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bookId);
        JsonObject body = await CallAsync(
            HttpMethod.Get,
            "/book/getProgress",
            new Dictionary<string, string> { ["bookId"] = bookId },
            payload: null,
            cancellationToken).ConfigureAwait(false);

        if (body["book"] is not JsonObject book)
        {
            return (null, 0);
        }

        long chapterUid = ReadLong(book, "chapterUid");
        double progress = ReadDouble(book, "progress");
        return chapterUid > 0 ? (chapterUid, progress) : (null, 0);
    }

    private async Task<JsonObject> CallAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string>? query,
        JsonObject? payload,
        CancellationToken cancellationToken)
    {
        string url = BaseUrl + path;
        if (query is { Count: > 0 })
        {
            var builder = new StringBuilder(url);
            bool first = true;
            foreach (KeyValuePair<string, string> pair in query)
            {
                builder.Append(first ? '?' : '&')
                    .Append(Uri.EscapeDataString(pair.Key)).Append('=')
                    .Append(Uri.EscapeDataString(pair.Value));
                first = false;
            }

            url = builder.ToString();
        }

        bool replayable = method == HttpMethod.Get || method == HttpMethod.Head;
        AccessToken token = await _tokens.GetAsync(false, cancellationToken).ConfigureAwait(false);

        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("vid", token.Vid);
            request.Headers.TryAddWithoutValidation("accessToken", token.AccessTokenValue);
            TokenManager.ApplyVersionHeaders(request);

            if (payload is not null)
            {
                request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new WeReadException($"请求 {path} 失败：{ex.Message}", path, inner: ex);
            }

            using (response)
            {
                string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (attempt == 0 && response.StatusCode == HttpStatusCode.Unauthorized && replayable)
                {
                    token = await RefreshTokenAsync(token, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                JsonObject? body = TryParseObject(text);
                int? code = ReadErrorCode(body);

                if (attempt == 0 && code == -2012 && replayable)
                {
                    token = await RefreshTokenAsync(token, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (code == -2041)
                {
                    throw new WeReadException("微信读书要求人工验证（-2041），请先在官方客户端验证后重试。", path, (int)response.StatusCode, code);
                }

                if (code is -2010 or -2012 or -2013)
                {
                    throw new WeReadAuthException($"会话已失效（{code}），请重新扫码登录。", path);
                }

                if (!response.IsSuccessStatusCode || (code is not null and not 0))
                {
                    throw new WeReadException(
                        $"接口 {path} 返回 {code?.ToString(CultureInfo.InvariantCulture) ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)} "
                        + $"{ReadString(body, "errMsg") ?? ReadString(body, "errmsg")}".Trim(),
                        path,
                        (int)response.StatusCode,
                        code);
                }

                return body ?? throw new WeReadException($"接口 {path} 返回了非 JSON 响应。", path, (int)response.StatusCode);
            }
        }
    }

    private async Task<AccessToken> RefreshTokenAsync(AccessToken stale, CancellationToken cancellationToken)
    {
        AccessToken fresh = await _tokens.GetAsync(false, cancellationToken).ConfigureAwait(false);
        if (fresh.AccessTokenValue != stale.AccessTokenValue)
        {
            return fresh;
        }

        return await _tokens.GetAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject? TryParseObject(string text) => JsonDom.ParseObject(text);

    private static int? ReadErrorCode(JsonObject? body) => JsonDom.ReadErrorCode(body);

    internal static string? ReadString(JsonObject? body, string name)
    {
        if (body?[name] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out string? text))
        {
            return text;
        }

        return value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString() : null;
    }

    internal static long ReadLong(JsonObject? body, string name)
    {
        if (body?[name] is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue(out long number))
        {
            return number;
        }

        if (value.TryGetValue(out string? text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }

        return 0;
    }

    internal static double ReadDouble(JsonObject? body, string name)
    {
        if (body?[name] is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue(out double number))
        {
            return number;
        }

        if (value.TryGetValue(out string? text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        return 0;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
