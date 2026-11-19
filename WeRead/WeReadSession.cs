using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace xinread.WeRead;

/// <summary>
/// 把凭证存储、令牌刷新和两个接口客户端组合成一个可复用的会话对象。
/// 界面层只依赖这里，不直接处理令牌与 HTTP。
/// </summary>
public sealed class WeReadSession : IDisposable
{
    private readonly CredentialStore _store;
    private TokenManager? _tokens;

    public WeReadSession(CredentialStore? store = null)
    {
        _store = store ?? new CredentialStore();
    }

    public WeReadCredentials? Credentials { get; private set; }

    public bool IsSignedIn => Credentials is { IsComplete: true } && _tokens is not null;

    public MobileClient Mobile { get; private set; } = null!;

    public WebReaderClient Reader { get; private set; } = null!;

    /// <summary>从本地凭证恢复会话；没有凭证时返回 false。</summary>
    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        WeReadCredentials? credentials = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (credentials is null)
        {
            return false;
        }

        Attach(credentials);
        return true;
    }

    /// <summary>保存刚拿到的凭证并立即启用会话。</summary>
    public async Task SignInAsync(WeReadCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        await _store.SaveAsync(credentials, cancellationToken).ConfigureAwait(false);
        Attach(credentials);
    }

    public async Task SignOutAsync()
    {
        DisposeClients();
        Credentials = null;

        try
        {
            _store.Clear();
        }
        catch (IOException)
        {
            // 文件被占用时删不掉：下次启动读到凭证仍能登录，不算致命错误。
        }

        await Task.CompletedTask;
    }

    private void Attach(WeReadCredentials credentials)
    {
        // 切换账号会重建两个客户端；它们各自持有 HttpClient，不释放会一直占着连接池。
        DisposeClients();

        Credentials = credentials;

        _tokens = new TokenManager(
            credentials,
            http: null,
            onCredentialsChanged: rotated => _store.SaveAsync(rotated));

        Mobile = new MobileClient(_tokens);
        Reader = new WebReaderClient(_tokens);
    }

    private void DisposeClients()
    {
        Mobile?.Dispose();
        Reader?.Dispose();
        _tokens?.Dispose();

        Mobile = null!;
        Reader = null!;
        _tokens = null;
    }

    public void Dispose() => DisposeClients();
}
