using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace SnapLog.Ui;

/// <summary>
/// 时间选择：一个显示 <c>HH:mm</c> 的小按钮，点开是自己画的浮层（24 个小时 + 4 档分钟）。
///
/// 不用 Avalonia 自带的 TimePicker：它的浮层是三段灰色数字框（Fluent 默认样式），
/// 和这套浅色界面放一起很突兀，而且多数人设"每天几点"只会挑整点或半点。
/// </summary>
internal sealed class TimeField : UserControl
{
    private readonly Button _button = new() { Classes = { "chipValue" } };
    private readonly UniformGrid _hours = new() { Columns = 6 };
    private readonly UniformGrid _minutes = new() { Columns = 4 };

    private readonly Action<string> _changed;
    private int _hour;
    private int _minute;

    public TimeField(string hhmm, Action<string> changed)
    {
        _changed = changed;
        Set(hhmm);

        _button.Flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = Ui.CardShell(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Ui.Caption("小时"),
                    _hours,
                    Ui.Caption("分钟"),
                    _minutes,
                },
            }),
        };

        _hours.Children.AddRange(Enumerable.Range(0, 24).Select(hour => Chip(
            $"{hour:00}",
            () => _hour == hour,
            () =>
            {
                _hour = hour;
                Apply();
            })));

        _minutes.Children.AddRange(new[] { 0, 15, 30, 45 }.Select(minute => Chip(
            $"{minute:00}",
            () => _minute == minute,
            () =>
            {
                _minute = minute;
                Apply();
            })));

        Content = _button;
        Render();   // 构造时不回调：初始值不是用户改动，页面这时还没建完
    }

    /// <summary>当前值，形如 22:00。</summary>
    public string Value => $"{_hour:00}:{_minute:00}";

    /// <summary>从外部（配置）设值，不触发回调。</summary>
    public void Set(string hhmm)
    {
        if (TimeOnly.TryParse(hhmm, out var parsed))
        {
            _hour = parsed.Hour;
            _minute = parsed.Minute;
        }

        Render();
    }

    private static Button Chip(string text, Func<bool> isOn, Action click)
    {
        var button = new Button { Content = text, Classes = { "chip" } };

        button.Click += (_, _) =>
        {
            click();
            button.Flyout?.Hide();
        };

        button.AttachedToVisualTree += (_, _) => Mark(button, isOn());
        return button;
    }

    private static void Mark(Button button, bool on)
    {
        if (on)
        {
            if (!button.Classes.Contains("on"))
            {
                button.Classes.Add("on");
            }
        }
        else
        {
            button.Classes.Remove("on");
        }
    }

    private void Apply()
    {
        Render();
        _changed(Value);
    }

    private void Render()
    {
        _button.Content = Value;

        foreach (var child in _hours.Children.OfType<Button>())
        {
            Mark(child, string.Equals(child.Content?.ToString(), $"{_hour:00}", StringComparison.Ordinal));
        }

        foreach (var child in _minutes.Children.OfType<Button>())
        {
            Mark(child, string.Equals(child.Content?.ToString(), $"{_minute:00}", StringComparison.Ordinal));
        }
    }
}

/// <summary>
/// 日期选择：显示 <c>yyyy-MM-dd</c> 的小按钮，点开是"今天 / 昨天 / 前天"三个快捷选择 +
/// 前后各一天的微调。补生成日报的真实场景就这几种，不需要一整个月历。
/// </summary>
internal sealed class DateField : UserControl
{
    private readonly Button _button = new() { Classes = { "chipValue" } };
    private readonly TextBlock _current = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };

    private readonly Action<DateOnly> _changed;
    private DateOnly _value;

    public DateField(DateOnly value, Action<DateOnly> changed)
    {
        _changed = changed;
        _value = value;

        var quick = new UniformGrid { Columns = 3 };
        var today = DateOnly.FromDateTime(DateTime.Today);

        quick.Children.Add(Quick("今天", () => today));
        quick.Children.Add(Quick("昨天", () => today.AddDays(-1)));
        quick.Children.Add(Quick("前天", () => today.AddDays(-2)));

        var stepper = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var back = Step("◀", -1);
        Grid.SetColumn(back, 0);
        stepper.Children.Add(back);

        _current.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(_current, 1);
        stepper.Children.Add(_current);

        var forward = Step("▶", 1);
        Grid.SetColumn(forward, 2);
        stepper.Children.Add(forward);

        _button.Flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = Ui.CardShell(new StackPanel
            {
                Spacing = 10,
                Children = { quick, stepper },
            }),
        };

        Content = _button;
        Render();
    }

    public DateOnly Value => _value;

    public void Set(DateOnly value)
    {
        _value = value;
        Render();
    }

    private Button Quick(string text, Func<DateOnly> pick)
    {
        var button = new Button { Content = text, Classes = { "chip" } };
        button.Click += (_, _) =>
        {
            Select(pick());
            button.Flyout?.Hide();
        };

        return button;
    }

    private Button Step(string text, int days)
    {
        var button = new Button { Content = text, Classes = { "chip" } };
        button.Click += (_, _) => Select(_value.AddDays(days));
        return button;
    }

    private void Select(DateOnly day)
    {
        _value = day;
        Render();
        _changed(day);
    }

    private void Render()
    {
        _button.Content = _value.ToString("yyyy-MM-dd");
        _current.Text = _value.ToString("yyyy-MM-dd");
    }
}
