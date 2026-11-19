using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace xinread.WeRead;

/// <summary>
/// JSON 解析入口。服务端响应里存在重复键（例如 <c>centPrice</c>），
/// <see cref="JsonNode.Parse(string)"/> 遇到重复键会抛异常，而 JavaScript 的
/// <c>JSON.parse</c> 是「后者覆盖前者」，因此这里统一用 <see cref="JsonDocument"/>
/// 解析后再转成可写 DOM，保持与 SDK 相同的语义。
/// </summary>
internal static class JsonDom
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowDuplicateProperties = true,
        MaxDepth = 128,
    };

    /// <summary>解析 JSON 对象；不是对象或格式非法时返回 null。</summary>
    public static JsonObject? ParseObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text, Options);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? (JsonObject)ToNode(document.RootElement)!
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把带引号的 JSON 字符串字面量还原成文本；不是字符串或格式非法时返回空串。</summary>
    public static string Unquote(string raw)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>把字典原样转成 JSON 对象，供请求体使用。</summary>
    public static JsonObject FromValues(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var body = new JsonObject();
        foreach (KeyValuePair<string, object?> pair in values)
        {
            body[pair.Key] = pair.Value switch
            {
                null => null,
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(pair.Value.ToString()),
            };
        }

        return body;
    }

    /// <summary>
    /// 读取业务错误码：服务端在不同接口上分别用 <c>errCode</c> / <c>errcode</c> / <c>code</c>，
    /// 只有网页阅读接口会带泛用的 <c>code</c>，移动端接口不认它，因此按需开启。
    /// </summary>
    public static int? ReadErrorCode(JsonObject? body, bool includeGenericCode = false)
    {
        if (body is null)
        {
            return null;
        }

        string? raw = MobileClient.ReadString(body, "errCode") ?? MobileClient.ReadString(body, "errcode");
        raw ??= includeGenericCode ? MobileClient.ReadString(body, "code") : null;

        return raw is not null
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : null;
    }

    private static JsonNode? ToNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    // 重复键取最后一个，与 JSON.parse 一致。
                    obj[property.Name] = ToNode(property.Value);
                }

                return obj;

            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    array.Add(ToNode(item));
                }

                return array;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;

            default:
                return JsonNode.Parse(element.GetRawText());
        }
    }
}
