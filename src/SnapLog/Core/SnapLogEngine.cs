using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SnapLog.Capture;
using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Imaging;
using SnapLog.Interop;
using SnapLog.Ocr;
using SnapLog.Storage;

namespace SnapLog.Core;

public enum CaptureTrigger
{
    /// <summary>前台窗口变化触发，带稳定等待和周期内去重。</summary>
    ForegroundChange,

    /// <summary>用户手动点"立即抓取"，绕过所有节流和过滤。</summary>
    Manual,
}

public sealed record CaptureOutcome(bool Captured, string Reason, ActivityRecord? Record)
{
    public static CaptureOutcome Skipped(string reason) => new(false, reason, null);

    public static CaptureOutcome Ok(ActivityRecord record) => new(true, string.Empty, record);
}

/// <summary>
/// 抓取流水线的调度中心：窗口事件 → 稳定等待 → 节流/过滤 → 截图 → OCR → 落盘。
/// 所有耗时环节都在后台线程上异步执行，不阻塞 UI。
/// </summary>
public sealed class SnapLogEngine : IAsyncDisposable
{
    private readonly AppOptions _options;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;
    private readonly IActivityStore _store;
    private readonly IOcrEngine _ocr;
    private readonly ForegroundWindowWatcher _watcher = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    private CancellationTokenSource? _lifecycle;
    private CancellationTokenSource? _pendingForegroundCapture;
    private DateTime _lastAutomaticCaptureUtc = DateTime.MinValue;

    /// <summary>本抓取周期内已经记录过的窗口标识（进程+类名+标题），周期一到就清空。</summary>
    private readonly HashSet<string> _capturedThisCycle = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前周期的起始时刻。每次触发都和现在比，跨周期就清空注册表。</summary>
    private DateTime _currentCycleStart = DateTime.MinValue;
    private bool _disposed;

    public SnapLogEngine(AppOptions options, AppPaths paths, FileLogger log, IOcrEngine ocr, IActivityStore store)
    {
        _options = options;
        _paths = paths;
        _log = log;
        _ocr = ocr;
        _store = store;

        _watcher.ForegroundWindowChanged += OnForegroundWindowChanged;
    }

    public bool IsRunning { get; private set; }

    public ActivityRecord? LastRecord { get; private set; }

    public string? LastStatus { get; private set; }

    public string DatabasePath => _store.Location;

    /// <summary>
    /// 指定抓取目标窗口，而不是取当前前台窗口。仅用于命令行按需抓取（<c>--once --target</c>）。
    /// 正常运行时保持 <see cref="IntPtr.Zero"/>——记录"用户正在看的窗口"是这个工具的立足点，
    /// 没有前台窗口时宁可跳过，也不该随便抓一个别的窗口。
    /// </summary>
    public IntPtr TargetWindowOverride { get; set; }

    /// <summary>每写成功一条记录触发一次；在后台线程上触发，界面需自行 Invoke。</summary>
    public event EventHandler<ActivityRecord>? RecordCaptured;

