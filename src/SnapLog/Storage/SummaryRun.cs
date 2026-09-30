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

    /// <summary>
    /// 这次总结覆盖的是哪一天（yyyy-MM-dd）；空串表示覆盖的是"最近的记录"，不属于某一天。
    /// 定时生成按天汇总就靠它判断"这一天是否已经生成过"。
    /// </summary>
    public string CoveredDay { get; set; } = string.Empty;

    /// <summary>
    /// 生成时那一天的"数据指纹"（当天最大记录 id；0 表示未按天生成）。
    /// 之后这一天又新增了记录，指纹就会变，定时任务据此重新生成并覆盖旧总结。
    /// </summary>
    public long CoveredMarks { get; set; }

    /// <summary>
    /// 生成时那一天"最后一次记录改动时间"（yyyy-MM-dd HH:mm:ss）。
    /// 记录被后续识别回填过文字，这个值就会变，定时任务据此重新生成那天的总结。
    /// </summary>
    public string CoveredTextRevision { get; set; } = string.Empty;

    /// <summary>
    /// 写进飞书多维表格的时间；null = 还没写入过。
    /// 去重就靠这个标记：写成功才打标，重复点“立即写入”不会在表里刷出重复行。
    /// </summary>
    public DateTime? PushedAt { get; set; }

    /// <summary>已经写进飞书了吗。</summary>
    public bool Pushed => PushedAt is not null;

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

    /// <summary>
    /// 取还没写进飞书的多维表格的成功总结，按时间正序（先发生的先写）。
    /// 只取 <paramref name="from"/> 之后生成的，避免第一次开启时把历史总结一股脑导进表里。
    /// </summary>
    Task<IReadOnlyList<SummaryRun>> GetPendingPushRunsAsync(
        DateTime from,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>符合待写入条件的总结条数，界面上用来先告诉用户"这一次会写几条"。</summary>
    Task<int> CountPendingPushRunsAsync(DateTime from, CancellationToken cancellationToken);

    /// <summary>
    /// 按 id 删除总结历史，返回这些总结保存过的 Markdown 文件路径。
    /// 与记录删除同理：只删库里的行，文要不要删由调用方按用户勾选决定。
    /// </summary>
    Task<IReadOnlyList<string>> DeleteSummaryRunsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken);

    /// <summary>把若干条总结标记成已写进飞书。</summary>
    Task MarkSummaryRunsPushedAsync(
        IReadOnlyList<long> ids,
        DateTime pushedAt,
        CancellationToken cancellationToken);

    Task<int> DeleteSummaryRunsBeforeAsync(DateTime cutoff, CancellationToken cancellationToken);
}
