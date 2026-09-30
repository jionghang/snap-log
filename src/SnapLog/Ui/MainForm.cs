using System.Diagnostics;
using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;
using SnapLog.Interop;
using SnapLog.Ocr;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 主窗口。关闭按钮只收起窗口，进程继续在托盘里记录；要真正退出走托盘菜单。
///
/// 八个标签页：状态 / 抓取记录 / 总结记录 / 抓取配置 / OCR配置 / 大模型配置 / 推送配置 / 关于。
/// 原来一个"设置"标签页里堆了一百多个控件、要下拉很久才能看到全部内容，
/// 现在按用途拆成四个独立的配置页，每页只有它自己的那一类。
/// </summary>
internal sealed class MainForm : Form
{
    private const int MaxLogLines = 500;

    private readonly AppOptions _options;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;
    private readonly SnapLogEngine _engine;
    private readonly SummaryRunner _summaryRunner;
    private readonly IActivityRepository _store;
    private readonly string? _configSourcePath;

    private readonly Label _stateValue = new();

    private readonly Label _lastValue = new();
    private readonly Label _countValue = new();
    private readonly Label _totalValue = new();
    private readonly ListBox _logList = new();
    private readonly Button _toggleButton = new();
    private readonly CheckBox _autoStart = new();
    private readonly ToolStripStatusLabel _statusStripLabel = new();
    private readonly TabControl _tabs = new();



    private int _sessionRecordCount;

    /// <summary>引擎是否运行过。用来区分"正在启动"和"已被暂停"。</summary>
    private bool _sawRunning;

    public MainForm(
        AppOptions options,
        AppPaths paths,
        FileLogger log,
        SnapLogEngine engine,
        SummaryRunner summaryRunner,
        IActivityRepository store,
        string? configSourcePath)
    {
        _options = options;
        _paths = paths;
        _log = log;
        _engine = engine;
        _summaryRunner = summaryRunner;
        _store = store;
        _configSourcePath = configSourcePath;

        Text = "SnapLog · 屏幕活动记录";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 560);
        Size = new Size(940, 660);
        Icon = IconFactory.AppIcon;

