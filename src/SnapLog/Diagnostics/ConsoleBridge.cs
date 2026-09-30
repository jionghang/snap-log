using System.Runtime.InteropServices;
using System.Text;

namespace SnapLog.Diagnostics;

/// <summary>
/// WinExe 默认没有控制台，命令行模式下需要挂到父进程的控制台才能看到输出。
/// 输出被重定向到管道/文件时本来就有句柄，这里只处理交互式运行的情况。
/// </summary>
internal static class ConsoleBridge
{
    private const int ATTACH_PARENT_PROCESS = -1;

    public static void Attach()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 没有可用的标准输出句柄时设置编码会抛异常，忽略即可。
        }

        if (NativeConsole.GetConsoleWindow() != IntPtr.Zero)
        {
            return;
        }

        if (!NativeConsole.AttachConsole(ATTACH_PARENT_PROCESS))
        {
            return;
        }

        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
    }

    private static class NativeConsole
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachConsole(int dwProcessId);
    }
}
