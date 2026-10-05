using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 总结记录：只看历史，不放任何"手动生成/手动推送"的入口。
/// 这个软件只有一条路径——到点自动跑；失败了规划器会在下一次自动补上，不需要人来点。
/// 选择某一行会在详情窗口打开正文（和抓取记录页一致）。
/// </summary>
internal sealed class SummariesPage : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly ListBox _list;
    private readonly TextBlock _count;
    private readonly TextBlock _empty;

    public SummariesPage(AppServices services)
    {
        _services = services;

        _count = Ui.Caption(string.Empty);
        _empty = Ui.Hint(string.Empty);
        _list = new ListBox { SelectionMode = SelectionMode.Single, MaxHeight = 520 };
        _list.SelectionChanged += (_, _) => OpenDetail();

        Content = Ui.Scroller(Ui.Page(
            BuildHeader(),
            BuildList()));

        Refresh();
    }

    public void Refresh() => _ = LoadAsync();

    private Control BuildHeader()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(Ui.PageHeader("总结记录",
            "保存生成过的每日总结，可回看正文与推送结果。失败或遗漏的日期会在下一次执行时重做。"));

        Grid.SetColumn(_count, 1);
        _count.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_count);

        return new StackPanel { Spacing = 0, Children = { grid } };
    }

    private Control BuildList()
    {
        var header = new Grid { ColumnDefinitions = Columns() };
        Ui.ListHeaderCell(header, 0, "生成时间");
        Ui.ListHeaderCell(header, 1, "总结日期");
        Ui.ListHeaderCell(header, 2, "记录数", right: true);
        Ui.ListHeaderCell(header, 3, "飞书推送");
        Ui.ListHeaderCell(header, 4, "结果");

        _list.ItemTemplate = new FuncDataTemplate<SummaryRun>((run, _) =>
        {
            var line = new Grid { ColumnDefinitions = Columns() };

            // 回收容器时 Avalonia 会用 null 重建模板（刷新列表、切页都会走到）。
            // 不挡住就会抛 NullReferenceException，界面随即显示"读取失败"，看着就是"没有内容"。
            if (run is null)
            {
                return line;
            }

            Ui.ListCell(line, 0, run.StartedAt.ToString("MM-dd HH:mm"));
            Ui.ListCell(line, 1, run.CoveredDay.Length > 0 ? run.CoveredDay : "—");
            Ui.ListCell(line, 2, run.RecordCount.ToString(), right: true);
            Ui.ListCell(line, 3, run.PushedAt is not null ? "已推送" : "未推送",
                color: run.PushedAt is not null ? "#15803D" : "#6B7280");
            Ui.ListCell(line, 4, run.Success ? "成功" : "失败",
                color: run.Success ? "#15803D" : "#B91C1C");
            return line;
        }, supportsRecycling: true);

        return Ui.ListCard(header, _empty, _list);
    }

    private static ColumnDefinitions Columns() => new("150,*,72,84,72");

    private async Task LoadAsync()
    {
        try
        {
            var runs = await _services.Store.GetSummaryRunsAsync(200, CancellationToken.None);
            _list.ItemsSource = runs;

            var pushed = runs.Count(run => run.PushedAt is not null);
            _count.Text = $"共 {runs.Count} 条　已推送 {pushed} 条";

            // 空态说明放在列表的位置上，和"抓取记录"同一处、同一样式；页头只留计数。
            _empty.Text = "暂无总结记录。开启每天自动执行后，会在设定时间自动生成。";
            _empty.IsVisible = runs.Count == 0;
        }
        catch (Exception ex)
        {
            _services.Log.Error("读取总结历史失败", ex);

            // 读失败也要说一句，否则页面就是一片空白，用户以为没生成过。
            _count.Text = "读取失败，稍后重试";
            _list.ItemsSource = null;
            _empty.IsVisible = false;
        }
    }

    private void OpenDetail()
    {
        if (_list.SelectedItem is SummaryRun run && TopLevel.GetTopLevel(this) is Window owner)
        {
            SummaryDetailWindow.ShowFor(owner, _services, run);
        }
    }
}

