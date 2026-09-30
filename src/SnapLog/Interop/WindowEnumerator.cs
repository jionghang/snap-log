namespace SnapLog.Interop;

/// <summary>桌面上一个可见的顶层窗口。</summary>
public sealed record WindowListing(
    IntPtr Handle,
    string ProcessName,
    string WindowTitle,
    int Width,
    int Height,
    string ClassName = "")
{
    public string HandleText => $"0x{Handle.ToInt64():X}";
}

/// <summary>
/// 挑选一个"可以拿来抓"的窗口。
/// 正常情况下就是前台窗口；但在锁屏、非交互会话或远程会话里
/// GetForegroundWindow 会返回 0，此时退回到面积最大的可见顶层窗口，
/// 让 --selftest 这类验证在无人值守环境下也能跑通。
/// </summary>
public static class WindowEnumerator
{
    /// <summary>列出所有可见的顶层窗口，按面积从大到小。用于诊断和挑选排除项，也可按标题找测试目标。</summary>
    public static IReadOnlyList<WindowListing> ListVisibleWindows()
    {
        var results = new List<WindowListing>();
        var ownProcessId = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == ownProcessId)
            {
                return true;
            }

            var title = ForegroundWindowWatcher.GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            NativeMethods.GetWindowRect(hwnd, out var rect);
            results.Add(new WindowListing(
                hwnd,
                ForegroundWindowWatcher.GetProcessName(hwnd),
                title,
                rect.Width,
                rect.Height,
                ForegroundWindowWatcher.GetClassName(hwnd)));

            return true;
        }, IntPtr.Zero);

        return [.. results.OrderByDescending(window => (long)window.Width * window.Height)];
    }

    /// <summary>按标题或进程名子串（不区分大小写）找窗口，找不到返回 null。用于 --selftest 指定目标。</summary>
    public static WindowListing? Find(string keyword) =>
        ListVisibleWindows().FirstOrDefault(window =>
            window.WindowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || window.ProcessName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || window.HandleText.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    public static (WindowSnapshot Snapshot, bool IsFallback) FindCaptureTarget()
    {
        var foreground = ForegroundWindowWatcher.DescribeForegroundWindow();
        if (foreground.IsUsable)
        {
            return (foreground, false);
        }

        return (FindLargestVisibleWindow(), true);
    }

    public static WindowSnapshot FindLargestVisibleWindow()
    {
        var best = WindowSnapshot.Empty;
        var bestArea = 0L;
        var ownProcessId = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            {
                return true;
            }

            if (NativeMethods.GetWindowTextLengthW(hwnd) <= 0)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == ownProcessId)
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width < 200 || rect.Height < 150)
            {
                return true;
            }

            var area = (long)rect.Width * rect.Height;
            if (area <= bestArea)
            {
                return true;
            }

            bestArea = area;
            best = new WindowSnapshot(
                hwnd,
                ForegroundWindowWatcher.GetProcessName(hwnd),
                ForegroundWindowWatcher.GetWindowTitle(hwnd));

            return true;
        }, IntPtr.Zero);

        return best;
    }
}
