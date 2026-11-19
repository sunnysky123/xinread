using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace xinread.WeRead;

/// <summary>
/// 把书籍 / 章节 / 时间戳等标识编码成网页阅读器使用的短串。
/// 与 weread-omni 的 <c>encodeReaderId</c> 等价。
/// </summary>
public static class ReaderIdEncoder
{
    public static string Encode(object value)
    {
        string text = value switch
        {
            null => string.Empty,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        if (text.Length == 0)
        {
            throw new ArgumentException("reader id 输入不能为空。", nameof(value));
        }

        string hash = Hash(text);
        bool numeric = text.All(char.IsAsciiDigit);

        string[] parts = numeric ? Chunk(text, 9).Select(HexFromDecimal).ToArray() : [HexFromChars(text)];

        var builder = new StringBuilder(text.Length + 32);
        builder.Append(hash, 0, 3)
            .Append(numeric ? '3' : '4')
            .Append('2')
            .Append(hash, hash.Length - 2, 2);

        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('g');
            }

            builder.Append(parts[i].Length.ToString("x2", CultureInfo.InvariantCulture)).Append(parts[i]);
        }

        if (builder.Length < 20)
        {
            builder.Append(hash, 0, Math.Min(20 - builder.Length, hash.Length));
        }

        string result = builder.ToString();
        return result + Hash(result)[..3];
    }

    private static string Hash(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexStringLower(MD5.HashData(bytes));
    }

    private static string HexFromDecimal(string digits)
    {
        // 每 9 位十进制切分后按数值转十六进制；超过 long 范围时退化为逐位十进制→十六进制转换。
        if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long status))
        {
            return status.ToString("x", CultureInfo.InvariantCulture);
        }

        return HexFromChars(digits);
    }

    private static string HexFromChars(string text)
    {
        var builder = new StringBuilder(text.Length * 2);
        foreach (char c in text)
        {
            builder.Append(((int)c).ToString("x", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string[] Chunk(string text, int size)
    {
        int count = (text.Length + size - 1) / size;
        var parts = new string[count];
        for (int i = 0; i < count; i++)
        {
            parts[i] = text.Substring(i * size, Math.Min(size, text.Length - (i * size)));
        }

        return parts;
    }
}
