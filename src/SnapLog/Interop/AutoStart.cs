using Microsoft.Win32;

namespace SnapLog.Interop;

/// <summary>
/// 开机自启动：在 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 里放一条指向本程序的命令。
///
/// 用当前用户这一支（HKCU）而不是 HKLM：不需要管理员权限，用户自己就能开关，
/// 而且不同用户的设置互不干扰。程序的"启动后最小化到托盘"是默认开着的，所以命令行不需要额外参数。
///
/// 判定"是否已启用"时连路径一起比对：程序被挪到别处（比如从 Debug 换成 Release 目录）后，
/// 旧路径的条目虽然还在，但启动的是一个不存在的文件——那种情况当作没启用，勾选时会写上新路径。
/// </summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>注册表里这一项的值名。</summary>
    private const string ValueName = "SnapLog";

    /// <summary>本程序当前的可执行文件路径（拿不到时返回空串）。</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    /// <summary>当前是否已设为开机自启动，并且指向的就是现在这个可执行文件。</summary>
    public static bool IsEnabled() => IsEnabled(ValueName, ExecutablePath);

    /// <summary>设置或取消开机自启动，返回是否成功。</summary>
    public static bool SetEnabled(bool enabled) => SetEnabled(ValueName, enabled, ExecutablePath);

    // ---------------------------------------------------------------- 可测的核心

    /// <summary>读取指定项，判断它是否指向 <paramref name="expectedPath"/>。</summary>
    internal static bool IsEnabled(string valueName, string expectedPath)
    {
        if (string.IsNullOrEmpty(expectedPath))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var stored = key?.GetValue(valueName) as string;
            return !string.IsNullOrWhiteSpace(stored) && PathsMatch(stored!, expectedPath);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>写入或删除指定项。写入时路径加引号，免得带空格的路径被拆成两段。</summary>
    internal static bool SetEnabled(string valueName, bool enabled, string executablePath)
    {
        if (enabled && string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(valueName, $"\"{executablePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>注册表里存的命令是带引号的路径，这里拆掉引号再比较，且忽略大小写与结尾斜杠。</summary>
    private static bool PathsMatch(string storedCommand, string expectedPath)
    {
        var stored = storedCommand.Trim().Trim('"');
        return string.Equals(
            stored.TrimEnd('\\'),
            expectedPath.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }
}
