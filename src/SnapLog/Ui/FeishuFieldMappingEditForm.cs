using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;

namespace SnapLog.Ui;

/// <summary>
/// 新增/编辑一条字段映射。
/// “总结字段”用下拉限定，避免手打错名字导致映射静默失效
/// （映射的 RecordField 对不上任何总结字段时，那一列就是空的）。
/// “飞书字段名”允许留空，表示这一列不写——表里没有对应列时就这么用。
/// </summary>
internal sealed class FeishuFieldMappingEditForm : Form
{
    private readonly FeishuFieldMapping _working;
    private readonly IReadOnlyList<FeishuBitablePublisher.FeishuTableField> _knownFields;

    private readonly ScrollSafeComboBox _recordField = new();
    private readonly ScrollSafeComboBox _feishuField = new();
    private readonly Label _hint = new();

    public FeishuFieldMappingEditForm(
        FeishuFieldMapping mapping,
        bool isNew,
        IReadOnlyList<FeishuBitablePublisher.FeishuTableField>? knownFields = null)
    {
        _working = mapping.Clone();
        _knownFields = knownFields ?? [];

        Text = isNew ? "新增字段映射" : "编辑字段映射";
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

        grid.Controls.Add(NewLabel("总结字段"), 0, 0);
        _recordField.DropDownStyle = ComboBoxStyle.DropDownList;
        _recordField.Dock = DockStyle.Fill;
        foreach (var field in FeishuFieldMapping.AvailableFields)
        {
            _recordField.Items.Add(new FieldChoice(field, FeishuFieldMapping.DescribeField(field)));
        }

        SelectRecordField(_working.RecordField);
        _recordField.SelectedIndexChanged += (_, _) => UpdateHint();
        grid.Controls.Add(_recordField, 1, 0);

        grid.Controls.Add(NewLabel("飞书字段名"), 0, 1);

        // 下拉里放的是表里真实的列名（点“读取表字段名”拿到的），但也允许直接输入：
        // 没读取过、或者想先填着、等表建好再用，都不该被挡住。
        _feishuField.DropDownStyle = ComboBoxStyle.DropDown;
        _feishuField.Dock = DockStyle.Fill;
        foreach (var field in _knownFields)
        {
            _feishuField.Items.Add(new ColumnChoice(field.Name, field.TypeName));
        }

        _feishuField.Text = _working.FeishuField;
        _feishuField.TextChanged += (_, _) => UpdateHint();
        _feishuField.SelectedIndexChanged += (_, _) =>
        {
            // 下拉项显示成"列名（类型）"，选中后只把列名留在输入框里，别把类型也带进映射。
            if (_feishuField.SelectedItem is ColumnChoice choice)
            {
                _feishuField.Text = choice.Name;
            }
        };

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

    private void SelectRecordField(string field)
    {
        foreach (var item in _recordField.Items)
        {
            if (item is FieldChoice choice && string.Equals(choice.Field, field, StringComparison.Ordinal))
            {
                _recordField.SelectedItem = item;
                return;
            }
        }

        if (_recordField.Items.Count > 0)
        {
            _recordField.SelectedIndex = 0;
        }
    }

    private void UpdateHint()
    {
        var field = (_recordField.SelectedItem as FieldChoice)?.Field ?? string.Empty;
        var target = _feishuField.Text.Trim();

        var lines = new List<string>
        {
            $"把“{FeishuFieldMapping.DescribeField(field)}”写到飞书的“{(target.Length == 0 ? "(未设置)" : target)}”列。",
        };

        lines.Add(field switch
        {
            nameof(Storage.SummaryRun.StartedAt) or nameof(Storage.SummaryRun.FinishedAt) =>
                "时间会按飞书列的类型自动处理：日期列发毫秒时间戳，文本列发 yyyy-MM-dd HH:mm:ss。",
            nameof(Storage.SummaryRun.RecordCount) or nameof(Storage.SummaryRun.ImageCount)
                or nameof(Storage.SummaryRun.ElapsedMilliseconds) or nameof(Storage.SummaryRun.Attempts) =>
                "这一项是数字，飞书列建议用数字类型。",
            nameof(Storage.SummaryRun.Markdown) =>
                "总结正文可能很长，超出长度上限会被截断并标注（上限在配置文件的 Feishu.MaxTextLength 里调）。",
            nameof(Storage.SummaryRun.Preview) => "总结摘要取正文前 120 字，适合放短文本列。",
            nameof(Storage.SummaryRun.SavedPath) => "总结 Markdown 文件在本机的路径，只对这台机器有意义。",
            nameof(Storage.SummaryRun.Message) => "成功时是保存说明，失败时是失败原因。",
            _ => "飞书列建议用文本类型。",
        });

        if (target.Length == 0)
        {
            lines.Add("目标字段留空 = 这一列不写。表里没有这一列时就留空，不必为了凑齐而硬填。");
        }
        else if (_knownFields.Count > 0)
        {
            var matched = _knownFields.FirstOrDefault(
                f => string.Equals(f.Name, target, StringComparison.Ordinal));

            lines.Add(matched is null
                ? "表内没有该列名。请从下拉中选择，或核对表内实际列名（空格与换行需完全一致）。"
                : $"表里这一列的类型是“{matched.TypeName}”，写入时会按它自动转换。");
        }

        _hint.Text = string.Join(Environment.NewLine, lines);
    }

    private void Confirm()
    {
        var field = (_recordField.SelectedItem as FieldChoice)?.Field ?? string.Empty;
        if (field.Length == 0)
        {
            MessageBox.Show("请选择总结字段。", "字段映射", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _working.RecordField = field;
        _working.FeishuField = _feishuField.Text.Trim();

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>下拉项：值是总结字段名，显示的是它的中文说明。</summary>
    private sealed record FieldChoice(string Field, string Label)
    {
        public override string ToString() => $"{Label}（{Field}）";
    }

    /// <summary>飞书列的下拉项：显示"列名（类型）"，选中后只取列名。</summary>
    private sealed record ColumnChoice(string Name, string TypeName)
    {
        public override string ToString() => $"{Name}（{TypeName}）";
    }
}
