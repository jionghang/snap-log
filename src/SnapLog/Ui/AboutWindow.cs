using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SnapLog.Interop;

namespace SnapLog.Ui;

/// <summary>关于：版本、做什么用的、项目主页。排错信息见命令行 --diagnose。</summary>
internal sealed class AboutWindow : Window
{
    /// <summary>项目主页。README 里写的是同一个地址。</summary>
    private const string GitHubUrl = "github.com/jionghang/snap-log";

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

        // 关于窗口首先是"这软件是干什么的"，目录只是附带信息。
        var intro = new StackPanel { Spacing = 6 };
        var versionLine = new TextBlock
        {
            Text = "SnapLog " + version?.Major + "." + version?.Minor + "." + version?.Build,
            Classes = { "headline" },
        };

        intro.Children.Add(versionLine);
        intro.Children.Add(Ui.Hint("截图与识别文字仅保存在本机；每天在设定时间由大模型将当天的屏幕活动整理成一份总结，再推送到飞书多维表格。"));

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(intro);
        panel.Children.Add(Ui.Inline(8, Ui.Label("项目主页"),
            Ui.Link(GitHubUrl, () => MainWindow.OpenPath("https://" + GitHubUrl))));

        Content = new Border
        {
            Padding = new Thickness(22, 20, 22, 20),
            Child = panel,
        };

        _ = services;
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
        content.Children.Add(Ui.Hint("尚未配置大模型与飞书接入。记录功能不受影响，但不会自动生成每天的总结。"));
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
