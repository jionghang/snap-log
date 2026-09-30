namespace SnapLog.Ui;

/// <summary>
/// 删除记录前的二次确认。
/// 截图文件单独勾选：删库是常规操作，删文件不可撤销，所以默认不勾——
/// 让人明确点一下才连文件一起删。
/// </summary>
internal sealed class DeleteRecordsDialog : Form
{
    private readonly CheckBox _deleteScreenshots = new();

    public DeleteRecordsDialog(int recordCount, int screenshotCount)
    {
        Text = "删除记录";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = IconFactory.AppIcon;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(440, 200);

        BuildLayout(recordCount, screenshotCount);
    }

    /// <summary>是否同时删除对应的截图文件。</summary>
    public bool DeleteScreenshots => _deleteScreenshots.Checked;

    private void BuildLayout(int recordCount, int screenshotCount)
    {
        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(16, 14, 16, 10),
        };

        root.Controls.Add(new Label
        {
            Text = $"将删除 {recordCount} 条记录，删除后无法恢复。",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });

        _deleteScreenshots.Text = screenshotCount > 0
            ? $"同时删除这 {screenshotCount} 张截图文件（不可恢复）"
            : "这些记录没有截图文件";
        _deleteScreenshots.AutoSize = true;
        _deleteScreenshots.Enabled = screenshotCount > 0;
        _deleteScreenshots.Margin = new Padding(0, 4, 0, 4);
        root.Controls.Add(_deleteScreenshots);

        root.Controls.Add(new Label
        {
            Text = screenshotCount > 0
                ? "不勾选时只删记录，截图文件留在磁盘上。"
                : "截图文件已被保留策略清理，或抓取时没有开启保存截图。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 0),
        });

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(16, 0, 16, 12),
        };

        var cancel = new Button { Text = "取消", Width = 88, Height = 30, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "删除", Width = 88, Height = 30, DialogResult = DialogResult.OK };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var wrapper = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, Dock = DockStyle.Top };
        wrapper.Controls.Add(root, 0, 0);
        wrapper.Controls.Add(buttons, 0, 1);

        Controls.Add(wrapper);
        AcceptButton = cancel;   // 危险操作不设默认为删除
        CancelButton = cancel;
    }
}
