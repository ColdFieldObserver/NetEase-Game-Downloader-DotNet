using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using NeteaseGameDownloader.Core;
using NeteaseGameDownloader.Models;
using Utils = NeteaseGameDownloader.Core.Utils;

namespace NeteaseGameDownloader;

/// <summary>
/// WPF 主窗口（对应 Python 的 tkinter GUI）。
/// 通过 JobStatus 快照（100ms 轮询）渲染进度 / 速度曲线 / 彩色日志 / 状态栏，
/// 并把按钮操作转译为对 Job / DownloadEngine 的控制。
/// </summary>
public partial class MainWindow : Window
{
    private readonly JobStatus _status = new();
    private Job? _activeJob;
    private Task<int>? _runTask;
    private bool _busy;
    private bool _notifiedFinish;
    private int _lastLogCount;
    private bool _confirmOpen;
    private readonly Paragraph _logParagraph = new();
    private readonly DispatcherTimer _timer = new();

    public MainWindow()
    {
        InitializeComponent();
        InitProducts();
        InitLog();
        InitTimer();
    }

    // ---------------------------------------------------------------------
    // 初始化
    // ---------------------------------------------------------------------
    private void InitProducts()
    {
        ProductCombo.ItemsSource = Products.ListProducts();
        ProductCombo.DisplayMemberPath = "DisplayName";
        ProductCombo.SelectedIndex = 0;
        UpdateDefaultPath();
        UpdateProductDesc();
    }

