using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Core;

/// <summary>一次清理的结果，用于日志和 --cleanup 的输出。</summary>
public sealed record RetentionReport(
    int DeletedRecords,
    int DeletedImages,
    long FreedImageBytes,
    int ClearedImagePaths,
    int DeletedLogs,
    string ImageDirectory)
{
    public bool DidNothing => DeletedRecords == 0 && DeletedImages == 0 && ClearedImagePaths == 0 && DeletedLogs == 0;

    public string Describe()
    {
        if (DidNothing)
        {
            return "没有需要清理的内容";
        }

        var parts = new List<string>();
        if (DeletedRecords > 0)
        {
            parts.Add($"删除 {DeletedRecords} 条过期记录");
        }

        if (DeletedImages > 0)
        {
            parts.Add($"删除 {DeletedImages} 张过期截图（释放 {FormatBytes(FreedImageBytes)}）");
        }

        if (ClearedImagePaths > 0)
        {
            parts.Add($"清空 {ClearedImagePaths} 条记录的截图路径");
        }

        if (DeletedLogs > 0)
        {
            parts.Add($"删除 {DeletedLogs} 个过期日志");
        }

        return string.Join("；", parts);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}

/// <summary>
/// 保留策略：按配置的天数清理过期记录和截图文件。
///
/// 设计上的两个取舍：
/// - 只按"记录的抓取时间"和"文件的修改时间"判断，不猜内容；
/// - 截图被删后，对应记录的 image_path 会被清空（而不是留一个指向不存在文件的路径），
///   这样记录查看器里"有没有截图"永远是真的。
/// </summary>
public sealed class RetentionService
{
    private readonly IActivityStore _store;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;

    public RetentionService(IActivityStore store, AppPaths paths, FileLogger log)
    {
        _store = store;
        _paths = paths;
        _log = log;
    }

    public async Task<RetentionReport> RunAsync(AppOptions options, CancellationToken cancellationToken)
    {
        var now = DateTime.Now;

        var deletedRecords = 0;
        if (options.Storage.RecordRetentionDays > 0)
        {
            var cutoff = now.Date.AddDays(-options.Storage.RecordRetentionDays);
            deletedRecords = await _store.DeleteOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
        }

        var imageDirectory = _paths.ResolveImageDirectory(options.Capture.ImageDirectory);
        var deletedImages = 0;
        var freedBytes = 0L;
        var clearedPaths = 0;

        if (options.Capture.ImageRetentionDays > 0)
        {
            var cutoff = now.Date.AddDays(-options.Capture.ImageRetentionDays);

            (deletedImages, freedBytes) = await Task
                .Run(() => DeleteOldImages(imageDirectory, cutoff), cancellationToken)
                .ConfigureAwait(false);

            // 文件删了，库里的路径也要清掉，否则记录查看器会指向不存在的文件。
            clearedPaths = await _store.ClearImagePathsBeforeAsync(cutoff, cancellationToken).ConfigureAwait(false);
        }

        // 日志的清理在 FileLogger 启动时已经做过一次，这里顺手再跑一遍（长期不重启的进程也有效）。
        var logsBefore = CountLogs();
        _log.CleanupExpired();
        var deletedLogs = Math.Max(0, logsBefore - CountLogs());

        var report = new RetentionReport(
            deletedRecords, deletedImages, freedBytes, clearedPaths, deletedLogs, imageDirectory);

        if (report.DidNothing)
        {
            _log.Debug("保留策略：没有需要清理的内容");
        }
        else
        {
            _log.Info($"保留策略执行完成：{report.Describe()}");
        }

        return report;
    }

    private (int Count, long Bytes) DeleteOldImages(string directory, DateTime cutoff)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        var count = 0;
        var bytes = 0L;

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime >= cutoff)
                {
                    continue;
                }

                var size = info.Length;
                info.Delete();
                count++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 文件被占用（比如正被图片查看器打开）就跳过，下次再说。
                _log.Warn($"删除过期截图失败：{Path.GetFileName(file)} —— {ex.Message}");
            }
        }

        return (count, bytes);
    }

    private int CountLogs()
    {
        try
        {
            return Directory.Exists(_paths.LogsDirectory)
                ? Directory.GetFiles(_paths.LogsDirectory, "snaplog-*.log").Length
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
