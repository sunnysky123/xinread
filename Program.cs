using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace xinread;

/// <summary>
/// 应用入口。取代 XAML 自动生成的 Main（通过 DISABLE_XAML_GENERATED_MAIN），
/// 以便在创建窗口之前先做单实例重定向：用户重复点击图标时，第二次启动会把已有窗口
/// 带到前台然后退出，而不是再开一个进程。
/// </summary>
public static class Program
{
    private const string InstanceKey = "xinread.MainInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // 与包身份绑定的键：同一用户下反复启动都会命中同一个实例。
        AppInstance main = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (!main.IsCurrent)
        {
            // 当前进程是"第二个实例"：把启动请求转交给已有实例，然后退出。
            // RedirectActivationToAsync 必须在没有 WinUI 同步上下文的线程上调用。
            Redirect(main);
            return;
        }

        // 本进程就是主实例：后续再有启动请求进来时，由它负责激活窗口。
        main.Activated += OnActivated;

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);

            new App();
        });
    }

    /// <summary>
    /// 把本次激活转交给主实例。主实例收到 <see cref="AppInstance.Activated"/> 后
    /// 会把自己的窗口提到前台；这里等它处理完再退出，避免请求丢失。
    /// </summary>
    private static void Redirect(AppInstance main)
    {
        try
        {
            main.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs())
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 重定向失败时宁可让用户看到一个新窗口，也不要静默失败。
            // 走到这里通常意味着主实例已退出，下面继续正常启动即可。
            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);

                new App();
            });
        }
    }

    /// <summary>
    /// 主实例收到被重定向过来的启动请求：把已有窗口激活到前台。
    /// 该回调可能不在 UI 线程上，需要切回 DispatcherQueue。
    /// </summary>
    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread()
            ?? App.MainDispatcherQueue;

        if (queue is null)
        {
            return;
        }

        queue.TryEnqueue(() => App.ActivateMainWindow());
    }
}
