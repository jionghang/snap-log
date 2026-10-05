using Avalonia.Controls;
using Avalonia.Input;
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

        Button? cancel = null;
        if (cancelText is not null)
        {
            cancel = new Button { Content = cancelText };
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);
        }

        buttons.Children.Add(confirm);

        // 键盘约定：Esc = 取消；Enter = 默认按钮。
        // 破坏性操作里默认键落在"取消"上——手快按回车不能把记录删掉。
        var enterConfirms = !danger || cancel is null;
        Opened += (_, _) => (enterConfirms ? confirm : cancel)?.Focus();

        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    Close();
                    break;
                case Key.Enter:
                    e.Handled = true;
                    _confirmed = enterConfirms;
                    Close();
                    break;
            }
        };

        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,          // 与全应用正文同号（不写会落回框架默认值）
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

    /// <summary>自检用：这次对话框是不是以"确认"收场的（Esc 关掉应为 false）。</summary>
    internal bool ConfirmedForProbe => _confirmed;

    /// <summary>返回 true 表示用户确认（只有一个按钮的提示框恒为 true）。</summary>
    public async Task<bool> ShowDialogAsync(Window owner)
    {
        await ShowDialog(owner);
        return _confirmed;
    }
}
