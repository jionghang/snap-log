using System.Drawing;
using System.Windows.Forms;
using Avalonia.Threading;
using SnapLog.Diagnostics;
using SnapLog.Interop;

namespace SnapLog.Ui;

/// <summary>
/// 托盘图标。界面是 Avalonia 的，但托盘仍用 WinForms 的 NotifyIcon：
/// Avalonia 自己不带托盘实现，而这里已经为了截图和 System.Drawing 引用了 WinForms。
/// 右键菜单用的是自己画的 Avalonia 弹窗（<see cref="TrayMenuWindow"/>），不是 WinForms 的灰菜单。
///
/// 两个踩过的坑：
/// - **开机自启时图标常常不出现**：程序比任务栏（资源管理器）先起来，这时候
///   Shell_NotifyIcon 会静默失败，之后也不会自己补上。所以这里要等 Shell_TrayWnd 出现再加图标，
///   加不上就每秒重试，一分钟还不行就把主窗口亮出来（不能变成一个"看不见的进程"）。
/// - **状态文案写得太长**：托盘提示最多 63 个字符，超了会抛异常，所以统一截断。
/// </summary>
public sealed class TrayHost : IDisposable
{
    /// <summary>重试上限：约一分钟。到点还加不上就改成显示主窗口。</summary>
    private const int MaxAttempts = 60;

    private readonly AppServices _services;
    private readonly NotifyIcon _icon;
    private readonly DispatcherTimer _retry;

    private TrayMenuWindow? _menu;
    private readonly DispatcherTimer _clickDelay;
    private bool _doubleClicked;
    private bool _suppressNextLeftClick;
    private int _attempts;
    private bool _ready;
    private bool _disposed;

    /// <summary>图标真正进了托盘之后触发一次（首次运行的气泡要等这时候才能显示）。</summary>
    public event Action? IconReady;

    /// <summary>图标是否已经进了托盘。订阅 <see cref="IconReady"/> 前先看这个，避免错过。</summary>
    public bool IsReady => _ready;

    public TrayHost(AppServices services)
    {
        _services = services;

        // 不设 ContextMenuStrip：右键由我们自己弹 Avalonia 菜单。
        _icon = new NotifyIcon
        {
            Icon = IconFactory.AppIcon,
            Text = "SnapLog",
            Visible = false,
        };

        _clickDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _clickDelay.Tick += (_, _) =>
        {
            _clickDelay.Stop();

            if (!_doubleClicked)
            {
                ToggleMenu();
            }

            _doubleClicked = false;
        };

        _icon.MouseUp += OnIconMouseUp;
        _icon.DoubleClick += (_, _) =>
        {
            _doubleClicked = true;
            _suppressNextLeftClick = true;   // 双击之后还会来一次 MouseUp，不能让它再触发弹菜单
            _services.ShowMainWindow();
        };
        _services.StatusChanged += OnStatusChanged;

        TrayBalloon.Register((message, icon) => ShowBalloon("SnapLog", message, icon));

        _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _retry.Tick += (_, _) => TryAddToTray();

        TryAddToTray();
    }

    /// <summary>托盘区域（任务栏）已经存在。没起来的时候加图标会失败。</summary>
    private static bool ShellReady() =>
        NativeMethods.FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero;

    private void TryAddToTray()
    {
        if (_disposed || _ready)
        {
            return;
        }

        _attempts++;

        // 等任务栏起来再加；最多等一分钟，再不行就把窗口显示出来。
        if (ShellReady() || _attempts > MaxAttempts)
        {
            _retry.Stop();
            _icon.Visible = true;
            _ready = true;
            UpdateState();
            IconReady?.Invoke();

            if (!ShellReady())
            {
                _services.Log.Warn("托盘区域一直没就绪，已改为显示主窗口（图标可能不会出现在托盘里）");
                _services.ShowMainWindow();
            }

            return;
        }

        _retry.Start();
    }

    public void UpdateState()
    {
        if (!_ready)
        {
            return;
        }

        _icon.Text = _services.Engine.IsRunning ? "SnapLog · 记录中" : "SnapLog · 已暂停";
    }

    public void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        try
        {
            if (_icon.Visible)
            {
                _icon.ShowBalloonTip(4000, title, message, icon);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Log.Warn($"托盘提示失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 右键弹自己画的菜单。左键单击也弹菜单——很多人在托盘上习惯左键点一下，
    /// 而 Windows 10 的隐藏图标浮出层对左键的响应比右键可靠得多。
    /// 双击仍然直接打开主窗口，所以左键按下后先等 260 毫秒，确认不是双击再弹菜单。
    /// </summary>
    private void OnIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_suppressNextLeftClick)
            {
                _suppressNextLeftClick = false;
                return;
            }

            _doubleClicked = false;
            _clickDelay.Stop();
            _clickDelay.Start();
            return;
        }

        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        ToggleMenu();
    }

    /// <summary>弹菜单；已经开着就收起来。右键和左键单击都走这里。</summary>
    private void ToggleMenu()
    {
        if (_menu is { IsVisible: true })
        {
            _menu.Close();
            return;
        }

        var menu = new TrayMenuWindow(_services);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu))
            {
                _menu = null;
            }
        };

        _menu = menu;
        menu.ShowNearTray();

        // 托盘菜单在自动化里不好验（Win10 浮出层里的图标点不中），所以把它真的弹出来了
        // 这件事写进日志：用户随手一点，日志里就有位置和尺寸可核对。
        _services.Log.Info(
            $"托盘菜单已弹出：{menu.Bounds.Width:0}×{menu.Bounds.Height:0} @ ({menu.Position.X},{menu.Position.Y})");
    }

    /// <summary>
    /// 托盘提示固定显示"记录中 / 已暂停"：鼠标移上去最想确认的就是这个。
    /// 抓取消息只走窗口里的状态栏，不占用这里。
    /// </summary>
    private void OnStatusChanged(string message) => UpdateState();

    public void Hide()
    {
        _icon.Visible = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _retry.Stop();
        _services.StatusChanged -= OnStatusChanged;
        _icon.MouseUp -= OnIconMouseUp;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
