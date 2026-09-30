namespace SnapLog.Storage;

/// <summary>
/// 记录筛选条件。所有字段为 null 表示不限制。
/// 时间范围是闭区间，按秒比较（库里存的是 yyyy-MM-dd HH:mm:ss 文本，可直接字典序比较）。
/// </summary>
public sealed record ActivityQuery
{
    /// <summary>时间范围的默认下限，用于 DateTimePicker 的初始值。</summary>
    public static readonly DateTime DefaultFrom = DateTime.Today.AddDays(-7);

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    public string? ProcessName { get; init; }

    /// <summary>关键词，同时匹配窗口标题和识别文字，不区分大小写。</summary>
    public string? Keyword { get; init; }

    public RecordStatus? Status { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; } = 100;

    public ActivityQuery WithOffset(int offset) => this with { Offset = offset };
}

/// <summary>分页结果。</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Offset, int Limit)
{
    public static PagedResult<T> Empty { get; } = new([], 0, 0, 0);

    public int PageCount => Limit <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)Limit));

    public int PageNumber => Limit <= 0 ? 1 : (Offset / Limit) + 1;

    public bool HasPrevious => Offset > 0;

    public bool HasNext => Offset + Items.Count < TotalCount;
}

/// <summary>
/// 记录列表里的一行。刻意不带完整 OCR 文字——列表页只需要摘要，
/// 完整内容按需通过 <see cref="IActivityStore.GetByIdAsync"/> 单独取，
/// 这样即使库里存了几十万条，翻页仍然是常量级开销。
/// </summary>
public sealed record ActivityListItem(
    long Id,
    DateTime Timestamp,
    string ProcessName,
    string WindowTitle,
    int TextLength,
    long OcrMilliseconds,
    string CaptureMethod,
    RecordStatus Status,
    string Preview);

/// <summary>记录的读写接口。当前实现是 SQLite；换存储只要实现这个接口。</summary>
public interface IActivityStore : IAsyncDisposable
{
    /// <summary>建库建表、开启 WAL、必要时从旧版 CSV 导入。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>数据库文件路径。</summary>
    string Location { get; }

    Task AppendAsync(ActivityRecord record, CancellationToken cancellationToken);

    Task<PagedResult<ActivityListItem>> QueryAsync(ActivityQuery query, CancellationToken cancellationToken);

    Task<ActivityRecord?> GetByIdAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// 取最近 <paramref name="count"/> 条完整记录（含 OCR 文字），按时间升序返回。
    /// 供总结流程使用——<see cref="QueryAsync"/> 不带完整文字，这里刻意单独开一个方法，
    /// 让"最近的少量记录要全文"和"列表页要分页摘要"各自走最合适的查询。
    /// </summary>
    Task<IReadOnlyList<ActivityRecord>> GetRecentAsync(int count, CancellationToken cancellationToken);

    /// <summary>
    /// 取某一天（本地时间当天 00:00:00 到 23:59:59）的完整记录，按时间升序返回。
    /// 总结支持"只汇总某一天"时用它；<paramref name="limit"/> 兜住单次送入模型的条数上限。
    /// </summary>
    Task<IReadOnlyList<ActivityRecord>> GetByDayAsync(DateTime day, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// 按天统计记录数与当天最大记录 id（只看 <paramref name="fromDay"/> 当天及之后）。
    /// 定时生成用它算出"哪些天有记录""那一天的数据指纹是多少"。
    /// </summary>
    Task<IReadOnlyList<DayMark>> GetDayMarksAsync(DateTime fromDay, CancellationToken cancellationToken);

    /// <summary>
    /// 按 id 删除记录，返回这些记录引用过的截图文件路径（相对或绝对，未去重前的原始值）。
    /// 只负责删库里的行；要不要连截图文件一起删由调用方决定——那是不可撤销的操作，得让用户点头。
    /// </summary>
    Task<IReadOnlyList<string>> DeleteByIdsAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken);

    /// <summary>当前库里出现过的进程名，供筛选下拉框使用。</summary>
    Task<IReadOnlyList<string>> GetProcessNamesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 批量写入（从旧版 CSV 导入用）。按 <paramref name="batchSize"/> 分块提交，
    /// 内存占用与文件大小无关。
    /// </summary>
    Task<int> ImportAsync(IEnumerable<ActivityRecord> records, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// 按筛选条件分批读出记录（导出用），按时间升序。
    /// 交给回调而不是返回集合，避免一次性把几十万条读进内存。
    /// </summary>
    Task<int> ReadBatchesAsync(
        ActivityQuery query,
        int batchSize,
        Func<IReadOnlyList<ActivityRecord>, CancellationToken, Task> onBatch,
        CancellationToken cancellationToken);

    Task<long> CountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 删除 <paramref name="cutoff"/> 之前的记录，返回删除条数。
    /// 供保留策略使用；调用方负责算好 cutoff（本地时间）。
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken);

    /// <summary>
    /// 把这些记录之前的截图路径字段清空。和截图文件清理配套使用，
    /// 免得库里留着已经指向不存在文件的路径。
    /// </summary>
    Task<int> ClearImagePathsBeforeAsync(DateTime cutoff, CancellationToken cancellationToken);

    // ---------------------------------------------------------------- 定时批量识别

    /// <summary>
    /// 取还没识别过的记录（“识别方式 = 每日定时”下产生的 Pending 记录），按时间升序。
    /// 升序是有意的：批次先处理最早的，积压时优先补上历史。
    /// </summary>
    Task<IReadOnlyList<ActivityRecord>> GetPendingRecordsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>还有多少条待识别，用于界面显示和调度判断。</summary>
    Task<int> CountPendingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 回写一条记录的识别结果。批次任务专用；
    /// 图片文件丢了等情况会被记成 Error，而不是一直挂在 Pending 上每轮重试。
    /// </summary>
    Task UpdateOcrResultAsync(
        long id,
        string text,
        long ocrMilliseconds,
        RecordStatus status,
        string error,
        CancellationToken cancellationToken);
}

/// <summary>某一天的记录统计：条数与当天最大记录 id（用作"数据指纹"）。</summary>
public sealed record DayMark(DateTime Day, int Count, long MaxId);