/// <summary>总结正文窗口。和记录详情一样全进程只有一个，看另一条时内容被覆盖。</summary>
internal sealed class SummaryDetailWindow : Window
{
    private static SummaryDetailWindow? _current;

    /// <summary>标题下面的元数据与按钮：固定不动，滚动只滚正文。</summary>
    private readonly StackPanel _meta = new() { Spacing = 10 };
    private readonly SelectableTextBlock _text = Ui.BodyText(string.Empty);
    private readonly TextBlock _title = new() { Classes = { "headline" } };

    public SummaryDetailWindow()
    {
        Title = "总结正文";
        Width = 760;
        Height = 620;
        MinWidth = 560;
        MinHeight = 420;
        Icon = IconFactory.WindowIcon;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // 次要窗口的通用约定：Esc 关闭。
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        // 标题 / 元数据与按钮 / 分隔线 都是固定的，只有正文在滚动区里。
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        grid.Children.Add(Ui.Inline(8, _title, Ui.Tip("正文可以选中复制。")));

        _meta.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(_meta, 1);
        grid.Children.Add(_meta);

        var divider = Ui.Divider();
        divider.Margin = new Thickness(0, 12, 0, 10);
        Grid.SetRow(divider, 2);
        grid.Children.Add(divider);

        var scroller = new ScrollViewer { Content = _text };
        Grid.SetRow(scroller, 3);
        grid.Children.Add(scroller);

        Content = new Border { Padding = new Thickness(22, 18, 22, 18), Child = grid };
    }

    public static void ShowFor(Window owner, AppServices services, SummaryRun run)
    {
        if (_current is not { IsVisible: true })
        {
            _current = new SummaryDetailWindow();
            _current.Closed += (_, _) => _current = null;
            _current.Show(owner);
        }

        _current.Activate();
        _current.Render(services, run);
    }

    internal void Render(AppServices services, SummaryRun run)
    {
        _meta.Children.Clear();
        _text.Text = string.Empty;

        _title.Text = $"{run.CoveredDay} 的总结"
                      + (run.Success ? string.Empty : "（失败）");

        var meta = new System.Text.StringBuilder()
            .Append($"生成于 {run.StartedAt:yyyy-MM-dd HH:mm:ss}　{run.RecordCount} 条记录")
            .Append(run.Provider.Length > 0 ? $"　{run.Provider}" : string.Empty)
            .Append($"　耗时 {run.ElapsedMilliseconds / 1000.0:0.#} 秒");

        if (run.PushedAt is { } pushed)
        {
            // 生成后立刻推送时两个时间在同一分钟，写两遍只是噪音。
            var sameMinute = Math.Abs((pushed - run.StartedAt).TotalMinutes) < 1;
            meta.Append(sameMinute ? "　已推送到飞书" : $"　已推送到飞书 {pushed:MM-dd HH:mm}");
        }
        else
        {
            meta.Append("　尚未推送到飞书");
        }

        _meta.Children.Add(Ui.Hint(meta.ToString()));

        if (!run.Success)
        {
            _meta.Children.Add(Ui.Hint("失败原因：" + (run.Message.Length > 0
                ? run.Message
                : "没有记录原因，可能是模型接口没有返回内容。下次自动执行时会重试这一天。")));
        }

        if (run.SavedPath.Length > 0 && File.Exists(run.SavedPath))
        {
            _meta.Children.Add(Ui.ButtonRow(
                Ui.Secondary("打开文件", () => { MainWindow.OpenPath(run.SavedPath); return Task.CompletedTask; })));
        }

        _text.Text = run.Markdown.Length > 0 ? run.Markdown : run.Message;

        _ = services;
    }
}
