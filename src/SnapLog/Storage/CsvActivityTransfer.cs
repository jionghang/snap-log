using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using SnapLog.Diagnostics;

namespace SnapLog.Storage;

/// <summary>
/// CSV 的导入与导出。
/// 列定义复用 <see cref="ActivityRecordMap"/>，所以导出的文件可以再导入回来，两边不会漂移。
/// 写入用带 BOM 的 UTF-8，否则 Excel 打开中文是乱码。
/// </summary>
public static class CsvActivityTransfer
{
    private const int BatchSize = 500;

    /// <summary>
    /// 按筛选条件把记录导出成 CSV。返回导出的条数。
    /// 分批从库里读、边读边写，内存占用与记录总数无关。
    /// </summary>
    public static async Task<int> ExportAsync(
        IActivityStore store,
        ActivityQuery query,
        string path,
        FileLogger log,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        using var csv = new CsvWriter(writer, CreateWriteConfiguration());
        csv.Context.RegisterClassMap<ActivityRecordMap>();
        csv.WriteHeader<ActivityRecord>();
        await csv.NextRecordAsync().ConfigureAwait(false);

        var written = await store.ReadBatchesAsync(
            query,
            BatchSize,
            async (batch, ct) =>
            {
                foreach (var record in batch)
                {
                    csv.WriteRecord(record);
                    await csv.NextRecordAsync().ConfigureAwait(false);
                }

                await writer.FlushAsync(ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        log.Info($"已导出 {written} 条记录到 {path}");
        return written;
    }

    /// <summary>
    /// 从旧版 activity.csv 导入。逐行惰性读取，交给存储层分块提交，所以文件再大也不会一次性进内存。
    /// 返回导入条数。坏行会被跳过并记日志，不会中断整个导入。
    /// </summary>
    public static async Task<int> ImportLegacyAsync(
        string csvPath,
        IActivityStore store,
        FileLogger log,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(csvPath))
        {
            return 0;
        }

        var imported = await store
            .ImportAsync(ReadRecords(csvPath, log), BatchSize, cancellationToken)
            .ConfigureAwait(false);

        log.Info($"已从 {csvPath} 导入 {imported} 条历史记录");
        return imported;
    }

    /// <summary>
    /// 逐行惰性读取 CSV。行尾差异（LF / CRLF）由 <see cref="NormalizedTextReader"/> 统一抹平，
    /// 所以外部工具生成的文件也能正常导入。坏行跳过并记日志，不中断整个导入。
    /// </summary>
    public static IEnumerable<ActivityRecord> ReadRecords(string path, FileLogger log)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var inner = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var normalized = new NormalizedTextReader(inner);
        using var csv = new CsvReader(normalized, CreateReadConfiguration());
        csv.Context.RegisterClassMap<ActivityRecordMap>();

        while (csv.Read())
        {
            ActivityRecord? record;
            try
            {
                record = csv.GetRecord<ActivityRecord>();
            }
            catch (CsvHelperException ex)
            {
                // 上次运行被强杀时可能留下半行，跳过它而不是让整个历史读不出来。
                log.Warn($"跳过 CSV 中无法解析的一行：{ex.Message}");
                continue;
            }

            if (record is not null)
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// 写文件用 CRLF：Excel 和记事本都认。
    /// </summary>
    public static CsvConfiguration CreateWriteConfiguration() => new(CultureInfo.InvariantCulture)
    {
        // 字段内容里本来就有换行（OCR 是多行的），统一加引号最省事也最不容易出错。
        ShouldQuote = _ => true,
        MissingFieldFound = null,
        BadDataFound = null,
        TrimOptions = TrimOptions.Trim,
        NewLine = "\r\n",
    };

    /// <summary>
    /// 读取配置按 \n 断行，必须配合 <see cref="NormalizedTextReader"/> 使用——它保证流里只有 \n，
    /// 因此这一项既不会把 LF 文件误判成一行，也不会切坏 CRLF 文件里的多行字段。
    /// </summary>
    public static CsvConfiguration CreateReadConfiguration() => new(CultureInfo.InvariantCulture)
    {
        MissingFieldFound = null,
        BadDataFound = null,
        TrimOptions = TrimOptions.Trim,
        NewLine = "\n",
    };
}
