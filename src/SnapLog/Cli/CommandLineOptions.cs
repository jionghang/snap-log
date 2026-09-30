namespace SnapLog.Cli;

public enum CliCommand
{
    /// <summary>启动托盘应用。</summary>
    Run,

    /// <summary>立即抓取一次后退出，用于快速验证链路。</summary>
    Once,

    /// <summary>对已有记录生成总结后退出。</summary>
    Summarize,

    /// <summary>打印环境与配置诊断信息。</summary>
    Diagnose,

    /// <summary>端到端自检：窗口信息 → 三种抓取策略 → OCR → CSV 往返。</summary>
    SelfTest,

    /// <summary>列出当前可见的顶层窗口，用于诊断和挑选排除项。</summary>
    ListWindows,

    /// <summary>把全部记录导出成 CSV。</summary>
    ExportCsv,

    /// <summary>按保留策略清理过期记录与截图。</summary>
    Cleanup,

    /// <summary>立刻把待识别（Pending）的记录批量识别掉。</summary>
    OcrPending,

    /// <summary>立刻把还没写入过的总结写进飞书多维表格。</summary>
    Publish,

    /// <summary>
    /// 界面自检：依次构造并短暂显示各窗口，报告是否抛异常。
    /// 布局代码的问题（尺寸越界、控件树循环等）只在运行时才暴露，编译通过说明不了什么。
    /// </summary>
    UiSmoke,
    /// <summary>压测：连续抓同一窗口 N 次，用来复现偶发的 GDI+ 错误。</summary>
    CaptureStress,

    Help,

    Unknown,
}

public sealed record CommandLineOptions
{
    public CliCommand Command { get; init; } = CliCommand.Run;

    /// <summary>--yes：跳过"数据会发送到外部接口"的确认。</summary>
    public bool AssumeYes { get; init; }

    public string? ConfigPath { get; init; }

    public string? DataDirectory { get; init; }

    /// <summary>--target：自检时按标题子串指定要抓的窗口，而不是用前台窗口。</summary>
    public string? TargetTitle { get; init; }

    /// <summary>--export-csv：导出目标路径，缺省则写在数据目录下。</summary>
    public string? ExportPath { get; init; }

    /// <summary>--preview：只打印将要发送给模型的内容，不真的发请求。</summary>
    public bool PreviewOnly { get; init; }

    /// <summary>--date 指定只总结这一天的记录；为空表示总结最近的记录。</summary>
    public DateOnly? Day { get; init; }

    /// <summary>--dry-run：只算清单不实际执行（推送用）。</summary>
    public bool DryRun { get; init; }

    /// <summary>--test：只验证推送配置与仓库可达，不上传（推送用）。</summary>
    public bool TestOnly { get; init; }

    /// <summary>--count：压测次数。</summary>
    public int? Count { get; init; }

    public bool SaveImage { get; init; }

    public string? Error { get; init; }

    public static CommandLineOptions Parse(string[] args)
    {
        var command = CliCommand.Run;
        var assumeYes = false;
        var saveImage = false;
        string? configPath = null;
        string? dataDirectory = null;
        string? targetTitle = null;
        string? exportPath = null;
        var previewOnly = false;
        DateOnly? day = null;
        var dryRun = false;
        int? count = null;
        var testOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--once":
                    command = CliCommand.Once;
                    break;
                case "--summarize":
                    command = CliCommand.Summarize;
                    break;
                case "--diagnose":
                    command = CliCommand.Diagnose;
                    break;
                case "--selftest":
                    command = CliCommand.SelfTest;
                    break;
                case "--windows":
                    command = CliCommand.ListWindows;
                    break;
                case "--ui-smoke":
                    command = CliCommand.UiSmoke;
                    break;
                case "--cleanup":
                    command = CliCommand.Cleanup;
                    break;
                case "--preview":
                    previewOnly = true;
                    break;
                case "--date":
                    if (++i >= args.Length || !DateOnly.TryParse(args[i], out var parsedDay))
                    {
                        return Invalid("--date 后面需要 yyyy-MM-dd 格式的日期");
                    }

                    day = parsedDay;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--test":
                    testOnly = true;
                    break;
                case "--count":
                    if (++i >= args.Length || !int.TryParse(args[i], out var parsedCount))
                    {
                        return Invalid($"--count 后面缺少数字");
                    }

                    count = parsedCount;
                    break;
                case "--ocr-pending":
                    command = CliCommand.OcrPending;
                    break;
                case "--capture-stress":
                    command = CliCommand.CaptureStress;
                    break;
                case "--publish":
                    command = CliCommand.Publish;
                    break;
                case "--export-csv":                    command = CliCommand.ExportCsv;
                    // 路径可选：紧跟的下一个参数只要不以 -- 开头就当路径。
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        exportPath = args[++i];
                    }

