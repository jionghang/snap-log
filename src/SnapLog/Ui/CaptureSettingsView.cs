using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>抓取配置页：间隔、是否跟随窗口、截图保存与保留。</summary>
internal sealed class CaptureSettingsView : SettingsViewBase
{
    private NumericUpDown _intervalSeconds = null!;
    private CheckBox _watchForeground = null!;
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

        // ---- 触发 ----
        var trigger = NewSection("触发方式");

        var intervalRow = NewRow();
        _intervalSeconds = new NumericUpDown { Minimum = 5, Maximum = 86_400, Increment = 5, Width = 90 };
        intervalRow.Controls.Add(_intervalSeconds);
        intervalRow.Controls.Add(NewHint("秒（定时抓取的周期，默认 300）"));
        AddRow(trigger, "截图间隔", intervalRow);

        _watchForeground = new CheckBox
        {
            Text = "跟随前台窗口切换时也自动抓取",
            AutoSize = true,
            Margin = new Padding(3, 6, 0, 0),
        };
        AddRow(trigger, "监听窗口变化", _watchForeground);

        var settleRow = NewRow();
        _settleMilliseconds = new NumericUpDown { Minimum = 0, Maximum = 30_000, Increment = 100, Width = 90 };
        settleRow.Controls.Add(_settleMilliseconds);
        settleRow.Controls.Add(NewHint("毫秒。窗口第一次出现时先等这么久，页面才渲染出内容；"
                                       + "等满后还是这个窗口才截图。"));
        AddRow(trigger, "延时截图时长", settleRow);

        var cycleRow = NewRow();
        _captureCycleMinutes = new NumericUpDown { Minimum = 1, Maximum = 24 * 60, Width = 90 };
        cycleRow.Controls.Add(_captureCycleMinutes);
        cycleRow.Controls.Add(NewHint("分钟。同一个窗口在一个周期内只记一次；"
                                       + "过了一个周期再打开才重新记。"));
        AddRow(trigger, "抓取周期", cycleRow);

        // ---- 截图与记录 ----
        var retention = NewSection("截图与记录");

        _saveImages = new CheckBox
        {
            Text = "每次抓取都保存截图文件（记录才能对应到当时的画面）",
            AutoSize = true,
            Margin = new Padding(3, 6, 0, 0),
        };
        _saveImages.CheckedChanged += (_, _) => UpdateEnabledState();
        AddRow(retention, "保存截图", _saveImages);

        var directoryRow = NewRow();
        _imageDirectory = new TextBox { Width = 300, PlaceholderText = "留空 = 数据目录下的 images 子目录" };
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
        imageRetentionRow.Controls.Add(NewHint("天（超过就删掉截图文件，0 = 永久保留；截图很占磁盘）"));
        AddRow(retention, "截图保留", imageRetentionRow);

        var recordRetentionRow = NewRow();
        _recordRetentionDays = new NumericUpDown { Minimum = 0, Maximum = 3650, Width = 90 };
        recordRetentionRow.Controls.Add(_recordRetentionDays);
        recordRetentionRow.Controls.Add(NewHint("天（超过就从数据库删掉记录，0 = 永久保留）"));
        AddRow(retention, "记录保留", recordRetentionRow);

        AddRow(retention, string.Empty, NewHint(
            "定时识别模式下必须存图：没图就没法事后识别，所以那种模式下这个开关会被锁成开启。保留策略在"
            + "「关于」页里能看到清理记录。"));

        root.Controls.Add(trigger);
        root.Controls.Add(retention);
        return root;
    }

    protected override void LoadFromOptions()
    {
        var capture = Options.Capture;
        var triggers = Options.Triggers;
        var storage = Options.Storage;

        _intervalSeconds.Value = Math.Clamp(triggers.IntervalSeconds, (int)_intervalSeconds.Minimum, (int)_intervalSeconds.Maximum);
        _watchForeground.Checked = triggers.Mode is TriggerMode.ForegroundWindowChange or TriggerMode.Both;
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

        triggers.IntervalSeconds = (int)_intervalSeconds.Value;
        triggers.ForegroundSettleMilliseconds = (int)_settleMilliseconds.Value;
        triggers.CaptureCycleMinutes = (int)_captureCycleMinutes.Value;

        // 触发模式是三态，复选框只能表达"要不要监听窗口切换"：
        // 取消勾选退化成纯定时；勾选时原来若是纯定时就升成两者都开，否则保留用户原来的选择。
        if (!_watchForeground.Checked)
        {
            triggers.Mode = TriggerMode.Interval;
        }
        else if (triggers.Mode == TriggerMode.Interval)
        {
            triggers.Mode = TriggerMode.Both;
        }

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
