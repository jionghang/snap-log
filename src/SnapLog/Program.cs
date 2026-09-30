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
        if (!string.IsNullOrWhiteSpace(cli.DataDirectory))
        {
            options.Storage.DataDirectory = cli.DataDirectory;
        }

        var paths = AppPaths.Create(options.Storage.DataDirectory);
        paths.EnsureCreated();

        var log = new FileLogger(paths.LogsDirectory);
        log.Configure(options.Logging.Enabled, options.Logging.Level, options.Logging.RetentionDays);
        log.CleanupExpired();
        log.Info($"SnapLog 启动：{(args.Length == 0 ? "(无参数，托盘模式)" : string.Join(' ', args))}");

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
                return RunTrayApplication(options, paths, log, load.SourcePath, store);
            }

            ConsoleBridge.Attach();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                Shutdown.Cancel();
            };

            return CliRunner
                .RunAsync(cli, options, paths, log, load.SourcePath, store, Shutdown.Token)
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

    private static int RunTrayApplication(
        AppOptions options,
        AppPaths paths,
        FileLogger log,
        string? configSourcePath,
        IActivityRepository store)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 两级处理：
        //   ThreadException（UI 线程上的异常，多数来自一次抓取/一次按钮点击）：
        //     记日志 + 托盘提示，**不崩**。一次抓取失败不配弹崩溃框。
        //   UnhandledException（别的线程上没被接住的异常，进程真的要退出了）：
        //     记日志 + 弹窗 + 退出。
        Application.ThreadException += (_, eventArgs) => ReportRecoverable(log, paths, eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            ReportFatal(log, paths, eventArgs.ExceptionObject as Exception);

        var ocr = OcrEngineFactory.Create(options.Ocr, log);
        var engine = new SnapLogEngine(options, paths, log, ocr, store);
        var summaryRunner = new SummaryRunner(store, paths, log);

        using var context = new TrayApplicationContext(
            options, paths, log, engine, summaryRunner, store, configSourcePath);
        Application.Run(context);

        log.Info("SnapLog 退出");
        return 0;
    }

    /// <summary>UI 线程上可恢复的异常：记日志 + 托盘气泡，程序继续跑。</summary>
    private static void ReportRecoverable(FileLogger log, AppPaths paths, Exception exception)
    {
        log.Error("界面操作失败（已跳过，程序继续运行）", exception);

        // 用气球而不是模态框：后台工具不该因为一个可恢复的错误打断用户。
        TrayBalloon.TryShow($"{exception.GetType().Name}: {exception.Message}", ToolTipIcon.Warning);
    }

    private static void ReportFatal(FileLogger log, AppPaths paths, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        log.Error("未捕获的异常", exception);
        MessageBox.Show(
            $"SnapLog 遇到未处理的错误：\n\n{exception.GetType().Name}: {exception.Message}\n\n详情见日志目录：\n{paths.LogsDirectory}",
            "SnapLog",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
