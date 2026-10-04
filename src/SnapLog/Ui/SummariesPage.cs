using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 总结记录：只看历史，不放任何"手动生成/手动推送"的入口。
/// 这个软件只有一条路径——到点自动跑；失败了规划器会在下一次自动补上，不需要人来点。
/// 选择某一行会在详情窗口打开正文（和「抓取记录」一致）。
/// </summary>
internal sealed class SummariesPage : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly ListBox _list;
    private readonly TextBlock _count;

    public SummariesPage(AppServices services)
    {
        _services = services;

        _count = Ui.Caption(string.Empty);
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
            "每天到点自动生成；失败或遗漏的日期会在下一次执行时重做。选择一行可查看正文。"));

        Grid.SetColumn(_count, 1);
        _count.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_count);

        return new StackPanel { Spacing = 0, Children = { grid } };
    }

    private Control BuildList()
    {
        var header = new Grid { ColumnDefinitions = Columns() };
        AddHeaderCell(header, 0, "生成时间");
        AddHeaderCell(header, 1, "总结日期");
        AddHeaderCell(header, 2, "记录数", right: true);
        AddHeaderCell(header, 3, "飞书推送");
        AddHeaderCell(header, 4, "结果");

        _list.ItemTemplate = new FuncDataTemplate<SummaryRun>((run, _) =>
        {
            var line = new Grid { ColumnDefinitions = Columns() };
            AddCell(line, 0, run.StartedAt.ToString("MM-dd HH:mm"));
            AddCell(line, 1, run.CoveredDay.Length > 0 ? run.CoveredDay : "—");
            AddCell(line, 2, run.RecordCount.ToString(), right: true);
            AddCell(line, 3, run.PushedAt is not null ? "已推送" : "未推送", run.PushedAt is not null ? "#15803D" : "#6B7280");
            AddCell(line, 4, run.Success ? "成功" : "失败", run.Success ? "#15803D" : "#B91C1C");
            return line;
        }, supportsRecycling: true);

        var listBlock = new StackPanel { Spacing = 10 };
        listBlock.Children.Add(header);
        listBlock.Children.Add(Ui.Divider());
        listBlock.Children.Add(_list);

        return Ui.Card(null, null, listBlock);
    }

    private static ColumnDefinitions Columns() => new("110,*,56,72,56");

    private static void AddHeaderCell(Grid grid, int column, string text, bool right = false)
    {
        var block = Ui.Caption(text);
        block.FontWeight = FontWeight.SemiBold;
        if (right)
        {
            block.TextAlignment = TextAlignment.Right;
            block.Margin = new Thickness(0, 0, 14, 0);   // 和右边那一列留出间距
        }

        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private static void AddCell(Grid grid, int column, string text, string? color = null, bool right = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        if (right)
        {
            block.TextAlignment = TextAlignment.Right;
            block.Margin = new Thickness(0, 0, 14, 0);   // 和右边那一列留出间距
        }

        if (color is not null)
        {
            block.Foreground = new SolidColorBrush(Color.Parse(color));
        }

        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private async Task LoadAsync()
    {
        try
        {
            var runs = await _services.Store.GetSummaryRunsAsync(200, CancellationToken.None);
            _list.ItemsSource = runs;

            var pushed = runs.Count(run => run.PushedAt is not null);
            _count.Text = $"共 {runs.Count} 条　已推送 {pushed} 条";

            if (runs.Count == 0)
            {
                _list.ItemsSource = null;
                _count.Text = "暂无总结记录";
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error("读取总结历史失败", ex);

            // 读失败也要说一句，否则页面就是一片空白，用户以为没生成过。
            _count.Text = "读取失败，稍后重试";
            _list.ItemsSource = null;
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

    private readonly StackPanel _body = new() { Spacing = 10 };
    private readonly TextBlock _title = new() { FontSize = 15.5, FontWeight = FontWeight.SemiBold };

    public SummaryDetailWindow()
    {
        Title = "总结正文";
        Width = 760;
        Height = 620;
        MinWidth = 560;
        MinHeight = 420;
        Icon = IconFactory.WindowIcon;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(Ui.Inline(8, _title, Ui.Tip("正文可以选中复制。")));

        var scroller = new ScrollViewer { Content = _body };
        scroller.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(scroller, 1);
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
        _body.Children.Clear();

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

        _body.Children.Add(Ui.Hint(meta.ToString()));

        if (!run.Success)
        {
            _body.Children.Add(Ui.Hint("失败原因：" + (run.Message.Length > 0
                ? run.Message
                : "没有记录原因，可能是模型接口没有返回内容。下次自动执行时会重试这一天。")));
        }

        if (run.SavedPath.Length > 0 && File.Exists(run.SavedPath))
        {
            _body.Children.Add(Ui.ButtonRow(
                Ui.Secondary("打开文件", () => { MainWindow.OpenPath(run.SavedPath); return Task.CompletedTask; })));
        }

        _body.Children.Add(Ui.BodyText(run.Markdown.Length > 0 ? run.Markdown : run.Message));

        _ = services;
    }
}
