using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace NeteaseGameDownloader;

/// <summary>
/// WPF 应用程序入口。
/// 使用命名互斥体（Mutex）防止同一台机器上同时开启多个程序实例；
/// 当用户再次尝试启动时，不弹提示，而是激活（置前 / 还原）已运行的实例窗口。
/// </summary>
public partial class App : Application
{
    // 跨会话唯一，避免不同用户/会话重复启动
    private const string MutexName = @"Global\NeteaseGameDownloader_QwenGo_v3_0";
    private const string ActivateEventName = @"Global\NeteaseGameDownloader_QwenGo_v3_0_Activate";

    private static Mutex? _appMutex;
    private static EventWaitHandle? _activateEvent;
    private static Thread? _activateThread;
    private static MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 兜底：未观察的任务异常（如关闭窗口时被取消的下载任务）不再弹崩溃对话框
        TaskScheduler.UnobservedTaskException += (_, args) =>
            args.SetObserved();

        // 尝试以初始所有者身份创建互斥体；若已存在，说明已有实例在运行
        _appMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 通知已运行实例把窗口置前，而不是弹窗提示
            try
            {
                using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
                ev.Set();
            }
            catch { /* 命名对象可能因权限等原因打不开，忽略即可 */ }
            Shutdown();
            return;
        }

        // 创建"激活"事件并启动监听线程
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateThread = new Thread(WaitForActivate)
        {
            IsBackground = true,
            Name = "activate-waiter",
        };
        _activateThread.Start();

        base.OnStartup(e);
        _mainWindow = new MainWindow();
        _mainWindow.Show();
    }

    /// <summary>
    /// 后台线程：等待第二个实例发来的激活信号，把主窗口置前。
    /// </summary>
    private static void WaitForActivate()
    {
        while (_activateEvent is not null)
        {
            bool signaled;
            try
            {
                signaled = _activateEvent.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                break;
            }
            if (signaled)
            {
                try
                {
                    // 程序正在关闭时 Dispatcher 可能已停摆，Invoke 会抛
                    // TaskCanceledException / InvalidOperationException，需吞掉以免崩溃
                    if (_mainWindow is not null && !_mainWindow.Dispatcher.HasShutdownStarted
                        && !_mainWindow.Dispatcher.HasShutdownFinished)
                    {
                        _mainWindow.Dispatcher.Invoke(() => _mainWindow.ActivateExisting());
                    }
                }
                catch
                {
                    /* 忽略异常 */
                }
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateEvent?.Set();         // 唤醒监听线程以便退出
        _activateEvent?.Dispose();
        _activateEvent = null;
        _appMutex?.ReleaseMutex();
        _appMutex?.Dispose();
        _appMutex = null;
        base.OnExit(e);
    }
}
