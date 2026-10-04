using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SnapLog.Ui;

/// <summary>
/// 主窗口：左侧四个入口（概览 / 抓取记录 / 总结记录 / 设置）+ 内容区 + 状态栏。
///
/// 两个刻意的行为：
/// - 关闭按钮只是收起窗口，进程继续在托盘里记录；真正退出走托盘菜单或设置页。
/// - 导航项是单选语义，且页面按需创建：没点开的页面不构造，窗口打开更快。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);
    private string _currentKey = string.Empty;

    /// <summary>退出流程里置位，让关闭按钮不再拦。</summary>
    public bool AllowClose { get; set; }

    private bool _warnedAboutTray;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        Icon = IconFactory.WindowIcon;
        VersionText.Text = $"版本 {AppVersion()}";
        DataDirButton.Click += (_, _) => OpenPath(_services.Paths.DataDirectory);

        NavHome.Click += (_, _) => Activate("home");
        NavRecords.Click += (_, _) => Activate("records");
        NavSummaries.Click += (_, _) => Activate("summaries");
        NavSettings.Click += (_, _) => Activate("settings");

        _services.StateChanged += RefreshStatus;
        _services.StatusChanged += SetStatus;
        Closed += (_, _) =>
        {
            _services.StateChanged -= RefreshStatus;
            _services.StatusChanged -= SetStatus;
        };

        Activate("home");
        RefreshStatus();
        SetStatus(string.Empty);
    }

    /// <summary>从托盘打开：显示、从最小化恢复、抢焦点。</summary>
    /// <summary>供页面内部跳转（例如概览页的"查看全部总结"）。</summary>
    internal void ShowPage(string key) => Activate(key);

    /// <summary>启动时的一次性检查，见 HomePage.AskForConsentIfNeeded。</summary>
    internal void AskForConsentIfNeeded() => (Page("home") as HomePage)?.AskForConsentIfNeeded();

    public void ShowFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        RefreshCurrentPage();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && _services.Options.Ui.CloseToTrayInsteadOfExit)
        {
            e.Cancel = true;
            Hide();

            // 只提醒一次：否则每次关窗都弹，反而烦。
            if (!_warnedAboutTray)
            {
                _warnedAboutTray = true;
                _services.Tray?.ShowBalloon(
                    "SnapLog 还在托盘里",
                    "窗口关掉后仍在记录。要完全退出，请右键托盘图标选退出。",
                    System.Windows.Forms.ToolTipIcon.Info);
            }

            return;
        }

        base.OnClosing(e);
    }

    private void Activate(string key)
    {
        NavHome.IsChecked = key == "home";
        NavRecords.IsChecked = key == "records";
        NavSummaries.IsChecked = key == "summaries";
        NavSettings.IsChecked = key == "settings";

        _currentKey = key;
        PageHost.Content = Page(key);
        RefreshCurrentPage();
    }

    private Control Page(string key)
    {
        if (_pages.TryGetValue(key, out var existing))
        {
            return existing;
        }

        Control page = key switch
        {
            "records" => new RecordsPage(_services),
            "summaries" => new SummariesPage(_services),
            "settings" => new SettingsPage(_services),
            _ => new HomePage(_services),
        };

        _pages[key] = page;
        return page;
    }

    private void RefreshCurrentPage()
    {
        if (_pages.TryGetValue(_currentKey, out var page) && page is IRefreshable refreshable)
        {
            refreshable.Refresh();
        }
    }

    private void RefreshStatus()
    {
        var running = _services.Engine.IsRunning;
        Ui.UpdatePill(StatePill, running ? "记录中" : "已暂停", running ? PillKind.Ok : PillKind.Warn);
    }

    /// <summary>状态栏的默认文案：最近一次抓取（还没抓到就提示切换窗口）。</summary>
    private string DefaultStatus => _services.Engine.LastRecord is { } last
        ? $"最近一次抓取 {last.Timestamp:HH:mm:ss} · {last.WindowTitle}"
        : "本次启动尚未捕获画面，切换窗口后开始记录";

    private DispatcherTimer? _statusReset;

    /// <summary>状态栏先显示这条消息，几秒后回到默认文案——免得一次抓取把"最近一次抓取"永久顶掉。</summary>
    private void SetStatus(string message)
    {
        StatusText.Text = message.Length > 0 ? message : DefaultStatus;

        _statusReset ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusReset.Stop();

        if (message.Length == 0)
        {
            return;
        }

        _statusReset.Tick -= OnStatusReset;
        _statusReset.Tick += OnStatusReset;
        _statusReset.Start();
    }

    private void OnStatusReset(object? sender, EventArgs e)
    {
        _statusReset?.Stop();
        StatusText.Text = DefaultStatus;
    }

    private static string AppVersion() =>
        typeof(MainWindow).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "2.0";

    internal static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            TrayBalloon.TryShow($"打开失败：{ex.Message}", System.Windows.Forms.ToolTipIcon.Warning);
        }
    }
}
