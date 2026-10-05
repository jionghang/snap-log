using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SnapLog.Configuration;
using SnapLog.Interop;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 概览页。整个软件对用户的价值只有一件事：**每天那行日报有没有按时出现在飞书里**。
/// 所以这一页的主角是"最近一次日报"，抓取统计退成一行小字。
///
/// 页面上只有四项：状态与暂停、最近一次日报、每天几点自动跑、要不要开机自启。
/// 其余参数一律不在界面上出现。
/// </summary>
internal sealed class HomePage : UserControl, IRefreshable
{
    private const string DefaultPipelineTime = "22:00";

    private readonly AppServices _services;

    private readonly Border _stateDot;
    private readonly TextBlock _stateTitle;
    private readonly TextBlock _stateHint;
    private readonly Button _toggleButton;
    private readonly TextBlock _statsLine;

    private readonly Border _reportPill;
    private readonly TextBlock _reportTitle;
    private readonly TextBlock _reportDetail;

    private readonly ToggleSwitch _pipelineSwitch;
    private readonly TimeField _pipelineTime;
    private readonly TextBlock _pipelineNext;
    private readonly TextBlock _pipelineWarning;
    private readonly Button _unifyButton;

    private readonly ToggleSwitch _autoStartSwitch;

    private bool _suppressEvents = true;

    public HomePage(AppServices services)
    {
        _services = services;

        _stateDot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new Avalonia.CornerRadius(5),
            Background = new SolidColorBrush(Color.Parse("#B45309")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 10, 0),
        };
        _stateTitle = new TextBlock { Classes = { "metric" } };
        _stateHint = Ui.Caption(string.Empty);
        _toggleButton = Ui.Primary("暂停记录", ToggleRecordingAsync);
        _statsLine = Ui.Caption(string.Empty);

        _reportPill = Ui.Pill("—", PillKind.Neutral);
        _reportTitle = new TextBlock { Classes = { "headline" } };
        _reportDetail = Ui.Caption(string.Empty);

        _pipelineSwitch = Ui.Switch(false, OnPipelineToggled);
        _pipelineTime = new TimeField(services.Options.Summarization.ScheduleTimeOfDay, OnPipelineTimeChanged);
        _pipelineNext = Ui.Caption(string.Empty);
        _pipelineWarning = Ui.Hint(string.Empty);
        _pipelineWarning.Foreground = new SolidColorBrush(Color.Parse("#B45309"));
        _unifyButton = Ui.Link(string.Empty, UnifyTimes);

        _autoStartSwitch = Ui.Switch(AutoStart.IsEnabled(), OnAutoStartToggled);

        Content = Ui.Scroller(Ui.Page(
            Ui.PageTitle("概览"),
            BuildStateCard(),
            BuildReportCard(),
            BuildPipelineCard()));

        // 页面被主窗口缓存复用：切走时退订、切回来要重新订阅，
        // 否则回来以后状态不再更新（右下角说"已暂停"、卡片还写"正在记录"，按钮点下去是反的）。
        AttachedToVisualTree += (_, _) =>
        {
            _services.StateChanged -= Refresh;
            _services.DataChanged -= Refresh;
            _services.StateChanged += Refresh;
            _services.DataChanged += Refresh;
            Refresh();
        };

        DetachedFromVisualTree += (_, _) =>
        {
            _services.StateChanged -= Refresh;
            _services.DataChanged -= Refresh;
        };

