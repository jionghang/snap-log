using System.Diagnostics;
using System.Text;
using SnapLog.Capture;
using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;
using SnapLog.Imaging;
using SnapLog.Interop;
using SnapLog.Ocr;
using SnapLog.Storage;
using SnapLog.Summarization;
using SnapLog.Ui;

namespace SnapLog.Cli;

/// <summary>命令行模式的各条命令。GUI 启动逻辑在 Program 里，不在这里。</summary>
internal sealed class CliRunner
{
    private readonly AppOptions _options;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;
    private readonly IActivityRepository _store;
    private readonly string? _configSourcePath;

    private CliRunner(AppOptions options, AppPaths paths, FileLogger log, IActivityRepository store, string? configSourcePath)
    {
        _options = options;
        _paths = paths;
        _log = log;
        _store = store;
        _configSourcePath = configSourcePath;
    }

    public static async Task<int> RunAsync(
        CommandLineOptions cli,
        AppOptions options,
        AppPaths paths,
        FileLogger log,
        string? configSourcePath,
        IActivityRepository store,
        CancellationToken cancellationToken)
    {
        if (cli.SaveImage)
        {
            options.Capture.SaveImages = true;
        }

        var runner = new CliRunner(options, paths, log, store, configSourcePath);

        return cli.Command switch
        {
            CliCommand.Diagnose => await runner.DiagnoseAsync(configSourcePath, cancellationToken).ConfigureAwait(false),
            CliCommand.ListWindows => runner.ListWindows(),
            CliCommand.SelfTest => await runner.SelfTestAsync(cli.TargetTitle, cancellationToken).ConfigureAwait(false),
            CliCommand.Once => await runner.CaptureOnceAsync(cli.TargetTitle, cancellationToken).ConfigureAwait(false),
            CliCommand.Summarize => await runner.SummarizeAsync(cli.AssumeYes, cli.PreviewOnly, cli.Day, cancellationToken).ConfigureAwait(false),
            CliCommand.ExportCsv => await runner.ExportCsvAsync(cli.ExportPath, cancellationToken).ConfigureAwait(false),
            CliCommand.UiSmoke => runner.UiSmoke(),
            CliCommand.Cleanup => await runner.CleanupAsync(cancellationToken).ConfigureAwait(false),
            CliCommand.OcrPending => await runner.OcrPendingAsync(cancellationToken).ConfigureAwait(false),
            CliCommand.Publish => await runner.PublishAsync(cli.DryRun, cli.TestOnly, cancellationToken).ConfigureAwait(false),
            CliCommand.CaptureStress => await runner.CaptureStressAsync(cli.TargetTitle, cli.Count, cancellationToken).ConfigureAwait(false),
            _ => 1,
        };
    }

    private int ListWindows()
    {
        var windows = WindowEnumerator.ListVisibleWindows();
        Console.WriteLine($"=== 可见顶层窗口（{windows.Count} 个，按面积降序）===");
        Console.WriteLine($"{"句柄",-12} {"尺寸",-12} {"进程",-24} 标题");

        foreach (var window in windows)
        {
            var size = $"{window.Width}x{window.Height}";
            Console.WriteLine($"{window.HandleText,-12} {size,-12} {Fit(window.ProcessName, 24),-24} {window.WindowTitle}");
        }

        Console.WriteLine();
        Console.WriteLine("把某个“进程”名填进 设置 → 排除的进程名，SnapLog 就不会记录它。");
        return 0;
    }

