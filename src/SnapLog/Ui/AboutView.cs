using SnapLog.Configuration;
using SnapLog.Diagnostics;

namespace SnapLog.Ui;

/// <summary>关于页：软件说明、版本信息、许可证与项目地址，以及几个常用目录的入口。没有任何设置项。</summary>
internal sealed class AboutView : SettingsViewBase
{
    private const string RepositoryUrl = "https://github.com/jionghang/snap-log";

    public AboutView(SettingsContext context)
        : base(context)
    {
    }

    protected override Control BuildContent()
    {
        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 10, 12, 12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "SnapLog",
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 16f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        };

        var subtitle = new Label
        {
            Text = "屏幕活动的记录、识别、总结与归档",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10),
        };

        var info = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
        };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;
        void AddRow(string caption, string value)
        {
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            info.Controls.Add(new Label
            {
                Text = caption + "：",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 4, 4, 0),
            }, 0, row);
            info.Controls.Add(new Label { Text = value, AutoSize = true, Margin = new Padding(0, 4, 0, 0) }, 1, row);
            row++;
        }

        var description = new Label
        {
            Text = "本机运行的屏幕活动记录工具：跟随前台窗口切换截图 → 离线识别文字 → 存入本机数据库 → "
                   + "按需或定时调用大模型生成工作总结 → 可把总结写入飞书多维表格。",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 0, 0, 10),
        };

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        AddRow("版本", version?.ToString() ?? "未知");
        AddRow("运行时", $".NET {Environment.Version}");
        AddRow("系统", Environment.OSVersion.VersionString);
        AddRow("许可证", "MIT");

        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 0, 0) };

        links.Controls.Add(NewButton("打开项目主页", () => OpenUrl(RepositoryUrl)));

        links.Controls.Add(NewButton("打开数据目录", () => OpenPath(Paths.DataDirectory)));
        links.Controls.Add(NewButton("打开日志目录", () => OpenPath(Paths.LogsDirectory)));
        links.Controls.Add(NewButton("打开总结目录", () => OpenPath(Paths.SummariesDirectory)));
        links.Controls.Add(NewButton("打开截图目录", () => OpenPath(Paths.ResolveImageDirectory(Options.Capture.ImageDirectory))));

        var note = new Label
        {
            Text = "抓取、文字识别、数据库与截图均保存在本机。"
                   + "仅在主动执行“生成总结”“写入飞书”，或已开启相应定时与自动开关时，内容才会发送至外部服务。"
                   + Environment.NewLine + Environment.NewLine
                   + $"源代码与问题反馈：{RepositoryUrl}",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 14, 0, 0),
        };

        root.Controls.Add(title);
        root.Controls.Add(subtitle);
        root.Controls.Add(description);
        root.Controls.Add(info);
        root.Controls.Add(links);
        root.Controls.Add(note);
        return root;
    }

    protected override void LoadFromOptions()
    {
        // 这一页没有可编辑项。
    }

    protected override void WriteToOptions()
    {
        // 这一页没有可编辑项。
    }

    private static Button NewButton(string text, Action onClick)
    {
        var button = new Button { Text = text, Width = 130, Height = 30, Margin = new Padding(0, 0, 8, 0) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"打开链接失败：{ex.Message}");
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"打开路径失败：{ex.Message}");
        }
    }
}
