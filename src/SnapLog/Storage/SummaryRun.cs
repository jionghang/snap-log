namespace SnapLog.Storage;

/// <summary>一次总结尝试的记录。成功和失败都记，这样定时任务半夜失败了也查得到。</summary>
public sealed class SummaryRun
{
    public long Id { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.Now;

    public DateTime FinishedAt { get; set; } = DateTime.Now;

    /// <summary>触发来源：手动 / 定时 / 命令行。</summary>
    public string Trigger { get; set; } = "手动";

    public bool Success { get; set; }

    /// <summary>实际完成请求的模型（失败时为空）。</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>用了第几次尝试才成功。</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// 总结正文。刻意存进库里而不是只存文件路径：
    /// 历史查看不该依赖文件还在不在（保留策略可能已经清掉，用户也可能手动删过）。
    /// </summary>
    public string Markdown { get; set; } = string.Empty;

    /// <summary>同时落盘的 Markdown 文件路径。</summary>
    public string SavedPath { get; set; } = string.Empty;

    /// <summary>成功时的简述，失败时的原因（含每个模型的失败明细）。</summary>
    public string Message { get; set; } = string.Empty;

    public int RecordCount { get; set; }

    public int ImageCount { get; set; }

    public long ElapsedMilliseconds { get; set; }

    /// <summary>列表里显示的一行摘要。</summary>
    public string Preview
    {
        get
        {
            var source = Markdown.Length > 0 ? Markdown : Message;
            var flat = Flatten(source);
            return flat.Length <= 120 ? flat : flat[..120] + "…";
        }
    }

    /// <summary>列表里显示的时长。</summary>
    public string ElapsedText => ElapsedMilliseconds <= 0
        ? string.Empty
        : ElapsedMilliseconds < 1000
            ? $"{ElapsedMilliseconds} ms"
            : $"{ElapsedMilliseconds / 1000.0:0.#} s";

    private static string Flatten(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c is '\r' or '\n' or '\t' ? ' ' : c);
        }

        return builder.ToString().Trim();
    }
}

/// <summary>
/// 记录存储与总结历史的合并接口。两者共用同一个 SQLite 连接，所以由同一个实现类提供；
/// 汇总成一个接口是为了让"既要读记录又要写历史"的组件（如 SummaryRunner）能直接拿到，
/// 不用在调用点做类型转换。
/// </summary>
public interface IActivityRepository : IActivityStore, ISummaryHistoryStore
{
}

/// <summary>总结历史的读写。</summary>
public interface ISummaryHistoryStore
{
    Task AppendSummaryRunAsync(SummaryRun run, CancellationToken cancellationToken);

    /// <summary>按时间倒序取最近的若干条。</summary>
    Task<IReadOnlyList<SummaryRun>> GetSummaryRunsAsync(int limit, CancellationToken cancellationToken);

    Task<int> DeleteSummaryRunsBeforeAsync(DateTime cutoff, CancellationToken cancellationToken);
}
