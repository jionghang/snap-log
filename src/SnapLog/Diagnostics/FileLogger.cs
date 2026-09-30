using System.Text;

namespace SnapLog.Diagnostics;

public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message)
{
    public override string ToString() => $"{Timestamp:HH:mm:ss} [{Level}] {Message}";
}

/// <summary>
/// 极简文件日志：按天滚动、启动时清理超期文件，同时把最近若干条留在内存里给界面用。
/// 刻意不引入日志框架，避免为此多带一个依赖。
/// </summary>
public sealed class FileLogger
{
    private const int RingBufferSize = 500;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _recent = new(RingBufferSize);
    private readonly string _directory;

    private bool _enabled;
    private LogLevel _minimumLevel = LogLevel.Info;
    private int _retentionDays = 7;

    public FileLogger(string logDirectory)
    {
        _directory = logDirectory;
    }

    public static FileLogger Null { get; } = new(string.Empty) { _enabled = false };

    /// <summary>界面订阅这个事件来实时显示日志；会在写入线程上触发。</summary>
    public event Action<LogEntry>? EntryWritten;

    public void Configure(bool enabled, string levelName, int retentionDays)
    {
        _enabled = enabled;
        _minimumLevel = Enum.TryParse<LogLevel>(levelName, ignoreCase: true, out var parsed) ? parsed : LogLevel.Info;
        _retentionDays = retentionDays;
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _recent.ToArray();
        }
    }

    public void Trace(string message) => Write(LogLevel.Trace, message);

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warn(string message) => Write(LogLevel.Warn, message);

    public void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>异常日志要带上完整栈，否则排查只能靠猜。</summary>
    public void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message} :: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    public void Write(LogLevel level, string message)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        var entry = new LogEntry(DateTime.Now, level, message);

        lock (_gate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > RingBufferSize)
            {
                _recent.Dequeue();
            }
        }

        EntryWritten?.Invoke(entry);

        if (!_enabled || string.IsNullOrEmpty(_directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"snaplog-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, entry + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志写不进去不应该影响主流程，最多丢掉这行。
        }
    }

    /// <summary>启动时清理过期日志，避免长期运行把磁盘塞满。</summary>
    public void CleanupExpired()
    {
        if (!_enabled || _retentionDays <= 0 || string.IsNullOrEmpty(_directory) || !Directory.Exists(_directory))
        {
            return;
        }

        var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "snaplog-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
