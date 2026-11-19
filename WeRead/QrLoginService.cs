using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace xinread.WeRead;

public enum QrLoginState
{
    Waiting,
    Scanned,
    Confirmed,
    Expired,
    Declined,
}

public sealed record QrLoginUpdate(QrLoginState State, string Message);

/// <summary>
/// 扫码登录。对应 SDK 的 <c>qrlogin.ts</c>：
/// wxticket 取签名 → qrconnect 取 uuid → 轮询 long.open.weixin.qq.com → 用 wx_code 换微信读书凭证。
/// </summary>
public sealed class QrLoginService : IDisposable
{
    private const string WeReadBase = "https://i.weread.qq.com";
    private const string WxAppId = "wxab9b71ad2b90ff34";
    private const string WxScope = "snsapi_userinfo,snsapi_timeline,snsapi_friend";
    private const int PollTimeoutMs = 65_000;
    private const int PollDelayMs = 1_000;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public QrLoginService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
        _ownsHttp = http is null;
    }

    /// <summary>申请一次扫码会话，返回二维码应指向的确认地址。</summary>
    public async Task<string> RequestQrAsync(CancellationToken cancellationToken = default)
    {
        using var ticketRequest = new HttpRequestMessage(HttpMethod.Get, WeReadBase + "/wxticket?nonceStr=weread");
        TokenManager.ApplyVersionHeaders(ticketRequest);

        JsonObject ticket = await SendJsonAsync(ticketRequest, "WeRead QR ticket", cancellationToken).ConfigureAwait(false);
        string? signature = MobileClient.ReadString(ticket, "signature");
        string? timeStamp = MobileClient.ReadString(ticket, "timeStamp")
            ?? (ticket["timeStamp"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.ToJsonString() : null);

        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timeStamp))
        {
            throw new WeReadAuthException("获取扫码签名失败。", "/wxticket");
        }

        string query = string.Join(
            '&',
            "appid=" + WxAppId,
            "noncestr=weread",
            "timestamp=" + Uri.EscapeDataString(timeStamp),
            "scope=" + Uri.EscapeDataString(WxScope),
            "signature=" + Uri.EscapeDataString(signature));

        using var qrRequest = new HttpRequestMessage(HttpMethod.Get, "https://open.weixin.qq.com/connect/sdk/qrconnect?" + query);
        qrRequest.Headers.TryAddWithoutValidation("User-Agent", DeviceProfile.UserAgent);

        JsonObject qr = await SendJsonAsync(qrRequest, "WeChat qrconnect", cancellationToken).ConfigureAwait(false);
        long errcode = MobileClient.ReadLong(qr, "errcode");
        string? uuid = MobileClient.ReadString(qr, "uuid");
        if (errcode != 0 || string.IsNullOrEmpty(uuid))
        {
            throw new WeReadAuthException($"微信扫码初始化失败：{errcode}", "/connect/sdk/qrconnect");
        }

        return "https://open.weixin.qq.com/connect/confirm?uuid=" + Uri.EscapeDataString(uuid);
    }

    /// <summary>
    /// 轮询扫码状态直到确认（返回 wx_code）或失败。
    /// <paramref name="onUpdate"/> 用于把扫描/确认状态回传界面。
    /// </summary>
    public async Task<string> PollForCodeAsync(
        string confirmUrl,
        IProgress<QrLoginUpdate>? onUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(confirmUrl);

        int separator = confirmUrl.IndexOf('=');
        string uuid = Uri.UnescapeDataString(confirmUrl[(separator + 1)..]);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        int? last = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new WeReadAuthException("扫码登录超时，请重新获取二维码。", "/connect/l/qrconnect");
            }

            var url = new StringBuilder("https://long.open.weixin.qq.com/connect/l/qrconnect?f=json&uuid=")
                .Append(Uri.EscapeDataString(uuid));
            if (last is not null)
            {
                url.Append("&last=").Append(last.Value.ToString(CultureInfo.InvariantCulture));
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");

            JsonObject result = await SendJsonAsync(
                request,
                "WeChat QR poll",
                cancellationToken,
                TimeSpan.FromMilliseconds(Math.Min(PollTimeoutMs, remaining.TotalMilliseconds))).ConfigureAwait(false);

            int code = (int)MobileClient.ReadLong(result, "wx_errcode");
            switch (code)
            {
                case 405:
                    string? wxCode = MobileClient.ReadString(result, "wx_code");
                    if (string.IsNullOrEmpty(wxCode))
                    {
                        throw new WeReadAuthException("微信确认后未返回 wx_code。", "/connect/l/qrconnect");
                    }

                    onUpdate?.Report(new QrLoginUpdate(QrLoginState.Confirmed, "已确认，正在登录…"));
                    return wxCode;

                case 404:
                    onUpdate?.Report(new QrLoginUpdate(QrLoginState.Scanned, "已扫描，请在手机上确认"));
                    break;

                case 402:
                    onUpdate?.Report(new QrLoginUpdate(QrLoginState.Expired, "二维码已过期"));
                    throw new WeReadAuthException("二维码已过期，请重新获取。", "/connect/l/qrconnect");

                case 403:
                    onUpdate?.Report(new QrLoginUpdate(QrLoginState.Declined, "已取消登录"));
                    throw new WeReadAuthException("已在微信中取消登录。", "/connect/l/qrconnect");

                case 408:
                    TimeSpan backoff = TimeSpan.FromMilliseconds(Math.Min(PollDelayMs, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds));
                    if (backoff > TimeSpan.Zero)
                    {
                        await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                default:
                    throw new WeReadAuthException($"扫码状态未知：{code}", "/connect/l/qrconnect");
            }

            last = code;
        }
    }

    /// <summary>用 wx_code 换微信读书长期凭证。</summary>
    public async Task<WeReadCredentials> ExchangeAsync(
        string wxCode,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(wxCode);
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int random = Random.Shared.Next(1000);

        var body = new JsonObject
        {
            ["appFirstInstall"] = 1,
            ["code"] = wxCode,
            ["deviceId"] = deviceId,
            ["deviceName"] = DeviceProfile.DeviceName,
            ["installId"] = DeviceProfile.NewInstallId(),
            ["isAutoLogout"] = 0,
            ["isFromQrcode"] = 1,
            ["random"] = random,
            ["signature"] = DeviceProfile.Signature(timestamp, deviceId, random),
            ["timestamp"] = timestamp,
            ["trackId"] = string.Empty,
            ["deviceType"] = DeviceProfile.DeviceType,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, WeReadBase + "/login")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        TokenManager.ApplyVersionHeaders(request);

        JsonObject result = await SendJsonAsync(request, "WeRead QR login", cancellationToken).ConfigureAwait(false);
        string? accessToken = MobileClient.ReadString(result, "accessToken");
        string? refreshToken = MobileClient.ReadString(result, "refreshToken");

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
        {
            string code = MobileClient.ReadString(result, "errCode") ?? "unknown";
            string message = MobileClient.ReadString(result, "errMsg") ?? string.Empty;
            throw new WeReadAuthException($"扫码登录失败：{code} {message}".Trim(), "/login");
        }

        string vid = MobileClient.ReadString(result, "vid")
            ?? throw new WeReadAuthException("登录响应缺少 vid。", "/login");

        return new WeReadCredentials
        {
            Vid = vid,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            DeviceId = deviceId,
        };
    }

    private async Task<JsonObject> SendJsonAsync(
        HttpRequestMessage request,
        string label,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is not null)
        {
            timeoutSource.CancelAfter(timeout.Value);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WeReadException($"{label} 超时。", label);
        }
        catch (HttpRequestException ex)
        {
            throw new WeReadException($"{label} 请求失败：{ex.Message}", label, inner: ex);
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonDom.ParseObject(text)
                ?? throw new WeReadException($"{label} 返回了非 JSON 响应。", label, (int)response.StatusCode);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
