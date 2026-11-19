using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace xinread.WeRead;

/// <summary>返回访问令牌的提供者；多个调用共享同一次刷新。</summary>
public interface IAccessTokenProvider
{
    Task<AccessToken> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

public sealed record AccessToken(string Vid, string AccessTokenValue, string RefreshToken);

/// <summary>
/// 用 refreshToken 换 accessToken。与 weread-omni 的 <c>TokenManager</c> 等价：
/// 并发调用只触发一次刷新，刷新后凭证（可能被服务端轮换）通过回调持久化。
/// </summary>
public sealed class TokenManager : IAccessTokenProvider, IDisposable
{
    private const string BaseUrl = "https://i.weread.qq.com";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<WeReadCredentials, Task>? _onCredentialsChanged;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WeReadCredentials _credentials;
    private AccessToken? _cached;
    private Task<AccessToken>? _pending;

    public TokenManager(WeReadCredentials credentials, HttpClient? http = null, Func<WeReadCredentials, Task>? onCredentialsChanged = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!credentials.IsComplete)
        {
            throw new ArgumentException("凭证缺少 vid / refreshToken / deviceId。", nameof(credentials));
        }

        _credentials = credentials.Clone();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = http is null;
        _onCredentialsChanged = onCredentialsChanged;

        if (!string.IsNullOrEmpty(credentials.AccessToken))
        {
            _cached = new AccessToken(credentials.Vid, credentials.AccessToken, credentials.RefreshToken);
        }
    }

    public WeReadCredentials Credentials => _credentials.Clone();

    public string Vid => _credentials.Vid;

    public async Task<AccessToken> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        Task<AccessToken>? pending;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pending is not null)
            {
                pending = _pending;
            }
            else
            {
                if (_cached is not null)
                {
                    if (!forceRefresh)
                    {
                        return _cached;
                    }

                    _cached = null;
                }

                pending = _pending = MintAsync();
            }
        }
        finally
        {
            _gate.Release();
        }

        return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccessToken> MintAsync()
    {
        try
        {
            WeReadCredentials previous = _credentials;
            AccessToken token = await MintAccessTokenAsync(previous).ConfigureAwait(false);

            _credentials = new WeReadCredentials
            {
                Vid = token.Vid,
                AccessToken = token.AccessTokenValue,
                RefreshToken = token.RefreshToken,
                DeviceId = previous.DeviceId,
            };
            _cached = token;

            bool changed = token.AccessTokenValue != previous.AccessToken || token.RefreshToken != previous.RefreshToken;
            if (changed && _onCredentialsChanged is not null)
            {
                await _onCredentialsChanged(_credentials.Clone()).ConfigureAwait(false);
            }

            return token;
        }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _pending = null;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task<AccessToken> MintAccessTokenAsync(WeReadCredentials credentials)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int random = Random.Shared.Next(1, 1001);

        var body = new JsonObject
        {
            ["deviceId"] = credentials.DeviceId,
            ["deviceName"] = DeviceProfile.DeviceName,
            ["inBackground"] = 0,
            ["kickType"] = 1,
            ["random"] = random,
            ["refCgi"] = string.Empty,
            ["refreshToken"] = credentials.RefreshToken,
            ["signature"] = DeviceProfile.Signature(timestamp, credentials.DeviceId, random),
            ["timestamp"] = timestamp,
            ["trackId"] = string.Empty,
            ["deviceType"] = DeviceProfile.DeviceType,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/login")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyVersionHeaders(request);

        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        JsonObject? result = JsonDom.ParseObject(text);

        string? accessToken = result?["accessToken"] is JsonValue at && at.TryGetValue(out string? atText) ? atText : null;
        if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(accessToken))
        {
            string code = MobileClient.ReadString(result, "errCode") ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            string message = MobileClient.ReadString(result, "errMsg") ?? string.Empty;
            throw new WeReadAuthException($"刷新访问令牌失败：{code} {message}".Trim(), "/login");
        }

        string vid = MobileClient.ReadString(result, "vid") ?? credentials.Vid;
        if (vid != credentials.Vid)
        {
            throw new WeReadAuthException("刷新到的令牌属于另一个账号。", "/login");
        }

        string refreshToken = result!["refreshToken"] is JsonValue rt && rt.TryGetValue(out string? rtText)
            && !string.IsNullOrEmpty(rtText)
                ? rtText
                : credentials.RefreshToken;

        return new AccessToken(vid, accessToken, refreshToken);
    }

    internal static void ApplyVersionHeaders(HttpRequestMessage request)
    {
        foreach (KeyValuePair<string, string> header in DeviceProfile.VersionHeaders)
        {
            if (string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", header.Value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }

    public void Dispose()
    {
        // 不释放 _gate：令牌刷新可能还在后台收尾（finally 里 Release），
        // 释放后会抛 ObjectDisposedException，而线程池上的未处理异常会直接终止进程。
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
