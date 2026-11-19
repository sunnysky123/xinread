using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace xinread.WeRead;

/// <summary>
/// 章节正文请求里与登录态无关的公共部分：psvts 时间戳提取、分片序号与分片请求体，
/// 以及正文接口回 JSON 时的判定。登录 / 匿名两条链路共用同一套逻辑。
/// </summary>
internal static class ChapterRequest
{
    /// <summary>正文拆成 e_0 / e_1 / e_3 三段返回。</summary>
    public static readonly int[] PartIndexes = [0, 1, 3];

    private static readonly Regex PsvtsPattern = new("\"psvts\":(\"[^\"]*\"|\\d+)", RegexOptions.Compiled);

    /// <summary>从阅读器页面 HTML 里取 psvts 时间戳；取不到时返回 null。</summary>
    public static string? ReadPsvts(string page)
    {
        Match match = PsvtsPattern.Match(page);
        if (!match.Success)
        {
            return null;
        }

        string raw = match.Groups[1].Value;

        // 纯数字形式可能超出 int 范围，原样保留；带引号的形式要先还原成文本。
        return raw.StartsWith('"') ? JsonDom.Unquote(raw) : raw;
    }

    /// <summary>构造单个分片的请求体，并补上签名。</summary>
    public static Dictionary<string, object?> CreatePayload(
        string encodedBook,
        string encodedChapter,
        string ps,
        string pc)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["b"] = encodedBook,
            ["c"] = encodedChapter,
            ["r"] = (long)Math.Pow(Random.Shared.Next(10000), 2),
            ["st"] = 0,
            ["ct"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["ps"] = ps,
            ["pc"] = pc,
            ["sc"] = 0,
        };
        payload["s"] = ReaderChecksum.Compute(payload);
        return payload;
    }

    /// <summary>
    /// 判断正文接口返回的 JSON 是明文正文还是错误。明文正文一定带 <c>content</c> 字符串字段。
    /// 价格大于 0 说明该书未购买，<c>content</c> 只是试读节选。
    /// </summary>
    public static bool TryReadPlainText(string raw, out string content, out bool isExcerpt)
    {
        content = string.Empty;
        isExcerpt = false;

        JsonObject? body = JsonDom.ParseObject(raw);
        if (body is null)
        {
            return false;
        }

        if (body["content"] is not JsonValue value || !value.TryGetValue(out string? text) || text is null)
        {
            return false;
        }

        content = text;
        isExcerpt = MobileClient.ReadLong(body, "price") > 0;
        return true;
    }

    /// <summary>把错误 JSON 转成带原因与接口路径的异常。</summary>
    public static ChapterFormatException ToChapterError(string raw, string path)
    {
        JsonObject? body = JsonDom.ParseObject(raw);
        int? code = JsonDom.ReadErrorCode(body, includeGenericCode: true);
        if (code is -2010 or -2012 or -2013)
        {
            return new ChapterFormatException($"章节接口 {path} 会话已失效（{code}）。", path);
        }

        string? message = MobileClient.ReadString(body, "errMsg") ?? MobileClient.ReadString(body, "errmsg");
        if (string.IsNullOrEmpty(message))
        {
            message = code is null ? $"章节接口 {path} 不可用。" : $"章节接口 {path} 返回 {code}。";
        }

        return new ChapterFormatException(message, path);
    }
}