        BuildLayout();

        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab is not null)
            {
                EnsureTabContentBuilt(_tabs.SelectedTab);
            }
        };

        _engine.RecordCaptured += OnRecordCaptured;
        _engine.StatusChanged += OnStatusChanged;
        _log.EntryWritten += OnLogEntryWritten;

        RefreshStatus();
        SeedLogList();

        // 空闲时把懒加载的标签页都建好：程序启动后用户没在操作的那段空隙里，
        // 把内容建完，等你点开任何一个设置页时它已经在那里了。
        Application.Idle += OnIdlePrebuildTabs;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= OnIdlePrebuildTabs;
            _engine.RecordCaptured -= OnRecordCaptured;
            _engine.StatusChanged -= OnStatusChanged;
            _log.EntryWritten -= OnLogEntryWritten;
        }

        base.Dispose(disposing);
    }

    /// <summary>关闭按钮收起窗口而不是退出进程，方便长时间挂在托盘里记录。</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        _tabs.Dock = DockStyle.Fill;
        _tabs.Padding = new Point(12, 6);

        var context = new SettingsContext(_options, _paths, _log, _store, _summaryRunner, ApplyAndPersistOptions);

        // 用得最多的三个立刻建；配置页和关于页按需懒加载：第一次点开才建，之后即点即到。
        // 一个主窗口打开时不用再付那 8 个视图全建的几百毫秒。
        _tabs.TabPages.Add(BuildStatusTab());
        _tabs.TabPages.Add(BuildRecordsTab(context));
        _tabs.TabPages.Add(BuildSummaryTab(context));
        _tabs.TabPages.Add(BuildLazyTab("抓取配置", () => new CaptureSettingsView(context)));
        _tabs.TabPages.Add(BuildLazyTab("OCR 配置", () => new OcrSettingsView(context)));
        _tabs.TabPages.Add(BuildLazyTab("大模型配置", () => new LlmSettingsView(context)));
        _tabs.TabPages.Add(BuildLazyTab("推送配置", () => new FeishuSettingsView(context)));
        _tabs.TabPages.Add(BuildLazyTab("关于", () => new AboutView(context)));

        var statusStrip = new StatusStrip();
        _statusStripLabel.Text = "就绪";
        _statusStripLabel.Spring = true;
        _statusStripLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusStrip.Items.Add(_statusStripLabel);

        Controls.Add(_tabs);
        Controls.Add(statusStrip);
    }

    private static TabPage BuildSettingsTab(SettingsViewBase view)
    {
        var page = new TabPage(view.Text.Length == 0 ? "设置" : view.Text);
        page.Controls.Clear();
        page.Controls.Add(view);
        return page;
    }

    /// <summary>
    /// 按需懒加载的标签页：标签页先放个占位文字，第一次被选中才把真正的内容建出来。
    /// 一个主窗口打开时不用再付所有视图全建的几百毫秒。
    /// </summary>
    private TabPage BuildLazyTab(string title, Func<Control> factory)
    {
        var page = new TabPage(title);
        page.Tag = factory;
        page.Controls.Add(new Label
        {
            Text = "正在载入",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(12),
        });
        return page;
    }

    private void EnsureTabContentBuilt(TabPage page)
    {
        if (page.Tag is not Func<Control> factory)
        {
            return;
        }

        page.Tag = null;

        page.SuspendLayout();
        try
        {
            page.Controls.Clear();
            page.Controls.Add(factory());
        }
        finally
        {
            page.ResumeLayout(true);
        }
    }

    /// <summary>
    /// 空闲时把懒加载的标签页逐页建好。
    /// 一轮空闲只建一页：全部建在一轮里会让启动后有一秒左右的界面卡顿
    /// （窗口先画一半、剩下的等 UI 线程忙完才补上，看起来就是"打开后再刷新一下"）。
    /// 建完一页后主动投一个空消息，让下一轮空闲尽快到来，不用等用户动鼠标。
    /// </summary>
    private void OnIdlePrebuildTabs(object? sender, EventArgs e)
    {
        foreach (TabPage page in _tabs.TabPages)
        {
            if (page.Tag is Func<Control>)
            {
                EnsureTabContentBuilt(page);

                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(() => { });
                }

                return;
            }
        }

        Application.Idle -= OnIdlePrebuildTabs;
    }

    /// <summary>外部（托盘弹窗/高级页）改过配置后，把设置页的显示刷新过来。</summary>
    public void ReloadCommonSettings()
    {
        foreach (TabPage page in _tabs.TabPages)
        {
            if (page.Controls.Count > 0 && page.Controls[0] is SettingsViewBase view)
            {
                view.Reload();
            }
        }
    }

    /// <summary>外部操作（如托盘里的批量识别）之后刷新状态显示。</summary>
    public void ReloadStatus() => OnUiThread(RefreshStatus);
    // ---------------------------------------------------------------- 状态页

    private TabPage BuildStatusTab()
    {
        var page = new TabPage("状态") { Padding = new Padding(12) };

        var info = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 0, 0, 8),
        };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddInfoRow(info, "运行状态", _stateValue);
        AddInfoRow(info, "本次运行抓取", _countValue);
        AddInfoRow(info, "最近一次抓取", _lastValue);
        AddInfoRow(info, "总共抓取", _totalValue);

        // 开机自启动：改了立刻写注册表并生效（这一页没有"保存设置"，勾选本身就是动作）。
        _autoStart.Text = "开机自动启动（登录后最小化到托盘）";
        _autoStart.AutoSize = true;
        _autoStart.Margin = new Padding(3, 6, 0, 0);
        _autoStart.CheckedChanged += (_, _) => ToggleAutoStart();
        AddInfoRow(info, "开机自启动", _autoStart);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 8),
            WrapContents = true,
        };

        _toggleButton.Text = "暂停记录";
        _toggleButton.Width = 108;
        _toggleButton.Height = 30;
        _toggleButton.Click += async (_, _) => await ToggleRecordingAsync();
        buttons.Controls.Add(_toggleButton);

        buttons.Controls.Add(MakeButton("立即抓取一次", 108, async () => await CaptureOnceAsync()));

        var logHeader = new Label
        {
            Text = "运行日志（最近 500 条）",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 4),
        };

        _logList.Dock = DockStyle.Fill;
        _logList.Font = new Font("Consolas", 9f);
        _logList.IntegralHeight = false;
        _logList.HorizontalScrollbar = true;

        // 用外层容器固定顺序：日志区占满剩余空间，上面的信息区/按钮区各自按内容高度排列。
        var logContainer = new Panel { Dock = DockStyle.Fill };
        logContainer.Controls.Add(_logList);
        logContainer.Controls.Add(logHeader);

        page.Controls.Add(logContainer);
        page.Controls.Add(buttons);
        page.Controls.Add(info);
        return page;
    }

    private static void AddInfoRow(TableLayoutPanel panel, string caption, Control value)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = caption + "：",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 3, 4, 3),
        };

        value.AutoSize = true;
        value.Margin = new Padding(0, 3, 0, 3);

        panel.Controls.Add(label, 0, row);
        panel.Controls.Add(value, 1, row);
    }

    private static Button MakeButton(string text, int width, Action onClick)
    {
        var button = new Button { Text = text, Width = width, AutoSize = false, Height = 30 };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---------------------------------------------------------------- 抓取记录页

    private TabPage BuildRecordsTab(SettingsContext context)
    {
        var page = new TabPage("抓取记录");
        var view = new RecordsView(_options, _store, _paths, _log) { Dock = DockStyle.Fill };
        page.Controls.Add(view);
        return page;
    }

    // ---------------------------------------------------------------- 总结记录页

    private TabPage BuildSummaryTab(SettingsContext context)
    {
        var page = new TabPage("总结记录");
        var view = new SummaryHistoryView(context) { Dock = DockStyle.Fill };
        page.Controls.Add(view);
        return page;
    }

    // ---------------------------------------------------------------- 子窗口

    // ---------------------------------------------------------------- 行为

    private async Task ToggleRecordingAsync()
    {
        if (_engine.IsRunning)
        {
            await _engine.StopAsync();
        }
        else
        {
            _engine.Start();
        }

        RefreshStatus();
    }

    private async Task CaptureOnceAsync()
    {
        SetStatus("正在抓取…");
        try
        {
            var outcome = await _engine.CaptureNowAsync(CaptureTrigger.Manual, CancellationToken.None);
            SetStatus(outcome.Captured
                ? $"已记录 {outcome.Record!.TextLength} 字"
                : $"未记录：{outcome.Reason}");

            if (outcome.Captured)
            {
                await RefreshCountAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Error("手动抓取失败", ex);
            SetStatus($"抓取失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把内存里的配置写盘并让它生效。返回 false 表示写盘失败（调用方据此决定要不要关窗口）。
    /// </summary>
    private bool ApplyAndPersistOptions()
    {
        try
        {
            OptionsStore.Save(_options, AppPaths.ResolveConfigWriteTarget(_configSourcePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("保存配置失败", ex);
            MessageBox.Show($"保存配置失败：{ex.Message}", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _log.Configure(_options.Logging.Enabled, _options.Logging.Level, _options.Logging.RetentionDays);
        _engine.Start();
        RefreshStatus();
        return true;
    }

    // ---------------------------------------------------------------- 状态刷新

    /// <summary>
    /// 窗口句柄建好时重算一次状态。
    /// 引擎是在主窗口显示之前启动的，那次"已开始记录"的状态事件因为句柄还没建好被丢掉了，
    /// 只算构造那一次的话，状态页会一直显示"已暂停 / 开始记录"，而点下去实际执行的是暂停。
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RefreshStatus();
    }

    /// <summary>状态页里跟"是否在记录"有关的那几处。引擎可能在别处被切换（托盘菜单），所以每次状态变化都重算。</summary>
    private void UpdateRunState()
    {
        var running = _engine.IsRunning;
        _sawRunning |= running;

        // 还没启动起来过：显示"启动中"，别显示成"已暂停"——那会让人以为要自己点开始。
        _stateValue.Text = running ? "运行中" : _sawRunning ? "已暂停" : "启动中";
        _stateValue.ForeColor = running ? Color.SeaGreen : _sawRunning ? Color.OrangeRed : Color.RoyalBlue;
        _toggleButton.Text = running ? "暂停记录" : "开始记录";
    }

    private void RefreshStatus()
    {
        UpdateRunState();
        RefreshAutoStart();

        _countValue.Text = $"{_sessionRecordCount} 条";

        var last = _engine.LastRecord;
        _lastValue.Text = last is null
            ? "暂无记录"
            : $"{last.Timestamp:HH:mm:ss} · {last.WindowTitle}";

        _ = RefreshCountAsync();
    }

    private async Task RefreshCountAsync()
    {
        try
        {
            var total = await _engine.CountRecordsAsync(CancellationToken.None);
            _totalValue.Text = $"{total} 条";
        }
        catch (Exception ex)
        {
            _log.Warn($"读取记录总数失败：{ex.Message}");
            _totalValue.Text = "(读取失败)";
        }
    }

    private void SeedLogList()
    {
        foreach (var entry in _log.Snapshot().TakeLast(120))
        {
            AppendLog(entry);
        }
    }

    /// <summary>把注册表里的真实状态读回复选框：用户可能在"任务管理器 → 启动"里改过。</summary>
    private void RefreshAutoStart()
    {
        var enabled = AutoStart.IsEnabled();
        if (_autoStart.Checked != enabled)
        {
            _autoStart.Checked = enabled;
        }
    }

    private void ToggleAutoStart()
    {
        var wanted = _autoStart.Checked;

        if (!AutoStart.SetEnabled(wanted))
        {
            SetStatus($"设置开机自启动失败（{AutoStart.ExecutablePath}）");
            _log.Warn("设置开机自启动失败：写注册表被拒绝或路径为空");

            // 回到真实状态，别让复选框显示成用户以为的样子。
            RefreshAutoStart();
            return;
        }

        SetStatus(wanted ? "已设为开机自动启动" : "已取消开机自动启动");
        _log.Info(wanted
            ? $"已设为开机自动启动：{AutoStart.ExecutablePath}"
            : "已取消开机自动启动");
    }

    private void SetStatus(string message) => _statusStripLabel.Text = message;

    private void OnRecordCaptured(object? sender, ActivityRecord record)
    {
        OnUiThread(() =>
        {
            _sessionRecordCount++;
            RefreshStatus();
            SetStatus($"新记录：{record.WindowTitle}（{record.TextLength} 字）");
        });
    }

    private void OnStatusChanged(object? sender, string message)
    {
        OnUiThread(() =>
        {
            SetStatus(message);

            // 状态消息也可能意味着"开始/暂停"变了（引擎接口被托盘复用），跟着重算一遍。
            UpdateRunState();
        });
    }

    private void OnLogEntryWritten(LogEntry entry)
    {
        OnUiThread(() => AppendLog(entry));
    }

    private void AppendLog(LogEntry entry)
    {
        if (_logList.Items.Count >= MaxLogLines)
        {
            _logList.Items.RemoveAt(0);
        }

        _logList.Items.Add(entry.ToString());
        _logList.TopIndex = _logList.Items.Count - 1;
    }

    private void OnUiThread(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁，丢掉这一次更新即可。
            }

            return;
        }

        action();
    }
}
