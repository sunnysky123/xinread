using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace xinread.WeRead;

/// <summary>
/// 网页阅读器请求体签名。与 weread-omni 的 <c>readerChecksum</c> 等价，
/// 位运算语义按 JavaScript 的 32 位有符号行为逐条对齐。
/// </summary>
public static class ReaderChecksum
{
    public static string Compute(IReadOnlyDictionary<string, object?> payload)
    {
        var keys = new List<string>(payload.Keys);
        keys.Sort(StringComparer.Ordinal);

        var text = new StringBuilder();
        foreach (string key in keys)
        {
            if (text.Length > 0)
            {
                text.Append('&');
            }

            text.Append(key).Append('=').Append(EncodeUriComponent(FormatValue(payload[key])));
        }

        string source = text.ToString();
        int left = 0x15051505;
        int right = left;
        for (int index = source.Length - 1; index > 0; index -= 2)
        {
            left = 0x7fffffff & (left ^ (source[index] << ((source.Length - index) % 30)));
            right = 0x7fffffff & (right ^ (source[index - 1] << (index % 30)));
        }

        return ((long)left + right).ToString("x", CultureInfo.InvariantCulture);
    }

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>等价于 JavaScript <c>encodeURIComponent</c>。</summary>
    private static string EncodeUriComponent(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            bool safe = b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')');
            if (safe)
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
