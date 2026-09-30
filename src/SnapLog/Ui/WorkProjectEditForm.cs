using System.Drawing;
using SnapLog.Configuration;
using SnapLog.Diagnostics;

namespace SnapLog.Ui;

/// <summary>编辑一条工作项目。</summary>
internal sealed class WorkProjectEditForm : Form
{
    private readonly WorkProjectOptions _working;

    private readonly TextBox _name = new();
    private readonly TextBox _description = new();

    public WorkProjectEditForm(WorkProjectOptions project)
    {
        _working = project.Clone();

        Text = "工作项目";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = IconFactory.AppIcon;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(600, 300);

        BuildLayout();
    }

    public WorkProjectOptions Result => _working;

    private void BuildLayout()
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(16, 14, 16, 8),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430));

        grid.RowCount = 3;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        grid.Controls.Add(NewLabel("项目名称"), 0, 0);
        _name.Dock = DockStyle.Fill;
        grid.Controls.Add(_name, 1, 0);

        grid.Controls.Add(NewLabel("说明"), 0, 1);
        _description.Dock = DockStyle.Fill;
        _description.Multiline = true;
        _description.Height = 120;
        _description.ScrollBars = ScrollBars.Vertical;
        _description.PlaceholderText = "填写该项目的工作内容、涉及的系统或关键词。描述越具体，模型分组越准确。";

        // 多行框先设文本、后首次聚焦时会把全文选中（WinForms 老行为），打开时把光标放到开头。
        Shown += (_, _) => _description.Select(0, 0);
        grid.Controls.Add(_description, 1, 1);

        var hint = new Label
        {
            Text = "该说明随提示词发送给模型，用于把活动归到本项目下面。",
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 6, 0, 6),
        };
        grid.Controls.Add(hint, 1, 2);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(16, 0, 16, 14),
        };

        var cancel = new Button { Text = "取消", Width = 88, Height = 30, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "确定", Width = 88, Height = 30 };
        ok.Click += (_, _) => Confirm();

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var root = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, Dock = DockStyle.Top };
        root.Controls.Add(grid, 0, 0);
        root.Controls.Add(buttons, 0, 1);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        _name.Text = _working.Name;
        // 多行编辑框只认 CRLF：配置里手写的 LF 换行会显示成一整行。
        _description.Text = _working.Description.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private static Label NewLabel(string text) => new()
    {
        Text = text + "：",
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 8, 6, 0),
    };

    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            MessageBox.Show("项目名称不能为空。", "工作项目", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _working.Name = _name.Text.Trim();
        _working.Description = _description.Text.Trim();

        DialogResult = DialogResult.OK;
        Close();
    }
}