                    break;
                case "--run":
                    command = CliCommand.Run;
                    break;
                case "--help" or "-h" or "-?" or "/?":
                    command = CliCommand.Help;
                    break;
                case "--yes" or "-y":
                    assumeYes = true;
                    break;
                case "--save-image":
                    saveImage = true;
                    break;
                case "--config":
                    if (++i >= args.Length)
                    {
                        return Invalid($"--config 后面缺少路径");
                    }

                    configPath = args[i];
                    break;
                case "--data":
                    if (++i >= args.Length)
                    {
                        return Invalid($"--data 后面缺少路径");
                    }

                    dataDirectory = args[i];
                    break;
                case "--target":
                    if (++i >= args.Length)
                    {
                        return Invalid($"--target 后面缺少窗口标题关键字");
                    }

                    targetTitle = args[i];
                    break;
                default:
                    return Invalid($"无法识别的参数：{arg}");
            }
        }

        return new CommandLineOptions
        {
            Command = command,
            AssumeYes = assumeYes,
            ConfigPath = configPath,
            DataDirectory = dataDirectory,
            TargetTitle = targetTitle,
            ExportPath = exportPath,
            PreviewOnly = previewOnly,
            Day = day,
            DryRun = dryRun,
            Count = count,
            TestOnly = testOnly,
            SaveImage = saveImage,
        };
    }

    private static CommandLineOptions Invalid(string error) => new() { Command = CliCommand.Unknown, Error = error };

    public static string HelpText => """
        SnapLog —— 记录前台窗口活动并做 OCR，把文字落进 CSV，可按需调用大模型总结。

        用法：
          SnapLog                      启动托盘应用（默认）
          SnapLog --once [--target 关键字]
                                       立即抓取当前前台窗口一次并写库后退出；--target 可指定窗口
          SnapLog --summarize [--yes] [--preview] [--date yyyy-MM-dd]
                                       对已有记录生成总结；--yes 跳过外发确认，
                                       --preview 只打印要发送的内容而不真的发请求，
                                       --date 只总结指定日期的记录
          SnapLog --cleanup            按配置的保留天数清理过期记录与截图
          SnapLog --ocr-pending        立刻批量识别待识别（Pending）的记录
          SnapLog --publish [--dry-run|--test]
                                       把还没写入过的总结写进飞书多维表格；
                                       --dry-run 只列待写入清单与字段映射，--test 只验证权限与字段名
          SnapLog --diagnose           打印环境、配置、OCR 语言包等诊断信息
          SnapLog --selftest           端到端自检：抓取策略、OCR、CSV 读写往返
          SnapLog --windows            列出当前可见的顶层窗口（用于确定“排除的进程名”）
          SnapLog --export-csv [路径]  把全部记录导出成 CSV（缺省写到数据目录）
          SnapLog --ui-smoke           界面自检：依次构造并短暂显示各窗口，报告是否报错

        通用参数：
          --config <path>   指定配置文件（默认按 %LOCALAPPDATA%\SnapLog\appsettings.json → 程序目录\appsettings.json 的顺序查找）
          --data <dir>      覆盖数据目录，CSV/总结/日志都会写到这里
          --target <关键字> --once / --selftest：按窗口标题或进程名子串指定要抓的窗口，默认用前台窗口
          --save-image      本次抓取同时把截图存到数据目录的 images 子目录
          --yes, -y         免去需要确认的提示
          --help, -h        显示本帮助

        退出码：0 成功；1 参数或运行时错误；2 抓取/总结未完成。
        """;
}
