using SnapLog.Configuration;
using SnapLog.Diagnostics;

namespace SnapLog.Ui;

/// <summary>关于页：版本、数据位置、常用入口。没有任何设置项，只是告诉用户这个程序在干什么。</summary>
internal sealed class AboutView : SettingsViewBase
{
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
            Text = "把屏幕活动变成可查、可总结、可推送的记录",
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

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        AddRow("版本", version?.ToString() ?? "未知");
        AddRow("运行时", $".NET {Environment.Version}");
        AddRow("系统", Environment.OSVersion.VersionString);
        AddRow("数据目录", Paths.DataDirectory);
        AddRow("数据库", Paths.ResolveDatabasePath(Options.Storage.DatabaseFileName));
        AddRow("截图目录", Paths.ResolveImageDirectory(Options.Capture.ImageDirectory));
        AddRow("日志目录", Paths.LogsDirectory);

        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 0, 0) };

        links.Controls.Add(NewButton("打开数据目录", () => OpenPath(Paths.DataDirectory)));
        links.Controls.Add(NewButton("打开日志目录", () => OpenPath(Paths.LogsDirectory)));
        links.Controls.Add(NewButton("打开总结目录", () => OpenPath(Paths.SummariesDirectory)));
        links.Controls.Add(NewButton("打开截图目录", () => OpenPath(Paths.ResolveImageDirectory(Options.Capture.ImageDirectory))));

        var note = new Label
        {
            Text = "一切都在本机：抓取、OCR、数据库、截图都存在你自己的电脑上。"
                   + "只有你主动点「生成小结」或「立即写入飞书」时，内容才会发出去。",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 16, 0, 0),
        };

        root.Controls.Add(title);
        root.Controls.Add(subtitle);
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
