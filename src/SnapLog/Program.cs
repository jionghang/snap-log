using Avalonia;
using Avalonia.Threading;
using SnapLog.Cli;
using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;
using SnapLog.Ocr;
using SnapLog.Storage;
using SnapLog.Summarization;
using SnapLog.Ui;

namespace SnapLog;

internal static class Program
{
    /// <summary>进程级取消信号，Ctrl+C 或窗口关闭时用来收尾。</summary>
    private static readonly CancellationTokenSource Shutdown = new();

    [STAThread]
    private static int Main(string[] args)
    {
        var cli = CommandLineOptions.Parse(args);

        if (cli.Command is CliCommand.Help or CliCommand.Unknown)
        {
            ConsoleBridge.Attach();
            if (cli.Command == CliCommand.Unknown)
            {
                Console.Error.WriteLine($"参数错误：{cli.Error}");
            }

            Console.WriteLine(CommandLineOptions.HelpText);
            return cli.Command == CliCommand.Unknown ? 1 : 0;
        }

        var load = OptionsStore.Load(cli.ConfigPath);
        var options = load.Options;

        // 自检类命令放进临时沙盒：它们会模拟点击、跑清理、写测试记录，
        // 不能让这些动作碰到真实配置和数据库（--ui-smoke 曾经把关掉的开关写回真实配置）。
        // 显式传了 --data 时按用户指定的目标来（本机预览就是这么用临时目录的）。
        string? sandboxDirectory = null;
        var configSourcePath = load.SourcePath;

        if (string.IsNullOrWhiteSpace(cli.DataDirectory) && ProbeSandbox.AppliesTo(cli.Command))
        {
            var sandbox = ProbeSandbox.Prepare(options, load.SourcePath);
            options.Storage.DataDirectory = sandbox.DataDirectory;
            options.Capture.ImageDirectory = string.Empty;   // 截图也别落到真实图库
            sandboxDirectory = sandbox.DataDirectory;

            if (sandbox.ConfigPath is not null)
            {
                configSourcePath = sandbox.ConfigPath;
            }
        }
        else if (!string.IsNullOrWhiteSpace(cli.DataDirectory))
        {
            options.Storage.DataDirectory = cli.DataDirectory;
        }

        var paths = AppPaths.Create(options.Storage.DataDirectory);
        paths.EnsureCreated();

        var log = new FileLogger(paths.LogsDirectory);
        log.Configure(options.Logging.Enabled, options.Logging.Level, options.Logging.RetentionDays);
        log.CleanupExpired();
        log.Info($"SnapLog 启动：{(args.Length == 0 ? "(无参数，托盘模式)" : string.Join(' ', args))}");

        if (sandboxDirectory is not null)
        {
            log.Info($"自检沙盒：{sandboxDirectory}（配置与数据库均为副本，不写真实数据）");
        }

        if (load.Warning is not null)
        {
            log.Warn(load.Warning);
        }

        IActivityRepository store;
        try
        {
            store = OpenStore(options, paths, log);
        }
        catch (Exception ex)
        {
            // 存储打不开就没法记录，也没有兜底方案，直接说明原因。
            log.Error("打开数据库失败", ex);
            ConsoleBridge.Attach();
            Console.Error.WriteLine($"[错误] 打开数据库失败：{ex.Message}");
            return 1;
        }

        try
        {
            if (cli.Command == CliCommand.Run)
            {
                return RunTrayApplication(options, paths, log, configSourcePath, store, load.Warning);
            }

            ConsoleBridge.Attach();

            if (sandboxDirectory is not null)
            {
                Console.WriteLine($"[沙盒] 自检在临时副本里运行，不写真实数据：{sandboxDirectory}");
            }

            if (load.Warning is not null)
            {
                Console.WriteLine($"[警告] {load.Warning}");
            }
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                Shutdown.Cancel();
            };

            return CliRunner
                .RunAsync(cli, options, paths, log, configSourcePath, store, Shutdown.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[结果] 已取消");
            return 1;
        }
        catch (Exception ex)
        {
            log.Error("执行失败", ex);
            Console.Error.WriteLine($"[错误] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// 建库 + 必要时导入旧版 CSV。
    /// 这里用同步等待：调用点都在消息循环启动之前，而且存储层内部一律 ConfigureAwait(false)，
    /// 不存在把 UI 线程锁死的风险。
    /// </summary>
    private static IActivityRepository OpenStore(AppOptions options, AppPaths paths, FileLogger log)
    {
        var store = new SqliteActivityStore(
            paths.ResolveDatabasePath(options.Storage.DatabaseFileName),
            log);

        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

        if (options.Storage.ImportLegacyCsv)
        {
            TryImportLegacyCsv(store, paths, log);
        }

        return store;
    }

    /// <summary>把旧版 activity.csv 一次性导进 SQLite。只在库为空时做，且不删除原文件。</summary>
    private static void TryImportLegacyCsv(IActivityStore store, AppPaths paths, FileLogger log)
    {
        try
        {
            if (!File.Exists(paths.LegacyCsvPath))
            {
                return;
            }

            var existing = store.CountAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (existing > 0)
            {
                log.Info($"数据库中已有 {existing} 条记录，跳过旧版 CSV 导入");
                return;
            }

            var imported = CsvActivityTransfer
                .ImportLegacyAsync(paths.LegacyCsvPath, store, log, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            log.Info($"旧版 CSV 导入完成：{imported} 条（原文件保留在 {paths.LegacyCsvPath}，确认无误后可自行删除）");
        }
        catch (Exception ex)
        {
            // 导入失败不能挡住新记录写入。
            log.Warn($"导入旧版 CSV 失败，将从空库开始：{ex.Message}");
        }
    }

    /// <summary>
    /// 托盘常驻模式：Avalonia 跑界面，WinForms 只负责托盘图标。
    /// </summary>
    private static int RunTrayApplication(
        AppOptions options,
        AppPaths paths,
        FileLogger log,
        string? configSourcePath,
        IActivityRepository store,
        string? startupWarning = null)
    {
        // 只允许一个实例：两个一起跑会把同一条记录写两遍，托盘图标也变成两个。
        // 命令行的一次性命令不走这里，仍可与托盘那份并存。
        using var instanceLock = SingleInstanceLock.TryAcquire();
        if (instanceLock is null)
        {
            return ReportAlreadyRunning(log, paths);
        }

        // 启动时清理一次过期的截图与记录。以前这一步只有命令行的 --cleanup 会做，
        // 托盘模式下等于永远不清理（界面和文档却写着会自动清理）。
        try
        {
            var cleanup = new RetentionService(store, paths, log)
                .RunAsync(options, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            log.Info($"启动清理：{cleanup.Describe()}");
        }
        catch (Exception ex)
        {
            log.Warn($"启动清理失败（不影响记录）：{ex.Message}");
        }

        var engine = new SnapLogEngine(options, paths, log, OcrEngineFactory.Create(options.Ocr, log), store);
        var summaryRunner = new SummaryRunner(store, paths, log);

        // 三个"每天到点跑一次"的任务共用一套调度：批量识别、定时总结、定时推送。
        // 配置路径要一并传进去：调度器每轮重新读配置，读的必须是这次启动用的那份
        // （否则用 --config 启动时，定时任务会去看用户目录里另一份配置）。
        var scheduler = new ScheduledJobsService(
            [
                new OcrBatchJob(store, paths, log, () => OcrEngineFactory.Create(options.Ocr, log)),
                new SummaryJob(summaryRunner, store),
                new FeishuPushJob(new FeishuWriter(store, log)),
            ],
            paths,
            log,
            configSourcePath,
            () => engine.IsRunning);

        var services = new AppServices(
            options, paths, log, store, engine, summaryRunner, scheduler, configSourcePath,
            startupWarning: startupWarning);
        App.Services = services;

        // UI 线程上可恢复的异常：记日志 + 托盘气泡，程序继续跑。
        // 一次抓取失败、一次按钮点错都不该把托盘里的记录任务带走。
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            ReportRecoverable(log, eventArgs.Exception);
            eventArgs.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            ReportFatal(log, paths, eventArgs.ExceptionObject as Exception);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        }
        finally
        {
            services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            App.Services = null;
            log.Info("SnapLog 退出");
        }

        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// 已经有一个托盘实例在跑：说清楚怎么找到它，然后退出这次启动。
    /// 双击启动没有控制台，只能弹窗；从终端/脚本启动则写一行到标准错误，
    /// 免得脚本挂在一个没人点的模态框上。
    ///
    /// 这里用 WinForms 的 MessageBox：它发生在 Avalonia 应用启动之前，
    /// 这时候还没有任何窗口可以当弹窗的宿主。
    /// </summary>
    private static int ReportAlreadyRunning(FileLogger log, AppPaths paths)
    {
        const string advice = "SnapLog 已经在运行（在托盘里）。\n\n"
                              + "同时运行两个实例会把同一条记录写两遍，所以这次没有启动。\n"
                              + "要打开界面，请点托盘图标；要重新启动，先在托盘菜单里退出那个实例。";

        log.Warn("检测到已有实例在运行，本次启动未继续");

        if (ConsoleBridge.HasConsole())
        {
            Console.Error.WriteLine("[提示] " + advice.Replace("\n\n", " ").Replace('\n', ' '));
        }
        else
        {
            System.Windows.Forms.MessageBox.Show(
                $"{advice}\n\n日志目录：\n{paths.LogsDirectory}",
                "SnapLog 已在运行",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
        }

        return 0;
    }

    /// <summary>UI 线程上可恢复的异常：记日志 + 托盘气泡，程序继续跑。</summary>
    private static void ReportRecoverable(FileLogger log, Exception exception)
    {
        log.Error("界面操作失败（已跳过，程序继续运行）", exception);

        // 用气泡而不是模态框：后台工具不该因为一个可恢复的错误打断用户。
        TrayBalloon.TryShow($"{exception.GetType().Name}: {exception.Message}",
            System.Windows.Forms.ToolTipIcon.Warning);
    }

    private static void ReportFatal(FileLogger log, AppPaths paths, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        log.Error("未捕获的异常", exception);
        System.Windows.Forms.MessageBox.Show(
            $"SnapLog 遇到未处理的错误：\n\n{exception.GetType().Name}: {exception.Message}\n\n详情见日志目录：\n{paths.LogsDirectory}",
            "SnapLog",
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Error);
    }
}
