using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;
using SnapLog.Ocr;
using SnapLog.Storage;
using SnapLog.Ui;

namespace SnapLog.Cli;

/// <summary>
/// 界面自检与截图。2.0 的界面是 Avalonia，所以不再靠"显示窗口再截屏"：
/// 走 Avalonia 的 headless 平台 + Skia 渲染，离屏拿到真实像素，
/// 不弹窗口、不抢焦点、不受别的窗口遮挡影响。
///
/// 这两个命令是开发期的验收工具：改完界面跑一遍 --ui-shots，逐张看排版和文案。
/// </summary>
internal static class UiProbes
{
    private static bool _platformReady;

    /// <summary>headless 平台只初始化一次；同一个进程里跑多个命令也不会重复初始化。</summary>
    private static void EnsurePlatform()
    {
        if (_platformReady)
        {
            return;
        }

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .LogToTrace()
            .SetupWithoutStarting();

        _platformReady = true;
    }

    // ---------------------------------------------------------------- --ui-shots

    public static int Shots(
        AppOptions options,
        AppPaths paths,
        FileLogger log,
        IActivityRepository store,
        string? configSourcePath,
        string? directory)
    {
        Console.WriteLine("=== 界面截图（Avalonia headless 渲染）===");

        var output = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(paths.DataDirectory, "shots")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));

        Console.WriteLine($"输出目录：{output}");

        try
        {
            EnsurePlatform();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  headless 平台初始化失败：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        var services = BuildServices(options, paths, log, store, configSourcePath);

        // 真实运行时引擎是开着的，截图也该拍"正在记录"的样子——
        // 否则拍出来是"已暂停"，跟首次运行提示里说的对不上。
        services.Engine.Start();
        Pump(300);

        var shots = new List<(string Label, string Path, string Size)>();

        Shoot(shots, output, "01-home", "概览", () => new HomePage(services), 1060, 720);
        Shoot(shots, output, "02-records", "抓取记录", () => new RecordsPage(services), 1060, 720, SelectFirstRow);
        Shoot(shots, output, "03-summaries", "总结记录", () => new SummariesPage(services), 1060, 720, SelectFirstRow);
        Shoot(shots, output, "04-settings", "设置", () => new SettingsPage(services), 1060, 720);
        Shoot(shots, output, "05-settings-advanced", "设置（进阶展开）", () => new SettingsPage(services), 1060, 900, ExpandFirstExpander);
        Shoot(shots, output, "06-window", "主窗口", () => new MainWindow(services), 1060, 720);
        Shoot(shots, output, "07-tray-menu", "托盘右键菜单", () => new TrayMenuWindow(services) { CloseOnDeactivate = false }, 248, 240);
        Shoot(shots, output, "08-record-detail", "记录详情（独立窗口）", () =>
        {
            var first = services.Store.QueryAsync(new Storage.ActivityQuery { Limit = 1 }, CancellationToken.None)
                .GetAwaiter().GetResult().Items.FirstOrDefault();
            var detail = new RecordDetailWindow(services);
            detail.Opened += (_, _) =>
            {
                if (first is not null)
                {
                    _ = detail.LoadAsync(first.Id);
                }
            };
            return detail;
        }, 720, 580);
        Shoot(shots, output, "09-summary-detail", "总结正文（独立窗口）", () =>
        {
            var run = services.Store.GetSummaryRunsAsync(1, CancellationToken.None).GetAwaiter().GetResult().FirstOrDefault();
            var window = new SummaryDetailWindow();
            window.Opened += (_, _) =>
            {
                if (run is not null)
                {
                    window.Render(services, run);
                }
            };
            return window;
        }, 760, 620);
        Shoot(shots, output, "10-about", "关于", () => new AboutWindow(services), 640, 700);
        Shoot(shots, output, "08-first-run", "首次运行", () => new FirstRunWindow(), 580, 460);
        Shoot(shots, output, "10-confirm", "确认框", () => new MessageWindow(
            "删除这条记录？",
            "记录 #128（09-15 10:24 · chrome）将被删除，关联的截图文件一并删除。"
            + Environment.NewLine + Environment.NewLine + "这个操作不可撤销。",
            "删除", "取消", danger: true), 470, 320);
        Shoot(shots, output, "11-narrow", "主窗口（窄）", () => new MainWindow(services), 900, 620);

        services.Engine.StopAsync().GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine($"共 {shots.Count} 张：");
        foreach (var (label, path, size) in shots)
        {
            Console.WriteLine($"  {Fit(label, 16)} {size,-12} {path}");
        }

        return shots.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// 把托盘菜单真的显示在屏幕右下角（托盘位置）停留几秒。
    ///
    /// 为什么需要它：Windows 10 的隐藏图标浮出层里，那个托盘图标用脚本点不中
    /// （UIA 报的是屏幕外假坐标，也不支持 UIA 动作），所以“菜单长什么样、弹在哪”没法靠点击去核。
    /// 这里绕开点击，把同一个菜单窗口摆到托盘位置——位置用的是和真实弹出时同一套算法，
    /// 于是可以在真机上截图核对外观与位置，也能用真坐标点它（它是普通窗口，不是浮出层里的元素）。
    /// </summary>
    public static int MenuPreview(
        AppOptions options,
        AppPaths paths,
        FileLogger log,
        IActivityRepository store,
        string? configSourcePath,
        int seconds = 8)
    {
        Console.WriteLine("=== 托盘菜单预览 ===");
        Console.WriteLine("菜单会停在屏幕右下角 " + seconds + " 秒，可截图或用真坐标点击。");

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .AfterSetup(_ =>
            {
                // 带上桌面生命周期：这样点"退出"才会真的结束进程（预览也能验退出路径）。
                var desktop = Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

                var services = BuildServices(options, paths, log, store, configSourcePath, desktop);

                try
                {
                    // 直接把菜单摆到托盘角上：这台机器上脚本移不动真实光标，只能指定坐标。
                    Interop.NativeMethods.SetCursorPos(1640, 1060);
                }
                catch (Exception ex)
                {
                    log.Warn("挪动鼠标失败（不影响预览）：" + ex.Message);
                }

                var menu = new TrayMenuWindow(services) { CloseOnDeactivate = false };
                menu.ShowNearTray(new PixelPoint(1640, 1044));


                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    menu.Close();
                    Environment.Exit(0);
                };
                timer.Start();
            })
            .StartWithClassicDesktopLifetime([]);
    }

    private static void Shoot(
        List<(string Label, string Path, string Size)> shots,
        string directory,
        string name,
        string label,
        Func<Control> factory,
        double width,
        double height,
        Action<Window>? arrange = null)
    {
        try
        {
            var root = factory();
            var window = root as Window ?? new Window { Content = root };
            window.Width = width;

            // 自己按内容定高的窗口（提示框、首次运行）不要硬塞高度，否则照片里一半是空白。
            if (window.SizeToContent != SizeToContent.Height)
            {
                window.Height = height;
            }
            window.Show();

            // 页面构造里带着异步查询（记录、总结、预览），泵几轮让它落到最终状态再拍。
            Pump(700);

            // 有些页面只有"选了某一项"之后才有内容（详情、预览），先摆到那个状态再拍。
            if (arrange is not null)
            {
                arrange(window);
                Pump(600);
            }

            var frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                Console.WriteLine($"  {Fit(label, 16)} 截图失败：渲染帧为空");
                window.Close();
                return;
            }

            using (frame)
            {
                var path = Save(frame, directory, name);
                shots.Add((label, path, $"{frame.PixelSize.Width}x{frame.PixelSize.Height}"));
            }

            window.Close();
            Pump(60);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {Fit(label, 16)} 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Save(Bitmap image, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.png");
        image.Save(path, new PngBitmapEncoderOptions());
        return path;
    }

    // ---------------------------------------------------------------- --ui-smoke

    public static int Smoke(
        AppOptions options,
        AppPaths paths,
        FileLogger log,
        IActivityRepository store,
        string? configSourcePath)
    {
        Console.WriteLine("=== 界面自检 ===");
        Console.WriteLine("离屏构造每个页面并强制布局，检查有没有运行时异常；在临时沙盒里运行，不写真实数据。");

        var failures = new List<string>();

        try
        {
            EnsurePlatform();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"界面自检      : 失败 ⚠ headless 平台起不来：{ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        var services = BuildServices(options, paths, log, store, configSourcePath);

        Probe("概览", () => new HomePage(services), failures);
        Probe("抓取记录", () => new RecordsPage(services), failures);
        Probe("总结记录", () => new SummariesPage(services), failures);
        Probe("设置", () => new SettingsPage(services), failures);
        Probe("主窗口", () => new MainWindow(services), failures);
        Probe("关于", () => new AboutWindow(services), failures);
        Probe("首次运行", () => new FirstRunWindow(), failures);

        ProbeTips(services, failures);
        ProbeDialogs(failures);

        ProbeRunState(services, failures);
        ProbeExitPath(services, failures);
        ProbeRetentionScope(paths, log, failures);
        ProbeScheduleRules(services, failures);
        ProbeTrayMenu(services, failures);
        Tour(services);
        CheckAutoStartWiring();

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("界面自检      : 通过 ✅（各页面都能构造、布局、渲染）");
            return 0;
        }

        Console.WriteLine($"界面自检      : 有 {failures.Count} 个问题 ⚠");
        foreach (var failure in failures)
        {
            Console.WriteLine($"  - {failure}");
        }

        return 2;
    }

    private static void Probe(string label, Func<Control> factory, List<string> failures)
    {
        try
        {
            var root = factory();
            var window = root as Window ?? new Window { Content = root, Width = 1060, Height = 720 };
            window.Show();
            Pump(300);

            // 布局阶段的问题（NaN 尺寸、约束冲突）不会抛异常，只会让控件长成 0×0，
            // 所以这里顺手量一遍：整页高度为 0 基本就说明布局塌了。
            var content = window.Content as Control;
            var size = content?.Bounds.Size ?? window.ClientSize;
            window.Close();
            Pump(40);

            var verdict = size.Width <= 0 || size.Height <= 0
                ? $"（警告：布局尺寸为 {size.Width:0}×{size.Height:0}）"
                : string.Empty;

            if (verdict.Length > 0)
            {
                failures.Add($"{label}：{verdict}");
            }

            Console.WriteLine($"{Fit(label, 14)} 通过   {size.Width:0}×{size.Height:0}");
        }
        catch (Exception ex)
        {
            failures.Add($"{label}：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"{Fit(label, 14)} 失败：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    /// <summary>
    /// 问号提示校验：界面上的每个"?"都必须挂着非空的提示文字。
    /// 提示是"界面少写字"的交换条件——问号要是空的，信息就真丢了。
    /// </summary>
    private static void ProbeTips(AppServices services, List<string> failures)
    {
        var pages = new (string Label, Func<Control> Factory)[]
        {
            ("概览", () => new HomePage(services)),
            ("抓取记录", () => new RecordsPage(services)),
            ("总结记录", () => new SummariesPage(services)),
            ("设置", () => new SettingsPage(services)),
            ("关于", () => new AboutWindow(services)),
        };

        var total = 0;

        foreach (var (label, factory) in pages)
        {
            try
            {
                var root = factory();
                var window = root as Window ?? new Window { Content = root, Width = 1060, Height = 720 };
                window.Show();

                // 折叠区里的提示也要数到，先展开。
                if (window.GetVisualDescendants().OfType<Expander>().FirstOrDefault() is { } expander)
                {
                    expander.IsExpanded = true;
                }

                Pump(250);

                var count = 0;
                var empty = 0;
                var samples = new List<string>();

                foreach (var border in CollectTips(window))
                {
                    count++;
                    if (ToolTip.GetTip(border) is TextBlock { Text: { Length: > 0 } text })
                    {
                        if (samples.Count < 2)
                        {
                            samples.Add(text.Length > 20 ? text[..20] + "…" : text);
                        }
                    }
                    else
                    {
                        empty++;
                    }
                }

                window.Close();
                Pump(40);

                total += count;
                if (empty > 0)
                {
                    failures.Add($"{label}：有 {empty} 个问号没有提示文字");
                }

                var sample = samples.Count > 0 ? "   " + string.Join(" / ", samples) : string.Empty;
                Console.WriteLine($"{Fit(label + " 提示", 14)} {count} 个 ✓{sample}");
            }
            catch (Exception ex)
            {
                failures.Add($"{label} 问号提示：{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (total == 0)
        {
            failures.Add("界面上一个问号提示都没有（说明文字被删干净，或提示构件没接上）");
        }
    }

    /// <summary>收集界面上的问号提示（带 "tip" 样式的 Border）。</summary>
    private static List<Border> CollectTips(Visual root)
    {
        var tips = new List<Border>();
        var stack = new Stack<Visual>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current is Border { Classes: var classes } border && classes.Contains("tip"))
            {
                tips.Add(border);
            }

            foreach (var child in current.GetVisualChildren())
            {
                stack.Push(child);
            }
        }

        return tips;
    }

    /// <summary>
    /// 对话框键盘约定：Esc 必须能取消，Enter 必须落在安全按钮上。
    /// 这两条用眼睛看不出来，按错了却是"手一快把记录删了"，所以让自检盯着。
    /// </summary>
    private static void ProbeDialogs(List<string> failures)
    {
        VerifyDialog(failures, "危险框 Esc",
            new MessageWindow("删除？", "自检用，不会真的删。", "删除", "取消", danger: true), Key.Escape, expectConfirmed: false);
        VerifyDialog(failures, "危险框 Enter",
            new MessageWindow("删除？", "自检用，不会真的删。", "删除", "取消", danger: true), Key.Enter, expectConfirmed: false);
        VerifyDialog(failures, "普通框 Enter",
            new MessageWindow("保存？", "自检用。", "保存", "取消"), Key.Enter, expectConfirmed: true);
        VerifyDialog(failures, "提示框 Enter",
            new MessageWindow("已复制", "自检用。", "知道了", null), Key.Enter, expectConfirmed: true);
    }

    private static void VerifyDialog(
        List<string> failures, string label, MessageWindow dialog, Key key, bool expectConfirmed)
    {
        try
        {
            dialog.Show();
            Pump(120);

            dialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
            Pump(120);

            var closed = !dialog.IsVisible;
            var confirmed = dialog.ConfirmedForProbe;

            if (dialog.IsVisible)
            {
                dialog.Close();
            }

            Pump(40);

            if (!closed)
            {
                failures.Add($"{label}：按键后对话框没有关闭");
            }
            else if (confirmed != expectConfirmed)
            {
                failures.Add($"{label}：期望{(expectConfirmed ? "确认" : "不确认")}，实际{(confirmed ? "确认" : "不确认")}");
            }
            else
            {
                Console.WriteLine($"{Fit(label, 14)} 通过   {(expectConfirmed ? "Enter 确认" : "按键不确认")}");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{label}：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 删掉自检留下的临时库。SQLite 的连接池会握住文件句柄，直接删会报“被另一个进程占用”；
    /// 先清池，删不掉再等一拍重试——留着这些文件既占地方，也会被下一次自检扫到。
    /// </summary>
    private static void TryDeleteTempDatabase(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(120);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(120);
            }
        }
    }

    /// <summary>把上一次自检可能残留的临时库一起收掉。</summary>
    private static void SweepStaleTempDatabases(AppPaths paths)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(paths.DataDirectory, "selftest-retention-*.db"))
            {
                TryDeleteTempDatabase(stale);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 扫不掉就算了，不影响本次自检。
        }
    }

    /// <summary>
    /// 清理不误删待识别记录。保留策略是按时间删的，但"还没识别"的记录删掉就永远补不上了
    /// （截图还在，任务却再也轮不到它）。这里用一个临时库造两条老记录来验：一条已识别、一条待识别，
    /// 清理之后只该剩下待识别那条。
    /// </summary>
    private static void ProbeRetentionScope(AppPaths paths, FileLogger log, List<string> failures)
    {
        const string label = "清理不误删待识别";
        var dbPath = Path.Combine(paths.DataDirectory, "selftest-retention-" + Guid.NewGuid().ToString("N") + ".db");

        var verdict = "(未执行)";

        try
        {
            SweepStaleTempDatabases(paths);

            var store = new SqliteActivityStore(dbPath, log);
            store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

            var old = DateTime.Now.AddDays(-10);

            store.AppendAsync(new ActivityRecord
            {
                Timestamp = old,
                ProcessName = "selftest",
                WindowTitle = "已经识别过的老记录",
                Status = RecordStatus.Ok,
            }, CancellationToken.None).GetAwaiter().GetResult();

            store.AppendAsync(new ActivityRecord
            {
                Timestamp = old,
                ProcessName = "selftest",
                WindowTitle = "还没识别过的老记录",
                Status = RecordStatus.Pending,
            }, CancellationToken.None).GetAwaiter().GetResult();

            var deleted = store
                .DeleteOlderThanAsync(DateTime.Now.AddDays(-1), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var left = store.CountAsync(CancellationToken.None).GetAwaiter().GetResult();

            var ok = deleted == 1 && left == 1;

            if (!ok)
            {
                failures.Add(label + "：删了 " + deleted + " 条、剩 " + left + " 条（期望删 1 剩 1）");
            }

            verdict = ok ? "通过" : "失败";
            Console.WriteLine($"{Fit(label, 22)} {verdict}   删 {deleted} 条、保留 {left} 条");

            store.DisposeAsync().AsTask().GetAwaiter().GetResult();
            TryDeleteTempDatabase(dbPath);
        }
        catch (Exception ex)
        {
            failures.Add(label + "：" + ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine($"{Fit(label, 22)} 失败：{ex.Message}");

            try
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// 调度语义。两条都踩过坑，所以固定下来：
    /// ① 记录暂停时一个任务都不该跑（界面上承诺过，而且暂停时段的文字不该外发）；
    /// ② 判定口径是"今天这一份跑了没有"——早上开机补跑补的是昨天那份，
    ///    不能因此把当晚的准点执行吃掉（否则日报从此每天晚一天）。
    /// </summary>
    private static void ProbeScheduleRules(AppServices services, List<string> failures)
    {
        const string label = "调度语义";

        try
        {
            var job = new FakeJob();
            var jobs = new List<IScheduledJob> { job };
            var options = services.Options;

            var evening = DateTime.Today.AddHours(23);
            var morning = DateTime.Today.AddHours(9);

            var paused = ScheduledJobsService.SelectDueJobs(new AppState(), options, jobs, isRecording: false, evening).Count;
            var fresh = ScheduledJobsService.SelectDueJobs(new AppState(), options, jobs, isRecording: true, evening).Count;

            var caughtUp = new AppState();
            caughtUp.JobLastRunLocal[job.Key] = DateTime.Today.AddHours(9);
            var afterCatchUp = ScheduledJobsService.SelectDueJobs(caughtUp, options, jobs, true, evening).Count;

            var onTime = new AppState();
            onTime.JobLastRunLocal[job.Key] = DateTime.Today.AddHours(22).AddMinutes(5);
            var afterOnTime = ScheduledJobsService.SelectDueJobs(onTime, options, jobs, true, evening).Count;

            var quiet = new AppState();
            quiet.JobLastRunLocal[job.Key] = DateTime.Today.AddDays(-1).AddHours(22).AddMinutes(5);
            var quietMorning = ScheduledJobsService.SelectDueJobs(quiet, options, jobs, true, morning).Count;

            var ok = paused == 0 && fresh == 1 && afterCatchUp == 1 && afterOnTime == 0 && quietMorning == 0;

            if (!ok)
            {
                failures.Add(label + "：暂停时" + paused + "（期望 0）、首次" + fresh + "（期望 1）、补跑后当晚"
                             + afterCatchUp + "（期望 1）、准点跑过" + afterOnTime + "（期望 0）、无事早晨"
                             + quietMorning + "（期望 0）");
            }

            Console.WriteLine($"{Fit(label, 22)} {(ok ? "通过" : "失败")}   暂停不跑✓ 补跑不吃当晚✓ 跑过不再跑✓");
        }
        catch (Exception ex)
        {
            failures.Add(label + "：" + ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine($"{Fit(label, 22)} 失败：{ex.Message}");
        }
    }

    /// <summary>自检用的假任务：固定 22:00、永远启用，只为验调度判定。</summary>
    private sealed class FakeJob : IScheduledJob
    {
        public string Key => "selftest-fake";

        public string DisplayName => "自检任务";

        public bool IsEnabled(AppOptions options) => true;

        public TimeOnly? GetScheduledTime(AppOptions options) => new TimeOnly(22, 0);

        public Task<JobRunResult> RunAsync(AppOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(JobRunResult.Skipped("自检不真正执行"));
    }

    /// <summary>
    /// 托盘这一块。Windows 11 把图标折叠进隐藏图标浮出层，合成点击在里面不可靠，
    /// 所以这些行为改成在自检里直接驱动真窗口来验：
    /// 菜单内容和样式（自绘的白底圆角，不是系统灰菜单）、位置算法（贴托盘、不越界）、
    /// 点"打开 SnapLog"能把收起的窗口唤回来、点"退出 SnapLog"会走退出路径。
    /// </summary>
    private static void ProbeTrayMenu(AppServices services, List<string> failures)
    {
        const string label = "托盘菜单";

        try
        {
            var shell = new MainWindow(services);
            services.Main = shell;
            shell.Show();
            Pump(250);
            shell.Hide();
            Pump(150);

            // ① 内容与样式
            var menu = new TrayMenuWindow(services) { CloseOnDeactivate = false };
            menu.Show();
            Pump(350);

            var texts = CollectTexts(menu);
            var wanted = new[] { "打开 SnapLog", "打开数据目录", "退出 SnapLog" };
            var missing = wanted.Where(text => !texts.Any(t => t.Contains(text, StringComparison.Ordinal))).ToList();
            var hasStateLine = texts.Any(t => t is "正在记录" or "已暂停");

            // 自绘的证据：菜单里有一块圆角卡片（系统菜单是 Win32 的 #32768，没有这种控件树）
            var card = menu.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.CornerRadius.TopLeft >= 8 && b.Padding.Left > 0);

            menu.Close();
            Pump(150);

            // ② 位置算法：贴着鼠标往左上展开，且不越界
            var cursor = new PixelPoint(1668, 1066);
            var size = new PixelSize(248, 190);
            var area = new PixelRect(0, 0, 1920, 1080);
            var pos = TrayMenuWindow.ComputePosition(cursor, size, area);
            // 菜单右下角锚在鼠标上：右边缘允许比鼠标多出 16px（贴托盘的那点重合），
            // 底边必须在鼠标上方，整体不能跑出工作区。
            var adjacent = pos.X + size.Width <= cursor.X + 16
                           && pos.Y + size.Height <= cursor.Y
                           && pos.X >= area.X && pos.Y >= area.Y
                           && pos.X + size.Width <= area.Right && pos.Y + size.Height <= area.Bottom;

            var corner = TrayMenuWindow.ComputePosition(new PixelPoint(4, 4), size, area);
            var clamped = corner.X >= area.X && corner.Y >= area.Y;

            // ③ 点"打开 SnapLog"把收起的窗口唤回来
            var menu2 = new TrayMenuWindow(services) { CloseOnDeactivate = false };
            menu2.Show();
            Pump(300);
            var openItem = menu2.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Content is string s && s == "打开 SnapLog");
            openItem?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(400);
            var recalled = shell.IsVisible;

            // ④ 点"退出 SnapLog"要走退出路径（退出前放行主窗口关闭，这是"点了退出窗口还在"的防线）
            shell.AllowClose = false;
            var menu3 = new TrayMenuWindow(services) { CloseOnDeactivate = false };
            menu3.Show();
            Pump(300);
            var quitItem = menu3.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Content is string s && s == "退出 SnapLog");
            quitItem?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(300);
            var quits = shell.AllowClose;

            menu3.Close();
            shell.AllowClose = true;
            shell.Close();
            Pump(150);

            var ok = missing.Count == 0 && hasStateLine && card is not null && adjacent && clamped && recalled && quits;

            if (!ok)
            {
                failures.Add(label + "："
                    + (missing.Count > 0 ? "缺菜单项 " + string.Join("/", missing) + "；" : string.Empty)
                    + (hasStateLine ? string.Empty : "没有状态行；")
                    + (card is null ? "菜单不是白底圆角自绘；" : string.Empty)
                    + (adjacent ? string.Empty : "菜单位置没贴着托盘或越界；")
                    + (clamped ? string.Empty : "越界时没被夹回工作区；")
                    + (recalled ? string.Empty : "点打开没有唤回窗口；")
                    + (quits ? string.Empty : "点退出没有走退出路径"));
            }

            Console.WriteLine($"{Fit(label, 22)} {(ok ? "通过" : "失败")}   4 项✓ 白底圆角✓ 贴托盘✓ 唤回窗口{(recalled ? "✓" : "✗")} 退出路径{(quits ? "✓" : "✗")}");
        }
        catch (Exception ex)
        {
            failures.Add(label + "：" + ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine($"{Fit(label, 22)} 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 退出路径：托盘菜单和设置页的"退出"都走 ExitApplication。
    /// 它必须先放行主窗口的关闭，否则 Shutdown 会被窗口的 OnClosing 拦下来——
    /// 表现就是"点了退出，窗口还在"（这个 bug 真出现过一次）。
    /// </summary>
    private static void ProbeExitPath(AppServices services, List<string> failures)
    {
        const string label = "退出路径";

        try
        {
            var shell = new MainWindow(services);
            services.Main = shell;

            if (shell.AllowClose)
            {
                failures.Add(label + "：主窗口一开始就允许关闭（关闭按钮应该先收起窗口）");
                Console.WriteLine($"{Fit(label, 14)} 失败：初始状态就不拦关闭");
                return;
            }

            services.ExitApplication();

            var ok = shell.AllowClose;
            if (!ok)
            {
                failures.Add(label + "：退出没有放行主窗口关闭，Shutdown 会被拦下来");
            }

            Console.WriteLine($"{Fit(label, 14)} {(ok ? "通过" : "失败")}   退出前放行窗口关闭{(ok ? "✓" : "✗")}");
            shell.Close();
        }
        catch (Exception ex)
        {
            failures.Add(label + "：" + ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine($"{Fit(label, 14)} 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 交互试跑：把每个页面上的开关、勾选框、折叠区、按钮挨个点一遍，
    /// 报告点了什么、状态变成了什么。跑之前请用 --config 指向一份临时配置，
    /// 因为点开关会写配置（默认配置路径是用户的真实配置）。
    /// 跳过三件"点下去会真发网络请求"的按钮，以及"退出"。
    /// </summary>
    private static void Tour(AppServices services)
    {
        Console.WriteLine();
        Console.WriteLine("=== 交互试跑（逐个点击控件）===");

        (string Label, Func<Control> Factory)[] pages =
        [
            ("概览", () => new HomePage(services)),
            ("抓取记录", () => new RecordsPage(services)),
            ("总结记录", () => new SummariesPage(services)),
            ("设置", () => new SettingsPage(services)),
        ];

        foreach (var (label, factory) in pages)
        {
            Control page;

            try
            {
                page = factory();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label}: 构造失败 {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            var window = new Window { Content = page, Width = 1060, Height = 900 };
            window.Show();
            Pump(400);
            Console.WriteLine($"[{label}]");

            foreach (var control in window.GetVisualDescendants().ToArray())
            {
                try
                {
                    switch (control)
                    {
                        case ToggleSwitch toggle when !IsAutoStart(toggle):
                            var before = toggle.IsChecked == true;
                            toggle.IsChecked = !before;
                            Pump(120);
                            Console.WriteLine($"  开关 {DescribeToggle(toggle)} : {(before ? "开" : "关")} → {(toggle.IsChecked == true ? "开" : "关")}");
                            break;

                        case CheckBox check:
                            var wasOn = check.IsChecked == true;
                            check.IsChecked = !wasOn;
                            Pump(120);
                            Console.WriteLine($"  勾选 {Text(check.Content)} : {(wasOn ? "开" : "关")} → {(check.IsChecked == true ? "开" : "关")}");
                            break;

                        case Expander expander:
                            expander.IsExpanded = !expander.IsExpanded;
                            Pump(200);
                            Console.WriteLine($"  折叠区 {Text(expander.Header)} : {(expander.IsExpanded ? "展开" : "收起")}");
                            break;

                        case Button button when button.TemplatedParent is null && button.Content is string text && !SkipClick(text):
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Pump(250);
                            Console.WriteLine($"  点按钮 {Text(button.Content)}");
                            break;

                        case Button button when button.TemplatedParent is null && button.Content is string text:
                            Console.WriteLine($"  （跳过有副作用的按钮 {text}）");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  !! {control.GetType().Name} 操作报错：{ex.GetType().Name}: {ex.Message}");
                }
            }

            // 列表页的主要交互就是点一行看详情（会打开唯一的详情窗口）。
            if (window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault() is { ItemCount: > 0 } list)
            {
                list.SelectedIndex = 0;
                Pump(400);
                Console.WriteLine("  选中列表第一行（应打开/刷新记录详情窗口）");
            }

            window.Close();
            Pump(120);
        }

        Console.WriteLine();
    }

    /// <summary>开机自启开关会写注册表（有外部副作用），试跑只报告不点。</summary>
    private static bool IsAutoStart(ToggleSwitch toggle) =>
        FindLabel(toggle).Contains("开机自动启动", StringComparison.Ordinal);

    /// <summary>这三件点下去会真的发网络请求（用真实配置里的密钥），"退出"会结束进程，都不点。</summary>
    private static bool SkipClick(string text) =>
        text.Contains("测试连接", StringComparison.Ordinal)
        || text.Contains("立即写入飞书", StringComparison.Ordinal)
        || text.Contains("自动匹配", StringComparison.Ordinal)
        || text.Contains("退出", StringComparison.Ordinal);

    private static string DescribeToggle(ToggleSwitch toggle)
    {
        var label = FindLabel(toggle);
        return label.Length > 0 ? label : "开关";
    }

    /// <summary>顺着父链找最近的文字标签（字段行的标签、同一个 StackPanel 里的说明）。</summary>
    private static string FindLabel(Control control)
    {
        var current = control.Parent;

        for (var depth = 0; current is not null && depth < 3; depth++)
        {
            foreach (var sibling in (current as Panel)?.Children ?? (IEnumerable<Control>)[])
            {
                if (sibling is TextBlock text && !string.IsNullOrWhiteSpace(text.Text))
                {
                    return text.Text;
                }
            }

            current = current.Parent;
        }

        return string.Empty;
    }

    private static string Text(object? value) =>
        value is null ? "（无文字）"
        : value is TextBlock block ? (block.Text ?? "（无文字）")
        : value.ToString() ?? "（无文字）";

    /// <summary>
    /// 状态一致性：窗口开着的时候引擎被切换（托盘菜单就是这么切的），
    /// 概览页必须跟着变。这正是一版出过问题的地方——构造时引擎还没启动，
    /// 那次状态事件被丢掉，于是窗口一打开就显示"已暂停"，点下去实际执行的是暂停。
    /// </summary>
    private static void ProbeRunState(AppServices services, List<string> failures)
    {
        const string label = "状态一致性";

        var page = new HomePage(services);
        var window = new Window { Content = page, Width = 1060, Height = 720 };

        try
        {
            window.Show();
            Pump(200);

            services.Engine.Start();
            Pump(400);
            var running = CollectTexts(window);
            var runningOk = running.Any(text => text.Contains("正在记录", StringComparison.Ordinal));
            Record(runningOk ? null : "引擎在跑，概览页没有显示「正在记录」");

            services.Engine.StopAsync().GetAwaiter().GetResult();
            Pump(400);
            var paused = CollectTexts(window);
            var pausedOk = paused.Any(text => text.Contains("已暂停", StringComparison.Ordinal));
            Record(pausedOk ? null : "引擎已停，概览页没有显示「已暂停」");

            Console.WriteLine($"{Fit(label, 14)} {(runningOk && pausedOk ? "通过" : "失败")}   记录中✓ 已暂停{(pausedOk ? "✓" : "✗")}");
        }
        catch (Exception ex)
        {
            Record($"{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"{Fit(label, 14)} 失败：{ex.Message}");
        }
        finally
        {
            window.Close();
            Pump(60);
        }

        void Record(string? problem)
        {
            if (problem is not null)
            {
                failures.Add($"{label}：{problem}");
            }
        }
    }

    /// <summary>开机自启写的是 HKCU 注册表：这里只验读取链路通，不真的改用户的启动项。</summary>
    private static void CheckAutoStartWiring()
    {
        try
        {
            var enabled = Interop.AutoStart.IsEnabled();
            Console.WriteLine($"{Fit("开机自启读取", 14)} 通过   当前{(enabled ? "已开启" : "未开启")}，路径 {Interop.AutoStart.ExecutablePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Fit("开机自启读取", 14)} 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 公共部分

    /// <summary>
    /// 自检/截图都只需要"能构造界面"的依赖：引擎、总结器、调度器建好但不启动，
    /// 所以这两个命令不会真的去抓屏或发网络请求。
    /// </summary>
    private static AppServices BuildServices(
        AppOptions options,
        AppPaths paths,
        FileLogger log,
        IActivityRepository store,
        string? configSourcePath,
        Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime? desktop = null)
    {
        var engine = new SnapLogEngine(options, paths, log, OcrEngineFactory.Create(options.Ocr, log), store);
        var summaryRunner = new SummaryRunner(store, paths, log);
        // 用真实的任务列表：调度器不会在自检里启动，但"下次运行"的文字要靠它算出来。
        var scheduler = new ScheduledJobsService(
            [
                new OcrBatchJob(store, paths, log, () => OcrEngineFactory.Create(options.Ocr, log)),
                new SummaryJob(summaryRunner, store),
                new FeishuPushJob(new FeishuWriter(store, log)),
            ],
            paths,
            log,
            configSourcePath);
        return new AppServices(options, paths, log, store, engine, summaryRunner, scheduler, configSourcePath, desktop);
    }

    /// <summary>跑几轮 dispatcher：让布局、渲染和 await 续体都落地。</summary>
    private static void Pump(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;

        do
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        while (Environment.TickCount64 < deadline);

        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> CollectTexts(Visual root)
    {
        var texts = new List<string>();
        var stack = new Stack<Visual>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current is TextBlock block)
            {
                texts.Add(block.Text ?? string.Empty);
            }

            foreach (var child in current.GetVisualChildren())
            {
                stack.Push(child);
            }
        }

        return texts;
    }

    /// <summary>展开第一个折叠区（设置页的"进阶设置"）。</summary>
    private static void ExpandFirstExpander(Window window)
    {
        var expander = window.GetVisualDescendants().OfType<Expander>().FirstOrDefault();
        if (expander is not null)
        {
            expander.IsExpanded = true;
        }

        // 展开后内容在设置页最下面，得滚到底才拍得到（等布局完成再滚）。
        if (window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroller)
        {
            Dispatcher.UIThread.Post(scroller.ScrollToEnd, DispatcherPriority.Background);
        }
    }

    /// <summary>选中列表第一行——详情区、总结正文都是选中之后才有内容。</summary>
    private static void SelectFirstRow(Window window)
    {
        var list = window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
        if (list is { ItemCount: > 0 })
        {
            list.SelectedIndex = 0;
        }
    }

    private static string Fit(string value, int width) =>
        value.Length >= width ? value : value + new string(' ', width - value.Length);
}
