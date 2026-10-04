using SnapLog.Configuration;

namespace SnapLog.Cli;

/// <summary>
/// 自检类命令（--selftest / --ui-smoke / --ui-shots / --menu-preview / --capture-stress）的临时沙盒：
/// 把配置和数据库各复制一份，所有读取照常、写入落在副本上。
///
/// 这类命令会模拟点击按钮、跑清理、往库里写测试记录。踩过一次真实事故：
/// --ui-smoke 点完开关又按"保存"，把"启用大模型总结/飞书推送"关掉写回了真实配置，
/// 当天的自动流程因此在配置里被关了。隔离之后，自检再怎么点都不影响真实数据。
/// </summary>
internal static class ProbeSandbox
{
    private const string FolderName = "snaplog-probe";

    /// <summary>需要沙盒的命令：都会写数据，而且写的都是"测试性质"的内容。</summary>
    public static bool AppliesTo(CliCommand command) => command is
        CliCommand.SelfTest or
        CliCommand.UiSmoke or
        CliCommand.UiShots or
        CliCommand.MenuPreview or
        CliCommand.CaptureStress;

    /// <summary>
    /// 建好沙盒目录并复制配置、数据库、状态文件。返回沙盒数据目录与副本配置路径
    /// （配置副本为空表示配置本身没找到，调用方沿用原路径）。
    /// </summary>
    public static (string DataDirectory, string? ConfigPath) Prepare(AppOptions options, string? loadedConfigPath)
    {
        var real = AppPaths.Create(options.Storage.DataDirectory);
        var root = Path.Combine(Path.GetTempPath(), FolderName);
        CleanupOld(root);

        var directory = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId);
        Directory.CreateDirectory(directory);

        string? sandboxConfig = null;
        if (!string.IsNullOrWhiteSpace(loadedConfigPath) && File.Exists(loadedConfigPath))
        {
            sandboxConfig = Path.Combine(directory, Path.GetFileName(loadedConfigPath));
            File.Copy(loadedConfigPath, sandboxConfig, overwrite: true);
        }

        // WAL 模式下最近提交的数据还在 -wal 里，只复制主文件会丢掉它们。
        var database = real.ResolveDatabasePath(options.Storage.DatabaseFileName);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var source = database + suffix;
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(directory, Path.GetFileName(source)), overwrite: true);
            }
        }

        if (File.Exists(real.AppStatePath))
        {
            File.Copy(real.AppStatePath, Path.Combine(directory, Path.GetFileName(real.AppStatePath)), overwrite: true);
        }

        return (directory, sandboxConfig);
    }

    /// <summary>顺手清掉几天前的沙盒，别让临时目录一直涨。</summary>
    private static void CleanupOld(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            var cutoff = DateTime.Now.AddDays(-3);
            foreach (var directory in Directory.GetDirectories(root))
            {
                if (Directory.GetCreationTime(directory) < cutoff)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch
        {
            // 清理失败不能影响自检本身。
        }
    }
}
