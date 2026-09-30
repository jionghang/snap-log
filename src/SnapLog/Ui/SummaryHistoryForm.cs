using SnapLog.Configuration;
using SnapLog.Diagnostics;

namespace SnapLog.Ui;

/// <summary>总结历史的独立窗口形态。内容就是 <see cref="SummaryHistoryView"/>。</summary>
internal sealed class SummaryHistoryForm : Form
{
    public SummaryHistoryForm(SettingsContext context)
    {
        var view = new SummaryHistoryView(context) { Dock = DockStyle.Fill };

        Text = "总结历史";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 560);
        Size = new Size(1080, 700);
        Icon = IconFactory.AppIcon;
        KeyPreview = true;

        Controls.Add(view);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                Close();
            }
        };
    }
}
