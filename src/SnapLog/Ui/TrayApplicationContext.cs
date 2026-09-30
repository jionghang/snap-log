using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;
using SnapLog.Ocr;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 托盘应用的宿主：负责托盘图标、菜单，以及主窗口/记录查看器/设置窗口的打开。
/// 启动即开始记录，用户随时可以从托盘暂停。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppOptions _options;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;
    private readonly SnapLogEngine _engine;
    private readonly IActivityRepository _store;
    private readonly MainForm _mainForm;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _stateMenuItem;
    private readonly string? _configSourcePath;

    private readonly SummaryRunner _summaryRunner;
    private readonly ScheduledJobsService _scheduler;
    /// <summary>UI 线程的同步上下文。引擎的事件在后台线程触发，操作托盘/窗口前必须先切回去。</summary>
    private readonly SynchronizationContext? _uiContext;
    private bool _disposed;

    public TrayApplicationContext(
        AppOptions options,
        AppPaths paths,
        FileLogger log,
        SnapLogEngine engine,
        SummaryRunner summaryRunner,
        IActivityRepository store,
        string? configSourcePath)
    {
        _options = options;
        _paths = paths;
        _log = log;
        _engine = engine;
        _store = store;
        _summaryRunner = summaryRunner;
        _configSourcePath = configSourcePath;

        // TrayApplicationContext 在 Application.Run 的主线程上构造，这里取到的就是 UI 线程。
        _uiContext = SynchronizationContext.Current;

        _mainForm = new MainForm(options, paths, log, engine, summaryRunner, store, configSourcePath);


        _stateMenuItem = new ToolStripMenuItem("运行中", null, Guarded(ToggleRecordingAsync))
        {
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        };

        // 菜单只留三件事：启停、打开主窗口、退出。
        // 其余操作（抓取、记录、设置、总结、飞书、导出）都在主窗口的页签里，不必在这里重复一遍。
        var menu = new ContextMenuStrip();
        menu.Items.Add(_stateMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("显示主窗口", null, (_, _) => ShowMainWindow()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApplication()));

        _trayIcon = new NotifyIcon
        {
            Icon = IconFactory.AppIcon,
            Text = "SnapLog",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        // 注册托盘气泡的实现：别的模块（比如全局异常处理）也能安全地弹提示。
        TrayBalloon.Register((message, icon) =>
        {
            try
            {
                if (_trayIcon.Visible)
                {
                    _trayIcon.ShowBalloonTip(4000, "SnapLog", message, icon);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        });

        _engine.StatusChanged += OnStatusChanged;

        // 三个"每天到点跑一次"的任务共用一套调度：批量识别、定时总结、定时推送。
        var ocrBatch = new OcrBatchJob(store, paths, log, () => OcrEngineFactory.Create(options.Ocr, log));
        var summaryJob = new SummaryJob(summaryRunner, store);
        var feishuWriter = new FeishuWriter(store, log);
        var publishJob = new FeishuPushJob(feishuWriter);

        _scheduler = new ScheduledJobsService([ocrBatch, summaryJob, publishJob], paths, log);
        _scheduler.JobCompleted += OnJobCompleted;

        _engine.Start();
        _scheduler.Start();
        UpdateTrayState();

        if (_options.Ui.StartMinimizedToTray)
        {
            _trayIcon.ShowBalloonTip(4000, "SnapLog 已开始记录", BuildBalloonText(), ToolTipIcon.Info);
        }
        else
        {
            ShowMainWindow();
        }

        // "首次运行提示"的状态存在独立的状态文件里，不写用户配置——
        // 一旦为了记这个标记去写 appsettings.json，那份快照就会永久挡住以后版本改默认值。
        var state = AppStateStore.Load(_paths.AppStatePath);
        if (!state.FirstRunNoticeShown)
        {
            state.FirstRunNoticeShown = true;
            AppStateStore.Save(_paths.AppStatePath, state);
            ShowFirstRunNotice();
        }
    }

    private string BuildBalloonText() =>
        $"记录前台窗口并识别文字，数据保存在本机：\n{_paths.DataDirectory}\n\n双击图标可打开主窗口。";

    private void ShowFirstRunNotice()
    {
        MessageBox.Show(
            "SnapLog 已在托盘运行，并开始记录。\n\n"
            + $"- 数据库与日志均保存在本机：{_paths.DataDirectory}\n"
            + "- 默认不联网：仅在主动执行“生成总结”并确认后，才会把内容发送到所配置的模型接口\n"
            + "- 不需要记录的进程可在“抓取配置”的排除列表中屏蔽\n"
            + "- 可随时在托盘菜单中暂停记录\n\n"
            + "如需屏蔽密码管理器等敏感应用，请先在设置中将其排除。",
            "SnapLog 首次运行",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task ToggleRecordingAsync()
    {
        if (_engine.IsRunning)
        {
            await _engine.StopAsync();
        }
        else
        {
            _engine.Start();
        }

        UpdateTrayState();
    }

    private void ShowMainWindow()
    {
        if (_mainForm.IsDisposed)
        {
            return;
        }

        _mainForm.Show();
        if (_mainForm.WindowState == FormWindowState.Minimized)
        {
            _mainForm.WindowState = FormWindowState.Normal;
        }

        _mainForm.Activate();
    }

    /// <summary>
    /// 定时任务跑完后给个托盘提示——尤其是失败，用户需要知道。
    /// 这个事件在调度器的后台线程触发，必须切回 UI 线程再动 NotifyIcon，
    /// 否则和 UI 线程上正在进行的图标操作相碰，会撞出 GDI+ 泛型错误。
    /// </summary>
    private void OnJobCompleted(object? sender, (IScheduledJob Job, JobRunResult Result) result)
    {
        if (_uiContext is not null)
        {
            _uiContext.Post(_ => ShowJobBalloon(result), null);
        }
        else
        {
            ShowJobBalloon(result);
        }
    }

    private void ShowJobBalloon((IScheduledJob Job, JobRunResult Result) result)
    {
        try
        {
            if (!_trayIcon.Visible)
            {
                return;
            }

            _trayIcon.ShowBalloonTip(
                5000,
                result.Result.Success ? $"{result.Job.DisplayName} 完成" : $"{result.Job.DisplayName} 失败",
                result.Result.Message,
                result.Result.Success ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            _log.Warn($"显示托盘提示失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 给 async void 的菜单处理器统一包一层：里面的任何异常都不该炸掉程序，
    /// 记日志 + 托盘提示即可。这层兜底比挨个去每个方法里 try/catch 更可靠。
    /// </summary>
    private EventHandler Guarded(Func<Task> action) =>
        async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error("菜单操作失败（已跳过，程序继续运行）", ex);
                TrayBalloon.TryShow($"{ex.GetType().Name}: {ex.Message}", ToolTipIcon.Warning);
            }
        };

    /// <summary>
    /// 引擎的状态事件在后台线程触发。NotifyIcon 虽然不强制 STA，但它背后有一个
    /// Windows 图标句柄，跨线程操作它撞到 GDI+ 泛型错误是真实发生过的事故——
    /// 所以这里必须切回 UI 线程再动它。
    /// </summary>
    private void OnStatusChanged(object? sender, string message)
    {
        var text = message.Length > 60 ? message[..60] : message;

        if (_uiContext is not null)
        {
            _uiContext.Post(_ => { if (_trayIcon.Visible) { _trayIcon.Text = text; } }, null);
        }
        else if (_trayIcon.Visible)
        {
            _trayIcon.Text = text;
        }
    }

    private void UpdateTrayState()
    {
        _stateMenuItem.Text = _engine.IsRunning ? "运行中（点击暂停）" : "已暂停（点击开始）";
        _trayIcon.Text = _engine.IsRunning ? "SnapLog · 记录中" : "SnapLog · 已暂停";
    }

    private void ExitApplication()
    {
        _engine.StatusChanged -= OnStatusChanged;
        _trayIcon.Visible = false;

        // 让 Application.Run 退出，随后由 Program 统一收尾。
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _engine.StatusChanged -= OnStatusChanged;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _mainForm.Dispose();

            _scheduler.JobCompleted -= OnJobCompleted;
            _scheduler.Dispose();

            _engine.StopAsync().GetAwaiter().GetResult();
            _engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }
}