    /// <summary>状态文案变化（跳过原因、错误等）；在后台线程上触发。</summary>
    public event EventHandler<string>? StatusChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            return;
        }

        _lifecycle = new CancellationTokenSource();

        // SetWinEventHook 要求安装钩子的线程有消息循环，所以只能在 UI 线程上装。
        _watcher.Start();

        IsRunning = true;
        ReportStatus("已开始记录");
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;

        _watcher.Stop();
        _pendingForegroundCapture?.Cancel();

        if (_lifecycle is not null)
        {
            await _lifecycle.CancelAsync().ConfigureAwait(false);
        }

        _lifecycle?.Dispose();
        _lifecycle = null;
        ReportStatus("已暂停");
    }

    public async Task<CaptureOutcome> CaptureNowAsync(CaptureTrigger trigger, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 自动触发时不排队：上一次还没跑完就直接跳过，避免事件堆积。
        bool acquired;
        if (trigger == CaptureTrigger.Manual)
        {
            await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
        }
        else
        {
            acquired = await _captureGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        }

        if (!acquired)
        {
            return CaptureOutcome.Skipped("上一次抓取还没结束，本次跳过");
        }

        try
        {
            return await CaptureCoreAsync(trigger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async Task<CaptureOutcome> CaptureCoreAsync(CaptureTrigger trigger, CancellationToken cancellationToken)
    {
        try
        {
            return await CaptureCoreInternalAsync(trigger, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 抓取路径上的任何异常都不该把程序带崩：最多跳过这一次。
            // GDI+ 的 "A generic error occurred" 是其中最常见的。
            _log.Error("抓取失败（已跳过本次）", ex);
            return CaptureOutcome.Skipped($"抓取失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<CaptureOutcome> CaptureCoreInternalAsync(CaptureTrigger trigger, CancellationToken cancellationToken)
    {
        // 锁屏期间不抓：这时输入桌面已经切到 Winlogon，抓到的东西没有意义。
        if (SessionLockState.IsLocked())
        {
            return CaptureOutcome.Skipped("屏幕已锁定");
        }

        var snapshot = TargetWindowOverride != IntPtr.Zero
            ? ForegroundWindowWatcher.Describe(TargetWindowOverride)
            : ForegroundWindowWatcher.DescribeForegroundWindow();

        if (!snapshot.IsUsable)
        {
            return CaptureOutcome.Skipped("前台窗口无标题，已跳过");
        }

        var skipReason = CheckFilters(snapshot, trigger);
        if (skipReason is not null)
        {
            return CaptureOutcome.Skipped(skipReason);
        }

        _lastAutomaticCaptureUtc = DateTime.UtcNow;

        using var capture = WindowCapturer.Capture(snapshot.Handle, _options.Capture.MaxImageDimension);
        if (!capture.Success || capture.Image is null)
        {
            return CaptureOutcome.Skipped($"截图失败：{capture.Error}");
        }

        var record = new ActivityRecord
        {
            Timestamp = DateTime.Now,
            ProcessName = snapshot.ProcessName,
            WindowTitle = snapshot.WindowTitle,
            WindowClass = snapshot.ClassName,
            CaptureMethod = capture.Method,
            ImageWidth = capture.Image.Width,
            ImageHeight = capture.Image.Height,
        };

        // 定时识别模式：白天只截图，识别留给批次任务。
        // 这条路径必须存图，否则事后无从识别——所以即使配置关了保存也会强制存。
        var batchMode = _options.Ocr.Mode == OcrRunMode.ScheduledBatch;

        if (batchMode)
        {
            record.ImagePath = SaveImage(capture.Image, record.Timestamp, snapshot.ProcessName);

            if (record.ImagePath.Length == 0)
            {
                // 没存下图就永远识别不了。标成 Error 而不是挂一个永远处理不掉的 Pending。
                record.Status = RecordStatus.Error;
                record.Error = "定时识别需要截图，但截图保存失败";
                ReportStatus("截图保存失败，该记录无法参与批量识别");
            }
            else
            {
                record.Status = RecordStatus.Pending;

                if (!_options.Capture.SaveImages)
                {
                    _log.Warn("识别方式为“每日定时批量识别”，该模式必须保存截图，已自动为本次记录存图");
                }
            }
        }
        else if (_options.Ocr.Enabled)
        {
            var outcome = await _ocr.RecognizeAsync(capture.Image, cancellationToken).ConfigureAwait(false);
            if (!outcome.Success)
            {
                var reason = outcome.Error ?? "未知错误";
                ReportStatus($"OCR 失败，未记录：{reason}");
                return CaptureOutcome.Skipped($"OCR 失败：{reason}");
            }

            record.OcrText = outcome.Text;
            record.OcrMilliseconds = (long)outcome.Elapsed.TotalMilliseconds;

            // 识别成功但有需要留意的点（例如某个区域超过引擎的单次上限被跳过）。
            // 状态仍是 Ok，但把提示写进“错误”列，记录查看器的详情里能看到。
            if (outcome.Warning is not null)
            {
                record.Error = outcome.Warning;
            }

            // 判断"有没有文字"只在开着 OCR 时才有意义；
            // 关掉 OCR 是"只记标题"的用法，不该被当成空记录丢掉。
            if (record.OcrText.Length < _options.Ocr.MinTextLength)
            {
                record.Status = RecordStatus.NoText;
                if (_options.Ocr.DropEmptyRecords)
                {
                    ReportStatus($"“{Truncate(snapshot.WindowTitle)}”未识别到文字，未记录");
                    return CaptureOutcome.Skipped("没有识别到有效文字");
                }
            }
        }

        if (_options.Capture.SaveImages && record.ImagePath.Length == 0)
        {
            record.ImagePath = SaveImage(capture.Image, record.Timestamp, snapshot.ProcessName);
        }
        try
        {
            await _store.AppendAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 程序退出时的取消是正常的收尾，不是写入失败——如实跳过即可。
            throw;
        }
        catch (Exception ex)
        {
            record.Status = RecordStatus.Error;
            record.Error = ex.Message;
            ReportStatus($"写入数据库失败：{ex.Message}");
            return CaptureOutcome.Skipped($"写入数据库失败：{ex.Message}");
        }

        LastRecord = record;

        if (trigger == CaptureTrigger.ForegroundChange)
        {
            // 记录成功后才把窗口标进本周期注册表——跳过、失败的都不算"记过了"。
            _capturedThisCycle.Add(WindowIdentity(snapshot));
        }

        RecordCaptured?.Invoke(this, record);

        if (batchMode)
        {
            ReportStatus(record.Status == RecordStatus.Error
                ? "已记录“" + Truncate(snapshot.WindowTitle) + "”但截图保存失败，无法参与批量识别"
                : $"已记录“{Truncate(snapshot.WindowTitle)}”（等定时识别）");
        }
        else
        {
            ReportStatus($"已记录“{Truncate(snapshot.WindowTitle)}”{record.TextLength} 字（{capture.Method}）");
        }

        return CaptureOutcome.Ok(record);
    }

    private string? CheckFilters(WindowSnapshot snapshot, CaptureTrigger trigger)
    {
        if (_options.Triggers.ExcludedProcesses.Any(
                name => !string.IsNullOrWhiteSpace(name)
                        && string.Equals(name.Trim(), snapshot.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            // 手动抓取最常撞上的就是这一条：按钮点下去的那一刻前台还是 SnapLog 自己。
            // 报"位于排除列表"对用户没有指导意义，直接说下一步该做什么。
            return trigger == CaptureTrigger.Manual && IsOwnProcess(snapshot.ProcessName)
                ? "前台窗口是 SnapLog 自己，先切到要记录的窗口"
                : $"进程 {snapshot.ProcessName} 位于排除列表";
        }

        if (_options.Triggers.ExcludedWindowTitles.Any(
                title => !string.IsNullOrWhiteSpace(title)
                         && string.Equals(title.Trim(), snapshot.WindowTitle.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return "窗口标题在排除列表里";
        }

        if (trigger == CaptureTrigger.Manual)
        {
            return null;
        }

        var minimumGap = TimeSpan.FromSeconds(_options.Triggers.MinSecondsBetweenCaptures);
        if (DateTime.UtcNow - _lastAutomaticCaptureUtc < minimumGap)
        {
            return $"距上次抓取不足 {minimumGap.TotalSeconds:0} 秒";
        }

        // 周期内去重：同一个窗口在一个周期里只记一次。
        if (trigger == CaptureTrigger.ForegroundChange)
        {
            ResetCycleIfNeeded();

            if (_capturedThisCycle.Contains(WindowIdentity(snapshot)))
            {
                return "该窗口在本抓取周期内已记录";
            }
        }

        return null;
    }

    /// <summary>窗口的身份：进程 + 类名 + 标题。同一个窗口在一个周期内只记一次。</summary>
    private static string WindowIdentity(WindowSnapshot snapshot) =>
        $"{snapshot.ProcessName}\u0001{snapshot.ClassName}\u0001{snapshot.WindowTitle}";

    /// <summary>前台窗口是不是本程序自己。手动抓取时没切走窗口就是这个情况。</summary>
    private static bool IsOwnProcess(string processName) =>
        !string.IsNullOrWhiteSpace(processName)
        && string.Equals(processName, Environment.ProcessPath is { } path
                ? System.IO.Path.GetFileNameWithoutExtension(path)
                : string.Empty,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>跨过抓取周期就清空"已记录"注册表，下一个周期里的窗口又能被记一次。</summary>
    private void ResetCycleIfNeeded()
    {
        var cycleMinutes = Math.Max(1, _options.Triggers.CaptureCycleMinutes);
        var now = DateTime.Now;
        var cycleStart = now.Date.AddMinutes(
            Math.Floor(now.TimeOfDay.TotalMinutes / cycleMinutes) * cycleMinutes);

        if (cycleStart != _currentCycleStart)
        {
            _currentCycleStart = cycleStart;
            _capturedThisCycle.Clear();
        }
    }

    private string SaveImage(Bitmap image, DateTime timestamp, string processName)
    {
        try
        {
            var directory = _paths.ResolveImageDirectory(_options.Capture.ImageDirectory);
            return ImageArchive.Save(image, directory, _options.Capture.ImageFormat, timestamp, processName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            _log.Warn($"保存截图失败：{ex.Message}");
            return string.Empty;
        }
    }

    private void OnForegroundWindowChanged(object? sender, WindowSnapshot snapshot)
    {
        if (!IsRunning)
        {
            return;
        }

        // 重新计时：用户还在连续切换窗口时不要抓，等画面稳定下来再抓。
        _pendingForegroundCapture?.Cancel();
        _pendingForegroundCapture?.Dispose();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycle?.Token ?? CancellationToken.None);
        _pendingForegroundCapture = cts;
        var scheduledHandle = snapshot.Handle;

        _ = Task.Run(async () =>
        {
            try
            {
                var settle = Math.Clamp(_options.Triggers.ForegroundSettleMilliseconds, 0, 30_000);
                // 只有"等画面稳定"用 pending 令牌：下一次切换会把等待取消，重来一遍。
                await Task.Delay(settle, cts.Token).ConfigureAwait(false);

                // 等满后还是这个窗口才抓：延时期间用户可能已经切走又切回来，或者干脆切到别的窗口。
                // 拿当前前台窗口核对一次，不是它就说明要抓的内容已经不是用户正在看的了。
                var current = ForegroundWindowWatcher.DescribeForegroundWindow();
                if (current.Handle != scheduledHandle)
                {
                    _log.Debug("延时结束但前台已经不是刚才那个窗口，跳过本次抓取");
                    return;
                }

                // 已经开始抓了就改用生命周期令牌：一次识别可能要几秒，
                // 若仍然用 pending 令牌，用户随便切一下窗口就把已做一半的工作作废，
                // 而且取消会被误当成"写库失败"记进日志。
                var outcome = await CaptureNowAsync(CaptureTrigger.ForegroundChange, _lifecycle?.Token ?? CancellationToken.None).ConfigureAwait(false);
                if (!outcome.Captured)
                {
                    _log.Debug($"窗口切换抓取跳过：{outcome.Reason}");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Error("窗口切换抓取出错", ex);
            }
        });
    }

    /// <summary>按条件分页查询记录，供记录查看器使用。</summary>
    public Task<PagedResult<ActivityListItem>> QueryAsync(ActivityQuery query, CancellationToken cancellationToken) =>
        _store.QueryAsync(query, cancellationToken);

    /// <summary>库里已有的记录总数，用于状态页显示。</summary>
    public Task<long> CountRecordsAsync(CancellationToken cancellationToken) =>
        _store.CountAsync(cancellationToken);

    private void ReportStatus(string message)
    {
        LastStatus = message;
        _log.Info(message);
        StatusChanged?.Invoke(this, message);
    }

    private static string Truncate(string value, int maxLength = 30) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopAsync().ConfigureAwait(false);

        _watcher.ForegroundWindowChanged -= OnForegroundWindowChanged;
        _watcher.Dispose();
        _pendingForegroundCapture?.Dispose();
        _captureGate.Dispose();
        _ocr.Dispose();
    }
}
