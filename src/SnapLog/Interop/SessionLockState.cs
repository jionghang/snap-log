using System.Runtime.InteropServices;

namespace SnapLog.Interop;

/// <summary>
/// 判断当前会话是否处于锁屏状态，用于在锁屏期间跳过抓取。
///
/// 为什么需要显式判定：锁屏后输入桌面切到 Winlogon，前台窗口查询拿不到东西，
/// 抓取虽然会被"没有可用窗口"挡掉，但那属于副产物；这里直接问一次会话状态，
/// 免得在锁屏期间反复做无意义的截图尝试。
///
/// 三个取值上的坑：
///   1. WTSINFOEX_LEVEL_W 是 union，其中有 8 字节对齐的成员，因此 Data 前面有 4 字节填充：
///      SessionId 在偏移 8、SessionState 在 12、SessionFlags 在 16（按 12 读会把 SessionState 当旗标）。
///   2. SessionFlags 的取值与直觉相反：0 表示已锁定，1 表示未锁定。
///   3. SessionFlags 只对 WTSActive 的会话有意义——未激活的会话会读到 -1 或 0
///      （实测会话 0 = Services/已断开时是 -1），所以必须连同会话状态一起判断。
///
/// 判定失败一律按"未锁定"处理：宁可多抓一次，也不能因为判定错误而静默停止记录。
/// </summary>
internal static class SessionLockState
{
    private const uint WtsCurrentServerHandle = 0;
    private const uint WtsCurrentSession = 0xFFFFFFFF;

    /// <summary>WTS_INFO_CLASS.WTSSessionInfoEx</summary>
    private const int WtsSessionInfoEx = 25;

    /// <summary>WTS_CONNECTSTATE_CLASS.WTSActive</summary>
    private const int WtsActive = 0;

    /// <summary>WTS_SESSIONSTATE_LOCK（0 = 已锁定，1 = 未锁定）</summary>
    private const int WtsSessionStateLock = 0;

    private const int SessionStateOffset = 12;
    private const int SessionFlagsOffset = 16;
    private const int MinimumBufferSize = SessionFlagsOffset + 4;

    public static bool IsLocked()
    {
        var buffer = IntPtr.Zero;

        try
        {
            if (!NativeMethods.WTSQuerySessionInformationW(
                    (IntPtr)WtsCurrentServerHandle,
                    WtsCurrentSession,
                    WtsSessionInfoEx,
                    out buffer,
                    out var bytes)
                || buffer == IntPtr.Zero
                || bytes < MinimumBufferSize)
            {
                return false;
            }

            var sessionState = Marshal.ReadInt32(buffer, SessionStateOffset);
            var sessionFlags = Marshal.ReadInt32(buffer, SessionFlagsOffset);

            return sessionState == WtsActive && sessionFlags == WtsSessionStateLock;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // 拿不到 wtsapi32 就当未锁定，绝不因为判定本身失败而停掉记录。
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NativeMethods.WTSFreeMemory(buffer);
            }
        }
    }
}
