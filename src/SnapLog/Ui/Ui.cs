using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace SnapLog.Ui;

internal enum PillKind
{
    Neutral,
    Ok,
    Warn,
    Danger,
}

/// <summary>
/// 界面构件工厂。2.0 的页面用代码搭建（和 1.x 的手写界面一致），
/// 这里把"卡片 / 字段行 / 徽标 / 按钮"这些重复结构收在一处，
/// 保证所有页面长一个样子，改样式只改这里和 Theme.axaml。
/// </summary>
internal static class Ui
{
    /// <summary>字段行里左侧标题列的宽度。够放下最长的"自动生成总结时间"。</summary>
    private const double LabelWidth = 132;

    public static TextBlock PageTitle(string text) => new()
    {
        Text = text,
        Classes = { "page" },
        Margin = new Thickness(0, 0, 0, 2),
    };

    public static TextBlock Section(string text) => new() { Text = text, Classes = { "section" } };

    public static TextBlock Caption(string text) => new() { Text = text, Classes = { "caption" } };

    public static TextBlock Hint(string text) => new() { Text = text, Classes = { "hint" } };

    public static TextBlock Label(string text) => new() { Text = text, Classes = { "field" } };

    public static TextBlock Metric(string text) => new() { Text = text, Classes = { "metric" } };

    public static TextBlock Mono(string text) => new() { Text = text, Classes = { "mono" } };

