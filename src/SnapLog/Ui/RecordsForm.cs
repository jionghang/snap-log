using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 记录查看器的独立窗口形态。内容就是 <see cref="RecordsView"/>，
/// 和主窗口「抓取记录」标签页里内嵌的是同一个控件，改一处两处都跟着变。
/// </summary>
internal sealed class RecordsForm : Form
{
    private readonly RecordsView _view;

    public RecordsForm(AppOptions options, IActivityRepository store, AppPaths paths, FileLogger log)
    {
        _view = new RecordsView(options, store, paths, log) { Dock = DockStyle.Fill };

        Text = "SnapLog 记录";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(880, 560);
        Size = new Size(1120, 720);
        Icon = IconFactory.AppIcon;
        KeyPreview = true;

        Controls.Add(_view);

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
