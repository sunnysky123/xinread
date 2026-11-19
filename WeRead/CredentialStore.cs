using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace xinread.WeRead;

/// <summary>本地保存的登录凭证，对应 SDK 的 credentials.json。</summary>
public sealed class WeReadCredentials
{
    [JsonPropertyName("vid")]
    public string Vid { get; set; } = string.Empty;

    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrEmpty(Vid) && !string.IsNullOrEmpty(RefreshToken) && !string.IsNullOrEmpty(DeviceId);

    public WeReadCredentials Clone() => new()
    {
        Vid = Vid,
        AccessToken = AccessToken,
        RefreshToken = RefreshToken,
        DeviceId = DeviceId,
    };
}

/// <summary>凭证序列化的源生成上下文，保证 Trim/AOT 发布下仍可工作。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WeReadCredentials))]
internal sealed partial class CredentialJsonContext : JsonSerializerContext;

/// <summary>把凭证写入本机应用数据目录，权限仅限当前用户。</summary>
public sealed class CredentialStore
{
    private readonly string _path;

    public CredentialStore(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "xinread",
        "credentials.json");

    public string FilePath => _path;

    public async Task<WeReadCredentials?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using FileStream stream = File.OpenRead(_path);
        WeReadCredentials? credentials;
        try
        {
            credentials = await JsonSerializer.DeserializeAsync(stream, CredentialJsonContext.Default.WeReadCredentials, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }

        return credentials is { IsComplete: true } ? credentials : null;
    }

    public async Task SaveAsync(WeReadCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = _path + ".tmp";
        await using (FileStream stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, credentials, CredentialJsonContext.Default.WeReadCredentials, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, _path, overwrite: true);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.SetAttributes(_path, File.GetAttributes(_path) | FileAttributes.Hidden);
            }
            catch (IOException)
            {
                // 属性设置失败不影响凭证可用性。
            }
        }
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
