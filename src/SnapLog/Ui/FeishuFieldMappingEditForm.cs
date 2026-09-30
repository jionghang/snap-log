using SnapLog.Configuration;
using SnapLog.Diagnostics;

namespace SnapLog.Ui;

/// <summary>
/// 编辑一条字段映射。
/// 界面上的「记录字段」用下拉限定，避免手打错名字导致映射静默失效
/// （映射的 RecordField 对不上任何记录字段时，那一列就是空的）。
/// </summary>
internal sealed class FeishuFieldMappingEditForm : Form
{
    private readonly FeishuFieldMapping _working;

    private readonly ComboBox _recordField = new();
    private readonly TextBox _feishuField = new();
    private readonly Label _hint = new();

    public FeishuFieldMappingEditForm(FeishuFieldMapping mapping)
    {
        _working = mapping.Clone();

        Text = "字段映射";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = IconFactory.AppIcon;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(620, 320);

        BuildLayout();
    }

    public FeishuFieldMapping Result => _working;

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
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));

        grid.RowCount = 4;
        for (var i = 0; i < 4; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        grid.Controls.Add(NewLabel("记录字段"), 0, 0);
        _recordField.DropDownStyle = ComboBoxStyle.DropDownList;
        _recordField.Dock = DockStyle.Fill;
        foreach (var field in FeishuFieldMapping.AvailableRecordFields)
        {
            _recordField.Items.Add(field);
        }

        var index = _recordField.Items.IndexOf(_working.RecordField);
        _recordField.SelectedIndex = index >= 0 ? index : 0;
        _recordField.SelectedIndexChanged += (_, _) => UpdateHint();
        grid.Controls.Add(_recordField, 1, 0);

        grid.Controls.Add(NewLabel("飞书字段名"), 0, 1);
        _feishuField.Dock = DockStyle.Fill;
        _feishuField.Text = _working.FeishuField;
        _feishuField.PlaceholderText = "必须与数据表里的列名完全一致；留空表示不写这一列";
        _feishuField.TextChanged += (_, _) => UpdateHint();
        grid.Controls.Add(_feishuField, 1, 1);

        _hint.AutoSize = true;
        _hint.MaximumSize = new Size(420, 0);
        _hint.ForeColor = SystemColors.GrayText;
        _hint.Margin = new Padding(3, 8, 0, 6);
        grid.Controls.Add(_hint, 1, 2);

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

        UpdateHint();
    }

    private static Label NewLabel(string text) => new()
    {
        Text = text + "：",
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 8, 6, 0),
    };

    private void UpdateHint()
    {
        var recordField = _recordField.SelectedItem as string ?? string.Empty;
        var target = _feishuField.Text.Trim();

        var lines = new List<string> { $"把记录里的 {recordField} 写到飞书的「{(target.Length == 0 ? "(未设置)" : target)}」列。" };

        lines.Add(recordField switch
        {
            "Timestamp" => "时间会按飞书列的类型自动处理：日期列发毫秒时间戳，文本列发 yyyy-MM-dd HH:mm:ss。",
            "TextLength" or "OcrMilliseconds" => "这一项是数字，飞书列建议用数字类型。",
            "OcrText" => "OCR 文字可能很长，超出长度上限会被截断并标注（上限在「全部选项」里调）。",
            "Status" => "状态取值为 Ok / NoText / Error / Pending。",
            _ => "飞书列建议用文本类型。",
        });

        if (target.Length == 0)
        {
            lines.Add("目标字段留空 = 这一列不写。");
        }

        _hint.Text = string.Join(Environment.NewLine, lines);
    }

    private void Confirm()
    {
        _working.RecordField = _recordField.SelectedItem as string ?? string.Empty;
        _working.FeishuField = _feishuField.Text.Trim();

        if (_working.RecordField.Length == 0)
        {
            MessageBox.Show("请选择记录字段。", "字段映射", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
