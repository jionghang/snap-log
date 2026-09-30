using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Core;

/// <summary>
/// "写哪些记录到飞书"的那一层：取当天记录 → 调 <see cref="FeishuBitablePublisher"/>。
/// 单独拆出来是为了让定时任务、界面按钮、命令行三条入口共用同一段逻辑，
/// 也方便在写之前先做一次"要不要写"的判断（比如今天还没记录就别白跑一趟）。
/// </summary>
public sealed class FeishuWriter
{
    private readonly IActivityStore _store;
    private readonly FileLogger _log;

    public FeishuWriter(IActivityStore store, FileLogger log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>把当天的记录写进飞书多维表格。</summary>
    public async Task<FeishuPushResult> WriteTodayAsync(AppOptions options, CancellationToken cancellationToken)
    {
        var problem = FeishuBitablePublisher.Validate(options.Feishu);
        if (problem is not null)
        {
            return FeishuPushResult.Fail(problem);
        }

        var today = DateTime.Today;
        var records = await _store
            .QueryAsync(
                new ActivityQuery { From = today, To = today.AddDays(1).AddSeconds(-1), Limit = 5000 },
                cancellationToken)
            .ConfigureAwait(false);

        if (records.TotalCount == 0)
        {
            return new FeishuPushResult(true, "今天还没有记录，跳过推送", 0, 0);
        }

        // 列表查询只带摘要，写飞书需要完整文字，所以按 id 逐条补全。
        var full = new List<ActivityRecord>(records.Items.Count);
        foreach (var item in records.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = await _store.GetByIdAsync(item.Id, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                full.Add(record);
            }
        }

        _log.Info($"准备推送 {full.Count} 条记录到飞书多维表格");

        var publisher = new FeishuBitablePublisher(options.Feishu, _log);
        return await publisher.PushAsync(full, cancellationToken).ConfigureAwait(false);
    }
}
