using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;
using SnapLog.Interop;
using SnapLog.Storage;
using System.Windows.Forms;

namespace SnapLog.Ui;

/// <summary>
/// 界面层需要的一切运行时依赖与动作，集中在这一个对象上。
/// 页面只跟它打交道，不直接碰引擎/调度器，这样"改配置 → 立刻生效"只有一处实现。
///
/// 线程约定：引擎与调度器的事件在后台线程触发，这里统一转投到 UI 线程后再广播。
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    // 谁拿到桌面生命周期谁登记：AppServices 是在它存在之前构造的，
    // 只在构造时赋值会漏（真实托盘模式就是这么漏的，"点了退出窗口还在"）。
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public AppServices(
        AppOptions options,
        AppPaths paths,
        FileLogger log,
        IActivityRepository store,
        SnapLogEngine engine,
        SummaryRunner summaryRunner,
        ScheduledJobsService scheduler,
        string? configSourcePath,
        IClassicDesktopStyleApplicationLifetime? desktop = null,
        string? startupWarning = null)
    {
        StartupWarning = startupWarning;
        Options = options;
        Paths = paths;
        Log = log;
        Store = store;
        Engine = engine;
        SummaryRunner = summaryRunner;
        Scheduler = scheduler;
        ConfigSourcePath = configSourcePath;
        _desktop = desktop;

        Engine.RecordCaptured += OnRecordCaptured;
        Engine.StatusChanged += OnEngineStatus;
        _logEntryHook = entry => Post(() => LogEntryWritten?.Invoke(entry));
        Log.EntryWritten += _logEntryHook;
        Scheduler.JobCompleted += OnJobCompleted;
    }

    private readonly Action<LogEntry> _logEntryHook;

    /// <summary>启动时读配置遇到的问题（配置坏了会退回默认值）。为空表示一切正常。</summary>
    public string? StartupWarning { get; }

    /// <summary>
    /// 自检替身：设置后就由它代替真正的 <c>Shutdown()</c>。
    /// 用来验证"点了退出确实走到桌面生命周期"——这条链路曾经断过（托盘模式没登记生命周期，
    /// 表现是"点了托盘退出窗口还在"），而当时所有自动化用例都测不到。
    /// </summary>
    internal Action? ShutdownOverride { get; set; }

    public AppOptions Options { get; }

    public AppPaths Paths { get; }

    public FileLogger Log { get; }

    public IActivityRepository Store { get; }

    public SnapLogEngine Engine { get; }

    public SummaryRunner SummaryRunner { get; }

    public ScheduledJobsService Scheduler { get; }

    public string? ConfigSourcePath { get; }

    /// <summary>托盘宿主；只有托盘模式（<see cref="AttachDesktop"/>）下才有。</summary>
    public TrayHost? Tray { get; private set; }

    public MainWindow? Main { get; internal set; }

    /// <summary>本次运行已经记了多少条。</summary>
    public int SessionRecords { get; private set; }

    /// <summary>上一次广播出去的运行状态，用来判断"状态真的变了"。</summary>
    private bool _lastRunning;

    /// <summary>引擎的启停状态变了，或配置改动需要重画时触发。</summary>
    public event Action? StateChanged;

    /// <summary>新记录落库、任务跑完这类"数据变了"的通知。</summary>
    public event Action? DataChanged;

    /// <summary>给状态栏用的一行文字。</summary>
    public event Action<string>? StatusChanged;

    public event Action<LogEntry>? LogEntryWritten;

    /// <summary>定时任务跑完（后台线程已转 UI 线程）。</summary>
    public event Action<(IScheduledJob Job, JobRunResult Result)>? JobCompleted;

    // ---------------------------------------------------------------- 生命周期

    /// <summary>托盘模式：建托盘、建主窗口、按配置决定要不要直接显示。</summary>
    public void AttachDesktop(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        Tray = new TrayHost(this);
        Main = new MainWindow(this);
        desktop.MainWindow = Main;

        Engine.Start();
        Scheduler.Start();
        Tray.UpdateState();

        var state = AppStateStore.Load(Paths.AppStatePath);
        var firstRun = !state.FirstRunNoticeShown;

        // 首次运行一定把窗口亮出来：只留一个托盘气泡，用户根本不知道这东西装上了。
        if (!Options.Ui.StartMinimizedToTray || firstRun)
        {
            ShowMainWindow();
        }

        // 图标进托盘可能要等任务栏起来（开机自启时很常见），所以提示等它真进去了再弹；
        // 否则 ShowBalloon 会因为"图标还不可见"被静默丢掉。
        // 配置读坏了必须说出来：退回默认值后用户的密钥/飞书都没了，只写日志等于没说。
        void NotifyStartup()
        {
            if (!string.IsNullOrWhiteSpace(StartupWarning))
            {
                Tray.ShowBalloon(
                    "配置文件有问题，本次按默认设置启动",
                    StartupWarning + Environment.NewLine + "在设置里确认一遍，或把日志目录里的文件发给维护的人。",
                    ToolTipIcon.Warning);
                return;
            }

            Tray.ShowBalloon(
                "SnapLog 已开始记录",
                "数据保存在本机：" + Environment.NewLine + Paths.DataDirectory,
                ToolTipIcon.Info);
        }

        if (Tray.IsReady)
        {
            NotifyStartup();
        }
        else
        {
            Tray.IconReady += NotifyStartup;
        }

        // 配置里开着每日流程但没确认过隐私提示的话，启动时就把确认框弹出来。
        Dispatcher.UIThread.Post(() => Main?.AskForConsentIfNeeded(), DispatcherPriority.Background);

        if (firstRun)
        {
            state.FirstRunNoticeShown = true;
            AppStateStore.Save(Paths.AppStatePath, state);
            new FirstRunWindow(() =>
            {
                // 首次运行最重要的一步就是填接入信息，直接把人送过去。
                ShowMainWindow();
                Main?.ShowPage("settings");
            }).Show(Main);
        }
    }

    public void ShowMainWindow()
    {
        if (Main is null)
        {
            return;
        }

        Main.ShowFromTray();
    }

    public void ExitApplication()
    {
        // 这条日志是排"点了退出没反应"用的：能看出走到哪一步、环境是否齐全。
        Log.Info($"退出：开始（主窗口={(Main is null ? "无" : "有")}，桌面生命周期={(_desktop is null ? "无" : "有")}）");
        Tray?.Hide();

        // 主窗口的关闭按钮被重写成"只收起"，不先放行的话 Shutdown 会被 OnClosing 拦下来，
        // 表现就是"点了退出，窗口还在"。
        if (Main is { } main)
        {
            main.AllowClose = true;
        }

        if (_desktop is null)
        {
            // 命令行/自检环境没有桌面生命周期：关掉窗口就返回，进程由调用方结束。
            Log.Warn("退出：没有桌面生命周期，仅关闭窗口");
            Main?.Close();
            return;
        }

        // 双保险：先自己把主窗口关掉，再让生命周期收尾。
        // 只做其中一层都出过问题——窗口的关闭按钮被改写成"只收起"，而生命周期在托盘模式下
        // 曾经根本没被登记（见字段注释），两次的表现都是"点了退出窗口还在"。
        Main?.Close();

        if (ShutdownOverride is { } probe)
        {
            // 自检路径：确认"退出请求确实走到了桌面生命周期"就够了，
            // 不能真的调 Shutdown——那会把自检进程的调度器一起关掉。
            probe();
            return;
        }

        _desktop.Shutdown();
        Log.Info($"退出：Shutdown 已返回（剩余窗口 {_desktop.Windows.Count} 个）");

        // 兜底：万一消息循环仍然没退（历史问题复发或环境异常），两秒后直接结束进程——
        // 托盘程序退不掉是最严重的问题，只能由用户去任务管理器杀。
        // 数据是随写随存的（记录逐条提交、任务运行时间在每次任务后写 state.json），不会丢。
        // 只在真正拉起界面的进程里生效：自检/命令行调用 ExitApplication 不该被它结束。
        if (Main is null)
        {
            return;
        }

        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            Log.Warn("退出：Shutdown 之后进程仍未结束，兜底直接退出进程");
            Environment.Exit(0);
        };
        watchdog.Start();
    }

    // ---------------------------------------------------------------- 动作

    /// <summary>
    /// 改完 <see cref="Options"/> 后落盘。返回 false 表示写盘失败（界面据此提示用户，但不回滚内存值）。
    /// </summary>
    public bool SaveOptions()
    {
        try
        {
            OptionsStore.Save(Options, AppPaths.ResolveConfigWriteTarget(ConfigSourcePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("保存配置失败", ex);
            return false;
        }

        Log.Configure(Options.Logging.Enabled, Options.Logging.Level, Options.Logging.RetentionDays);
        StateChanged?.Invoke();
        return true;
    }

    public async Task ToggleRecordingAsync()
    {
        if (Engine.IsRunning)
        {
            await Engine.StopAsync().ConfigureAwait(true);
        }
        else
        {
            Engine.Start();
        }

        Tray?.UpdateState();
        StateChanged?.Invoke();
    }

    /// <summary>写开机自启（注册表）。失败时返回 false，界面要把开关拨回真实状态。</summary>
    public bool SetAutoStart(bool wanted)
    {
        if (!AutoStart.SetEnabled(wanted))
        {
            Log.Warn("设置开机自启动失败：写注册表被拒绝或路径为空");
            return false;
        }

        Log.Info(wanted ? $"已设为开机自动启动：{AutoStart.ExecutablePath}" : "已取消开机自动启动");
        return true;
    }

    /// <summary>某个定时任务最近一次的说法（下次什么时候跑 / 未启用）。</summary>
    public string DescribeSchedule(string jobKey)
    {
        foreach (var (job, enabled, nextRun) in Scheduler.DescribeSchedule(Options))
        {
            if (string.Equals(job.Key, jobKey, StringComparison.Ordinal))
            {
                return enabled ? nextRun : "未启用";
            }
        }

        return "未启用";
    }

    public async Task<(long Total, long Pending)> CountsAsync(CancellationToken cancellationToken)
    {
        var total = await Store.CountAsync(cancellationToken).ConfigureAwait(true);
        var pending = await Store.CountPendingAsync(cancellationToken).ConfigureAwait(true);
        return (total, pending);
    }

    // ---------------------------------------------------------------- 事件转发

    private void OnRecordCaptured(object? sender, ActivityRecord record) => Post(() =>
    {
        SessionRecords++;
        StatusChanged?.Invoke($"新记录：{record.WindowTitle}（{record.TextLength} 字）");
        DataChanged?.Invoke();
        StateChanged?.Invoke();
    });

    private void OnEngineStatus(object? sender, string message) => Post(() =>
    {
        StatusChanged?.Invoke(message);

        // 启停可能来自任意一处（界面按钮 / 托盘菜单 / 外部调用），
        // 界面只关心"运行状态有没有变"——每条状态文案都通知一次会让页面反复查库。
        if (_lastRunning == Engine.IsRunning)
        {
            return;
        }

        _lastRunning = Engine.IsRunning;
        Tray?.UpdateState();
        StateChanged?.Invoke();
    });

    private void OnJobCompleted(object? sender, (IScheduledJob Job, JobRunResult Result) result) => Post(() =>
    {
        JobCompleted?.Invoke(result);
        DataChanged?.Invoke();

        // 失败要说出来，成功就不打扰：这个工具平时应该安静地待在托盘里。
        if (!result.Result.Success)
        {
            Tray?.ShowBalloon($"{result.Job.DisplayName}失败", result.Result.Message, ToolTipIcon.Warning);
        }
        else
        {
            Log.Info($"{result.Job.DisplayName}：{result.Result.Message}");
        }
    });

    /// <summary>引擎事件是后台线程来的，界面只能被 UI 线程碰。</summary>
    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Engine.RecordCaptured -= OnRecordCaptured;
        Engine.StatusChanged -= OnEngineStatus;
        Log.EntryWritten -= _logEntryHook;
        Scheduler.JobCompleted -= OnJobCompleted;

        Tray?.Dispose();
        await Engine.DisposeAsync().ConfigureAwait(false);
        await Scheduler.StopAsync().ConfigureAwait(false);
    }
}
