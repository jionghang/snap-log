using Avalonia;
using Avalonia.Controls;
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
    private readonly StackPanel _body = new() { Spacing = 10 };
    private readonly TextBlock _title = new() { FontSize = 15.5, FontWeight = FontWeight.SemiBold };

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

        Content = new Border
        {
            Padding = new Thickness(22, 18, 22, 18),
            Child = BuildLayout(),
        };
    }

    private Grid BuildLayout()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        grid.Children.Add(Ui.Inline(8, _title, Ui.Tip("识别出的文字可以选中复制，也可以用「复制文字」整段复制。")));

        var body = new ScrollViewer { Content = _body };
        body.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

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
        _body.Children.Clear();
        _title.Text = "记录详情";

        ActivityRecord? record;

        try
        {
            record = await _services.Store.GetByIdAsync(recordId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _services.Log.Error("读取记录详情失败", ex);
            _body.Children.Add(Ui.Caption("读取失败：" + ex.Message));
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

        _body.Children.Add(Ui.Hint(meta.ToString()));

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
        _body.Children.Add(buttons);

        _body.Children.Add(Ui.BodyText(record.OcrText.Length > 0 ? record.OcrText : "（这条没有识别到文字）"));
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
        if (!await Ui.Confirm(this, "删除这条记录？", "记录和它的截图文件都会被删除，这个操作不可撤销。", "删除", danger: true))
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
