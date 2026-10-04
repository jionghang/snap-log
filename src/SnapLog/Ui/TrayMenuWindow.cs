using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace SnapLog.Ui;

/// <summary>
/// 托盘右键菜单。刻意不用 WinForms 的 ContextMenuStrip：
/// 那套是 2010 年代的灰色系统菜单，和 Avalonia 画的主界面放一起像两个软件。
/// 这里用一个无边框弹窗自己画，样式跟着主界面走。
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private readonly AppServices _services;

    /// <summary>点了别处就关（截图/自检时置 false，否则刚显示就被判为失焦而关掉）。</summary>
    internal bool CloseOnDeactivate { get; set; } = true;

    public TrayMenuWindow(AppServices services)
    {
        _services = services;

        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        Width = 248;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;   // 四周留出的那圈用来画阴影

        var rows = new StackPanel { Spacing = 2 };

        var state = new TextBlock
        {
            Text = services.Engine.IsRunning ? "正在记录" : "已暂停",
            Classes = { "caption" },
            Margin = new Thickness(12, 4, 12, 2),
        };

        rows.Children.Add(state);
        rows.Children.Add(new Border
        {
            Classes = { "divider" },
            Margin = new Thickness(8, 4, 8, 4),
        });

        rows.Children.Add(MenuRow(
            services.Engine.IsRunning ? "暂停记录" : "开始记录",
            async () =>
            {
                Close();
                await services.ToggleRecordingAsync();
            }));

        rows.Children.Add(MenuRow("打开 SnapLog", () =>
        {
            Close();
            services.ShowMainWindow();
            return Task.CompletedTask;
        }));

        rows.Children.Add(MenuRow("打开数据目录", () =>
        {
            Close();
            MainWindow.OpenPath(services.Paths.DataDirectory);
            return Task.CompletedTask;
        }));

        rows.Children.Add(new Border
        {
            Classes = { "divider" },
            Margin = new Thickness(8, 6, 8, 6),
        });

        rows.Children.Add(MenuRow("退出 SnapLog", () =>
        {
            Close();
            services.ExitApplication();
            return Task.CompletedTask;
        }));

        Content = new Border
        {
            Margin = new Thickness(10),
            Background = (IBrush?)this.FindResource("Surface") ?? Brushes.White,
            BorderBrush = (IBrush?)this.FindResource("Line") ?? Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            BoxShadow = BoxShadows.Parse("0 8 24 0 #26000000"),
            Child = rows,
        };

        // 点到别处就收起，跟系统菜单一样。Avalonia 12 里是事件而不是可重写方法。
        Deactivated += (_, _) =>
        {
            if (CloseOnDeactivate)
            {
                Close();
            }
        };
    }

    /// <summary>
    /// 算出菜单该出现的位置：右下角贴着鼠标（也就是托盘图标），往上、往左展开，
    /// 并且夹在屏幕工作区内——越界时宁可贴边，也不能跑到屏幕外面去。
    /// 抽成纯函数是为了能在无屏幕的自检里直接验。
    /// </summary>
    internal static PixelPoint ComputePosition(PixelPoint cursor, PixelSize menuSize, PixelRect workArea)
    {
        var x = Math.Clamp(cursor.X - menuSize.Width + 16, workArea.X, Math.Max(workArea.X, workArea.Right - menuSize.Width));
        var y = Math.Clamp(cursor.Y - menuSize.Height - 8, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - menuSize.Height));

        return new PixelPoint(x, y);
    }

    private Button MenuRow(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Classes = { "menu" } };
        button.Click += async (_, _) =>
        {
            _services.Log.Info($"托盘菜单：点了 {text}");
            await action();
        };

        return button;
    }

    /// <summary>
    /// 贴着鼠标（也就是托盘图标）弹出，并保证落在屏幕工作区内。
    ///
    /// 顺序很关键：**必须先设 Position 再 Show**。先前是先 Show 再设，在这台机器上不生效，
    /// 菜单会跑到屏幕左上角（用户报过"菜单不在托盘旁"）。尺寸又要等布局完成才准（SizeToContent），
    /// 所以 Show 之前先用估算尺寸摆一次，布局完成后按真实尺寸再校正一次。
    /// </summary>
    public void ShowNearTray(PixelPoint? at = null)
    {
        // 光标用 Win32 读（托管的 Cursor.Position 在某些环境里读出的是 0,0）。
        // 预览会显式传入位置：这台机器上脚本移不动真实光标，只能直接指定。
        var cursor = at ?? (Interop.NativeMethods.GetCursorPos(out var native)
            ? new PixelPoint(native.X, native.Y)
            : new PixelPoint(0, 0));

        var point = cursor;

        // 工作区用 WinForms 的屏幕信息：Avalonia 的 Screens 在这个进程里返回空矩形，
        // clamp 之后菜单只能落在 (0,0)（用户报的"菜单不在托盘旁"就是这个）。
        // WinForms 给的本来就是物理像素，正好和 SetWindowPos 的坐标系一致。
        var work = System.Windows.Forms.Screen.PrimaryScreen is { } primary
            ? primary.WorkingArea
            : new System.Drawing.Rectangle(0, 0, 1920, 1080);

        var area = new PixelRect(work.X, work.Y, work.Width, work.Height);

        // Position 是物理像素，Bounds 是逻辑像素：DPI 不是 100% 时必须换算。
        var scale = Screens.ScreenFromPoint(point)?.Scaling ?? 1.0;

        Position = ComputePosition(point, EstimateSize(scale), area);

        Show();
        Activate();

        // 布局完成后用 Win32 把窗口挪到托盘旁边：Avalonia 的 Position 在这个窗口上不生效，
        // 实测设了仍停在 0,0，菜单会跑到屏幕左上角。
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => MoveWithWin32(point, area, scale),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>按算好的位置直接移动窗口（用 Win32，绕开 Avalonia 的 Position）。</summary>
    private void MoveWithWin32(PixelPoint cursor, PixelRect area, double scale)
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);

        // 布局还没量出来时用一个贴近实际的估算高度，别把菜单摆到屏幕外。
        if (width < 80 || height < 60)
        {
            width = (int)Math.Ceiling(248 * scale);
            height = (int)Math.Ceiling(240 * scale);
        }

        var target = ComputePosition(cursor, new PixelSize(width, height), area);

        Interop.NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            target.X,
            target.Y,
            0,
            0,
            Interop.NativeMethods.SwpNoSize | Interop.NativeMethods.SwpNoZOrder | Interop.NativeMethods.SwpNoActivate);

        Position = target;

        _services.Log.Info("托盘菜单定位：" + width + "×" + height + " @ (" + target.X + "," + target.Y + ")"
                         + "　鼠标=(" + cursor.X + "," + cursor.Y + ")　工作区=(" + area.X + "," + area.Y + "," + area.Width + "×" + area.Height + ")");
    }

    /// <summary>Show 之前用的估算尺寸：宽度是固定的，高度按状态行 + 四个菜单项 + 分隔线估。</summary>
    private PixelSize EstimateSize(double scale) =>
        new((int)Math.Ceiling(Width * scale), (int)Math.Ceiling(240 * scale));
}
