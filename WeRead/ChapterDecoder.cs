using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace xinread.WeRead;

/// <summary>
/// 还原网页正文片段。与 weread-omni 的 <c>decodeChapterParts</c> 等价：
/// 每段前缀是后 32 字节的 MD5 校验，拼接后首字符是段类型标记，随后的字符按
/// 由尾部字符推导出的位置表两两交换，最终是 Base64 编码的 UTF-8 XHTML。
/// </summary>
public static class ChapterDecoder
{
    private const int MaxHtmlBytes = 8 * 1024 * 1024;

    public static string Decode(IReadOnlyList<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            throw new ChapterFormatException("章节正文为空。");
        }

        var builder = new StringBuilder(parts.Sum(static p => p.Length));
        foreach (string part in parts)
        {
            if (part.Length <= 32 || !string.Equals(part[..32], Md5Upper(part[32..]), StringComparison.Ordinal))
            {
                throw new ChapterFormatException("章节校验和不匹配。");
            }

            builder.Append(part, 32, part.Length - 32);
        }

        string checkedText = builder.ToString();
        if (checkedText.Length < 2)
        {
            throw new ChapterFormatException("章节正文过短。");
        }

        char[] chars = checkedText[1..].ToCharArray();
        int length = chars.Length;

        int[] swaps = ComputeSwaps(chars, length);

        for (int index = swaps.Length - 1; index >= 1; index -= 2)
        {
            for (int shift = 1; shift >= 0; shift--)
            {
                int first = swaps[index] + shift;
                int second = swaps[index - 1] + shift;
                (chars[first], chars[second]) = (chars[second], chars[first]);
            }
        }

        string encoded = new(chars);
        if (encoded.Length % 4 != 0 || !IsBase64(encoded))
        {
            throw new ChapterFormatException("章节编码不合法。");
        }

        byte[] bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > MaxHtmlBytes)
        {
            throw new ChapterFormatException("章节正文超出 8MiB 上限。");
        }

        string html;
        try
        {
            html = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ChapterFormatException("章节文本不是合法的 UTF-8。", inner: ex);
        }

        if (!ContainsBodyTag(html))
        {
            throw new ChapterFormatException("章节正文缺少 body 元素。");
        }

        return html;
    }

    private static int[] ComputeSwaps(char[] chars, int length)
    {
        if (length >= 4 && length < 11)
        {
            return [0, 2];
        }

        if (length < 11)
        {
            return [];
        }

        int count = Math.Min(4, (int)Math.Ceiling(length / 10.0));

        var seed = new StringBuilder(count * 16);
        for (int index = length - 1; index > length - 1 - count; index--)
        {
            // 与 SDK 一致：先把字符码取二进制字符串，再按 4 进制解析，最后拼成十进制文本。
            string binary = ((int)chars[index]).ToString("b", CultureInfo.InvariantCulture);
            seed.Append(ParseBase4Binary(binary).ToString(CultureInfo.InvariantCulture));
        }

        string seedText = seed.ToString();
        int modulus = length - count - 2;
        if (modulus <= 0)
        {
            return [];
        }

        int width = modulus.ToString(CultureInfo.InvariantCulture).Length;
        var swaps = new List<int>(10);
        for (int index = 0; swaps.Count < 10 && index + width < seedText.Length; index += width)
        {
            swaps.Add(ReadDigitWindow(seedText, index, width) % modulus);
            swaps.Add(ReadDigitWindow(seedText, index + 1, width) % modulus);
        }

        return [.. swaps];
    }

    private static long ParseBase4Binary(string binary)
    {
        // 二进制字符只含 '0'/'1'，按 4 进制解析等价于把它当作基 4 位的数。
        long value = 0;
        foreach (char c in binary)
        {
            value = (value * 4) + (c - '0');
        }

        return value;
    }

    private static int ReadDigitWindow(string text, int start, int width)
    {
        long value = 0;
        int end = Math.Min(start + width, text.Length);
        for (int i = start; i < end; i++)
        {
            value = (value * 10) + (text[i] - '0');
        }

        return (int)value;
    }

    private static bool IsBase64(string value)
    {
        int padding = 0;
        for (int i = value.Length - 1; i >= 0 && value[i] == '='; i--)
        {
            padding++;
        }

        if (padding > 2)
        {
            return false;
        }

        for (int i = 0; i < value.Length - padding; i++)
        {
            char c = value[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '/')
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsBodyTag(string html)
    {
        for (int i = html.Length - 6; i >= 0; i--)
        {
            if (html[i] != '<' || !html.AsSpan(i).StartsWith("<body", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int next = i + 5;
            if (next >= html.Length || char.IsWhiteSpace(html[next]) || html[next] == '>')
            {
                return true;
            }
        }

        return false;
    }

    private static string Md5Upper(string text) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text)));
}
