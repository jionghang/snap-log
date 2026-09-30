using System.Runtime.InteropServices;
using System.Text;

namespace SnapLog.Diagnostics;

/// <summary>
/// WinExe 默认没有控制台，命令行模式下需要挂到父进程的控制台才能看到输出。
///
/// 三种情形要分开处理：
///   1. 输出已经重定向到文件/管道 —— 句柄是现成的，直接用，别去挂父控制台，
///      否则输出会跑到你看不见的地方（从 cmd 里跑 "SnapLog.exe --diagnose &gt; out.txt" 时就是这样）。
///   2. 已经有控制台（自己开的或从控制台启动的）—— 直接用。
///   3. 没有控制台 —— 挂到父进程的控制台上，再把标准输出接到它的屏幕缓冲区。
/// </summary>
internal static class ConsoleBridge
{
    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static void Attach()
    {
        var stdout = NativeConsole.GetStdHandle(STD_OUTPUT_HANDLE);

        if (IsRedirected(stdout) || NativeConsole.GetConsoleWindow() != IntPtr.Zero)
        {
            UseEncoding();
            return;
        }

        if (!NativeConsole.AttachConsole(ATTACH_PARENT_PROCESS))
        {
            return;
        }

        UseEncoding();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
    }

    /// <summary>句柄有效、但不是控制台句柄 —— 说明输出被重定向到了文件或管道。</summary>
    private static bool IsRedirected(IntPtr handle) =>
        handle != IntPtr.Zero
        && handle != InvalidHandle
        && !NativeConsole.GetConsoleMode(handle, out _);

    private static void UseEncoding()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 没有可用的标准输出句柄时设置编码会抛异常，忽略即可。
        }
    }

    private static class NativeConsole
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    }
}
