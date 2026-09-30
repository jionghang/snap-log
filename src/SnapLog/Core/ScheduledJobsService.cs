using SnapLog.Configuration;
using SnapLog.Diagnostics;

namespace SnapLog.Core;

/// <summary>定时任务跑完的结果，用于日志和界面显示。</summary>
public sealed record JobRunResult(bool Success, string Message)
{
    public static JobRunResult Skipped(string reason) => new(true, reason);
}

/// <summary>
/// 一个"每天到点跑一次"的任务。
/// 只负责"该不该跑"和"跑什么"，什么时候轮询、跑过之后怎么记录由调度器统一管。
/// </summary>
public interface IScheduledJob
{
    /// <summary>稳定标识，用来在状态文件里记住"今天跑过了"。</summary>
    string Key { get; }

    /// <summary>显示名。</summary>
    string DisplayName { get; }

    /// <summary>当前配置下这个任务是否启用。</summary>
    bool IsEnabled(AppOptions options);

    /// <summary>设定的执行时刻。返回 null 表示配置里的时间不合法，任务会被跳过并报错。</summary>
    TimeOnly? GetScheduledTime(AppOptions options);

    /// <summary>不按时间、立刻执行（界面按钮和命令行用）。</summary>
    Task<JobRunResult> RunAsync(AppOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// 定时任务调度器。每半分钟醒一次，看有没有任务到点了还没跑。
///
/// 几个刻意的设计：
/// - **一天只跑一次**，而且这个"跑过了"记在状态文件里，重启程序不会重跑
///   （否则电脑一天重启三次就识别三次、总结三次、推送三次）。
/// - **错过就补跑**：如果设定时间在程序启动之前（比如定了 02:00，早上 9 点才开机），
///   启动后第一次检查就会跑。这符合"每日任务"的预期。
/// - 三个任务互不影响，一个失败不影响另一个。
/// </summary>
public sealed class ScheduledJobsService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<IScheduledJob> _jobs;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public ScheduledJobsService(IReadOnlyList<IScheduledJob> jobs, AppPaths paths, FileLogger log)
    {
        _jobs = jobs;
        _paths = paths;
        _log = log;
    }

    /// <summary>某个任务跑完时触发（在后台线程）。界面据此刷新状态。</summary>
    public event EventHandler<(IScheduledJob Job, JobRunResult Result)>? JobCompleted;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _log.Info($"定时任务调度已启动（{string.Join("、", _jobs.Select(j => j.DisplayName))}）");
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _loop = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>当前时刻各任务的下一轮执行时间，给界面和 --diagnose 显示用。</summary>
    public IReadOnlyList<(IScheduledJob Job, bool Enabled, string NextRun)> DescribeSchedule(AppOptions options)
    {
        var state = AppStateStore.Load(_paths.AppStatePath);
        var now = DateTime.Now;
        var result = new List<(IScheduledJob, bool, string)>();

        foreach (var job in _jobs)
        {
            var enabled = job.IsEnabled(options);
            if (!enabled)
            {
                result.Add((job, false, "未启用"));
                continue;
            }

            var time = job.GetScheduledTime(options);
            if (time is null)
            {
                result.Add((job, true, "时间配置无效"));
                continue;
            }

            result.Add((job, true, DescribeNextRun(state, job, time.Value, now)));
        }

        return result;
    }

    private static string DescribeNextRun(AppState state, IScheduledJob job, TimeOnly time, DateTime now)
    {
        var lastRun = state.JobLastRunLocal.TryGetValue(job.Key, out var recorded) ? recorded : (DateTime?)null;

        // 今天的时间点已经过了、而且已经跑过 → 下次是明天
        var todayAt = now.Date.Add(time.ToTimeSpan());
        if (lastRun is not null && lastRun.Value.Date >= now.Date)
        {
            return $"明天 {time:HH\\:mm}（今天已跑）";
        }

        return now >= todayAt
            ? $"待补跑（设定 {time:HH\\:mm} 已过）"
            : $"今天 {time:HH\\:mm}";
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                // 配置可能被界面改了，每轮重新读，改完不用重启。
                var load = OptionsStore.Load(null);
                await RunDueJobsAsync(load.Options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("定时任务调度出错", ex);
            }
        }
    }

    private async Task RunDueJobsAsync(AppOptions options, CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var state = AppStateStore.Load(_paths.AppStatePath);
        var changed = false;

        foreach (var job in _jobs)
        {
            if (!job.IsEnabled(options))
            {
                continue;
            }

            var time = job.GetScheduledTime(options);
            if (time is null)
            {
                continue;
            }

            if (!IsDue(state, job, time.Value, now))
            {
                continue;
            }

            _log.Info($"定时任务「{job.DisplayName}」开始执行");

            // 先记下"今天跑过了"再执行：任务本身跑很久或中途被杀，也不该在半小时后再来一遍。
            state.JobLastRunLocal[job.Key] = now;
            AppStateStore.Save(_paths.AppStatePath, state);
            changed = false;

            JobRunResult result;
            try
            {
                result = await job.RunAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error($"定时任务「{job.DisplayName}」执行失败", ex);
                result = new JobRunResult(false, $"{ex.GetType().Name}: {ex.Message}");
            }

            _log.Info($"定时任务「{job.DisplayName}」结束：{(result.Success ? "成功" : "失败")} —— {result.Message}");
            JobCompleted?.Invoke(this, (job, result));
        }

        if (changed)
        {
            AppStateStore.Save(_paths.AppStatePath, state);
        }
    }

    /// <summary>今天的时间点已过，且今天还没跑过 → 该跑（含"错过就补跑"）。</summary>
    private static bool IsDue(AppState state, IScheduledJob job, TimeOnly time, DateTime now)
    {
        if (now < now.Date.Add(time.ToTimeSpan()))
        {
            return false;
        }

        if (!state.JobLastRunLocal.TryGetValue(job.Key, out var lastRun))
        {
            return true;
        }

        return lastRun.Date < now.Date;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
    }
}