    private void InitLog()
    {
        LogBox.Document = new FlowDocument(_logParagraph)
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 22, 26)),
            Foreground = new SolidColorBrush(Colors.White),
            PagePadding = new Thickness(6),
            FontFamily = new FontFamily("Consolas, Microsoft YaHei, monospace"),
            FontSize = 12,
        };
    }

    private void InitTimer()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    // ---------------------------------------------------------------------
    // 表单 -> 配置
    // ---------------------------------------------------------------------
    private JobConfig BuildConfig(bool infoOnly)
    {
        var product = (ProductSpec?)ProductCombo.SelectedItem ?? Products.GetProduct("eggy-android");
        return new JobConfig
        {
            ProductKey = product.Key,
            Output = PathBox.Text,
            AutoThreads = AutoThreadsCheck.IsChecked == true,
            Threads = (int)ThreadsSlider.Value,
            Retries = 5,
            Verify = VerifyCheck.IsChecked == true,
            KeepChecksum = KeepCheck.IsChecked == true,
            Force = ForceCheck.IsChecked == true,
            InfoOnly = infoOnly,
            AssumeYes = false,
        };
    }

    private void UpdateDefaultPath()
    {
        if (_busy) return;
        if (ProductCombo.SelectedItem is ProductSpec p)
            PathBox.Text = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), p.DefaultOutput));
    }

    private void UpdateProductDesc()
    {
        if (ProductCombo.SelectedItem is ProductSpec p)
            ProductDesc.Text = $"{p.Short}　{p.Desc}";
    }

    // ---------------------------------------------------------------------
    // 控件事件
    // ---------------------------------------------------------------------
    private void ProductCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDefaultPath();
        UpdateProductDesc();
    }

    private void ThreadsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ThreadsValue?.Text = ((int)ThreadsSlider.Value).ToString();
    }

    private void AutoThreadsCheck_Changed(object sender, RoutedEventArgs e)
    {
        ThreadsSlider.IsEnabled = AutoThreadsCheck.IsChecked != true;
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "选择保存位置",
            FileName = Path.GetFileName(PathBox.Text),
        };
        var dir = Path.GetDirectoryName(PathBox.Text);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true)
            PathBox.Text = dlg.FileName;
    }

    private async void StartBtn_Click(object sender, RoutedEventArgs e) => await StartJob(false);
    private async void InfoBtn_Click(object sender, RoutedEventArgs e) => await StartJob(true);

    private void PauseBtn_Click(object sender, RoutedEventArgs e) => _activeJob?.Pause();
    private void ResumeBtn_Click(object sender, RoutedEventArgs e) => _activeJob?.Resume();
    private void CancelBtn_Click(object sender, RoutedEventArgs e) => _activeJob?.Cancel();

    private void OpenBtn_Click(object sender, RoutedEventArgs e)
    {
        var p = PathBox.Text;
        if (!string.IsNullOrWhiteSpace(p)) OpenFolder(p);
    }

    // ---------------------------------------------------------------------
    // 任务执行
    // ---------------------------------------------------------------------
    private async Task StartJob(bool infoOnly)
    {
        if (_busy) return;
        var cfg = BuildConfig(infoOnly);
        if (string.IsNullOrWhiteSpace(cfg.Output))
        {
            MessageBox.Show(this, "请先设置保存路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _status.Reset();
        _notifiedFinish = false;
        _lastLogCount = 0;
        ClearLog();
        Utils.LogInfo($"=== 新任务（{(infoOnly ? "仅查看信息" : "下载")}）===");

        var job = new Job(cfg, _status);
        _activeJob = job;
        _busy = true;

        int code;
        try
        {
            _runTask = Task.Run(() => job.RunAsync());
            code = await _runTask;
        }
        catch (OperationCanceledException)
        {
            // 关闭窗口时 Dispatcher 续体被中止会抛出该异常，属正常退出路径
            Utils.LogInfo("任务已取消。");
            code = 130;
        }
        catch (Exception ex)
        {
            Utils.LogError($"未预期异常: {ex.Message}");
            code = 1;
        }
        _busy = false;
        Utils.LogInfo($"任务结束（退出码 {code}）。");
    }

    // ---------------------------------------------------------------------
    // 轮询渲染
    // ---------------------------------------------------------------------
    private void OnTick(object? sender, EventArgs e)
    {
        var snap = _status.Snapshot();

        // 完成 / 失败通知（仅一次）
        if (snap.Finished && !_notifiedFinish)
        {
            _notifiedFinish = true;
            if (snap.Ok == true)
            {
                if (snap.IsInfo)
                    Utils.LogInfo("信息查询完成（仅查看，未下载任何文件）。");
                else
                    OnCompleted(snap);
            }
            else if (snap.Ok == false)
                Utils.LogError($"任务失败：{snap.Error}");
        }

        bool busy = _busy;
        bool paused = snap.Paused;

        // 控件可用性
        ProductCombo.IsEnabled = !busy;
        ThreadsSlider.IsEnabled = !busy && AutoThreadsCheck.IsChecked != true;
        AutoThreadsCheck.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        BrowseBtn.IsEnabled = !busy;
        VerifyCheck.IsEnabled = !busy;
        KeepCheck.IsEnabled = !busy;
        ForceCheck.IsEnabled = !busy;
        StartBtn.IsEnabled = !busy;
        InfoBtn.IsEnabled = !busy;
        PauseBtn.IsEnabled = busy && !paused;
        ResumeBtn.IsEnabled = busy && paused;
        CancelBtn.IsEnabled = busy;
        OpenBtn.IsEnabled = true;

        // 总进度
        TotalBar.Value = snap.Percent;
        PercentLabel.Text = $"{snap.Percent:F1}%";
        DoneLabel.Text = $"{Utils.FormatSize(snap.Done)} / {Utils.FormatSize(snap.Total)}";
        SpeedLabel.Text = Utils.FormatSpeed(snap.Speed);
        AvgLabel.Text = Utils.FormatSpeed(snap.AvgSpeed);
        EtaLabel.Text = Utils.FormatEta(snap.Eta);
        ElapsedLabel.Text = Utils.FormatEta(snap.Elapsed);
        WorkersLabel.Text = $"{snap.WorkersActive}/{snap.WorkersTotal}";
        SegsLabel.Text = $"{snap.SegmentsDone}/{snap.SegmentsTotal}";

        // 校验进度条
        if (snap.Phase == Phases.Verify)
        {
            VerifyRow.Visibility = Visibility.Visible;
            VerifyBar.IsIndeterminate = true;
            VerifyLabel.Text = "完整性校验中…";
        }
        else if (snap.Phase == Phases.Cleanup)
        {
            VerifyRow.Visibility = Visibility.Visible;
            VerifyBar.IsIndeterminate = true;
            VerifyLabel.Text = "清理临时文件…";
        }
        else if (snap.Finished && snap.Ok == true && !snap.IsInfo)
        {
            VerifyRow.Visibility = Visibility.Visible;
            VerifyBar.IsIndeterminate = false;
            VerifyBar.Value = 100;
            VerifyLabel.Text = "校验完成";
        }
        else
        {
            VerifyRow.Visibility = Visibility.Collapsed;
            VerifyBar.IsIndeterminate = false;
            VerifyBar.Value = 0;
        }

        // 状态栏
        StatusPhase.Text = "阶段: " + snap.PhaseLabel;
        StatusDetail.Text = snap.Detail;

        ProcessLogs();
        ProcessConfirm();
        DrawSpeed(snap.History, snap.Speed);
    }

    // ---------------------------------------------------------------------
    // 日志
    // ---------------------------------------------------------------------
    private void ProcessLogs()
    {
        var logs = _status.Logs();
        if (logs.Count < _lastLogCount) { ClearLog(); _lastLogCount = 0; }
        for (int i = _lastLogCount; i < logs.Count; i++)
            AppendLog(logs[i].level, logs[i].message);
        _lastLogCount = logs.Count;
    }

    private void AppendLog(string level, string message)
    {
        var brush = level switch
        {
            "ok" => new SolidColorBrush(ColorsHelper.Ok),
            "warn" => new SolidColorBrush(ColorsHelper.Warn),
            "error" => new SolidColorBrush(ColorsHelper.Err),
            "debug" => new SolidColorBrush(ColorsHelper.Muted),
            _ => new SolidColorBrush(ColorsHelper.Info),
        };
        _logParagraph.Inlines.Add(new Run($"[{level.ToUpper(),-5}] {message}")
        {
            Foreground = brush,
        });
        _logParagraph.Inlines.Add(new LineBreak());

        if (_logParagraph.Inlines.Count > 4000)
        {
            // 防止无限增长：从头部删除若干行
            for (int i = 0; i < 200 && _logParagraph.Inlines.Count > 0; i++)
                _logParagraph.Inlines.Remove(_logParagraph.Inlines.FirstInline);
        }
        LogBox.ScrollToEnd();
    }

    private void ClearLog() => _logParagraph.Inlines.Clear();

    // ---------------------------------------------------------------------
    // 确认对话框
    // ---------------------------------------------------------------------
    private void ProcessConfirm()
    {
        if (_confirmOpen) return;
        var req = _status.PendingConfirm();
        if (req is null) return;
        _confirmOpen = true;
        var r = MessageBox.Show(
            this,
            req.Prompt + (req.DefaultYes ? "\n（默认选择：是）" : "\n（默认选择：否）"),
            "请确认",
            MessageBoxButton.YesNo,
            req.DefaultYes ? MessageBoxImage.Question : MessageBoxImage.Warning);
        _status.AnswerConfirm(r == MessageBoxResult.Yes);
        _confirmOpen = false;
    }

    // ---------------------------------------------------------------------
    // 完成处理
    // ---------------------------------------------------------------------
    private void OnCompleted(JobSnapshot snap)
    {
        var msg = $"下载完成！\n文件：{snap.OutputPath}\n大小：{Utils.FormatSize(snap.Done)}";
        if (snap.ResultInfo.TryGetValue("summary", out var s) && s is string ss)
            msg += $"\n校验：{ss}";
        var r = MessageBox.Show(this, msg + "\n\n是否打开所在文件夹？", "下载完成",
            MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (r == MessageBoxResult.Yes) OpenFolder(snap.OutputPath);
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
            {
                var dir = Path.GetDirectoryName(path) ?? ".";
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Utils.LogError($"打开文件夹失败: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------
    // 速度曲线
    // ---------------------------------------------------------------------
    private void DrawSpeed(List<double> history, double currentSpeed)
    {
        var canvas = SpeedCanvas;
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double max = 1.0;
        foreach (var v in history) if (v > max) max = v;
        if (currentSpeed > max) max = currentSpeed;
        max *= 1.15;

        var gridBrush = new SolidColorBrush(ColorsHelper.Grid);
        for (int i = 1; i < 4; i++)
        {
            double y = h * i / 4.0;
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1,
            });
        }

        if (history.Count >= 2)
        {
            var pts = new PointCollection();
            int n = history.Count;
            for (int i = 0; i < n; i++)
            {
                double x = n == 1 ? 0 : w * i / (double)(n - 1);
                double y = h - (history[i] / max) * h;
                pts.Add(new Point(x, y));
            }
            canvas.Children.Add(new System.Windows.Shapes.Polyline
            {
                Points = pts,
                Stroke = new SolidColorBrush(ColorsHelper.Accent),
                StrokeThickness = 2,
            });
        }

        var cur = new TextBlock
        {
            Text = "当前 " + Utils.FormatSpeed(currentSpeed),
            Foreground = new SolidColorBrush(Colors.White),
            FontSize = 11,
        };
        Canvas.SetLeft(cur, 6);
        Canvas.SetTop(cur, 6);
        canvas.Children.Add(cur);

        var peak = new TextBlock
        {
            Text = "峰值 " + Utils.FormatSpeed(max / 1.15),
            Foreground = new SolidColorBrush(ColorsHelper.Muted),
            FontSize = 10,
        };
        Canvas.SetRight(peak, 6);
        Canvas.SetTop(peak, 6);
        canvas.Children.Add(peak);
    }

    /// <summary>
    /// 关闭窗口时：停止轮询、取消活动任务并等待其结束，
    /// 防止 Dispatcher 先行关闭导致 await 续体被中止而抛出 TaskCanceledException。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        var run = _runTask;
        if (_busy && run is not null && !run.IsCompleted)
        {
            _activeJob?.Cancel();
            try { run.Wait(TimeSpan.FromSeconds(3)); }
            catch { /* 等待失败则交给进程退出兜底 */ }
        }
        base.OnClosing(e);
    }

    // ---------------------------------------------------------------------
    // 多实例激活
    // ---------------------------------------------------------------------
    public void ActivateExisting()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        try { WindowInterop.BringToFront(new WindowInteropHelper(this).Handle); }
        catch { /* 原生调用失败忽略 */ }
    }
}

/// <summary>日志 / 曲线配色（与 XAML 资源保持一致）。</summary>
internal static class ColorsHelper
{
    public static readonly Color Info = Color.FromRgb(207, 210, 220);
    public static readonly Color Ok = Color.FromRgb(109, 214, 109);
    public static readonly Color Warn = Color.FromRgb(255, 209, 102);
    public static readonly Color Err = Color.FromRgb(255, 107, 107);
    public static readonly Color Muted = Color.FromRgb(154, 154, 165);
    public static readonly Color Accent = Color.FromRgb(76, 194, 255);
    public static readonly Color Grid = Color.FromRgb(60, 60, 70);
}