    /// <summary>详情窗口里的正文（识别文字、总结正文）。13px 配默认行高对中文太挤，统一 14 / 22。</summary>
    public static SelectableTextBlock BodyText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
        LineHeight = 22,
    };

    /// <summary>
    /// 问号提示：成段的说明不直接铺在界面上，收成一个"?"，鼠标悬停才展开。
    /// 界面上只留"要做什么"，"为什么、细节、边界情况"放进这里。
    /// </summary>
    public static Border Tip(string text)
    {
        var badge = new Border
        {
            Classes = { "tip" },
            Child = new TextBlock { Text = "?" },
        };

        ToolTip.SetTip(badge, new TextBlock
        {
            Text = text,
            MaxWidth = 320,
            TextWrapping = TextWrapping.Wrap,
        });

        // 读屏软件只会念出"?"，把提示内容同时登记成可访问名。
        AutomationProperties.SetName(badge, text);

        return badge;
    }

    /// <summary>横向排一行，间距 8：标题+问号、正文+提示这类"并排"都用它。</summary>
    public static StackPanel Inline(params Control[] children) => Inline(8, children);

    public static StackPanel Inline(double spacing, params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    /// <summary>卡片标题；带提示时标题旁挂一个问号。</summary>
    public static Control Header(string title, string? tip = null) =>
        string.IsNullOrEmpty(tip) ? Section(title) : Inline(Section(title), Tip(tip));

    /// <summary>页面标题；带提示时标题旁挂一个问号（原来标题下面那行说明改挂这里）。</summary>
    public static Control PageHeader(string text, string tip) => Inline(10, PageTitle(text), Tip(tip));

    /// <summary>列表表头的一格：列名。列宽由各页的 Columns() 决定，两个记录页共用这套。</summary>
    public static void ListHeaderCell(Grid grid, int column, string text, bool right = false)
    {
        var block = Caption(text);
        block.FontWeight = FontWeight.SemiBold;
        if (right)
        {
            block.TextAlignment = TextAlignment.Right;
            block.Margin = new Thickness(0, 0, 14, 0);   // 和右边那一列留出间距
        }

        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    /// <summary>列表行的一格：正文。右对齐的数字列同样留出 14px 间距。</summary>
    public static void ListCell(Grid grid, int column, string text, bool right = false, string? color = null)
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
            block.Margin = new Thickness(0, 0, 14, 0);
        }

        if (color is not null)
        {
            block.Foreground = new SolidColorBrush(Color.Parse(color));
        }

        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    /// <summary>
    /// 列表卡片骨架：表头 + 分隔线 + 空态说明 + 列表。
    /// 抓取记录和总结记录共用它，保证两个页面的列表长得一模一样。
    /// </summary>
    public static Border ListCard(Grid header, TextBlock empty, ListBox list)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(header);
        body.Children.Add(Divider());
        body.Children.Add(empty);
        body.Children.Add(list);
        return Card(null, null, body);
    }

    /// <summary>一张白卡片：标题 + 说明 + 内容。内容之间默认 14px 间距。</summary>
    public static Border Card(string? title = null, string? hint = null, params Control[] children) =>
        CardWith(string.IsNullOrEmpty(title) ? null : Section(title),
            string.IsNullOrEmpty(hint) ? null : Hint(hint),
            children);

    /// <summary>副标题传控件：状态行这种要随配置刷新内容的地方用它（单独一个名字，避免和字符串重载撞车）。</summary>
    public static Border CardWithHeader(string? title, Control? hintControl, params Control[] children) =>
        CardWith(string.IsNullOrEmpty(title) ? null : Section(title), hintControl, children);

    /// <summary>卡片：标题自己给（可以带问号提示），下面可选一行说明。</summary>
    public static Border CardWith(Control? header, Control? hintControl, params Control[] children)
    {
        var body = new StackPanel { Spacing = 0 };

        if (header is not null)
        {
            body.Children.Add(header);
        }

        if (hintControl is not null)
        {
            hintControl.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(hintControl);
        }

        foreach (var child in children)
        {
            child.Margin = child.Margin == default
                ? new Thickness(0, 14, 0, 0)
                : child.Margin;
            body.Children.Add(child);
        }

        return new Border { Classes = { "card" }, Child = body };
    }

    /// <summary>只要卡片外观、内容自己排版。给"列表要占满剩余高度"的页面用。</summary>
    public static Border CardShell(Control child) => new() { Classes = { "card" }, Child = child };

    /// <summary>
    /// 一行：左侧固定宽度标题（可带问号提示），右侧控件，下面可选一行灰色说明。
    /// 能收进问号的说明就别写成 hint——界面上少一行字，扫起来更快。
    /// </summary>
    public static Grid FieldRow(string label, Control editor, string? hint = null, string? tip = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{LabelWidth},*"),
        };

        var caption = Label(label);
        caption.VerticalAlignment = VerticalAlignment.Center;

        if (string.IsNullOrEmpty(tip))
        {
            Grid.SetColumn(caption, 0);
            grid.Children.Add(caption);
        }
        else
        {
            var labelRow = Inline(6, caption, Tip(tip));
            Grid.SetColumn(labelRow, 0);
            grid.Children.Add(labelRow);
        }

        // 靠左对齐：显式设了 Width 的控件在 Stretch 下会被居中，同一张卡里就会"两个控件列"。
        editor.HorizontalAlignment = HorizontalAlignment.Left;
        editor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);

        if (string.IsNullOrEmpty(hint))
        {
            return grid;
        }

        var wrapper = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        wrapper.Children.Add(grid);

        var hintText = Hint(hint);
        hintText.Margin = new Thickness(LabelWidth, 4, 0, 0);
        hintText.MaxWidth = 560;
        // 靠左：MaxWidth + 默认 Stretch 会把这行说明居中，看起来像飘在半空。
        hintText.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(hintText, 1);
        wrapper.Children.Add(hintText);
        return wrapper;
    }

    /// <summary>竖排字段组，行距 12。</summary>
    public static StackPanel RowStack(params Control[] rows)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var row in rows)
        {
            panel.Children.Add(row);
        }

        return panel;
    }

    public static Border Pill(string text, PillKind kind = PillKind.Neutral) => new()
    {
        Classes = { "pill", KindClass(kind) },
        Child = new TextBlock { Text = text },
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static void UpdatePill(Border pill, string text, PillKind kind)
    {
        if (pill.Child is TextBlock block)
        {
            block.Text = text;
        }

        var classes = pill.Classes;
        classes.Remove("ok");
        classes.Remove("warn");
        classes.Remove("danger");

        var wanted = KindClass(kind);
        if (wanted.Length > 0 && !classes.Contains(wanted))
        {
            classes.Add(wanted);
        }
    }

    private static string KindClass(PillKind kind) => kind switch
    {
        PillKind.Ok => "ok",
        PillKind.Warn => "warn",
        PillKind.Danger => "danger",
        _ => string.Empty,
    };

    public static Button Primary(string text, Func<Task> onClick)
    {
        var button = new Button { Content = text, Classes = { "primary" } };
        button.Click += async (_, _) => await Guarded(onClick);
        return button;
    }

    public static Button Secondary(string text, Func<Task> onClick)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await Guarded(onClick);
        return button;
    }

    public static Button Link(string text, Action onClick)
    {
        var button = new Button { Content = text, Classes = { "link" } };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>按钮里跑的是异步逻辑，异常不能炸掉界面——统一吞成日志 + 状态栏提示。</summary>
    private static async Task Guarded(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            TrayBalloon.TryShow($"{ex.GetType().Name}: {ex.Message}",
                System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    public static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = text, IsChecked = value };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    public static ToggleSwitch Switch(bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsChecked = value, OnContent = string.Empty, OffContent = string.Empty };
        toggle.IsCheckedChanged += (_, _) => changed(toggle.IsChecked == true);
        return toggle;
    }

    public static TextBox Input(string value, Action<string> changed, string? watermark = null, double width = 320)
    {
        var box = new TextBox
        {
            Text = value,
            Width = width,
            PlaceholderText = watermark,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        box.TextChanged += (_, _) => changed(box.Text ?? string.Empty);
        return box;
    }

    public static TextBox Secret(string value, Action<string> changed, double width = 320)
    {
        var box = new TextBox
        {
            Text = value,
            Width = width,
            PasswordChar = '●',
            PlaceholderText = "留空则从环境变量读取",
        };
        box.TextChanged += (_, _) => changed(box.Text ?? string.Empty);
        return box;
    }

    public static NumericUpDown Number(double value, Action<double> changed, double min, double max, double increment = 1)
    {
        var box = new NumericUpDown
        {
            Value = (decimal)value,
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            Increment = (decimal)increment,
            Width = 130,
            FormatString = "0",
        };
        box.ValueChanged += (_, _) => changed((double)(box.Value ?? 0));
        return box;
    }

    public static TimeSpan? ParseTime(string? hhmm) =>
        TimeOnly.TryParse(hhmm, out var parsed) ? parsed.ToTimeSpan() : null;

    public static ComboBox Choice<T>(IReadOnlyList<(T Value, string Label)> items, T selected, Action<T> changed, double width = 220)
        where T : notnull
    {
        var combo = new ComboBox { Width = width };
        var index = 0;

        for (var i = 0; i < items.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = items[i].Label, Tag = items[i].Value });
            if (EqualityComparer<T>.Default.Equals(items[i].Value, selected))
            {
                index = i;
            }
        }

        combo.SelectedIndex = index;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T value })
            {
                changed(value);
            }
        };

        return combo;
    }

    /// <summary>横向按钮条，间距 8。</summary>
    public static StackPanel ButtonRow(params Control[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }

    public static Border Divider() => new() { Classes = { "divider" } };

    /// <summary>把页面内容裹成"卡片之间留 16px"的竖排。</summary>
    public static StackPanel Page(params Control[] blocks)
    {
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(20, 18, 20, 20) };
        foreach (var block in blocks)
        {
            panel.Children.Add(block);
        }

        return panel;
    }

    /// <summary>带滚动条 + 内边距的页面外壳，页面一律用它，滚动条位置才一致。</summary>
    public static ScrollViewer Scroller(Control content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    public static string FormatTime(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss");

    public static string Shorten(string text, int max)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    // ---------------------------------------------------------------- 对话框

    /// <summary>大模型是否配置齐全（地址 + 模型 + 密钥，密钥可以来自环境变量）。</summary>
    public static bool IsLlmReady(Configuration.AppOptions options)
    {
        var provider = options.Summarization.Providers.FirstOrDefault(p => p.Enabled);
        return provider is not null
               && !string.IsNullOrWhiteSpace(provider.Endpoint)
               && !string.IsNullOrWhiteSpace(provider.Model)
               && (!string.IsNullOrWhiteSpace(provider.ApiKey)
                   || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                       string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable)
                           ? "SNAPLOG_OPENAI_API_KEY"
                           : provider.ApiKeyEnvironmentVariable)));
    }

    /// <summary>飞书接入信息是否填全（四个标识）。</summary>
    public static bool IsFeishuReady(Configuration.AppOptions options)
    {
        var feishu = options.Feishu;
        return !string.IsNullOrWhiteSpace(feishu.AppId)
               && (!string.IsNullOrWhiteSpace(feishu.AppSecret)
                   || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                       string.IsNullOrWhiteSpace(feishu.AppSecretEnvironmentVariable)
                           ? "SNAPLOG_FEISHU_APP_SECRET"
                           : feishu.AppSecretEnvironmentVariable)))
               && !string.IsNullOrWhiteSpace(feishu.AppToken)
               && !string.IsNullOrWhiteSpace(feishu.TableId);
    }

    /// <summary>弹出子窗口。取不到宿主窗口时（界面自检就没有）直接不弹，免得空引用。</summary>
    public static void ShowDialog(Control from, Window window)
    {
        if (TopLevel.GetTopLevel(from) is Window owner)
        {
            window.Show(owner);
        }
    }

    public static async Task Info(Window owner, string title, string message) =>
        await new MessageWindow(title, message, "确定", null).ShowDialogAsync(owner);

    /// <summary>确认框。返回 true 表示用户点了确认按钮。</summary>
    public static async Task<bool> Confirm(Window owner, string title, string message, string confirmText, bool danger = false) =>
        await new MessageWindow(title, message, confirmText, "取消", danger).ShowDialogAsync(owner);
}
