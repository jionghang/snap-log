using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Core;

/// <summary>
/// "写哪些小结到飞书"的那一层：取还没写入过的小结 → 调 <see cref="FeishuBitablePublisher"/> → 打上已写入标记。
/// 单独拆出来是为了让生成后自动写入、定时任务、界面按钮、命令行四条入口共用同一段逻辑，
/// 也方便在写之前先做一次"有没有东西要写"的判断（没有就别白跑一趟去换令牌）。
/// </summary>
public sealed class FeishuWriter
{
    /// <summary>单次最多写多少条。批量接口一次最多 500，留点余量。</summary>
    private const int MaxRunsPerPush = 200;

    private readonly IActivityRepository _store;
    private readonly FileLogger _log;

    public FeishuWriter(IActivityRepository store, FileLogger log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>写入范围的最早时间点：只写这么多天内生成的小结。</summary>
    public static DateTime GetEarliestRunTime(FeishuOptions options) =>
        DateTime.Today.AddDays(-(Math.Clamp(options.PushLookbackDays, 1, 365) - 1));

    /// <summary>符合写入条件的小结条数，界面上用来先说明"这一次会写几条"。</summary>
    public Task<int> CountPendingAsync(AppOptions options, CancellationToken cancellationToken) =>
        _store.CountPendingPushRunsAsync(GetEarliestRunTime(options.Feishu), cancellationToken);

    /// <summary>把还没写入过的小结写进飞书多维表格，成功后打标记，重复调用不会写出重复行。</summary>
    public async Task<FeishuPushResult> WritePendingAsync(AppOptions options, CancellationToken cancellationToken)
    {
        var problem = FeishuBitablePublisher.Validate(options.Feishu);
        if (problem is not null)
        {
            return FeishuPushResult.Fail(problem);
        }

        var from = GetEarliestRunTime(options.Feishu);
        var runs = await _store
            .GetPendingPushRunsAsync(from, MaxRunsPerPush, cancellationToken)
            .ConfigureAwait(false);

        if (runs.Count == 0)
        {
            return new FeishuPushResult(
                true,
                $"没有待写入的小结（只写 {from:yyyy-MM-dd} 之后生成、且还没写进飞书的小结）。"
                + "先生成一次小结，或把「写入范围（天）」调大以补上更早的。",
                0,
                0);
        }

        _log.Info($"准备写入 {runs.Count} 条小结到飞书多维表格");

        var publisher = new FeishuBitablePublisher(options.Feishu, _log);
        var result = await publisher.PushAsync(runs, cancellationToken).ConfigureAwait(false);

        // 只有真写成功的才打标记。失败的那几条留着，下次重试不会漏。
        var writtenIds = result.WrittenIds.Where(id => id > 0).ToList();
        if (writtenIds.Count > 0)
        {
            try
            {
                await _store.MarkSummaryRunsPushedAsync(writtenIds, DateTime.Now, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 标记失败只影响去重，不影响已经写进表里的数据，所以不当作推送失败。
                _log.Warn($"标记「已写入飞书」失败（下次可能会重复写入这几条）：{ex.Message}");
            }
        }

        return result;
    }
}
