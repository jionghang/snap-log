using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>抓取配置页：抓取时机（延时与周期）、截图保存与保留。</summary>
internal sealed class CaptureSettingsView : SettingsViewBase
{
    private CheckBox _saveImages = null!;
    private TextBox _imageDirectory = null!;
    private NumericUpDown _imageRetentionDays = null!;
    private NumericUpDown _recordRetentionDays = null!;
    private NumericUpDown _captureCycleMinutes = null!;
    private NumericUpDown _settleMilliseconds = null!;

    public CaptureSettingsView(SettingsContext context)
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
            Padding = new Padding(4, 4, 4, 8),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ---- 抓取时机 ----
        var trigger = NewSection("抓取时机");

        AddRow(trigger, string.Empty, NewHint(
            "抓取由前台窗口切换驱动：窗口切换后等待下面的时长，仍在前台才截图。"));

        var settleRow = NewRow();
        _settleMilliseconds = new NumericUpDown { Minimum = 0, Maximum = 30_000, Increment = 100, Width = 90 };
        settleRow.Controls.Add(_settleMilliseconds);
        settleRow.Controls.Add(NewHint("毫秒（默认 5000）。窗口首次出现后等待此时长，仍在前台才截图"));
        AddRow(trigger, "延时截图时长", settleRow);

        var cycleRow = NewRow();
        _captureCycleMinutes = new NumericUpDown { Minimum = 1, Maximum = 24 * 60, Width = 90 };
        cycleRow.Controls.Add(_captureCycleMinutes);
        cycleRow.Controls.Add(NewHint("分钟（默认 10）。同一窗口在每个周期内仅记录一次"));
        AddRow(trigger, "抓取周期", cycleRow);

        // ---- 截图与记录 ----
        var retention = NewSection("截图与记录");

        _saveImages = new CheckBox
        {
            Text = "保存截图文件，记录可追溯到当时的画面",
            AutoSize = true,
            Margin = new Padding(3, 6, 0, 0),
        };
        _saveImages.CheckedChanged += (_, _) => UpdateEnabledState();
        AddRow(retention, "保存截图", _saveImages);

        var directoryRow = NewRow();
        _imageDirectory = new TextBox { Width = 300, PlaceholderText = "留空则保存到数据目录的 images 子目录" };
        directoryRow.Controls.Add(_imageDirectory);

        var browse = new Button { Text = "浏览…", Width = 70, Height = 26, Margin = new Padding(6, 1, 0, 0) };
        browse.Click += (_, _) => BrowseImageDirectory();
        directoryRow.Controls.Add(browse);

        var open = new Button { Text = "打开", Width = 56, Height = 26, Margin = new Padding(6, 1, 0, 0) };
        open.Click += (_, _) => OpenDirectory(_imageDirectory.Text);
        directoryRow.Controls.Add(open);
        AddRow(retention, "截图目录", directoryRow);

        var imageRetentionRow = NewRow();
        _imageRetentionDays = new NumericUpDown { Minimum = 0, Maximum = 3650, Width = 90 };
        imageRetentionRow.Controls.Add(_imageRetentionDays);
        imageRetentionRow.Controls.Add(NewHint("天。超期删除截图文件，0 表示永久保留"));
        AddRow(retention, "截图保留", imageRetentionRow);

        var recordRetentionRow = NewRow();
        _recordRetentionDays = new NumericUpDown { Minimum = 0, Maximum = 3650, Width = 90 };
        recordRetentionRow.Controls.Add(_recordRetentionDays);
        recordRetentionRow.Controls.Add(NewHint("天。超期删除数据库记录，0 表示永久保留"));
        AddRow(retention, "记录保留", recordRetentionRow);

        AddRow(retention, string.Empty, NewHint(
            "定时识别模式必须保存截图，该模式下本开关锁定为开启。"));

        root.Controls.Add(trigger);
        root.Controls.Add(retention);
        return root;
    }

    protected override void LoadFromOptions()
    {
        var capture = Options.Capture;
        var triggers = Options.Triggers;
        var storage = Options.Storage;

        _settleMilliseconds.Value = Math.Clamp(triggers.ForegroundSettleMilliseconds, (int)_settleMilliseconds.Minimum, (int)_settleMilliseconds.Maximum);
        _captureCycleMinutes.Value = Math.Clamp(triggers.CaptureCycleMinutes, (int)_captureCycleMinutes.Minimum, (int)_captureCycleMinutes.Maximum);

        _saveImages.Checked = capture.SaveImages;
        _imageDirectory.Text = capture.ImageDirectory;
        _imageRetentionDays.Value = Math.Clamp(capture.ImageRetentionDays, 0, (int)_imageRetentionDays.Maximum);
        _recordRetentionDays.Value = Math.Clamp(storage.RecordRetentionDays, 0, (int)_recordRetentionDays.Maximum);

        UpdateEnabledState();
    }

    protected override void WriteToOptions()
    {
        var triggers = Options.Triggers;
        var capture = Options.Capture;
        var storage = Options.Storage;

        triggers.ForegroundSettleMilliseconds = (int)_settleMilliseconds.Value;
        triggers.CaptureCycleMinutes = (int)_captureCycleMinutes.Value;

        capture.SaveImages = _saveImages.Checked;
        capture.ImageDirectory = _imageDirectory.Text.Trim();
        capture.ImageRetentionDays = (int)_imageRetentionDays.Value;
        storage.RecordRetentionDays = (int)_recordRetentionDays.Value;
    }

    private void UpdateEnabledState()
    {
        _imageDirectory.Enabled = _saveImages.Checked;
    }

    private void BrowseImageDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择截图存放目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        var current = _imageDirectory.Text.Trim();
        var resolved = string.IsNullOrWhiteSpace(current)
            ? Paths.ImagesDirectory
            : Environment.ExpandEnvironmentVariables(current);

        if (Directory.Exists(resolved))
        {
            dialog.SelectedPath = resolved;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _imageDirectory.Text = dialog.SelectedPath;
        }
    }

    private void OpenDirectory(string directory)
    {
        try
        {
            var resolved = string.IsNullOrWhiteSpace(directory)
                ? Paths.ImagesDirectory
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim()));

            Directory.CreateDirectory(resolved);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(resolved) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"打开截图目录失败：{ex.Message}");
        }
    }
}
