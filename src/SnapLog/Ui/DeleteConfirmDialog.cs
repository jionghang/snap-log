namespace SnapLog.Ui;

/// <summary>
/// 删除前的二次确认（记录与总结共用，保证两处的交互一致）。
///
/// 附带的文件单独勾选：删库是常规操作，删文件不可撤销，所以默认不勾——
/// 让人明确点一下才连文件一起删。默认按钮是"取消"，避免回车误删。
/// </summary>
internal sealed class DeleteConfirmDialog : Form
{
    private readonly CheckBox _deleteFiles = new();

    /// <param name="subject">删的是什么，例如"记录"或"总结"。</param>
    /// <param name="count">条数。</param>
    /// <param name="fileLabel">附带文件的说法，例如"截图文件"或"总结文件（.md）"。</param>
    /// <param name="fileCount">附带文件的数量；为 0 时复选框不可勾。</param>
    /// <param name="noFileHint">没有附带文件时的说明。</param>
    public DeleteConfirmDialog(string subject, int count, string fileLabel, int fileCount, string noFileHint)
    {
        Text = $"删除{subject}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = IconFactory.AppIcon;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(460, 210);

        BuildLayout(subject, count, fileLabel, fileCount, noFileHint);
    }

    /// <summary>是否同时删除对应的文件。</summary>
    public bool DeleteFiles => _deleteFiles.Checked;

    private void BuildLayout(string subject, int count, string fileLabel, int fileCount, string noFileHint)
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
            Text = $"将删除 {count} 条{subject}，删除后无法恢复。",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });

        _deleteFiles.Text = fileCount > 0
            ? $"同时删除这 {fileCount} 个{fileLabel}（不可恢复）"
            : $"这些{subject}没有对应的{fileLabel}";
        _deleteFiles.AutoSize = true;
        _deleteFiles.Enabled = fileCount > 0;
        _deleteFiles.Margin = new Padding(0, 4, 0, 4);
        root.Controls.Add(_deleteFiles);

        root.Controls.Add(new Label
        {
            Text = fileCount > 0 ? $"不勾选时只删{subject}，{fileLabel}留在磁盘上。" : noFileHint,
            AutoSize = true,
            MaximumSize = new Size(420, 0),
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
        AcceptButton = cancel;
        CancelButton = cancel;
    }
}
