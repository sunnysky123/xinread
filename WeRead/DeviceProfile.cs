using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace xinread.WeRead;

/// <summary>
/// 固定的 E-Ink 客户端身份。与 weread-omni 的 <c>einkDevice</c> / <c>einkProfile</c> 等价：
/// 服务端按 UA 与版本头挑选接口返回，缺一项就会被当成未知客户端拒绝。
/// </summary>
public static class DeviceProfile
{
    public const string DeviceName = "BOOX";

    public const int DeviceType = 3;

    public const string UserAgent = "WeRead/2.1.2 WRBrand/Onyx wr_eink Dalvik/2.1.0 (Linux; U; Android 11; BOOX Build/onyx)";

    private const string AppVersion = "2.1.2.10245900";

    private const string DeviceIdPrefix = "eink334691225";

    public static IReadOnlyDictionary<string, string> VersionHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["baseapi"] = "30",
        ["appver"] = AppVersion,
        ["basever"] = AppVersion,
        ["osver"] = "11",
        ["channelId"] = "900",
        ["User-Agent"] = UserAgent,
    };

    /// <summary>生成形如 <c>eink33469122500000000000123456789</c> 的 32 位设备号。</summary>
    public static string NewDeviceId()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);

        var raw = new BigInteger(buffer, isUnsigned: true, isBigEndian: true);
        var masked = raw & new BigInteger(0x7FFFFFFFFFFFFFFFUL);

        return DeviceIdPrefix + masked.ToString(CultureInfo.InvariantCulture).PadLeft(19, '0');
    }

    public static string NewInstallId()
    {
        var builder = new StringBuilder(32);
        builder.Append("eink31");
        for (int i = 0; i < 26; i++)
        {
            builder.Append((char)('0' + RandomNumberGenerator.GetInt32(10)));
        }

        return builder.ToString();
    }

    /// <summary>登录与刷新令牌共用的签名：sha256(`${timestamp}${deviceId}${random}`)。</summary>
    public static string Signature(long timestamp, string deviceId, int random) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            timestamp.ToString(CultureInfo.InvariantCulture) + deviceId + random.ToString(CultureInfo.InvariantCulture))));
}
