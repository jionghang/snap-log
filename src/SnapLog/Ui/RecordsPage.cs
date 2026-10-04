using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 抓取记录页：只回答两个问题——"某天记了什么"和"这条到底记了什么"。
///
/// 所以只有两个筛选（日期 + 关键词）、没有分页、没有排序、没有导出。
/// 这些是数据库工具的思维，不是这个软件的目的（目的是每天那份日报）。
/// </summary>
internal sealed class RecordsPage : UserControl, IRefreshable
{
    private const int MaxRows = 300;

    private readonly AppServices _services;

    private readonly TextBox _keyword;
    private readonly ComboBox _range;
    private readonly TextBlock _summary;
    private readonly ListBox _list;


    private CancellationTokenSource? _debounce;
    private bool _loading;

    public RecordsPage(AppServices services)
    {
        _services = services;

        _keyword = Ui.Input(string.Empty, _ => DebouncedReload(), "搜窗口标题或识别到的文字", 300);

        _range = Ui.Choice(
            [
                (RangeFilter.Today, "今天"),
                (RangeFilter.Week, "近 7 天"),
                (RangeFilter.Month, "近 30 天"),
                (RangeFilter.All, "全部"),
            ],
            RangeFilter.Week,
            _ => StartLoad(),
            120);

        _summary = Ui.Caption("正在读取…");
        _list = new ListBox { SelectionMode = SelectionMode.Single, MaxHeight = 460 };
        // 详情单独开窗（全进程只有一个，看另一条时内容被覆盖）：挤在列表下面两边都不够用。
        _list.SelectionChanged += (_, _) => OpenDetail();

        // 在详情窗里删掉记录后，列表要跟着刷新。页面会被复用，所以切回来要重新订阅。
        AttachedToVisualTree += (_, _) =>
        {
            RecordDetailWindow.RecordDeleted -= OnRecordDeleted;
            RecordDetailWindow.RecordDeleted += OnRecordDeleted;
        };

        DetachedFromVisualTree += (_, _) => RecordDetailWindow.RecordDeleted -= OnRecordDeleted;

        Content = Ui.Scroller(Ui.Page(
            BuildHeader(),
            BuildFilterCard(),
            BuildListCard()));

        Refresh();
    }

    public void Refresh()
    {
        if (!_loading)
        {
            StartLoad();
        }
    }

    /// <summary>筛选项一变就重查；不 await——界面不该为一次查询停住。</summary>
    private void StartLoad() => _ = LoadAsync();

    /// <summary>关键词是边打边查，等 350 毫秒再发一次，免得每敲一个字都查一遍库。</summary>
    private void DebouncedReload()
    {
        _debounce?.Cancel();
        var cts = new CancellationTokenSource();
        _debounce = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, cts.Token);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(StartLoad);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    // ---------------------------------------------------------------- 布局

    private Control BuildHeader()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(Ui.PageHeader("抓取记录",
            "最多列出最近 300 条；更早的记录请把时间范围改为「全部」。选择一行查看详情。"));
        Grid.SetColumn(_summary, 1);
        _summary.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_summary);
        return grid;
    }

    private Control BuildFilterCard()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(_keyword);
        row.Children.Add(_range);

        return Ui.Card(null, null, row);
    }

    private Control BuildListCard()
    {
        var header = new Grid { ColumnDefinitions = Columns() };
        AddHeaderCell(header, 0, "时间");
        AddHeaderCell(header, 1, "进程");
        AddHeaderCell(header, 2, "窗口标题");
        AddHeaderCell(header, 3, "识别字数", right: true);
        AddHeaderCell(header, 4, "状态");

        _list.ItemTemplate = new FuncDataTemplate<RecordRow>((row, _) =>
        {
            var line = new Grid { ColumnDefinitions = Columns() };
            AddCell(line, 0, row.Time);
            AddCell(line, 1, row.Process);
            AddCell(line, 2, row.Title);
            AddCell(line, 3, row.Chars, right: true);
            AddCell(line, 4, row.Status);
            return line;
        }, supportsRecycling: true);

        var listBlock = new StackPanel { Spacing = 10 };
        listBlock.Children.Add(header);
        listBlock.Children.Add(Ui.Divider());
        listBlock.Children.Add(_list);

        return Ui.Card(null, null, listBlock);
    }

    private static ColumnDefinitions Columns() => new("150,90,*,64,72");

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

    private static void AddCell(Grid grid, int column, string text, bool right = false)
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

        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    // ---------------------------------------------------------------- 数据

    private ActivityQuery BuildQuery() => new()
    {
        From = RangeStart(),
        Keyword = string.IsNullOrWhiteSpace(_keyword.Text) ? null : _keyword.Text!.Trim(),
        Limit = MaxRows,
    };

    private DateTime? RangeStart() => _range.SelectedItem is ComboBoxItem { Tag: RangeFilter range }
        ? range switch
        {
            RangeFilter.Today => DateTime.Today,
            RangeFilter.Week => DateTime.Today.AddDays(-7),
            RangeFilter.Month => DateTime.Today.AddDays(-30),
            _ => null,
        }
        : DateTime.Today.AddDays(-7);

    private async Task LoadAsync()
    {
        _loading = true;

        try
        {
            var result = await _services.Store.QueryAsync(BuildQuery(), CancellationToken.None);

            _list.ItemsSource = result.Items
                .Select(item => new RecordRow(
                    item.Id,
                    item.Timestamp.ToString("MM-dd HH:mm:ss"),
                    item.ProcessName,
                    item.WindowTitle,
                    item.TextLength.ToString(),
                    Describe(item.Status)))
                .ToList();

            _summary.Text = result.Items.Count == 0
                ? "没有匹配的记录。可更换关键词，或把时间范围改为「全部」。"
                : result.TotalCount > MaxRows
                    ? $"共 {result.TotalCount} 条，显示最近 {result.Items.Count} 条"
                    : $"共 {result.TotalCount} 条";


        }
        catch (Exception ex)
        {
            _services.Log.Error("查询记录失败", ex);
            _summary.Text = "读取失败";
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnRecordDeleted() => StartLoad();

    /// <summary>选中一行 → 打开（或复用）唯一的详情窗口。</summary>
    private void OpenDetail()
    {
        if (_list.SelectedItem is not RecordRow row || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        RecordDetailWindow.ShowFor(owner, _services, row.Id);

        // 清掉选中状态：否则关掉详情窗后再点同一行不会触发 SelectionChanged，像卡住了。
        Dispatcher.UIThread.Post(() => _list.SelectedItem = null, DispatcherPriority.Background);
    }

    private static string Describe(RecordStatus status) => status switch
    {
        RecordStatus.Ok => "已识别",
        RecordStatus.NoText => "无文字",
        RecordStatus.Error => "失败",
        _ => "待识别",
    };

    private enum RangeFilter
    {
        Today,
        Week,
        Month,
        All,
    }

    private sealed record RecordRow(long Id, string Time, string Process, string Title, string Chars, string Status);
}
