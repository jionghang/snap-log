using System.Diagnostics;
using System.Drawing;
using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Ocr;
using SnapLog.Storage;

namespace SnapLog.Core;

/// <summary>把 Pending 记录批量补识别。对应“识别方式 = 每日定时”。</summary>
public sealed class OcrBatchJob : IScheduledJob
{
    /// <summary>一批最多处理多少条。设上限是为了别让单次任务跑几个小时，剩下的下一轮继续。</summary>
    private const int BatchLimit = 2000;

    private readonly IActivityStore _store;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;
    private readonly Func<IOcrEngine> _engineFactory;

    public OcrBatchJob(IActivityStore store, AppPaths paths, FileLogger log, Func<IOcrEngine> engineFactory)
    {
        _store = store;
        _paths = paths;
        _log = log;
        _engineFactory = engineFactory;
    }

    public string Key => "ocr-batch";

    public string DisplayName => "批量文字识别";

    public bool IsEnabled(AppOptions options) => options.Ocr.Mode == OcrRunMode.ScheduledBatch;

    public TimeOnly? GetScheduledTime(AppOptions options) =>
        TimeOnly.TryParse(options.Ocr.BatchTimeOfDay, out var time) ? time : null;

    public async Task<JobRunResult> RunAsync(AppOptions options, CancellationToken cancellationToken)
    {
        var pending = await _store.CountPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending == 0)
        {
            return JobRunResult.Skipped("没有待识别的记录");
        }

        var records = await _store
            .GetPendingRecordsAsync(BatchLimit, cancellationToken)
            .ConfigureAwait(false);

        _log.Info($"批量识别开始：待识别 {pending} 条，本批处理 {records.Count} 条");

        using var engine = _engineFactory();
        if (!engine.IsAvailable)
        {
            return new JobRunResult(false, $"OCR 引擎不可用：{engine.Description}");
        }

        var stopwatch = Stopwatch.StartNew();
        var succeeded = 0;
        var noText = 0;
        var failed = 0;

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imagePath = _paths.ResolveStoredImagePath(record.ImagePath);
            if (imagePath.Length == 0 || !File.Exists(imagePath))
            {
                // 图不在了（保留策略清掉了、或者被外部删除）。标成 Error，
                // 否则这条会永远挂在 Pending 上每轮重试一遍。
                await _store.UpdateOcrResultAsync(
                    record.Id, string.Empty, 0, RecordStatus.Error, "截图文件不存在，无法识别", cancellationToken)
                    .ConfigureAwait(false);
                failed++;
                continue;
            }

            try
            {
                using var bitmap = new Bitmap(imagePath);
                var outcome = await engine.RecognizeAsync(bitmap, cancellationToken).ConfigureAwait(false);

                if (!outcome.Success)
                {
                    await _store.UpdateOcrResultAsync(
                        record.Id, string.Empty, 0, RecordStatus.Error, outcome.Error ?? "识别失败", cancellationToken)
                        .ConfigureAwait(false);
                    failed++;
                    continue;
                }

                var status = outcome.Text.Length >= options.Ocr.MinTextLength ? RecordStatus.Ok : RecordStatus.NoText;

                await _store.UpdateOcrResultAsync(
                    record.Id,
                    outcome.Text,
                    (long)outcome.Elapsed.TotalMilliseconds,
                    status,
                    outcome.Warning ?? string.Empty,
                    cancellationToken).ConfigureAwait(false);

                if (status == RecordStatus.Ok)
                {
                    succeeded++;
                }
                else
                {
                    noText++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"批量识别第 {record.Id} 条失败：{ex.Message}");
                await _store.UpdateOcrResultAsync(
                    record.Id, string.Empty, 0, RecordStatus.Error, ex.Message, cancellationToken)
                    .ConfigureAwait(false);
                failed++;
            }
        }

        stopwatch.Stop();

        var remaining = await _store.CountPendingAsync(cancellationToken).ConfigureAwait(false);
        var summary = $"识别 {records.Count} 条：成功 {succeeded}、无文字 {noText}、失败 {failed}，"
                      + $"耗时 {stopwatch.Elapsed.TotalSeconds:0.#} 秒"
                      + (remaining > 0 ? $"，还剩 {remaining} 条（下一轮继续）" : string.Empty);

        return new JobRunResult(failed == 0, summary);
    }
}

/// <summary>
/// 定时生成总结：按天补生成。
///
/// 每次只做两件事：把还没生成过的天生成出来；已经生成过、但那天后来又有了新记录的天，
/// 重新生成并覆盖旧的那份。规划逻辑在 <see cref="SummaryPlanner"/> 里，这里只负责执行。
/// </summary>
public sealed class SummaryJob : IScheduledJob
{
    private readonly SummaryRunner _runner;
    private readonly IActivityRepository _store;

    public SummaryJob(SummaryRunner runner, IActivityRepository store)
    {
        _runner = runner;
        _store = store;
    }

    public string Key => "summary";

