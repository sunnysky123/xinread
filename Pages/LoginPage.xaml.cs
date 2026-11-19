using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using xinread.Services;
using xinread.WeRead;

namespace xinread.Pages;

/// <summary>扫码登录页：展示微信二维码并跟踪扫描状态。</summary>
public sealed partial class LoginPage : Page
{
    private QrLoginService _login = new();
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _attempt;
    private bool _disposed;

    public LoginPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelAttempt();
        _login.Dispose();
        _disposed = true;
        _lifetime.Cancel();
    }

    private void CancelAttempt()
    {
        _attempt?.Cancel();
        _attempt?.Dispose();
        _attempt = null;
    }

    private void ResetLifetime()
    {
        if (_lifetime.IsCancellationRequested)
        {
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
        }

        // 页面实例会被返回栈复用；上一轮卸载时释放的客户端要重建。
        if (_disposed)
        {
            _login = new QrLoginService();
            _disposed = false;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ResetLifetime();
        _ = StartLoginAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await StartLoginAsync();

    /// <summary>返回上一页；从访客首页点进来时就是回首页，直接从启动页进来时退回首页而非空白。</summary>
    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(ShelfPage));
        }
    }

    private async Task StartLoginAsync()
    {
        CancelAttempt();
        _attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _attempt.Token;

        RefreshButton.IsEnabled = false;
        ErrorBar.IsOpen = false;
        StatusText.Text = "正在获取二维码…";
        QrProgress.IsActive = true;
        QrImage.Source = null;

        try
        {
            string confirmUrl = await _login.RequestQrAsync(token);
            BitmapImage qr = await QrCodeRenderer.RenderAsync(confirmUrl);
            if (token.IsCancellationRequested)
            {
                return;
            }

            QrImage.Source = qr;
            QrProgress.IsActive = false;
            StatusText.Text = "请用微信扫描二维码";
            RefreshButton.IsEnabled = true;

            var progress = new Progress<QrLoginUpdate>(update =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                StatusText.Text = update.Message;
                if (update.State is QrLoginState.Expired or QrLoginState.Declined)
                {
                    QrProgress.IsActive = false;
                }
            });

            string wxCode = await _login.PollForCodeAsync(confirmUrl, progress, token);
            StatusText.Text = "正在登录…";
            WeReadCredentials credentials = await _login.ExchangeAsync(wxCode, DeviceProfile.NewDeviceId(), token);
            await App.Session.SignInAsync(credentials, token);

            // 登录成功后清掉导航栈，避免返回键回到登录页。
            Frame.Navigate(typeof(ShelfPage));
            Frame.BackStack.Clear();
        }
        catch (OperationCanceledException)
        {
            // 被新的登录尝试或页面卸载取消。
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            ShowError(ex.Message);
            QrProgress.IsActive = false;
            StatusText.Text = "登录失败";
            RefreshButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
