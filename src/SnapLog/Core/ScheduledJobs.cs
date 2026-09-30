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

/// <summary>定时生成总结。结果（含失败原因）会记进总结历史。</summary>
public sealed class SummaryJob : IScheduledJob
{
    private readonly SummaryRunner _runner;

    public SummaryJob(SummaryRunner runner)
    {
        _runner = runner;
    }

    public string Key => "summary";

    public string DisplayName => "定时生成总结";

    public bool IsEnabled(AppOptions options) => options.Summarization.ScheduleEnabled;

    public TimeOnly? GetScheduledTime(AppOptions options) =>
        TimeOnly.TryParse(options.Summarization.ScheduleTimeOfDay, out var time) ? time : null;

    public async Task<JobRunResult> RunAsync(AppOptions options, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(options, "定时", cancellationToken).ConfigureAwait(false);
        return new JobRunResult(result.Success, result.Message);
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

    public bool IsEnabled(AppOptions options) => options.Feishu.Enabled;

    public TimeOnly? GetScheduledTime(AppOptions options) =>
        TimeOnly.TryParse(options.Feishu.ScheduleTimeOfDay, out var time) ? time : null;

    public async Task<JobRunResult> RunAsync(AppOptions options, CancellationToken cancellationToken)
    {
        var result = await _writer.WritePendingAsync(options, cancellationToken).ConfigureAwait(false);
        return new JobRunResult(result.Success, result.Message);
    }
}
