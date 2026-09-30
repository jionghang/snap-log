namespace SnapLog.Configuration;

/// <summary>集中解析所有文件系统位置，避免各处硬编码路径。</summary>
public sealed class AppPaths
{
    public const string AppFolderName = "SnapLog";

    /// <summary>旧版把记录写成这个文件，首次启动 SQLite 时会从它导入。</summary>
    public const string LegacyCsvFileName = "activity.csv";

    private AppPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    public string DataDirectory { get; }

    public string SummariesDirectory => Path.Combine(DataDirectory, "summaries");

    public string ImagesDirectory => Path.Combine(DataDirectory, "images");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>旧版 CSV 的固定位置，用于一次性导入。</summary>
    public string LegacyCsvPath => Path.Combine(DataDirectory, LegacyCsvFileName);

    /// <summary>程序运行状态文件（界面标记、定时任务上次执行时间），刻意和用户配置分开，见 AppStateStore。</summary>
    public string AppStatePath => Path.Combine(DataDirectory, "state.json");

    /// <summary>用户级配置：优先级高于程序目录里的 appsettings.json。</summary>
    public static string UserConfigPath => Path.Combine(UserDataDirectory, "appsettings.json");

    /// <summary>随程序发布的默认配置。</summary>
    public static string ShippedConfigPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private static string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static AppPaths Create(string? dataDirectoryFromConfig)
    {
        var root = string.IsNullOrWhiteSpace(dataDirectoryFromConfig)
            ? UserDataDirectory
            : Environment.ExpandEnvironmentVariables(dataDirectoryFromConfig.Trim());

        return new AppPaths(Path.GetFullPath(root));
    }

    /// <summary>SQLite 数据库文件的完整路径。</summary>
    public string ResolveDatabasePath(string fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "activity.db" : fileName.Trim();
        return Path.Combine(DataDirectory, name);
    }

    /// <summary>截图存放目录：配置了就用配置的（支持环境变量），否则用数据目录下的 images。</summary>
    public string ResolveImageDirectory(string configuredDirectory) =>
        string.IsNullOrWhiteSpace(configuredDirectory)
            ? ImagesDirectory
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredDirectory.Trim()));

    /// <summary>
    /// 把库里存的截图路径还原成绝对路径。
    /// 新记录存的是绝对路径；旧版本存的是相对数据目录的 "images\\xxx.png"，两种都要能打开。
    /// </summary>
    public string ResolveStoredImagePath(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(storedPath)
            ? storedPath
            : Path.Combine(DataDirectory, storedPath);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(SummariesDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    /// <summary>
    /// 决定配置该写回哪里：沿用加载时用的那份（可能是 --config 指定的），
    /// 但不能往程序目录写（安装路径可能没有写权限），所以随程序附带的那份要落到用户目录。
    /// </summary>
    public static string ResolveConfigWriteTarget(string? loadedConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(loadedConfigPath)
            && !string.Equals(loadedConfigPath, ShippedConfigPath, StringComparison.OrdinalIgnoreCase))
        {
            return loadedConfigPath;
        }

        return UserConfigPath;
    }
}
