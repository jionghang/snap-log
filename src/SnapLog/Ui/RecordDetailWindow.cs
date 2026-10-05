using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 记录详情窗口。原来详情挤在列表下面，列表和正文互相压；现在单独开窗，正文给足高度。
///
/// 全进程只允许一个：再看另一条时是**新内容覆盖旧内容**，不会开出一堆窗口。
/// </summary>
internal sealed class RecordDetailWindow : Window
{
    private static RecordDetailWindow? _current;

    /// <summary>在详情窗里删掉一条记录后触发，让列表刷新自己。</summary>
    public static event Action? RecordDeleted;

    private readonly AppServices _services;

    /// <summary>标题下面的元数据与按钮：固定不动，滚动只滚正文。</summary>
    private readonly StackPanel _meta = new() { Spacing = 10 };
    private readonly SelectableTextBlock _text = Ui.BodyText(string.Empty, raw: true);
    private readonly TextBlock _title = new() { Classes = { "headline" } };

    public RecordDetailWindow(AppServices services)
    {
        _services = services;

        Title = "记录详情";
        Width = 720;
        Height = 580;
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

        Content = new Border
        {
            Padding = new Thickness(22, 18, 22, 18),
            Child = BuildLayout(),
        };
    }

    private Grid BuildLayout()
    {
        // 标题 / 元数据与按钮 / 分隔线 都是固定的，只有正文在滚动区里。
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };

        grid.Children.Add(Ui.Inline(8, _title, Ui.Tip("识别出的文字可选中复制，或用复制文字按钮整段复制。")));

        _meta.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(_meta, 1);
        grid.Children.Add(_meta);

        var divider = Ui.Divider();
        divider.Margin = new Thickness(0, 12, 0, 10);
        Grid.SetRow(divider, 2);
        grid.Children.Add(divider);

        // 正文加分组框：长文本只在框内滚动，框本身不跟着动。
        // 注意行要设在框上（设在里面的 ScrollViewer 上，框会落回第 0 行）。
        var frame = new Border { Classes = { "well" }, Child = new ScrollViewer { Content = _text } };
        Grid.SetRow(frame, 3);
        grid.Children.Add(frame);

        return grid;
    }

    /// <summary>打开或复用唯一那个详情窗口，并把内容换成这条记录。</summary>
    public static void ShowFor(Window owner, AppServices services, long recordId)
    {
        if (_current is not { IsVisible: true })
        {
            _current = new RecordDetailWindow(services);
            _current.Closed += (_, _) => _current = null;
            _current.Show(owner);
        }

        _current.Activate();
        _ = _current.LoadAsync(recordId);
    }

    internal async Task LoadAsync(long recordId)
    {
        _meta.Children.Clear();
        _text.Text = string.Empty;
        _title.Text = "记录详情";

        ActivityRecord? record;

        try
        {
            record = await _services.Store.GetByIdAsync(recordId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _services.Log.Error("读取记录详情失败", ex);
            _meta.Children.Add(Ui.Caption("读取失败：" + ex.Message));
            return;
        }

        if (record is null)
        {
            _title.Text = "这条记录已经不在了";
            return;
        }

        var meta = new System.Text.StringBuilder()
            .Append($"{record.Timestamp:yyyy-MM-dd HH:mm:ss}　{record.ProcessName}　")
            .Append($"状态：{Describe(record.Status)}　字数：{record.TextLength}");

        if (!string.IsNullOrWhiteSpace(record.Error))
        {
            // 旧版本写进库里的提示里有"免费版单次 100 块上限"这类话，会让人以为要收费；
            // 展示时换成现在的新说法（库里那份不动）。
            var note = System.Text.RegularExpressions.Regex.Replace(
                record.Error,
                @"内容超过免费版单次 (\d+) 块上限，已拆分为 (\d+) 块识别",
                "画面文字较多，已分 $2 批识别（单次最多 $1 个文本块）");

            // 更早的版本写的是"已拆为"，一并兼容。
            note = System.Text.RegularExpressions.Regex.Replace(
                note,
                @"内容超过单次 (\d+) 块上限，已拆为 (\d+) 块识别",
                "画面文字较多，已分 $2 批识别（单次最多 $1 个文本块）");

            note = note.Replace("免费版单次", "单次");

            meta.AppendLine().Append("提示：").Append(note);
        }

        _title.Text = $"记录详情 · {record.Timestamp:MM-dd HH:mm}";

        // 元数据只留一行浅字，不加框：框留给正文。
        _meta.Children.Add(Ui.Caption(meta.ToString()));

        var imagePath = _services.Paths.ResolveStoredImagePath(record.ImagePath);
        var copy = Ui.Secondary("复制文字", () => Task.CompletedTask);
        copy.Click += (_, _) =>
        {
            CopyText(record.OcrText);
            copy.Content = "已复制";
        };

        var buttons = Ui.ButtonRow(copy);

        if (imagePath.Length > 0 && File.Exists(imagePath))
        {
            buttons.Children.Add(Ui.Secondary("打开截图", () =>
            {
                MainWindow.OpenPath(imagePath);
                return Task.CompletedTask;
            }));
        }

        buttons.Children.Add(Ui.Secondary("删除这条", () => DeleteAsync(recordId)));
        _meta.Children.Add(buttons);

        _text.Text = record.OcrText.Length > 0 ? record.OcrText : "（这条没有识别到文字）";
    }

    private void CopyText(string text)
    {
        if (Clipboard is { } clipboard && text.Length > 0)
        {
            _ = clipboard.SetTextAsync(text);
        }
    }

    private async Task DeleteAsync(long recordId)
    {
        if (!await Ui.Confirm(this, "删除这条记录？", "记录及其截图文件将一并删除，此操作不可撤销。", "删除", danger: true))
        {
            return;
        }

        try
        {
            var images = await _services.Store.DeleteByIdsAsync([recordId], CancellationToken.None);
            foreach (var path in images)
            {
                var full = _services.Paths.ResolveStoredImagePath(path);
                if (full.Length > 0 && File.Exists(full))
                {
                    File.Delete(full);
                }
            }

            RecordDeleted?.Invoke();
            Close();
        }
        catch (Exception ex)
        {
            _services.Log.Error("删除记录失败", ex);
            await Ui.Info(this, "删除失败", ex.Message);
        }
    }

    private static string Describe(RecordStatus status) => status switch
    {
        RecordStatus.Ok => "已识别",
        RecordStatus.NoText => "无文字",
        RecordStatus.Error => "失败",
        _ => "待识别",
    };
}
