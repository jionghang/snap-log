using System.Diagnostics;

namespace SnapLog.Interop;

/// <summary>前台窗口在某一时刻的可读信息。</summary>
public sealed record WindowSnapshot(IntPtr Handle, string ProcessName, string WindowTitle, string ClassName = "")
{
    public static WindowSnapshot Empty { get; } = new(IntPtr.Zero, string.Empty, string.Empty);

    public bool IsUsable => Handle != IntPtr.Zero && !string.IsNullOrWhiteSpace(WindowTitle);
}

/// <summary>
/// 基于 SetWinEventHook(EVENT_SYSTEM_FOREGROUND) 的前台窗口监听。
/// 事件驱动、零轮询，空闲时几乎不占 CPU。
/// 注意：钩子必须装在带消息循环的线程上（本程序就是 UI 线程），
/// 且委托实例必须一直持有，否则被 GC 回收后回调会直接崩进程。
/// </summary>
public sealed class ForegroundWindowWatcher : IDisposable
{
    private readonly NativeMethods.WinEventProc _callback;
    private IntPtr _hook;
    private bool _disposed;

    public ForegroundWindowWatcher()
    {
        _callback = OnWinEvent;
    }

    public event EventHandler<WindowSnapshot>? ForegroundWindowChanged;

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    public static WindowSnapshot Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return WindowSnapshot.Empty;
        }

        return new WindowSnapshot(hwnd, GetProcessName(hwnd), GetWindowTitle(hwnd), GetClassName(hwnd));
    }

    public static WindowSnapshot DescribeForegroundWindow() => Describe(NativeMethods.GetForegroundWindow());

    internal static string GetWindowTitle(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(length + 1);
        var copied = NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return copied <= 0 ? string.Empty : buffer.ToString(0, copied);
    }

    /// <summary>窗口类名。拿不到时返回空字符串，不影响记录本身。</summary>
    internal static string GetClassName(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        var copied = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);
        return copied <= 0 ? string.Empty : buffer.ToString(0, copied);
    }

    internal static string GetProcessName(IntPtr hwnd)    {
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 高权限进程拿不到信息是正常的，不视为错误。
            return string.Empty;
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero)
        {
            return;
        }

        // 只有顶层窗口（idObject == OBJID_WINDOW）才关心。
        var snapshot = Describe(hwnd);
        if (!snapshot.IsUsable)
        {
            return;
        }

        ForegroundWindowChanged?.Invoke(this, snapshot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