    public string DisplayName => "定时生成总结";

    public bool IsEnabled(AppOptions options) => options.Summarization.ScheduleEnabled;

    public TimeOnly? GetScheduledTime(AppOptions options) =>
        TimeOnly.TryParse(options.Summarization.ScheduleTimeOfDay, out var time) ? time : null;

    public async Task<JobRunResult> RunAsync(AppOptions options, CancellationToken cancellationToken)
    {
        // 只总结"已经过完的日子"：规划器处理到今天之前（含昨天）。
        //
        // 为什么不再"准点跑今天"：当天还没过完，22:00 之后的新记录、以及次日凌晨补识别的文字
        // 都会让当天的数据指纹变化 → 第二天判定为"有更新"→ 重新生成 → 又推一次 →
        // 飞书表里同一天出现两行。改成只跑整天之后，这份总结落库就不会再变，也就不会重复。
        // 代价是日报晚几个小时到位（第二天到点出前一天的），换来的是表里每天干净一行。
        var boundary = DateOnly.FromDateTime(DateTime.Today);

        var marks = await _store
            .GetDayMarksAsync(boundary.AddDays(-SummaryPlanner.LookbackDays).ToDateTime(TimeOnly.MinValue), cancellationToken)
            .ConfigureAwait(false);

        var history = await _store.GetSummaryRunsAsync(200, cancellationToken).ConfigureAwait(false);

        var plan = SummaryPlanner.Plan(boundary, marks, history);
        if (plan.Count == 0)
        {
            return JobRunResult.Skipped("没有需要生成的天");
        }

        var lines = new List<string>();
        var succeeded = 0;

        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _runner.RunAsync(options, "定时", item.Day, cancellationToken).ConfigureAwait(false);

            // 生成成功之后才删旧的那一份（同一天只留最新）：以前是先删后生成，
            // 模型一失败（断网、额度、密钥失效）旧记录和旧文件就没了，一次故障能抹掉好几天历史。
            if (result.Success && item.Reason == SummaryPlanReason.Updated)
            {
                await RemovePreviousRunAsync(item.Day, history, cancellationToken).ConfigureAwait(false);
            }

            if (result.Success)
            {
                succeeded++;
                lines.Add($"{item.Day:yyyy-MM-dd} 已生成（{(item.Reason == SummaryPlanReason.Missing ? "新增" : "覆盖旧版")}）");
            }
            else
            {
                lines.Add($"{item.Day:yyyy-MM-dd} 未生成：{result.Message}");
            }
        }

        var summary = $"计划 {plan.Count} 天（{SummaryPlanner.Describe(plan)}）；成功 {succeeded} 天。"
                      + Environment.NewLine + string.Join(Environment.NewLine, lines);

        return new JobRunResult(succeeded == plan.Count, summary);
    }

    /// <summary>删掉某一天旧的总结记录，并顺手清掉它的 Markdown 文件（内容已被新版取代）。</summary>
    private async Task RemovePreviousRunAsync(
        DateOnly day,
        IReadOnlyList<SummaryRun> history,
        CancellationToken cancellationToken)
    {
        var key = day.ToString("yyyy-MM-dd");

        var stale = history
            .Where(run => string.Equals(run.CoveredDay, key, StringComparison.Ordinal))
            .ToList();

        if (stale.Count == 0)
        {
            return;
        }

        // 新版会写到 summary-<日期>.md；旧版可能是带时间戳的文件名，那些要删掉免得留一堆孤儿。
        var expected = $"summary-{key}.txt";

        foreach (var run in stale)
        {
            if (run.SavedPath.Length > 0
                && !string.Equals(Path.GetFileName(run.SavedPath), expected, StringComparison.OrdinalIgnoreCase)
                && File.Exists(run.SavedPath))
            {
                try
                {
                    File.Delete(run.SavedPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 删不掉不影响生成，留个日志。
                }
            }
        }

        await _store
            .DeleteSummaryRunsAsync([.. stale.Select(run => run.Id)], cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>定时把还没写入过的总结写进飞书多维表格。</summary>
public sealed class FeishuPushJob : IScheduledJob
{
    private readonly FeishuWriter _writer;

    public FeishuPushJob(FeishuWriter writer)
    {
        _writer = writer;
    }

    public string Key => "feishu-push";

    public string DisplayName => "定时写入总结到飞书";

    public bool IsEnabled(AppOptions options) =>
        options.Feishu.Enabled && options.Feishu.ScheduleEnabled && options.Summarization.ConsentGranted;

    public TimeOnly? GetScheduledTime(AppOptions options) =>
        TimeOnly.TryParse(options.Feishu.ScheduleTimeOfDay, out var time) ? time : null;

    public async Task<JobRunResult> RunAsync(AppOptions options, CancellationToken cancellationToken)
    {
        var result = await _writer.WritePendingAsync(options, cancellationToken).ConfigureAwait(false);
        return new JobRunResult(result.Success, result.Message);
    }
}