        _suppressEvents = false;
        Refresh();
    }

    public void Refresh()
    {
        _suppressEvents = true;

        try
        {
            var running = _services.Engine.IsRunning;
            _stateTitle.Text = running ? "正在记录" : "已暂停";
            _stateHint.Text = running
                ? "跟随前台窗口自动识别，截图与文字仅保存在本机。"
                : "暂停期间不截图，也不执行每日流程。";
            _stateDot.Background = new SolidColorBrush(Color.Parse(running ? "#15803D" : "#B45309"));
            _toggleButton.Content = running ? "暂停记录" : "开始记录";

            _pipelineSwitch.IsChecked = PipelineEnabled;
            _pipelineTime.IsEnabled = PipelineEnabled;
            _pipelineTime.Set(PipelineTime);

            RefreshPipelineText();
            _autoStartSwitch.IsChecked = AutoStart.IsEnabled();
        }
        finally
        {
            _suppressEvents = false;
        }

        _ = RefreshDataAsync();
    }

    /// <summary>抓取统计（次要信息）与最近一次日报（主角）一起刷新。</summary>
    private async Task RefreshDataAsync()
    {
        try
        {
            var (total, _) = await _services.CountsAsync(CancellationToken.None);

            var today = await _services.Store.QueryAsync(
                new Storage.ActivityQuery { From = DateTime.Today, Limit = 1 }, CancellationToken.None);

            _statsLine.Text = $"这次启动后 {_services.SessionRecords} 条　·　今天 {today.TotalCount} 条　·　累计 {total} 条";

            var runs = await _services.Store.GetSummaryRunsAsync(1, CancellationToken.None);
            ShowReport(runs.Count > 0 ? runs[0] : null);
        }
        catch (Exception ex)
        {
            _services.Log.Warn($"读取概览数据失败：{ex.Message}");
        }
    }

    private void ShowReport(SummaryRun? run)
    {
        if (run is null)
        {
            Ui.UpdatePill(_reportPill, "暂无", PillKind.Neutral);
            _reportTitle.Text = "暂无总结记录";
            _reportDetail.Text = "配置大模型和飞书后，会在每天设定时间自动生成。";
            return;
        }

        if (!run.Success)
        {
            Ui.UpdatePill(_reportPill, "失败", PillKind.Danger);
            _reportTitle.Text = $"{DayLabel(run)}的总结　{run.StartedAt:MM-dd HH:mm} 生成失败";
            _reportDetail.Text = Ui.Shorten(run.Message, 90);
            return;
        }

        var pushed = run.PushedAt is not null;
        Ui.UpdatePill(_reportPill, pushed ? "已推送" : "未推送", pushed ? PillKind.Ok : PillKind.Warn);
        // 覆盖日与生成时刻是两个日期概念，别拼成"昨天 08:32"那种读着像同一时刻的样子：
        // 补跑（夜里没开机、早上启动才执行）时两者会差一天。
        _reportTitle.Text = $"{DayLabel(run)}的总结　{run.StartedAt:MM-dd HH:mm} 生成";

        var details = $"覆盖：{run.CoveredDay}　记录：{run.RecordCount} 条"
                      + (run.Provider.Length > 0 ? $"　模型：{run.Provider}" : string.Empty)
                      + $"　耗时：{run.ElapsedMilliseconds / 1000.0:0.#} 秒";

        if (!pushed)
        {
            details += _services.Options.Feishu.Enabled ? "　推送：会在设定时间自动推送" : "　推送：未启用";
        }

        _reportDetail.Text = details;
    }

    private static string DayLabel(SummaryRun run)
    {
        var day = DateOnly.TryParse(run.CoveredDay, out var parsed)
            ? parsed
            : DateOnly.FromDateTime(run.StartedAt);

        var today = DateOnly.FromDateTime(DateTime.Today);
        return day == today ? "今天" : day == today.AddDays(-1) ? "昨天" : day.ToString("yyyy-MM-dd");
    }

    // ---------------------------------------------------------------- 每日流水线

    private bool PipelineEnabled =>
        _services.Options.Summarization.ScheduleEnabled
        && _services.Options.Feishu.ScheduleEnabled
        && _services.Options.Ocr.Mode == OcrRunMode.ScheduledBatch;

    private string PipelineTime
    {
        get
        {
            var configured = _services.Options.Summarization.ScheduleTimeOfDay;
            return TimeOnly.TryParse(configured, out var parsed)
                ? $"{parsed.Hour:00}:{parsed.Minute:00}"
                : DefaultPipelineTime;
        }
    }

    private void RefreshPipelineText()
    {
        var options = _services.Options;
        var enabled = PipelineEnabled;
        var time = PipelineTime;

        // 调度器返回的已经是一句完整的话（含"下次自动执行：…"），这里不再加前缀。
        _pipelineNext.Text = enabled ? _services.DescribeSchedule("summary") : "没有开启";

        var canonical = TimeOnly.TryParse(time, out var value) ? value : new TimeOnly(22, 0);
        var drifted = new[] { options.Ocr.BatchTimeOfDay, options.Feishu.ScheduleTimeOfDay }
            .Any(candidate => TimeOnly.TryParse(candidate, out var other) && other != canonical);

        _unifyButton.IsVisible = enabled && drifted;
        if (_unifyButton.IsVisible)
        {
            _unifyButton.Content = $"三个步骤的时间不一致，统一为 {time}";
        }

        _pipelineWarning.Text = enabled ? BuildWarning() : string.Empty;
        _pipelineWarning.IsVisible = _pipelineWarning.Text.Length > 0;
    }

    /// <summary>没配好的东西提前说清楚：不然用户第二天只会看到一条"失败"。</summary>
    private string BuildWarning()
    {
        var options = _services.Options;
        var provider = options.Summarization.Providers.FirstOrDefault(p => p.Enabled);

        var llmReady = provider is not null
                       && !string.IsNullOrWhiteSpace(provider.Model)
                       && !string.IsNullOrWhiteSpace(provider.Endpoint)
                       && (!string.IsNullOrWhiteSpace(provider.ApiKey)
                           || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                               string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable)
                                   ? "SNAPLOG_OPENAI_API_KEY"
                                   : provider.ApiKeyEnvironmentVariable)));

        if (!llmReady)
        {
            return "大模型尚未配置：请在设置中填写接口地址、模型名称和 API Key，否则不会生成总结。";
        }

        if (!Ui.IsFeishuReady(options))
        {
            return "飞书接入信息未填写，总结不会推送到多维表格。";
        }

        return string.Empty;
    }

    private void OnPipelineToggled(bool enabled)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (enabled && !_services.Options.Summarization.ConsentGranted)
        {
            _ = ConfirmAndEnableAsync();
            return;
        }

        ApplyPipeline(enabled);
    }

    /// <summary>
    /// 启动时检查一次：配置里开着每日流程、却没确认过隐私提示的话，它每天都会失败而没人知道。
    /// 这里直接把确认框弹出来；用户取消就把开关关掉。
    /// </summary>
    internal void AskForConsentIfNeeded()
    {
        if (PipelineEnabled && !_services.Options.Summarization.ConsentGranted)
        {
            _ = ConfirmAndEnableAsync();
        }
    }

    /// <summary>
    /// 首次打开每日自动执行时的隐私确认（原来在"生成总结"窗口里，那个手动入口已经撤掉）。
    /// 取消就把开关拨回去。
    /// </summary>
    private async Task ConfirmAndEnableAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var ok = await Ui.Confirm(
            owner,
            "开启每天自动执行？",
            "每天在设定时间会将当天的活动内容发送到配置的模型接口，并将生成的总结推送到飞书多维表格。"
            + "发送方式（仅文字或附带截图）取决于大模型设置中的发送内容选项。可随时在此关闭。",
            "开启");

        if (!ok)
        {
            _suppressEvents = true;
            _pipelineSwitch.IsChecked = false;
            _suppressEvents = false;
            return;
        }

        _services.Options.Summarization.ConsentGranted = true;
        ApplyPipeline(true);
    }

    private void ApplyPipeline(bool enabled)
    {
        var options = _services.Options;
        var time = PipelineTime;

        options.Ocr.Mode = enabled ? OcrRunMode.ScheduledBatch : OcrRunMode.Realtime;
        options.Summarization.ScheduleEnabled = enabled;
        options.Feishu.ScheduleEnabled = enabled;

        if (enabled)
        {
            // 定时识别必须存图，否则事后无从识别。
            options.Capture.SaveImages = true;

            options.Ocr.BatchTimeOfDay = time;
            options.Summarization.ScheduleTimeOfDay = time;
            options.Feishu.ScheduleTimeOfDay = time;

            options.Summarization.Enabled = true;
            options.Feishu.Enabled = true;
        }

        PersistAndRefresh();
    }

    private void OnPipelineTimeChanged(string hhmm)
    {
        if (_suppressEvents)
        {
            return;
        }

        var options = _services.Options;
        options.Summarization.ScheduleTimeOfDay = hhmm;
        options.Ocr.BatchTimeOfDay = hhmm;
        options.Feishu.ScheduleTimeOfDay = hhmm;
        PersistAndRefresh();
    }

    private void UnifyTimes()
    {
        var options = _services.Options;
        options.Ocr.BatchTimeOfDay = PipelineTime;
        options.Summarization.ScheduleTimeOfDay = PipelineTime;
        options.Feishu.ScheduleTimeOfDay = PipelineTime;
        PersistAndRefresh();
    }

    // ---------------------------------------------------------------- 布局

    private Control BuildStateCard()
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        var titleColumn = new StackPanel { Spacing = 3 };
        titleColumn.Children.Add(Ui.Inline(8, _stateTitle, Ui.Tip(
            "记录状态：开启后自动截图并识别文字，每天在设定时间生成总结并推送到飞书；暂停后两者都停止。")));
        titleColumn.Children.Add(_stateHint);
        Grid.SetColumn(titleColumn, 1);
        head.Children.Add(_stateDot);
        head.Children.Add(titleColumn);

        return Ui.Card(null, null, head, Ui.ButtonRow(_toggleButton), _statsLine);
    }

    private Control BuildReportCard()
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(_reportTitle, 1);
        _reportTitle.VerticalAlignment = VerticalAlignment.Center;

        var pillRow = new Panel { Height = 20 };
        var pill = _reportPill;
        pill.VerticalAlignment = VerticalAlignment.Center;
        pillRow.Children.Add(pill);
        head.Children.Add(pillRow);
        head.Children.Add(_reportTitle);

        // 这个软件只有一条路径：到点自动跑。所以这里只报告结果，不放任何"手动执行"的按钮。
        var buttons = Ui.ButtonRow(
            Ui.Secondary("查看全部总结", () =>
            {
                if (TopLevel.GetTopLevel(this) is MainWindow shell)
                {
                    shell.ShowPage("summaries");
                }

                return Task.CompletedTask;
            }));

        return Ui.CardWith(
            Ui.Header("最近一次总结", "最近生成的一条结果。覆盖日期指总结针对的那一天；生成时间可能晚一天，"
                                      + "因为定时点电脑未开机时会在下次启动后补生成。"),
            null, head, _reportDetail, buttons);
    }

    private Control BuildPipelineCard()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(_pipelineSwitch);
        row.Children.Add(Ui.Label("每天"));
        row.Children.Add(_pipelineTime);
        row.Children.Add(Ui.Label("自动执行"));
        _pipelineNext.Margin = new Avalonia.Thickness(8, 0, 0, 0);
        _pipelineNext.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_pipelineNext);

        var autoStart = Ui.FieldRow("开机自动启动", _autoStartSwitch,
            tip: "登录 Windows 后自动在后台启动并开始记录。");

        return Ui.CardWith(
            Ui.Header("每天自动执行",
                "每天在设定时间依次执行三步：识别、生成总结、推送飞书。总结的是已经结束的那一天；"
                + "设定时间电脑未开机时，会在下次启动后自动执行。"),
            null, row, _pipelineWarning, _unifyButton, Ui.Divider(), autoStart);
    }

    // ---------------------------------------------------------------- 动作

    private async Task ToggleRecordingAsync()
    {
        await _services.ToggleRecordingAsync();
        Refresh();
    }

    private void OnAutoStartToggled(bool wanted)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (_services.SetAutoStart(wanted))
        {
            return;
        }

        _suppressEvents = true;
        _autoStartSwitch.IsChecked = AutoStart.IsEnabled();
        _suppressEvents = false;

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            _ = Ui.Info(owner, "设置开机自启动失败", $"写入注册表被拒绝，或程序路径无法获取。\n\n路径：{AutoStart.ExecutablePath}");
        }
    }

    private void PersistAndRefresh()
    {
        if (!_services.SaveOptions() && TopLevel.GetTopLevel(this) is Window owner)
        {
            _ = Ui.Info(owner, "配置没能写入磁盘", "改动已在内存中生效，但重启后会丢失。请检查配置目录的写权限。");
        }

        Refresh();
    }
}
