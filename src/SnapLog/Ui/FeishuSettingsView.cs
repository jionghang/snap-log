using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>推送配置页：飞书多维表格的定时写入。</summary>
internal sealed class FeishuSettingsView : SettingsViewBase
{
    private CheckBox _feishuEnabled = null!;
    private TextBox _feishuTime = null!;
    private TextBox _feishuAppId = null!;
    private TextBox _feishuSecret = null!;
    private TextBox _feishuAppToken = null!;
    private TextBox _feishuTableId = null!;
    private ListBox _fieldMappingList = null!;
    private Button _testFeishu = null!;
    private Label _feishuHint = null!;

    public FeishuSettingsView(SettingsContext context)
        : base(context)
    {
    }

    protected override Control BuildContent()
    {
        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(4, 4, 4, 8),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var grid = NewSection("写入飞书多维表格");

        var enableRow = NewRow();
        _feishuEnabled = new CheckBox
        {
            Text = "每天定时把当天的记录写进飞书多维表格",
            AutoSize = true,
            Margin = new Padding(3, 6, 12, 0),
        };
        _feishuEnabled.CheckedChanged += (_, _) => UpdateEnabledState();
        enableRow.Controls.Add(_feishuEnabled);

        enableRow.Controls.Add(new Label { Text = "时间", AutoSize = true, Margin = new Padding(0, 9, 4, 0) });
        _feishuTime = new TextBox { Width = 70, PlaceholderText = "HH:mm" };
        enableRow.Controls.Add(_feishuTime);
        enableRow.Controls.Add(new Label { Text = "(HH:mm)", AutoSize = true, Margin = new Padding(4, 9, 0, 0) });
        AddRow(grid, "定时推送", enableRow);

        // ---- 应用凭证 ----
        var appRow = NewRow();
        _feishuAppId = new TextBox { Width = 240, PlaceholderText = "App ID（cli_…）" };
        appRow.Controls.Add(_feishuAppId);
        appRow.Controls.Add(new Label { Text = "App Secret", AutoSize = true, Margin = new Padding(12, 9, 4, 0) });
        _feishuSecret = new TextBox { Width = 200, UseSystemPasswordChar = true };
        appRow.Controls.Add(_feishuSecret);
        AddRow(grid, "应用凭证", appRow);

        AddRow(grid, string.Empty, NewHint(
            "App Secret 留空则读环境变量 SNAPLOG_FEISHU_APP_SECRET（推荐）。"
            + "这个应用需要两层权限：开发者后台开通多维表格读写（bitable:app），"
            + "并且被加为这张多维表格的可编辑协作者——只开一层会分别报 99991672 和 91403。"));

        // ---- 数据表 ----
        var tableRow = NewRow();
        _feishuAppToken = new TextBox { Width = 250, PlaceholderText = "app_token（整张多维表格）" };
        tableRow.Controls.Add(_feishuAppToken);
        tableRow.Controls.Add(new Label { Text = "table_id", AutoSize = true, Margin = new Padding(12, 9, 4, 0) });
        _feishuTableId = new TextBox { Width = 150, PlaceholderText = "tbl…" };
        tableRow.Controls.Add(_feishuTableId);
        AddRow(grid, "数据表", tableRow);

        // ---- 字段映射 ----
        var mappingRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
        };
        mappingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 440));
        mappingRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _fieldMappingList = new ListBox { Height = 120, Width = 440, IntegralHeight = false };
        _fieldMappingList.DoubleClick += (_, _) => EditSelectedMapping();
        mappingRow.Controls.Add(_fieldMappingList, 0, 0);

        var mappingButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(8, 0, 0, 0),
        };
        mappingButtons.Controls.Add(NewSmallButton("编辑…", EditSelectedMapping));
        mappingButtons.Controls.Add(NewSmallButton("恢复默认", ResetMappings));
        mappingRow.Controls.Add(mappingButtons, 1, 0);

        AddRow(grid, "字段映射", mappingRow);
        AddRow(grid, string.Empty, NewHint(
            "飞书按字段名精确匹配（差一个空格或换行都会报 1254045），"
            + "所以这里写的名字必须和表里的列名完全一致。点「测试连接」会先核对一遍。"));

        // ---- 操作 ----
        var actionsRow = NewRow();
        _testFeishu = new Button { Text = "测试连接", Width = 100, Height = 28, Margin = new Padding(0, 3, 8, 0) };
        _testFeishu.Click += async (_, _) => await TestFeishuAsync();
        actionsRow.Controls.Add(_testFeishu);

        var pushNow = new Button { Text = "立即写入…", Width = 100, Height = 28, Margin = new Padding(0, 3, 8, 0) };
        pushNow.Click += async (_, _) => await PushToFeishuNowAsync();
        actionsRow.Controls.Add(pushNow);

        _feishuHint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(4, 9, 0, 0) };
        actionsRow.Controls.Add(_feishuHint);
        AddRow(grid, "操作", actionsRow);

        root.Controls.Add(grid);
        return root;
    }

    protected override void LoadFromOptions()
    {
        var feishu = Options.Feishu;

        _feishuEnabled.Checked = feishu.Enabled;
        _feishuTime.Text = feishu.ScheduleTimeOfDay;
        _feishuAppId.Text = feishu.AppId;
        _feishuSecret.Text = feishu.AppSecret;
        _feishuAppToken.Text = feishu.AppToken;
        _feishuTableId.Text = feishu.TableId;

        RefreshFieldMappingList();
        UpdateEnabledState();
        UpdateFeishuHint();
    }

    protected override void WriteToOptions()
    {
        var feishu = Options.Feishu;

        feishu.Enabled = _feishuEnabled.Checked;
        feishu.ScheduleTimeOfDay = _feishuTime.Text.Trim();
        feishu.AppId = _feishuAppId.Text.Trim();
        feishu.AppSecret = _feishuSecret.Text.Trim();
        feishu.AppToken = _feishuAppToken.Text.Trim();
        feishu.TableId = _feishuTableId.Text.Trim();
    }

    private void UpdateEnabledState()
    {
        _feishuTime.Enabled = _feishuEnabled.Checked;
    }

    private void UpdateFeishuHint()
    {
        var variableName = FeishuBitablePublisher.SecretVariableName(Options.Feishu);
        var fromEnvironment = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName));

        _feishuHint.ForeColor = SystemColors.GrayText;
        _feishuHint.Text = fromEnvironment
            ? $"已检测到环境变量 {variableName}"
            : _feishuSecret.Text.Length > 0
                ? "App Secret 来自这里的明文配置"
                : $"等待配置（App Secret 可从环境变量 {variableName} 读取）";
    }

    // ---------------------------------------------------------------- 字段映射

    private void RefreshFieldMappingList()
    {
        var selected = _fieldMappingList.SelectedIndex;

        _fieldMappingList.Items.Clear();
        foreach (var mapping in Options.Feishu.FieldMappings)
        {
            _fieldMappingList.Items.Add(mapping);
        }

        if (_fieldMappingList.Items.Count > 0)
        {
            _fieldMappingList.SelectedIndex = Math.Clamp(selected, 0, _fieldMappingList.Items.Count - 1);
        }
    }

    private void EditSelectedMapping()
    {
        if (_fieldMappingList.SelectedItem is not FeishuFieldMapping mapping)
        {
            MessageBox.Show("先选中一条映射。", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new FeishuFieldMappingEditForm(mapping);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        var index = _fieldMappingList.SelectedIndex;
        Options.Feishu.FieldMappings[index] = dialog.Result;
        RefreshFieldMappingList();
        _fieldMappingList.SelectedIndex = index;
    }

    private void ResetMappings()
    {
        var confirm = MessageBox.Show(
            "把字段映射恢复成默认的 9 条？" + Environment.NewLine + Environment.NewLine
            + "飞书列名会写回「时间 / 进程 / 窗口标题 / 窗口类名 / 字数 / 识别耗时 / 抓取方式 / 状态 / 识别文字」。",
            "SnapLog", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        Options.Feishu.FieldMappings = FeishuFieldMapping.CreateDefault();
        RefreshFieldMappingList();
    }

    // ---------------------------------------------------------------- 操作

    private async Task TestFeishuAsync()
    {
        WriteToOptions();

        var original = _testFeishu.Text;
        _testFeishu.Enabled = false;
        _testFeishu.Text = "测试中…";
        _feishuHint.ForeColor = SystemColors.GrayText;
        _feishuHint.Text = "正在换取令牌并核对字段…";

        try
        {
            var publisher = new FeishuBitablePublisher(Options.Feishu, Log);
            var result = await publisher.TestAsync(CancellationToken.None);

            _feishuHint.ForeColor = result.Success ? Color.SeaGreen : Color.OrangeRed;
            _feishuHint.Text = Flatten(result.Message);

            MessageBox.Show(
                result.Message,
                result.Success ? "飞书连接测试通过" : "飞书连接测试未通过",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            _testFeishu.Enabled = true;
            _testFeishu.Text = original;
        }
    }

    private async Task PushToFeishuNowAsync()
    {
        WriteToOptions();

        var problem = FeishuBitablePublisher.Validate(Options.Feishu);
        if (problem is not null)
        {
            MessageBox.Show(
                "飞书配置不完整：" + Environment.NewLine + Environment.NewLine + problem,
                "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            "将把今天的记录写进飞书多维表格：" + Environment.NewLine + Environment.NewLine
            + $"app_token：{Options.Feishu.AppToken}" + Environment.NewLine
            + $"table_id：{Options.Feishu.TableId}" + Environment.NewLine + Environment.NewLine
            + "记录内容会上传到飞书，确认继续？",
            "写入飞书", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        _feishuHint.ForeColor = SystemColors.GrayText;
        _feishuHint.Text = "正在写入…";

        var writer = new FeishuWriter(Store, Log);
        var result = await writer.WriteTodayAsync(Options, CancellationToken.None);

        _feishuHint.ForeColor = result.Success ? Color.SeaGreen : Color.OrangeRed;
        _feishuHint.Text = Flatten(result.Message);

        MessageBox.Show(
            result.Message,
            result.Success ? "写入完成" : "写入失败",
            MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }
}
