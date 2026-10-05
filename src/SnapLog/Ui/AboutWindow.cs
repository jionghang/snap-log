using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using SnapLog.Configuration;
using SnapLog.Interop;

namespace SnapLog.Ui;

/// <summary>关于：版本、位置、当前状态。诊断问题的第一步信息都在这。</summary>
internal sealed class AboutWindow : Window
{
    /// <summary>项目主页。README 里写的是同一个地址。</summary>
    private const string GitHubUrl = "github.com/jionghang/snap-log";

    private readonly string _diagnostics;

    public AboutWindow(AppServices services)
    {
        Title = "关于 SnapLog";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 660;
        CanResize = false;
        Icon = IconFactory.WindowIcon;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var version = typeof(AboutWindow).Assembly.GetName().Version;
        var options = services.Options;

        // 关于窗口首先是"这软件是干什么的"，目录只是附带信息。
        var intro = new StackPanel { Spacing = 6 };
        var versionLine = new TextBlock
        {
            Text = "SnapLog " + version?.Major + "." + version?.Minor + "." + version?.Build,
            Classes = { "headline" },
        };

        intro.Children.Add(Ui.Inline(8, versionLine, Ui.Tip(
            "截图与识别文字仅保存在本机；每天在设定时间才发送到配置的模型接口（默认仅发送文字）。")));
        intro.Children.Add(Ui.Hint("每天在设定时间由大模型将当天的屏幕活动整理成一份总结，再推送到飞书多维表格。"));

        var rows = new List<(string Label, string Value)>
        {
            ("数据目录", services.Paths.DataDirectory),
            ("日志目录", services.Paths.LogsDirectory),
        };

        // 这些只在排错时要看，界面上不铺开；复制诊断信息时会一起带上。
        var diagnostics = new (string Label, string Value)[]
        {
            ("版本", "SnapLog " + version?.Major + "." + version?.Minor + "." + version?.Build),
            ("运行状态", services.Engine.IsRunning ? "正在记录" : "已暂停"),
            ("开机自启动", AutoStart.IsEnabled() ? "已开启" : "未开启"),
            ("文字识别", DescribeOcr(options)),
            ("大模型总结", DescribeSummary(options)),
            ("飞书推送", options.Feishu.Enabled ? $"每天 {AsClock(options.Feishu.ScheduleTimeOfDay)}" : "未启用"),
            ("数据库", services.Store.Location),
            ("配置状态", string.IsNullOrWhiteSpace(services.StartupWarning) ? "正常" : services.StartupWarning),
            ("运行时", $".NET {Environment.Version}　{Environment.OSVersion.Version}　{(Environment.Is64BitOperatingSystem ? "64 位" : "32 位")}"),
            ("程序路径", Environment.ProcessPath ?? "(取不到)"),
        };

        _diagnostics = string.Join(
            Environment.NewLine,
            diagnostics.Concat(rows).Select(row => $"{row.Label}：{row.Value}"));

        // 关于窗口只讲"这是什么、去哪找它"；目录、版本这些排错信息都在复制诊断信息里。
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(intro);
        panel.Children.Add(Ui.Inline(8, Ui.Label("项目主页"),
            Ui.Link(GitHubUrl, () => MainWindow.OpenPath("https://" + GitHubUrl))));
        panel.Children.Add(Ui.Divider());
        panel.Children.Add(Ui.Hint("数据目录、日志目录与运行状态均包含在复制诊断信息中，排错时一并提供。"));

        var buttons = Ui.ButtonRow(CopyDiagnosticsButton());

        Content = new Border
        {
            Padding = new Thickness(22, 20, 22, 20),
            Child = new StackPanel { Spacing = 16, Children = { panel, buttons } },
        };
    }

    /// <summary>配置里可能写成 "2:30"；界面上统一显示成 02:30。</summary>
    internal static string AsClock(string value) =>
        TimeOnly.TryParse(value, out var parsed) ? $"{parsed.Hour:00}:{parsed.Minute:00}" : value;

    private static string DescribeOcr(AppOptions options) => options.Ocr.Engine switch
    {
        OcrEngineKind.PaddleOcr => options.Ocr.Mode == OcrRunMode.ScheduledBatch
            ? "离线 PaddleOCR · 定时批量"
            : "离线 PaddleOCR · 实时",
        OcrEngineKind.WindowsMedia => "系统内置 OCR",
        _ => "已关闭",
    };

    private static string DescribeSummary(AppOptions options)
    {
        if (!options.Summarization.Enabled)
        {
            return "未启用";
        }

        var enabled = options.Summarization.Providers.Count(provider => provider.Enabled);
        return $"{enabled} 个模型"
               + (options.Summarization.ScheduleEnabled ? $" · 每天 {AsClock(options.Summarization.ScheduleTimeOfDay)}" : " · 已关闭定时");
    }

    private Button CopyDiagnosticsButton()
    {
        var button = Ui.Secondary("复制诊断信息", () => Task.CompletedTask);
        ToolTip.SetTip(button, "出现问题时，将复制出来的内容提供给维护人员。");
        button.Click += async (_, _) =>
        {
            await CopyDiagnosticsAsync();
            button.Content = "已复制";
        };

        return button;
    }

    private async Task CopyDiagnosticsAsync()
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(_diagnostics);
        }
    }
}

/// <summary>首次运行说明。1.x 弹的是 WinForms 的 MessageBox，2.0 用自己样式的窗口。</summary>
internal sealed class FirstRunWindow : Window
{
    public FirstRunWindow(Action? openSettings = null)
    {
        Title = "SnapLog 首次运行";
        Width = 580;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Icon = IconFactory.WindowIcon;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "已在后台开始记录", Classes = { "section" } });
        content.Children.Add(Ui.Hint("尚未配置大模型和飞书。记录功能不受影响，但每天不会自动生成总结。"));
        content.Children.Add(Ui.Hint("平时仅在本机记录；每天在设定时间会将当天的内容发送到配置的模型接口并推送到飞书（默认仅发送文字）。"));

        var goSettings = Ui.Primary("去设置", () =>
        {
            Close();
            openSettings?.Invoke();
            return Task.CompletedTask;
        });

        var later = Ui.Secondary("稍后设置", () =>
        {
            Close();
            return Task.CompletedTask;
        });

        var buttons = Ui.ButtonRow(goSettings, later);
        buttons.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        content.Children.Add(buttons);

        // 键盘：Enter 走默认按钮（去设置），Esc 等于"稍后设置"。
        Opened += (_, _) => goSettings.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        Content = new Border { Padding = new Thickness(22, 20, 22, 20), Child = content };
    }
}
