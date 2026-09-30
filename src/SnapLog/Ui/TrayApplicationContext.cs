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
    private RecordsForm? _recordsForm;
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

        var menu = new ContextMenuStrip();
        menu.Items.Add(_stateMenuItem);
        menu.Items.Add(new ToolStripMenuItem("立即抓取一次", null, Guarded(CaptureOnceAsync)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("显示主窗口", null, (_, _) => ShowMainWindow()));
        menu.Items.Add(new ToolStripMenuItem("记录查看器…", null, (_, _) => ShowRecordsForm()));
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => ShowSettingsTab()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("生成小结…", null, (_, _) => ShowSummaryTab()));
        menu.Items.Add(new ToolStripMenuItem("打开总结目录", null, (_, _) => OpenPath(_paths.SummariesDirectory)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开数据目录", null, (_, _) => OpenPath(_paths.DataDirectory)));
        menu.Items.Add(new ToolStripMenuItem("导出全部记录为 CSV…", null, Guarded(ExportAllAsync)));
        menu.Items.Add(new ToolStripMenuItem("批量识别待识别记录…", null, Guarded(RunOcrBatchAsync)));
        menu.Items.Add(new ToolStripMenuItem("把小结写入飞书表格…", null, Guarded(PushToFeishuNowAsync)));
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
        var summaryJob = new SummaryJob(summaryRunner);
        var feishuWriter = new FeishuWriter(store, log);
        var publishJob = new FeishuPushJob(feishuWriter);

        _scheduler = new ScheduledJobsService([ocrBatch, summaryJob, publishJob], paths, log);
        _scheduler.JobCompleted += OnJobCompleted;

        _engine.Start();
        _scheduler.Start();

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
        $"记录前台窗口并做 OCR，数据写在本机：\n{_paths.DataDirectory}\n\n双击图标打开主窗口。";

    private void ShowFirstRunNotice()
    {
        MessageBox.Show(
            "SnapLog 已在托盘里开始记录。\n\n"
            + $"- 数据（数据库、日志）都存在本机：{_paths.DataDirectory}\n"
            + "- 默认不联网：只有你点「生成小结」并确认后，才会把记录发送到配置的模型接口\n"
            + "- 记录的窗口进程可以用「常用设置」或「排除的进程名」屏蔽\n"
            + "- 随时可以在托盘菜单里暂停记录\n\n"
            + "如果不想让它记录某个应用（比如密码管理器），请先在设置里排除。",
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

    private async Task CaptureOnceAsync()
    {
        try
        {
            var outcome = await _engine.CaptureNowAsync(CaptureTrigger.Manual, CancellationToken.None);
            if (!outcome.Captured)
            {
                _trayIcon.ShowBalloonTip(3000, "没有记录", outcome.Reason, ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _log.Error("托盘手动抓取失败", ex);
        }
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

    private void ShowRecordsForm()
    {
        // 复用同一个实例：重复点菜单只是把已有窗口带到前面，不会开出一堆。
        if (_recordsForm is null || _recordsForm.IsDisposed)
        {
            _recordsForm = new RecordsForm(_options, _store, _paths, _log);
        }

        ShowOwnedDialog(_recordsForm);
        _recordsForm = null;
    }

    private void ShowSettingsTab()
    {
        ShowMainWindow();
        _mainForm.SelectTab("抓取配置");
    }

    /// <summary>把模态窗口居中显示在主窗口上；主窗口隐藏时回退到屏幕居中。</summary>
    private void ShowOwnedDialog(Form form)
    {
        if (_mainForm.Visible)
        {
            form.ShowDialog(_mainForm);
            return;
        }

        form.StartPosition = FormStartPosition.CenterScreen;
        form.ShowDialog();
    }

    private void ShowSummaryTab()
    {
        ShowMainWindow();
        _mainForm.SelectTab("总结");
    }

    private async Task ExportAllAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出全部记录",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            DefaultExt = "csv",
            FileName = $"snaplog-全部-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            InitialDirectory = Directory.Exists(_paths.DataDirectory) ? _paths.DataDirectory : null,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        try
        {
            var count = await CsvActivityTransfer.ExportAsync(
                _store, new ActivityQuery(), dialog.FileName, _log, CancellationToken.None);

            _trayIcon.ShowBalloonTip(4000, "导出完成", $"已导出 {count} 条记录到\n{dialog.FileName}", ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("导出全部记录失败", ex);
            _trayIcon.ShowBalloonTip(4000, "导出失败", ex.Message, ToolTipIcon.Warning);
        }
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

    private async Task RunOcrBatchAsync()
    {
        try
        {
            await _engine.StopAsync();

            var job = new OcrBatchJob(_store, _paths, _log, () => OcrEngineFactory.Create(_options.Ocr, _log));
            var result = await job.RunAsync(_options, CancellationToken.None);

            _trayIcon.ShowBalloonTip(
                5000,
                result.Success ? "批量识别完成" : "批量识别有失败",
                result.Message,
                result.Success ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            _log.Error("手动批量识别失败", ex);
        }
        finally
        {
            _engine.Start();
            _mainForm.ReloadStatus();
        }
    }

    private async Task PushToFeishuNowAsync()
    {
        try
        {
            var problem = FeishuBitablePublisher.Validate(_options.Feishu);
            if (problem is not null)
            {
                MessageBox.Show(
                    "飞书推送配置不完整：" + Environment.NewLine + Environment.NewLine + problem
                    + Environment.NewLine + Environment.NewLine + "请在设置里补全后再试。",
                    "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var writer = new FeishuWriter(_store, _log);
            var pending = await writer.CountPendingAsync(_options, CancellationToken.None).ConfigureAwait(true);

            if (pending == 0)
            {
                MessageBox.Show(
                    "没有待写入的小结。" + Environment.NewLine + Environment.NewLine
                    + $"只写 {FeishuWriter.GetEarliestRunTime(_options.Feishu):yyyy-MM-dd} 之后生成、"
                    + "而且还没写进飞书的小结。先生成一次小结，或在设置里把「写入范围」调大。",
                    "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"将把 {pending} 条小结写进飞书多维表格：{Environment.NewLine}{Environment.NewLine}"
                + $"app_token：{_options.Feishu.AppToken}{Environment.NewLine}"
                + $"table_id：{_options.Feishu.TableId}{Environment.NewLine}{Environment.NewLine}"
                + "小结正文（可能包含屏幕上识别出的内容）会上传到飞书，确认继续？",
                "写入飞书", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

            if (confirm != DialogResult.OK)
            {
                return;
            }

            var result = await writer.WritePendingAsync(_options, CancellationToken.None);

            _trayIcon.ShowBalloonTip(
                5000,
                result.Success ? "已写入飞书" : "写入失败",
                result.Message.Length > 200 ? result.Message[..200] : result.Message,
                result.Success ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            _log.Error("手动写入飞书失败", ex);
            _trayIcon.ShowBalloonTip(5000, "写入失败", ex.Message, ToolTipIcon.Warning);
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

    private void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                _trayIcon.ShowBalloonTip(3000, "SnapLog", $"路径还不存在：{path}", ToolTipIcon.Info);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _log.Warn($"打开路径失败：{ex.Message}");
        }
    }

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
            _recordsForm?.Dispose();

            _scheduler.JobCompleted -= OnJobCompleted;
            _scheduler.Dispose();

            _engine.StopAsync().GetAwaiter().GetResult();
            _engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }
}
