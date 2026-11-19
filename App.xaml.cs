using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using xinread.Pages;
using xinread.Services;
using xinread.WeRead;

namespace xinread
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// 主窗口所属的 UI 线程派发队列。单实例重定向回调可能在别的线程上触发，
        /// 需要靠它切回 UI 线程。
        /// </summary>
        public static DispatcherQueue? MainDispatcherQueue { get; private set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>登录会话，界面层通过它访问书架与正文接口。</summary>
        public static WeReadSession Session { get; } = new WeReadSession();

        /// <summary>本地阅读进度。</summary>
        public static ReadingPositionStore Positions { get; } = new ReadingPositionStore();

        /// <summary>阅读器外观设置（正文字号）。</summary>
        public static ReaderSettingsStore Settings { get; } = new ReaderSettingsStore();

        /// <summary>未登录时的只读客户端：官网首页推荐与试看章节都走它。</summary>
        public static GuestClient Guest { get; } = new GuestClient();

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            MainDispatcherQueue = DispatcherQueue.GetForCurrentThread();

            var window = new MainWindow();
            _window = window;
            window.Activate();

            _ = NavigateToStartAsync(window);
        }

        /// <summary>
        /// 把主窗口带到前台。由 <see cref="Program"/> 在收到被重定向的启动请求时调用，
        /// 因此用户重复点击图标只会看到已有窗口被激活。
        /// </summary>
        public static void ActivateMainWindow()
        {
            if (Current is not App { _window: { } window })
            {
                return;
            }

            // 窗口最小化时先还原，否则 SetForegroundWindow 不会把它显示出来。
            if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter
                && presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            window.Activate();
        }

        private static async Task NavigateToStartAsync(MainWindow window)
        {
            try
            {
                await Session.RestoreAsync();
            }
            catch (Exception)
            {
                // 凭证损坏时按未登录处理，书架页会展示官网首页推荐。
            }

            // 未登录也先进书架页：那里展示官网首页的推荐书，可试看前三章，右上角提供登录入口。
            window.NavigateTo(typeof(ShelfPage));
        }
    }
}
