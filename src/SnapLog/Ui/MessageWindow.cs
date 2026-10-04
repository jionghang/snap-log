using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SnapLog.Ui;

/// <summary>
/// 通用提示/确认框。1.x 用的是 WinForms 的 MessageBox，2.0 自己画一个：
/// 这样配色、按钮文案、圆角都和主界面一致，也不会因为 WinForms 与 Avalonia 混用而出现两套外观。
/// </summary>
internal sealed class MessageWindow : Window
{
    private bool _confirmed;

    public MessageWindow(string title, string message, string confirmText, string? cancelText, bool danger = false)
    {
        Title = title;
        Width = 470;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 620;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = IconFactory.WindowIcon;

        var confirm = new Button
        {
            Content = confirmText,
            Classes = { danger ? "danger" : "primary" },
        };
        confirm.Click += (_, _) =>
        {
            _confirmed = true;
            Close();
        };

        var buttons = Ui.ButtonRow();
        buttons.HorizontalAlignment = HorizontalAlignment.Right;

        if (cancelText is not null)
        {
            var cancel = new Button { Content = cancelText };
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);
        }

        buttons.Children.Add(confirm);

        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 460,
        };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22, 20, 22, 18),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = title, Classes = { "section" } },
                new ScrollViewer { Content = body, MaxHeight = 460 },
                buttons,
            },
        };
    }

    /// <summary>返回 true 表示用户确认（只有一个按钮的提示框恒为 true）。</summary>
    public async Task<bool> ShowDialogAsync(Window owner)
    {
        await ShowDialog(owner);
        return _confirmed;
    }
}