    private async Task<int> DiagnoseAsync(string? configSourcePath, CancellationToken cancellationToken)
    {
        var report = new StringBuilder();
        report.AppendLine("=== SnapLog 诊断 ===");
        report.AppendLine($"系统版本      : {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64 位" : "32 位")})");
        report.AppendLine($"运行时        : .NET {Environment.Version}");
        report.AppendLine($"程序目录      : {AppContext.BaseDirectory}");
        report.AppendLine($"配置文件      : {configSourcePath ?? "(没有找到配置文件，使用内置默认值)"}");
        report.AppendLine($"用户级配置路径: {AppPaths.UserConfigPath}");
        report.AppendLine($"随程序配置路径: {AppPaths.ShippedConfigPath}");
        report.AppendLine($"数据目录      : {_paths.DataDirectory}");
        report.AppendLine($"数据库        : {_store.Location}");

        var count = await _store.CountAsync(cancellationToken).ConfigureAwait(false);
        var dbSize = File.Exists(_store.Location) ? new FileInfo(_store.Location).Length : 0;
        report.AppendLine($"记录总数      : {count} 条，主库 {FormatBytes(dbSize)}");

        var pending = await _store.CountPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending > 0)
        {
            report.AppendLine($"待识别        : {pending} 条（用 --ocr-pending 立刻处理）");
        }
        if (File.Exists(_paths.LegacyCsvPath))
        {
            report.AppendLine($"旧版 CSV      : 仍存在（{_paths.LegacyCsvPath}），库非空时不会重复导入");
        }

        report.AppendLine();

        report.AppendLine("--- OCR ---");
        var languages = WindowsMediaOcrEngine.AvailableLanguageTags();
        report.AppendLine($"已安装语言包  : {(languages.Count == 0 ? "(无)" : string.Join(", ", languages))}");
        report.AppendLine($"配置的引擎    : {_options.Ocr.Engine}");
        report.AppendLine($"识别方式      : {_options.Ocr.Mode}"
                          + (_options.Ocr.Mode == OcrRunMode.ScheduledBatch ? $"（每天 {_options.Ocr.BatchTimeOfDay}）" : string.Empty));
        report.AppendLine($"OCR 开关      : {(_options.Ocr.Enabled ? "开启" : "关闭")}");
        report.AppendLine($"图像尺寸上限  : OCR {WindowsMediaOcrEngine.MaxSupportedDimension}px / 截图 {_options.Capture.MaxImageDimension}px");

        using (var ocr = OcrEngineFactory.Create(_options.Ocr, _log))
        {
            report.AppendLine($"引擎可用      : {ocr.IsAvailable}");
            report.AppendLine($"引擎描述      : {ocr.Description}");
        }

        report.AppendLine();
        report.AppendLine("--- 抓取与触发 ---");
        report.AppendLine($"抓取周期      : {_options.Triggers.CaptureCycleMinutes} 分钟（同一窗口每周期一次）");
        report.AppendLine($"窗口稳定等待  : {_options.Triggers.ForegroundSettleMilliseconds} ms");
        report.AppendLine($"最小抓取间隔  : {_options.Triggers.MinSecondsBetweenCaptures} s");
        report.AppendLine($"排除进程      : {Join(_options.Triggers.ExcludedProcesses)}");
        report.AppendLine($"排除标题      : {Join(_options.Triggers.ExcludedWindowTitles)}");
        report.AppendLine($"当前前台窗口  : {DescribeForeground()}");

        report.AppendLine();
        report.AppendLine("--- 大模型总结 ---");
        report.AppendLine($"启用          : {_options.Summarization.Enabled}");
        report.AppendLine($"隐私提示已确认: {_options.Summarization.ConsentGranted}");
        report.AppendLine($"发送内容      : {SummaryPreparation.DescribeMode(_options.Summarization.PayloadMode)}");
        report.AppendLine($"失败重试      : {_options.Summarization.RetryCount} 次（起始 {_options.Summarization.RetryDelaySeconds} 秒，指数退避）");
        report.AppendLine($"模型列表      : 共 {_options.Summarization.Providers.Count} 个，启用 {_options.Summarization.Providers.Count(p => p.Enabled)} 个");
        foreach (var provider in _options.Summarization.Providers)
        {
            var state = provider.Enabled ? "启用" : "停用";
            report.AppendLine($"  [{state}] {provider.Name} · {provider.Model} @ {provider.Endpoint}"
                              + (provider.ApiKey.Length > 0 ? "（密钥写在配置里）" : string.Empty));
        }

        // 只报告"每个模型能不能拿到密钥"，绝不打印密钥本身。
        if (OpenAiCompatibleSummarizer.TryCreate(_options.Summarization, _log, out var probe, out var error))
        {
            report.AppendLine($"就绪情况      : 可用 —— {probe!.Description}");
        }
        else
        {
            report.AppendLine($"就绪情况      : 未就绪 —— {error}");
        }

        report.AppendLine();
        report.AppendLine("--- 定时任务 ---");
        // 只用来算"下次什么时候跑"，不会真的启动调度，所以传一个不执行的 runner 就够。
        var summaryRunner = new SummaryRunner(_store, _paths, _log);
        var jobs = new IScheduledJob[]
        {
            new OcrBatchJob(_store, _paths, _log, () => OcrEngineFactory.Create(_options.Ocr, _log)),
            new SummaryJob(summaryRunner),
            new FeishuPushJob(new FeishuWriter(_store, _log)),
        };
        var scheduler = new ScheduledJobsService(jobs, _paths, _log);
        foreach (var (job, enabled, next) in scheduler.DescribeSchedule(_options))
        {
            report.AppendLine($"  {(enabled ? job.DisplayName : job.DisplayName + "（未启用）"),-20} {next}");
        }

        report.AppendLine();
        report.AppendLine("--- 写入飞书多维表格 ---");
        var feishu = _options.Feishu;
        report.AppendLine($"启用          : {feishu.Enabled}");
        report.AppendLine($"App ID        : {(feishu.AppId.Length == 0 ? "(未配置)" : feishu.AppId)}");
        report.AppendLine($"app_token     : {(feishu.AppToken.Length == 0 ? "(未配置)" : feishu.AppToken)}");
        report.AppendLine($"table_id      : {(feishu.TableId.Length == 0 ? "(未配置)" : feishu.TableId)}");
        report.AppendLine($"字段映射      : {feishu.FieldMappings.Count(m => !string.IsNullOrWhiteSpace(m.FeishuField))} 条生效"
                          + $"（共 {feishu.FieldMappings.Count} 条）");
        var feishuProblem = FeishuBitablePublisher.Validate(feishu);
        report.AppendLine($"就绪情况      : {(feishuProblem is null ? "配置完整（用 --publish --test 验证权限与字段）" : feishuProblem)}");

        Console.WriteLine(report.ToString());
        _log.Info("已输出诊断信息");
        return 0;
    }

    private async Task<int> SelfTestAsync(string? targetTitle, CancellationToken cancellationToken)
    {
        var report = new StringBuilder();
        report.AppendLine("=== SnapLog 自检 ===");

        WindowSnapshot snapshot;
        var isFallback = false;

        if (!string.IsNullOrWhiteSpace(targetTitle))
        {
            var match = WindowEnumerator.Find(targetTitle);
            if (match is null)
            {
                report.AppendLine($"按标题/进程名“{targetTitle}”没有找到可见窗口，自检中止。");
                report.AppendLine("可以先跑 SnapLog --windows 看看当前有哪些窗口。");
                Console.WriteLine(report.ToString());
                return 2;
            }

            snapshot = new WindowSnapshot(match.Handle, match.ProcessName, match.WindowTitle);
            report.AppendLine($"指定抓取目标  : hwnd={match.HandleText} 进程={match.ProcessName} "
                          + $"类名={match.ClassName} 标题={match.WindowTitle}");
        }
        else
        {
            snapshot = ForegroundWindowWatcher.DescribeForegroundWindow();
            report.AppendLine($"前台窗口      : hwnd=0x{snapshot.Handle.ToInt64():X} 进程={snapshot.ProcessName} 标题={snapshot.WindowTitle}");

            if (!snapshot.IsUsable)
            {
                (snapshot, isFallback) = WindowEnumerator.FindCaptureTarget();
                report.AppendLine("说明          : 拿不到前台窗口（可能是锁屏或非交互会话），改用面积最大的可见窗口做测试");
            }
        }

        if (!snapshot.IsUsable)
        {
            report.AppendLine("找不到任何可抓取的窗口，自检中止。");
            Console.WriteLine(report.ToString());
            return 2;
        }

        if (isFallback)
        {
            report.AppendLine($"实际抓取目标  : hwnd=0x{snapshot.Handle.ToInt64():X} 进程={snapshot.ProcessName} 标题={snapshot.WindowTitle}");
        }

        // 1) 三种抓取策略各自的产出情况
        report.AppendLine();
        report.AppendLine("--- 抓取策略 ---");
        foreach (var attempt in WindowCapturer.Diagnose(snapshot.Handle))
        {
            report.AppendLine(
                $"  {attempt.Name,-12} 产出={attempt.Produced,-5} 尺寸={attempt.Width}x{attempt.Height,-6} "
                + $"采样色数={attempt.DistinctColors,-5} 纯色={attempt.Blank}"
                + (attempt.Error is null ? string.Empty : $" 错误={attempt.Error}"));
        }

        using var capture = WindowCapturer.Capture(snapshot.Handle, _options.Capture.MaxImageDimension);
        if (!capture.Success || capture.Image is null)
        {
            report.AppendLine($"最终选中的抓取方式：失败（{capture.Error}）");
            Console.WriteLine(report.ToString());
            return 2;
        }

        report.AppendLine($"最终选中      : {capture.Method}，{capture.Image.Width}x{capture.Image.Height}");
        if (capture.Error is not null)
        {
            report.AppendLine($"提示          : {capture.Error}");
        }

        // 2) OCR
        report.AppendLine();
        report.AppendLine("--- OCR ---");
        using var ocr = OcrEngineFactory.Create(_options.Ocr, _log);
        report.AppendLine($"引擎          : {ocr.Description}");

        var outcome = await ocr.RecognizeAsync(capture.Image, cancellationToken).ConfigureAwait(false);
        if (!outcome.Success)
        {
            report.AppendLine($"识别失败      : {outcome.Error}");
            Console.WriteLine(report.ToString());
            return 2;
        }

        report.AppendLine($"耗时          : {outcome.Elapsed.TotalMilliseconds:0} ms，{outcome.LineCount} 行，{outcome.Text.Length} 字");
        if (outcome.Warning is not null)
        {
            report.AppendLine($"提示          : {outcome.Warning}");
        }

        report.AppendLine("识别文字      :");
        foreach (var line in outcome.Text.Split('\n'))
        {
            report.AppendLine($"  | {line}");
        }

        // 3) 写入后立刻读回，验证多行中文文本的往返是否完整
        var record = new ActivityRecord
        {
            Timestamp = DateTime.Now,
            ProcessName = snapshot.ProcessName,
            WindowTitle = snapshot.WindowTitle,
            WindowClass = snapshot.ClassName,
            CaptureMethod = capture.Method,
            ImageWidth = capture.Image.Width,
            ImageHeight = capture.Image.Height,
            OcrMilliseconds = (long)outcome.Elapsed.TotalMilliseconds,
            OcrText = outcome.Text,
            // 和引擎里的行为保持一致：识别成功但有需要留意的点，写进“错误”列。
            Error = outcome.Warning ?? string.Empty,
            Status = outcome.Text.Length >= _options.Ocr.MinTextLength ? RecordStatus.Ok : RecordStatus.NoText,
        };

        report.AppendLine();
        report.AppendLine("--- 截图留档 ---");
        if (_options.Capture.SaveImages)
        {
            var pixelFormat = capture.Image.PixelFormat;
            var imageDirectory = _paths.ResolveImageDirectory(_options.Capture.ImageDirectory);
            var savedFull = ImageArchive.Save(
                capture.Image,
                imageDirectory,
                _options.Capture.ImageFormat,
                record.Timestamp,
                snapshot.ProcessName);

            record.ImagePath = savedFull;
            report.AppendLine($"已保存        : {savedFull}");
            report.AppendLine($"像素格式      : {pixelFormat}（不含 alpha 时存 PNG 不会整张透明）");
            report.AppendLine($"文件大小      : {new FileInfo(savedFull).Length:N0} 字节");
        }
        else
        {
            report.AppendLine("未保存（加 --save-image 可以把截图留档，便于排查 OCR 效果）");
        }

        report.AppendLine();
        report.AppendLine("--- SQLite 往返 ---");

        await _store.AppendAsync(record, cancellationToken).ConfigureAwait(false);
        var readBack = await _store.GetRecentAsync(1, cancellationToken).ConfigureAwait(false);

        report.AppendLine($"数据库        : {_store.Location}");
        var restored = readBack.Count > 0 ? readBack[^1] : null;
        if (restored is null)
        {
            report.AppendLine("读回结果      : 失败，没有读到任何记录");
            Console.WriteLine(report.ToString());
            return 2;
        }

        var textMatches = string.Equals(restored.OcrText, record.OcrText, StringComparison.Ordinal);
        report.AppendLine($"读回 id       : {restored.Id}");
        report.AppendLine($"读回标题      : {restored.WindowTitle}");
        report.AppendLine($"读回字数      : {restored.TextLength}（原 {record.TextLength}）");
        report.AppendLine($"文字完全一致  : {textMatches}");

        if (!textMatches)
        {
            report.AppendLine("读回文字      :");
            foreach (var line in restored.OcrText.Split('\n'))
            {
                report.AppendLine($"  | {line}");
            }
        }

        // 4) 筛选与分页：验证记录查看器依赖的查询能力
        var filtersOk = await VerifyQueriesAsync(report, record, cancellationToken).ConfigureAwait(false);

        // 5) CSV 导出：验证导出文件能被重新解析（列定义两边一致）
        var exportOk = await VerifyExportAsync(record, cancellationToken).ConfigureAwait(false);
        report.AppendLine($"导出校验      : {(exportOk ? "通过" : "失败")}");

        var ok = textMatches && restored.TextLength > 0 && filtersOk && exportOk;

        if (restored.TextLength == 0)
        {
            // 抓到的是没有文字的窗口（例如"前台窗口不可用"时退回最大可见窗口），
            // OCR 与关键词筛选就没被真正验证过——这不是失败，但也不能报成"通过"。
            report.AppendLine("自检提示      : 本次抓到的窗口没有文字，OCR 与关键词筛选未能验证；"
                              + "用 --target 指定一个有文字的窗口重跑即可。");
        }

        report.AppendLine();
        report.AppendLine(ok ? "自检结论      : 通过 ✅" : "自检结论      : 有异常 ⚠");

        Console.WriteLine(report.ToString());
        _log.Info($"自检完成，结论：{(ok ? "通过" : "有异常")}");
        return ok ? 0 : 2;
    }

    /// <summary>验证按进程/关键词筛选和分页，返回是否全部符合预期。</summary>
    private async Task<bool> VerifyQueriesAsync(StringBuilder report, ActivityRecord record, CancellationToken cancellationToken)
    {
        report.AppendLine();
        report.AppendLine("--- 筛选与分页 ---");

        var total = await _store.CountAsync(cancellationToken).ConfigureAwait(false);
        report.AppendLine($"记录总数      : {total}");

        var byProcess = await _store
            .QueryAsync(new ActivityQuery { ProcessName = record.ProcessName, Limit = 5 }, cancellationToken)
            .ConfigureAwait(false);
        report.AppendLine($"按进程筛选    : 进程={record.ProcessName} 命中 {byProcess.TotalCount} 条");

        // 从刚写入的文字里取一段做关键词，确保一定能命中。
        var keyword = ExtractKeyword(record.OcrText);
        var byKeyword = keyword is null
            ? null
            : await _store
                .QueryAsync(new ActivityQuery { Keyword = keyword, Limit = 5 }, cancellationToken)
                .ConfigureAwait(false);
        report.AppendLine(keyword is null
            ? "关键词筛选    : 跳过（本次没识别到可用文字）"
            : $"关键词筛选    : “{keyword}”命中 {byKeyword!.TotalCount} 条");

        // 分页：每页 1 条，翻两页，确认偏移生效且结果不重叠。
        var page1 = await _store
            .QueryAsync(new ActivityQuery { Limit = 1, Offset = 0 }, cancellationToken)
            .ConfigureAwait(false);
        var page2 = await _store
            .QueryAsync(new ActivityQuery { Limit = 1, Offset = 1 }, cancellationToken)
            .ConfigureAwait(false);

        var pagingOk = page1.Items.Count == Math.Min(1, page1.TotalCount);
        report.AppendLine($"分页          : 共 {page1.TotalCount} 条 / 每页 1 条 → {page1.PageCount} 页，"
                          + $"第 1 页 {page1.Items.Count} 条、第 2 页 {page2.Items.Count} 条");

        if (page1.Items.Count > 0 && page2.Items.Count > 0 && page1.Items[0].Id == page2.Items[0].Id)
        {
            report.AppendLine("              : 异常——两页返回了同一条");
            pagingOk = false;
        }

        var processOk = byProcess.TotalCount > 0 && byProcess.Items.Count > 0;
        var keywordOk = byKeyword is null || byKeyword.TotalCount > 0;
        return processOk && keywordOk && pagingOk;
    }

    /// <summary>导出全部记录到 CSV，再读回来比对，确认导出列和解析列一致。</summary>
    private async Task<bool> VerifyExportAsync(ActivityRecord expected, CancellationToken cancellationToken)
    {
        var exportPath = Path.Combine(_paths.DataDirectory, "selftest-export.csv");

        var exported = await CsvActivityTransfer
            .ExportAsync(_store, new ActivityQuery(), exportPath, _log, cancellationToken)
            .ConfigureAwait(false);

        if (exported == 0)
        {
            _log.Warn("导出校验失败：导出的记录数为 0");
            return false;
        }

        // 直接用导入用的读取器把文件读回来：列映射和换行处理都是同一套，
        // 能读通且找到刚写的那条，就说明"导出的文件能被重新导入"。
        var parsed = 0;
        var found = false;

        foreach (var item in CsvActivityTransfer.ReadRecords(exportPath, _log))
        {
            cancellationToken.ThrowIfCancellationRequested();
            parsed++;

            // 库里时间戳按秒存储（见 SqliteActivityStore.TimeFormat），比较时要对齐精度，
            // 否则原值的毫秒会被当成"不相等"。
            if (TruncateToSecond(item.Timestamp) == TruncateToSecond(expected.Timestamp)
                && string.Equals(item.WindowTitle, expected.WindowTitle, StringComparison.Ordinal)
                && string.Equals(item.OcrText, expected.OcrText, StringComparison.Ordinal))
            {
                found = true;
            }
        }

        _log.Info($"导出校验：写出 {exported} 条，读回 {parsed} 条，命中目标记录={found}");
        Console.WriteLine($"导出文件      : {exportPath}（写出 {exported} 条，读回 {parsed} 条，文字一致={found}）");
        return found && parsed == exported;
    }

    /// <summary>把时间对齐到存储精度（秒）。库里就是这么存的，比较时必须一致。</summary>
    private static DateTime TruncateToSecond(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Kind);

    /// <summary>从识别文字里取一段适合做关键词的片段（跳过空行和纯符号行）。</summary>
    private static string? ExtractKeyword(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 4 || line.Any(char.IsWhiteSpace))
            {
                continue;
            }

            return line.Length <= 8 ? line : line[..8];
        }

        return null;
    }

    private async Task<int> CaptureOnceAsync(string? targetTitle, CancellationToken cancellationToken)
    {
        using var ocr = OcrEngineFactory.Create(_options.Ocr, _log);
        await using var engine = new SnapLogEngine(_options, _paths, _log, ocr, _store);

        engine.StatusChanged += (_, message) => Console.WriteLine($"[状态] {message}");

        if (!string.IsNullOrWhiteSpace(targetTitle))
        {
            var match = WindowEnumerator.Find(targetTitle);
            if (match is null)
            {
                Console.WriteLine($"[结果] 按标题/进程名“{targetTitle}”没有找到可见窗口。可以先跑 SnapLog --windows 看看。");
                return 2;
            }

            engine.TargetWindowOverride = match.Handle;
            Console.WriteLine($"[状态] 指定目标：hwnd={match.HandleText} 进程={match.ProcessName} 标题={match.WindowTitle}");
        }

        var outcome = await engine.CaptureNowAsync(CaptureTrigger.Manual, cancellationToken).ConfigureAwait(false);

        if (!outcome.Captured || outcome.Record is null)
        {
            Console.WriteLine($"[结果] 没有记录：{outcome.Reason}");
            return 2;
        }

        var record = outcome.Record;
        Console.WriteLine("[结果] 已写入一条记录");
        Console.WriteLine($"  时间      : {record.Timestamp:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"  进程      : {record.ProcessName}");
        Console.WriteLine($"  窗口标题  : {record.WindowTitle}");
        Console.WriteLine($"  抓取方式  : {record.CaptureMethod}  {record.ImageWidth}x{record.ImageHeight}");
        Console.WriteLine($"  识别耗时  : {record.OcrMilliseconds} ms，{record.TextLength} 字");
        Console.WriteLine($"  数据库    : {_store.Location}");
        Console.WriteLine("  识别文字  :");
        foreach (var line in record.OcrText.Split('\n'))
        {
            Console.WriteLine($"    | {line}");
        }

        return 0;
    }

    private async Task<int> SummarizeAsync(bool assumeYes, bool previewOnly, DateOnly? cliDay, CancellationToken cancellationToken)
    {
        if (!_options.Summarization.Enabled)
        {
            Console.WriteLine("[结果] 大模型总结当前是关闭的。请在配置里把 Summarization.Enabled 改成 true 后重试。");
            return 2;
        }

        if (!_options.Summarization.ConsentGranted && !assumeYes)
        {
            var payload = SummaryPreparation.DescribeMode(_options.Summarization.PayloadMode);

            Console.WriteLine($"[确认] 生成总结会把{(cliDay is { } d ? $"{d:yyyy-MM-dd} 的记录" : "最近的活动记录")}（{payload}）发送到以下模型（按顺序使用）：");
            foreach (var provider in _options.Summarization.Providers.Where(p => p.Enabled))
            {
                Console.WriteLine($"         {provider.Name} · {provider.Model} @ {provider.Endpoint}");
            }

            Console.WriteLine("       记录可能包含隐私内容，请确认该发送链路可以接受。");
            if (_options.Summarization.PayloadMode != LlmPayloadMode.TextOnly)
            {
                Console.WriteLine("       注意：当前配置会把**截图原图**发出去，画面里的所有内容都会被上传。");
            }

            Console.WriteLine("       确认无误后加 --yes 重新执行：SnapLog --summarize --yes");
            Console.WriteLine("       想先看清要发什么：SnapLog --summarize --preview");
            return 2;
        }

        if (!_options.Summarization.ConsentGranted)
        {
            _options.Summarization.ConsentGranted = true;
            PersistConsent();
        }

        var runner = new SummaryRunner(_store, _paths, _log);

        if (previewOnly)
        {
            var (preparation, previewError) = await runner.PrepareAsync(_options, cliDay, cancellationToken).ConfigureAwait(false);
            if (preparation is null)
            {
                Console.WriteLine($"[结果] 无法预览：{previewError}");
                return 2;
            }

            Console.WriteLine(preparation.RenderForDisplay());
            Console.WriteLine("（以上只是预览，没有发出任何请求）");
            return 0;
        }

        var result = await runner.RunAsync(_options, "命令行", cliDay, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            Console.WriteLine($"[结果] 生成失败：{result.Message}");
            if (result.Preparation is not null)
            {
                Console.WriteLine();
                Console.WriteLine(result.Preparation.RenderForDisplay());
            }

            return 2;
        }

        Console.WriteLine("[结果] 总结已生成");
        Console.WriteLine($"  保存位置：{result.SavedPath}");
        Console.WriteLine();
        Console.WriteLine(result.Markdown);
        return 0;
    }

    private void PersistConsent()
    {
        // 写回加载时用的那份配置，而不是无条件写用户目录——
        // 否则用 --config 指向临时配置跑一次，会把那份配置整体克隆到用户配置位置。
        var target = AppPaths.ResolveConfigWriteTarget(_configSourcePath);
        try
        {
            OptionsStore.Save(_options, target);
            _log.Info($"已把隐私确认状态写入 {target}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"保存隐私确认状态失败：{ex.Message}");
        }
    }

    private static string DescribeForeground()
    {
        var (snapshot, isFallback) = WindowEnumerator.FindCaptureTarget();
        if (!snapshot.IsUsable)
        {
            return "(当前会话里找不到可用的窗口)";
        }

        var prefix = isFallback ? "[无前台窗口，以下为候补] " : string.Empty;
        return $"{prefix}进程={snapshot.ProcessName} 标题={snapshot.WindowTitle}";
    }

    private async Task<int> ExportCsvAsync(string? exportPath, CancellationToken cancellationToken)
    {
        var target = string.IsNullOrWhiteSpace(exportPath)
            ? Path.Combine(_paths.DataDirectory, $"activity-export-{DateTime.Now:yyyyMMdd-HHmmss}.csv")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(exportPath.Trim()));

        var count = await CsvActivityTransfer
            .ExportAsync(_store, new ActivityQuery(), target, _log, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[结果] 已导出 {count} 条记录");
        Console.WriteLine($"  文件：{target}");
        return 0;
    }

    /// <summary>
    /// 依次构造、显示、关闭每个窗口，任何异常都记下来。
    /// 目的是验证布局代码与 OnLoad 里的查询不会在运行时炸掉——这些编译期查不出来。
    ///
    /// 必须用 Application.Run 起真正的消息循环：Show() 创建句柄时会装上
    /// WindowsFormsSynchronizationContext，之后 OnLoad 里的 await 续体要靠消息循环来泵，
    /// 只调 Application.DoEvents 是泵不动的（实测会直接挂死）。
    /// </summary>
    private int UiSmoke()
    {
        Console.WriteLine("=== 界面自检 ===");
        Console.WriteLine("会依次短暂显示各窗口（每个约 1.5 秒），只做构造和加载，不修改任何数据。");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var failures = new List<string>();

        // 异步 void 的 OnLoad 里抛出的异常走这里，不接住的话进程会直接挂掉。
        Application.ThreadException += (_, e) =>
            failures.Add($"UI 线程异常：{e.Exception.GetType().Name}: {e.Exception.Message}");

        using var ocr = OcrEngineFactory.Create(_options.Ocr, _log);
        var engine = new SnapLogEngine(_options, _paths, _log, ocr, _store);
        var summaryRunner = new SummaryRunner(_store, _paths, _log);

        var settingsContext = new SettingsContext(_options, _paths, _log, _store, summaryRunner, () => true);
        ProbeForm("设置页（抓取）", () => WrapView("抓取配置", new CaptureSettingsView(settingsContext)), failures,
            CheckSettingsPage);
        ProbeForm("设置页（OCR）", () => WrapView("OCR 配置", new OcrSettingsView(settingsContext)), failures,
            CheckSettingsPage);
        ProbeForm("设置页（大模型）", () => WrapView("大模型配置", new LlmSettingsView(settingsContext)), failures,
            CheckLayoutSane);
        ProbeForm("设置页（推送）", () => WrapView("推送配置", new FeishuSettingsView(settingsContext)), failures,
            CheckLayoutSane);
        ProbeForm("设置页（关于）", () => WrapView("关于", new AboutView(settingsContext)), failures);
        ProbeForm("RecordsForm（记录查看器）", () => new RecordsForm(_options, _store, _paths, _log), failures,
            CheckDeleteButtonState);
        ProbeForm("删除确认框（有截图）", () => new DeleteRecordsDialog(3, 3), failures);
        ProbeForm("删除确认框（无截图）", () => new DeleteRecordsDialog(2, 0), failures);
        ProbeForm("SummaryHistoryForm（总结历史）", () => new SummaryHistoryForm(settingsContext), failures);

        // 字段映射窗体：带上"已知飞书字段"两种情形各构造一次（命中/不命中列名走的是不同提示分支）。
        var probeFields = new List<FeishuBitablePublisher.FeishuTableField>
        {
            new("时间", 5),
            new("工作内容", 1),
        };
        ProbeForm(
            "字段映射（新增）",
            () => new FeishuFieldMappingEditForm(new FeishuFieldMapping(), isNew: true, probeFields),
            failures,
            CheckMappingFieldPicker);
        ProbeForm(
            "字段映射（编辑已被删掉的列）",
            () => new FeishuFieldMappingEditForm(
                new FeishuFieldMapping { RecordField = "Markdown", FeishuField = "总结" },
                isNew: false,
                probeFields),
            failures);

        // 主窗口放最后：它会拦截 Close（只隐藏），需要直接结束消息循环。
        ProbeMainFormRunState(engine, summaryRunner, failures);

        engine.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("界面自检      : 通过 ✅（各窗口都能构造、显示、关闭）");
            return 0;
        }

        Console.WriteLine($"界面自检      : 有 {failures.Count} 个问题 ⚠");
        foreach (var failure in failures)
        {
            Console.WriteLine($"  - {failure}");
        }

        return 2;
    }

    private static Form WrapView(string title, Control view)
    {
        var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(860, 620),
            Size = new Size(860, 620),
            Icon = IconFactory.AppIcon,
            ShowInTaskbar = false,
        };

        view.Dock = DockStyle.Fill;
        form.Controls.Add(view);
        return form;
    }

    /// <summary>
    /// 主窗口的状态页必须和引擎的真实状态一致，且引擎在窗口开着的时候被切换时（托盘菜单就是这么切的）要跟着变。
    ///
    /// 时序刻意复刻真实启动：先建窗口 → 启动引擎 → 显示窗口。
    /// 这正是出过问题的地方：构造时引擎还没启动，启动那次状态事件因为窗口句柄还没建好被丢掉，
    /// 结果窗口一打开就显示"已暂停 / 开始记录"，点下去实际执行的是暂停。
    ///
    /// 一段消息循环里验两段：显示后先验"记录中"，再把引擎停掉验"已暂停"。
    /// 只探一次是因为 MainForm 的 Close 被重写成"只隐藏"，探针得靠 ExitThread 收尾，
    /// 而同一个线程上 ExitThread 之后再 Application.Run 会立刻返回（第二个探针根本跑不起来）。
    /// </summary>
    private void ProbeMainFormRunState(SnapLogEngine engine, SummaryRunner summaryRunner, List<string> failures)
    {
        const string label = "MainForm（状态一致性）";

        Form? form = null;
        var summary = "(未取到尺寸)";
        var runningVerdict = "(未执行)";
        var pausedVerdict = "(未执行)";

        var constructSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            form = new MainForm(_options, _paths, _log, engine, summaryRunner, _store, _configSourcePath);
            constructSw.Stop();

            // 复刻托盘启动的顺序：窗口已经存在但还没显示，此时引擎启动。
            engine.Start();

            void Record(string phase, string? problem)
            {
                if (problem is null)
                {
                    return;
                }

                failures.Add($"{label}（{phase}）：{problem}");
            }

            var step = 0;
            using var timer = new System.Windows.Forms.Timer { Interval = 700 };
            timer.Tick += (_, _) =>
            {
                step++;

                switch (step)
                {
                    // ≈1.4 秒：引擎在跑，状态页应当是"记录中"
                    case 2:
                        var runningProblem = CheckRunState(form, expectRunning: true);
                        Record("记录中", runningProblem);
                        runningVerdict = runningProblem is null ? "记录中✓" : "记录中✗";

                        // 窗口开着的时候停掉引擎：托盘菜单切换走的就是这条路径。
                        engine.StopAsync().GetAwaiter().GetResult();
                        break;

                    // ≈2.8 秒：引擎已停，状态页应当跟着变成"已暂停"，懒加载页也该被空闲预建补完了
                    case 4:
                        var pausedProblem = CheckRunState(form, expectRunning: false);
                        Record("已暂停", pausedProblem);
                        pausedVerdict = pausedProblem is null ? "已暂停✓" : "已暂停✗";

                        var pendingTabs = PendingLazyTabs(form);
                        var prebuildVerdict = pendingTabs == 0 ? "预建✓" : $"预建✗（还剩 {pendingTabs} 页）";
                        Record("空闲预建", pendingTabs == 0 ? null : $"还有 {pendingTabs} 个标签页没被预建出来");

                        timer.Stop();
                        summary = $"{form.Width}x{form.Height} 控件数={CountControls(form)} "
                                  + $"构造={constructSw.ElapsedMilliseconds} ms  {runningVerdict} {pausedVerdict} {prebuildVerdict}";
                        Application.ExitThread();
                        break;
                }
            };

            form.Shown += (_, _) => timer.Start();
            Application.Run(form);

            Console.WriteLine($"{Fit(label, 30)} 通过  {summary}");
        }
        catch (Exception ex)
        {
            failures.Add($"{label}：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"{Fit(label, 30)} 失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (form is not null)
            {
                try
                {
                    form.Dispose();
                }
                catch (Exception ex)
                {
                    failures.Add($"{label} 释放失败：{ex.Message}");
                }
            }
        }
    }

    /// <summary>把窗口构造出来、显示一会儿、再关掉。<paramref name="verify"/> 在关掉之前跑，返回非 null 表示这一项没通过。</summary>
    private static void ProbeForm(
        string label,
        Func<Form> factory,
        List<string> failures,
        Func<Form, string?>? verify = null)
    {
        Form? form = null;
        var summary = "(未取到尺寸)";

        var constructSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            form = factory();
            constructSw.Stop();

            using var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                var verdict = "通过";
                try
                {
                    if (verify?.Invoke(form) is { } problem)
                    {
                        failures.Add($"{label}：{problem}");
                        verdict = $"失败：{problem}";
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{label}：校验时抛出 {ex.GetType().Name}: {ex.Message}");
                    verdict = $"失败：{ex.GetType().Name}";
                }

                summary = $"{form.Width}x{form.Height} 可见={form.Visible} 控件数={CountControls(form)} 构造={constructSw.ElapsedMilliseconds} ms";

                if (ContentHeightOf(form) is { } contentHeight)
                {
                    summary += $" 内容高={contentHeight}";
                }
                if (verdict != "通过")
                {
                    summary += $"  {verdict}";
                }

                // MainForm 的 Close 被重写成"只隐藏"，得直接结束消息循环。
                if (form is MainForm)
                {
                    Application.ExitThread();
                }
                else
                {
                    form.Close();
                }
            };

            form.Shown += (_, _) => timer.Start();

            // 真正的消息循环：OnLoad 里的异步查询才能正常续跑。
            Application.Run(form);

            Console.WriteLine($"{Fit(label, 30)} 通过  {summary}");
        }
        catch (Exception ex)
        {
            failures.Add($"{label}：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"{Fit(label, 30)} 失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (form is not null)
            {
                try
                {
                    form.Dispose();
                }
                catch (Exception ex)
                {
                    failures.Add($"{label} 释放失败：{ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// 状态页里"是否在记录"的两处显示必须和引擎的真实状态一致。
    /// 引擎在主窗口显示之前就启动了，那次状态事件会因为句柄还没建好被丢掉，
    /// 只靠构造时算一次的话，窗口打开时显示"已暂停 / 开始记录"，而点下去执行的是暂停。
    /// </summary>
    private static string? CheckRunState(Form form, bool expectRunning)
    {
        var expectedState = expectRunning ? "运行中" : "已暂停";
        var expectedButton = expectRunning ? "暂停记录" : "开始记录";

        var texts = CollectTexts(form).ToList();

        if (!texts.Contains(expectedState))
        {
            return $"状态值不是“{expectedState}”（当前文本：{string.Join(" / ", texts.Where(t => t.Length <= 6))}）";
        }

        if (!texts.Contains(expectedButton))
        {
            return $"按钮文字不是“{expectedButton}”";
        }

        return null;
    }

    /// <summary>
    /// 从"飞书字段名"的下拉里选一列之后，输入框里必须只剩列名本身。
    /// 下拉项曾经显示成"列名（类型）"，如果那个显示文本被带进映射，飞书那边就会报字段名不匹配。
    /// </summary>
    private static string? CheckMappingFieldPicker(Form form)
    {
        var combo = FindControls<ComboBox>(form)
            .FirstOrDefault(c => (c.Items.Count > 0 ? c.Items[0]?.ToString() ?? string.Empty : string.Empty)
                .StartsWith("时间", StringComparison.Ordinal));

        if (combo is null)
        {
            return "找不到“飞书字段名”的下拉框";
        }

        combo.SelectedIndex = 0;

        var text = (combo.Text ?? string.Empty).Trim();
        return text == "时间"
            ? null
            : $"从下拉选中列名后输入框里是“{text}”，应当只剩列名“时间”（类型说明不该写进映射）";
    }

    /// <summary>
    /// "删除"的可用状态必须跟着选中行数走：没有选中就不能点（否则可能误删），
    /// 选中了就该能点。两个方向都验一遍。
    /// </summary>
    private static string? CheckDeleteButtonState(Form form)
    {
        var grid = FindControls<DataGridView>(form).FirstOrDefault();
        var delete = FindControls<Button>(form).FirstOrDefault(b => b.Text == "删除");

        if (grid is null || delete is null)
        {
            return "找不到记录表格或“删除”按钮";
        }

        if (grid.SelectedRows.Count > 0 && !delete.Enabled)
        {
            return $"选中 {grid.SelectedRows.Count} 行时“删除”却是灰的";
        }

        grid.ClearSelection();
        var disabledAfterClear = !delete.Enabled;

        if (grid.Rows.Count > 0)
        {
            grid.Rows[0].Selected = true;
            grid.CurrentCell = grid.Rows[0].Cells[0];
        }

        if (!disabledAfterClear)
        {
            return "清空选中后“删除”仍是可点的";
        }

        return delete.Enabled || grid.Rows.Count == 0 ? null : "重新选中后“删除”没有恢复可点";
    }

    /// <summary>设置页的通用检查：布局是否正常 + 保存按钮的灰/亮 + 滚轮不改值。</summary>
    private static string? CheckSettingsPage(Form form) =>
        CheckLayoutSane(form) ?? CheckSaveButtonState(form) ?? CheckWheelDoesNotChangeValue(form);

    /// <summary>还没被空闲预建出来的懒加载标签页数量（Tag 里还留着工厂的就是没建的）。</summary>
    private static int PendingLazyTabs(Form form)
    {
        var tabs = FindControls<TabControl>(form).FirstOrDefault();
        return tabs is null ? 0 : tabs.TabPages.Cast<TabPage>().Count(p => p.Tag is Func<Control>);
    }

    /// <summary>设置页里那块内容容器（Dock=Top 的那个）的高度；布局没跑过的话它会停在默认尺寸。</summary>
    private static int? ContentHeightOf(Form form)
    {
        var view = FindControls<SettingsViewBase>(form).FirstOrDefault();
        return view?.Controls.Cast<Control>().FirstOrDefault(c => c.Dock == DockStyle.Top)?.Height;
    }

    /// <summary>设置页内容高度的下限。建页时挂起了各容器的布局，忘了恢复就会塌成默认尺寸，这里兜住这种情况。</summary>
    private const int MinimumContentHeight = 200;

    /// <summary>
    /// 布局自检：可见控件要有实际尺寸，整页内容高度也要像个正常的页面。
    /// 空标题列里那些空白自适应标签是故意做成零尺寸的，跳过它们。
    /// </summary>
    private static string? CheckLayoutSane(Form form)
    {
        var collapsed = FindControls<Control>(form)
            .Where(c => c is Button or CheckBox or ComboBox or NumericUpDown or TextBox or ListBox
                        || (c is Label { AutoSize: false }))
            .Where(c => c.Visible && (c.Width <= 0 || c.Height <= 0))
            .Select(c => $"{c.GetType().Name}“{c.Text}”")
            .Take(3)
            .ToList();

        if (collapsed.Count > 0)
        {
            return $"有控件没有尺寸（布局没恢复？）：{string.Join("、", collapsed)}";
        }

        var height = ContentHeightOf(form);
        if (height is > 0 and < MinimumContentHeight)
        {
            return $"页面内容高度只有 {height}px，布局没有展开";
        }

        return null;
    }

    /// <summary>
    /// "保存设置/放弃修改"在没有任何改动时应当是灰的，改一处之后才亮起来。
    /// 这里在抓取页上翻一下复选框来验证这条链路。
    /// </summary>
    private static string? CheckSaveButtonState(Form form)
    {
        var save = FindControls<Button>(form).FirstOrDefault(b => b.Text == "保存设置");
        if (save is null)
        {
            return "找不到“保存设置”按钮";
        }

        if (save.Enabled)
        {
            return "没有任何改动时“保存设置”就是可点的，应当置灰";
        }

        var discard = FindControls<Button>(form).FirstOrDefault(b => b.Text == "放弃修改");
        if (discard is { Enabled: true })
        {
            return "没有任何改动时“放弃修改”应当置灰";
        }

        var checkBox = FindControls<CheckBox>(form).FirstOrDefault();
        if (checkBox is null)
        {
            return "这一页没有复选框，无法制造一次改动";
        }

        checkBox.Checked = !checkBox.Checked;

        if (!save.Enabled)
        {
            return "改动之后“保存设置”仍然置灰";
        }

        // 改回去，别把状态留给后面的检查。
        checkBox.Checked = !checkBox.Checked;
        return null;
    }

    /// <summary>
    /// 设置页里的下拉框/数值框必须都用滚轮保护版本——原版在有焦点时会被滚轮直接改值，
    /// 而"点过控件之后接着滚页面"是最容易发生的误操作。这里先查类型（可靠），
    /// 再在有焦点的前提下模拟一次滚轮确认值没变（拿不到焦点就跳过，不当失败）。
    /// </summary>
    private static string? CheckWheelDoesNotChangeValue(Form form)
    {
        var plain = FindControls<ComboBox>(form)
            .Where(c => c is not ScrollSafeComboBox)
            .Cast<Control>()
            .Concat(FindControls<NumericUpDown>(form).Where(n => n is not ScrollSafeNumericUpDown))
            .ToList();

        if (plain.Count > 0)
        {
            return $"有 {plain.Count} 个下拉框/数值框没走滚轮保护版本（{plain[0].GetType().Name}），滚轮会直接改值";
        }

        var guarded = FindControls<ScrollSafeComboBox>(form).Cast<Control>()
            .Concat(FindControls<ScrollSafeNumericUpDown>(form))
            .Where(c => c.Enabled && c.Focus() && c.Focused)
            .ToList();

        foreach (var control in guarded)
        {
            var before = ReadValue(control);
            InvokeWheel(control, -120);
            InvokeWheel(control, 120);

            var after = ReadValue(control);
            if (before != after)
            {
                return $"滚轮把 {control.GetType().Name} 的值从 {before} 改成了 {after}";
            }
        }

        return null;
    }

    private static string ReadValue(Control control) => control switch
    {
        ComboBox combo => combo.SelectedIndex.ToString(),
        NumericUpDown number => number.Value.ToString(),
        _ => "(未知)",
    };

    private static void InvokeWheel(Control control, int delta)
    {
        var wheel = control.GetType().GetMethod(
            "OnMouseWheel",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        wheel?.Invoke(control, [new MouseEventArgs(MouseButtons.None, 0, 1, 1, delta)]);
    }

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in FindControls<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<string> CollectTexts(Control root)
    {
        if (!string.IsNullOrWhiteSpace(root.Text))
        {
            yield return root.Text;
        }

        foreach (Control child in root.Controls)
        {
            foreach (var text in CollectTexts(child))
            {
                yield return text;
            }
        }
    }

    private static int CountControls(Control root)
    {
        var total = 0;
        var stack = new Stack<Control>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            foreach (Control child in stack.Pop().Controls)
            {
                total++;
                stack.Push(child);
            }
        }

        return total;
    }

    /// <summary>按保留策略清理，并如实报告删了什么。</summary>
    private async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("=== 保留策略清理 ===");
        Console.WriteLine($"  记录保留：{Describe(_options.Storage.RecordRetentionDays)}");
        Console.WriteLine($"  截图保留：{Describe(_options.Capture.ImageRetentionDays)}");
        Console.WriteLine($"  日志保留：{Describe(_options.Logging.RetentionDays)}");

        var service = new RetentionService(_store, _paths, _log);
        var report = await service.RunAsync(_options, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"  截图目录：{report.ImageDirectory}");
        Console.WriteLine();
        Console.WriteLine($"[结果] {report.Describe()}");
        return 0;

        static string Describe(int days) => days <= 0 ? "永久保留" : $"{days} 天";
    }

    /// <summary>立刻把待识别的记录批量识别掉。</summary>
    private async Task<int> OcrPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await _store.CountPendingAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"待识别记录：{pending} 条");
        Console.WriteLine($"识别方式：{_options.Ocr.Mode}");

        if (pending == 0)
        {
            if (_options.Ocr.Mode != OcrRunMode.ScheduledBatch)
            {
                Console.WriteLine("提示：当前是“实时识别”，正常不会积压待识别记录。");
            }

            return 0;
        }

        var job = new OcrBatchJob(_store, _paths, _log, () => OcrEngineFactory.Create(_options.Ocr, _log));
        var result = await job.RunAsync(_options, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[结果] {(result.Success ? "完成" : "有失败")}：{result.Message}");
        return result.Success ? 0 : 2;
    }

    /// <summary>把大模型生成的总结写进飞书多维表格。</summary>
    private async Task<int> PublishAsync(bool dryRun, bool testOnly, CancellationToken cancellationToken)
    {
        var feishu = _options.Feishu;

        if (testOnly)
        {
            Console.WriteLine($"验证飞书配置：app_id={feishu.AppId} app_token={feishu.AppToken} table_id={feishu.TableId}");
            var probe = await new FeishuBitablePublisher(feishu, _log).TestAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine($"[结果] {(probe.Success ? "通过" : "未通过")}：");
            foreach (var line in probe.Message.Split(Environment.NewLine))
            {
                Console.WriteLine($"  {line}");
            }

            return probe.Success ? 0 : 2;
        }

        var problem = FeishuBitablePublisher.Validate(feishu);
        if (problem is not null && !dryRun)
        {
            Console.WriteLine($"[结果] 配置不完整：{problem}");
            return 2;
        }

        if (dryRun)
        {
            var from = FeishuWriter.GetEarliestRunTime(feishu);
            var pending = await _store.GetPendingPushRunsAsync(from, 200, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"目标表格：app_token={feishu.AppToken} table_id={feishu.TableId}");
            Console.WriteLine($"字段映射（{feishu.FieldMappings.Count(m => !string.IsNullOrWhiteSpace(m.FeishuField))} 条生效）：");
            foreach (var mapping in feishu.FieldMappings)
            {
                Console.WriteLine($"  {mapping}");
            }

            Console.WriteLine();
            Console.WriteLine($"待写入的总结（{from:yyyy-MM-dd} 之后生成、还没写进飞书的）：{pending.Count} 条");
            foreach (var run in pending.Take(10))
            {
                Console.WriteLine($"  #{run.Id} {run.StartedAt:yyyy-MM-dd HH:mm} [{run.Trigger}] {run.Provider} - {run.Preview}");
            }

            if (pending.Count > 10)
            {
                Console.WriteLine($"  …另有 {pending.Count - 10} 条");
            }

            Console.WriteLine();
            Console.WriteLine($"[结果] 按每批 {feishu.BatchSize} 条提交");
            Console.WriteLine("       --dry-run：只列清单，没有发起任何写入");

            if (problem is not null)
            {
                Console.WriteLine($"       另外，正式写入会被拦下：{problem}");
            }

            return 0;
        }

        var writer = new FeishuWriter(_store, _log);
        var result = await writer.WritePendingAsync(_options, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"[结果] {(result.Success ? "成功" : "失败")}：{result.Message}");
        return result.Success ? 0 : 2;
    }

    /// <summary>
    /// 压测：连续抓同一窗口 N 次，每次立刻释放。
    /// 目的是复现偶发的 GDI+ 错误——那种错误只有连着抓几百次才可能冒出来。
    /// </summary>
    private async Task<int> CaptureStressAsync(string? targetTitle, int? count, CancellationToken cancellationToken)
    {
        var rounds = count ?? 200;
        var snapshot = WindowSnapshot.Empty;

        if (string.IsNullOrWhiteSpace(targetTitle))
        {
            (snapshot, _) = WindowEnumerator.FindCaptureTarget();
        }
        else if (WindowEnumerator.Find(targetTitle) is { } match)
        {
            snapshot = new WindowSnapshot(match.Handle, match.ProcessName, match.WindowTitle, match.ClassName);
        }

        if (!snapshot.IsUsable)
        {
            Console.WriteLine("找不到可抓取的窗口。先跑 SnapLog --windows 看看，或用 --target 指定。");
            return 2;
        }

        Console.WriteLine($"目标：进程={snapshot.ProcessName} 类名={snapshot.ClassName} 标题={snapshot.WindowTitle}");
        Console.WriteLine($"连续抓 {rounds} 次（不识别、不写库、不存图，只测截图本身）");

        var failures = 0;
        var total = 0L;
        var peak = 0L;
        var start = Environment.TickCount64;

        for (var i = 1; i <= rounds; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var capture = WindowCapturer.Capture(snapshot.Handle, _options.Capture.MaxImageDimension);
            total += capture.Image is null ? 0 : (long)capture.Image.Width * capture.Image.Height * 4;

            if (!capture.Success)
            {
                failures++;
            }

            peak = Math.Max(peak, Process.GetCurrentProcess().PrivateMemorySize64 / 1024 / 1024);

            if (i % 20 == 0 || i == rounds)
            {
                Console.WriteLine($"  {i,5}/{rounds}  失败 {failures}  峰值内存 {peak} MB");
            }
        }

        var elapsed = (Environment.TickCount64 - start) / 1000.0;
        Console.WriteLine();
        Console.WriteLine($"[结果] 共 {rounds} 次，失败 {failures} 次，耗时 {elapsed:0.#} 秒（每次 {elapsed * 1000 / rounds:0.#} ms）");
        Console.WriteLine($"       峰值内存 {peak} MB，累计拷出 {total / 1024 / 1024} MB 像素");
        Console.WriteLine(failures == 0 ? "       全部成功，没复现 GDI+ 错误。" : "       有失败，见上方日志。");
        return failures == 0 ? 0 : 2;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
    private static string Join(string[] values) =>
        values.Length == 0 ? "(无)" : string.Join(" | ", values);

    /// <summary>按显示宽度截断，避免把控制台的列冲乱。</summary>
    private static string Fit(string value, int width) =>
        value.Length <= width ? value : value[..Math.Max(1, width - 1)] + "…";
}
