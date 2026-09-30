using System.Text;
using SnapLog.Configuration;
using SnapLog.Ocr;
using SnapLog.Storage;

namespace SnapLog.Summarization;

/// <summary>压缩后的活动摘要，准备送进大模型。</summary>
public sealed record ActivityDigest(
    string Text,
    int IncludedRecords,
    int AvailableRecords,
    DateTime? FirstTimestamp,
    DateTime? LastTimestamp,
    bool Truncated)
{
    public bool IsEmpty => IncludedRecords == 0;

    public string DescribeRange()
    {
        if (FirstTimestamp is null || LastTimestamp is null)
        {
            return "无记录";
        }

        return $"{FirstTimestamp:yyyy-MM-dd HH:mm} ~ {LastTimestamp:yyyy-MM-dd HH:mm}";
    }
}

/// <summary>
/// 把 CSV 记录压成提示词载荷。
/// 两个降噪动作最关键：丢掉连续重复的同一画面（同一窗口反复切换会产生大量重复），
/// 以及按字符预算从最新往回截断（只保留最近的上下文）。
/// </summary>
public static class ActivityDigestBuilder
{
    private const int MaxCharactersPerRecord = 1200;

    public static ActivityDigest Build(IReadOnlyList<ActivityRecord> records, SummarizationOptions options)
    {
        if (records.Count == 0)
        {
            return new ActivityDigest(string.Empty, 0, 0, null, null, false);
        }

        var budget = options.MaxInputCharacters;
        var selected = new List<(ActivityRecord Record, string Entry)>();
        var used = 0;
        string? previousFingerprint = null;

        for (var i = records.Count - 1; i >= 0; i--)
        {
            var record = records[i];
            var body = TextNormalizer.Condense(record.OcrText, MaxCharactersPerRecord);
            if (body.Length == 0)
            {
                continue;
            }

            var fingerprint = $"{record.ProcessName}\u0001{record.WindowTitle}\u0001{body}";
            if (fingerprint == previousFingerprint)
            {
                continue;
            }

            var entry = Format(record, body);
            if (used + entry.Length > budget && selected.Count > 0)
            {
                break;
            }

            selected.Add((record, entry));
            used += entry.Length;
            previousFingerprint = fingerprint;

            if (selected.Count >= options.MaxRecords)
            {
                break;
            }
        }

        selected.Reverse();

        var builder = new StringBuilder(used);
        foreach (var (_, entry) in selected)
        {
            builder.Append(entry).Append('\n');
        }

        return new ActivityDigest(
            builder.ToString().TrimEnd(),
            selected.Count,
            records.Count,
            selected.Count > 0 ? selected[0].Record.Timestamp : null,
            selected.Count > 0 ? selected[^1].Record.Timestamp : null,
            Truncated: selected.Count < records.Count);
    }

    private static string Format(ActivityRecord record, string body)
    {
        var origin = string.IsNullOrWhiteSpace(record.ProcessName) ? "未知进程" : record.ProcessName;
        var title = string.IsNullOrWhiteSpace(record.WindowTitle) ? "(无标题)" : record.WindowTitle;

        // 汇报类文档的正文覆盖更早的时间，打上标记交给提示词里的规则处理。
        var marker = ReportDocuments.IsPeriodicReport(record.WindowTitle)
            ? $"（{ReportDocuments.Marker}）"
            : string.Empty;

        return $"[{record.Timestamp:yyyy-MM-dd HH:mm:ss}] {origin} | {title}{marker}\n{body}\n";
    }
}
