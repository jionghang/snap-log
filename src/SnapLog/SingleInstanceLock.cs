namespace SnapLog;

/// <summary>
/// 托盘常驻模式的单实例锁。
///
/// 两个实例同时跑会各抓一份：同一条记录被写两遍，托盘图标也出现两个，
/// 用户分不清该点哪个，还会互相抢数据库。
///
/// 只保护托盘模式。命令行的一次性命令（--once、--summarize、--ocr-pending…）
/// 不常驻抓取，需要能和托盘那份并存，所以不加这把锁。
/// </summary>
internal sealed class SingleInstanceLock : IDisposable
{
    /// <summary>Local\ 前缀表示只在当前登录会话内互斥：多用户/多会话各跑一份是允许的。</summary>
    private const string MutexName = @"Local\SnapLog.TrayInstance";

    private readonly Mutex _mutex;

    private SingleInstanceLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>拿到锁返回实例，已经有实例在跑则返回 null。进程退出（含被强杀）时由系统释放。</summary>
    public static SingleInstanceLock? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            return new SingleInstanceLock(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 不是持有者（理论上到不了这里）。释放锁失败不该影响退出流程。
        }

        _mutex.Dispose();
    }
}
