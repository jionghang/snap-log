using SnapLog.Interop;

namespace SnapLog.Ocr;

/// <summary>
/// 在调用 PaddleOCR 原生库期间，把进程的标准输出临时指向空设备，
/// 用来吞掉原生层自己打印的内容。
///
/// 为什么需要：原生层加载时会打一大块公司 banner，单次调用超过免费版文本框上限时
/// 还会往 stdout 打一行英文错误。这些是**直接写进程 stdout 句柄**的，.NET 的
/// <c>Console.SetOut</c> 拦不住。不管的话命令行模式下报告里会混进这些内容，
/// 而且它们不是 UTF-8，会让整个输出文件编码错乱、grep 都失效（实测踩到过）。
///
/// 实测行为（对照组/实验组都验证过）：
///   - 原生层的输出被挡住；
///   - .NET 自己的 Console.Out 不受影响，因为它在首次访问时就把句柄缓存成了 FileStream，
///     改 OS 句柄不影响已缓存的流。
/// 所以在静音窗口内调用 Console.WriteLine 仍然能正常输出——这正是我们想要的。
///
/// 没有控制台时（托盘应用正常运行）GetStdHandle 拿不到有效句柄，直接跳过即可。
/// </summary>
internal sealed class PaddleNativeOutputSilencer : IDisposable
{
    private const int StdOutputHandle = -11;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private readonly IntPtr _nulHandle;
    private readonly IntPtr _savedHandle;
    private bool _disposed;

    private PaddleNativeOutputSilencer(IntPtr savedHandle, IntPtr nulHandle)
    {
        _savedHandle = savedHandle;
        _nulHandle = nulHandle;
    }

    /// <summary>进入静音；拿不到可用的 stdout 句柄时返回一个什么都不做的实例。</summary>
    public static PaddleNativeOutputSilencer Enter()
    {
        var saved = NativeMethods.GetStdHandle(StdOutputHandle);
        if (saved == IntPtr.Zero || saved == InvalidHandleValue)
        {
            return new PaddleNativeOutputSilencer(IntPtr.Zero, IntPtr.Zero);
        }

        var nul = NativeMethods.CreateFileW(
            "NUL",
            NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (nul == InvalidHandleValue)
        {
            return new PaddleNativeOutputSilencer(IntPtr.Zero, IntPtr.Zero);
        }

        // 必须先让 .NET 把 Console.Out 建立起来（绑定到原句柄），
        // 否则等静音期间才首次访问，它会绑到空设备上，之后所有控制台输出都会消失。
        _ = Console.Out;
        Console.Out.Flush();

        NativeMethods.SetStdHandle(StdOutputHandle, nul);
        return new PaddleNativeOutputSilencer(saved, nul);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_savedHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetStdHandle(StdOutputHandle, _savedHandle);
        NativeMethods.CloseHandle(_nulHandle);
    }
}
