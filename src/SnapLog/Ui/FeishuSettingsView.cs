using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 推送配置页：把大模型生成的总结写进飞书多维表格。
/// 一条总结写表里的一行，字段映射决定哪些总结字段落到哪些列。
/// </summary>
internal sealed class FeishuSettingsView : SettingsViewBase
{
    private CheckBox _feishuEnabled = null!;
    private TextBox _feishuTime = null!;
    private CheckBox _feishuAfterSummary = null!;
    private NumericUpDown _feishuLookback = null!;
    private TextBox _feishuAppId = null!;
    private TextBox _feishuSecret = null!;
    private TextBox _feishuAppToken = null!;
    private TextBox _feishuTableId = null!;
    private ListBox _fieldMappingList = null!;
    private Button _testFeishu = null!;
    private Button _readFields = null!;
    private Label _feishuHint = null!;
    private Label _pendingHint = null!;
    private Label _legacyHint = null!;

    /// <summary>上一次“读取表字段名”拿到的真实列名，供映射编辑窗体的下拉使用。</summary>
    private IReadOnlyList<FeishuBitablePublisher.FeishuTableField> _knownFields = [];

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

        var grid = NewSection("写入内容");

        var enableRow = NewRow();
        _feishuEnabled = new CheckBox
        {
            Text = "将大模型生成的总结写入飞书多维表格",
            AutoSize = true,
            Margin = new Padding(3, 6, 12, 0),
        };
        _feishuEnabled.CheckedChanged += (_, _) => UpdateEnabledState();
        enableRow.Controls.Add(_feishuEnabled);
        AddRow(grid, "写入内容", enableRow);

        AddRow(grid, string.Empty, NewHint(
            "每条总结写为一行，包含正文与元数据（时间、触发来源、模型、条数等）。"
            + "已写入的总结会打标记，重复执行不会产生重复行。"));

        var scheduleRow = NewRow();
        scheduleRow.Controls.Add(new Label { Text = "每天", AutoSize = true, Margin = new Padding(0, 9, 4, 0) });
        _feishuTime = new TextBox { Width = 70, PlaceholderText = "HH:mm" };
        scheduleRow.Controls.Add(_feishuTime);
        scheduleRow.Controls.Add(new Label { Text = "(HH:mm)", AutoSize = true, Margin = new Padding(4, 9, 4, 0) });

        _feishuAfterSummary = new CheckBox
        {
            Text = "生成总结成功后立即写入",
            AutoSize = true,
            Margin = new Padding(16, 6, 0, 0),
        };
        scheduleRow.Controls.Add(_feishuAfterSummary);
        AddRow(grid, "触发时机", scheduleRow);

        var scopeRow = NewRow();
        _feishuLookback = new ScrollSafeNumericUpDown
        {
            Width = 60,
            Minimum = 1,
            Maximum = 365,
            Value = 1,
        };
        scopeRow.Controls.Add(_feishuLookback);
        scopeRow.Controls.Add(new Label
        {
            MaximumSize = new Size(420, 0),
            Text = "天内生成的总结（调大可补入更早的总结）",
            AutoSize = true,
            Margin = new Padding(6, 9, 0, 0),
        });
        AddRow(grid, "写入范围", scopeRow);

        // ---- 应用凭证 ----
        var connection = NewSection("连接参数");
        var appRow = NewRow();
        _feishuAppId = new TextBox { Width = 240, PlaceholderText = "App ID（cli_…）" };
        appRow.Controls.Add(_feishuAppId);
        appRow.Controls.Add(new Label { Text = "App Secret", AutoSize = true, Margin = new Padding(12, 9, 4, 0) });
        _feishuSecret = new TextBox { Width = 200, UseSystemPasswordChar = true };
        appRow.Controls.Add(_feishuSecret);
        AddRow(connection, "应用凭证", appRow);

        AddRow(connection, string.Empty, NewHint(
            "App Secret 留空时读取环境变量 SNAPLOG_FEISHU_APP_SECRET（推荐）。应用需同时具备多维表格读写权限"
            + "（bitable:app）与本文档的可编辑协作权限，缺失时分别返回 99991672 与 91403。"));

        // ---- 数据表 ----
        var tableRow = NewRow();
        _feishuAppToken = new TextBox { Width = 250, PlaceholderText = "app_token（整张多维表格）" };
        tableRow.Controls.Add(_feishuAppToken);
        tableRow.Controls.Add(new Label { Text = "table_id", AutoSize = true, Margin = new Padding(12, 9, 4, 0) });
        _feishuTableId = new TextBox { Width = 150, PlaceholderText = "tbl…" };
        tableRow.Controls.Add(_feishuTableId);

        _readFields = new Button
        {
            Text = "读取表字段名",
            Width = 110,
            Height = 26,
            Margin = new Padding(12, 4, 0, 0),
        };
        _readFields.Click += async (_, _) => await ReadTableFieldsAsync();
        tableRow.Controls.Add(_readFields);
        AddRow(connection, "数据表", tableRow);

        // ---- 字段映射 ----
        var mappingRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
        };
        mappingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mappingRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _fieldMappingList = new ListBox { Height = 132, IntegralHeight = false, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        _fieldMappingList.DoubleClick += (_, _) => EditSelectedMapping();
        mappingRow.Controls.Add(_fieldMappingList, 0, 0);

        var mappingButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(8, 0, 0, 0),
        };
        mappingButtons.Controls.Add(NewSmallButton("新增…", AddMapping));
        mappingButtons.Controls.Add(NewSmallButton("编辑…", EditSelectedMapping));
        mappingButtons.Controls.Add(NewSmallButton("删除", RemoveSelectedMapping));
        mappingButtons.Controls.Add(NewSmallButton("恢复默认", ResetMappings));
        mappingRow.Controls.Add(mappingButtons, 1, 0);

        AddRow(connection, "字段映射", mappingRow);
        AddRow(connection, string.Empty, NewHint(
            "总结字段与飞书列名的对应关系，条数不限：无对应列的映射将“飞书字段名”留空即可跳过，不必逐条填写。"
            + "飞书按名称精确匹配（含空格与换行），名称不一致会报 1254045；点“测试连接”可预先核对。"));

        _legacyHint = NewHint(string.Empty);
        _legacyHint.Visible = false;
        AddRow(grid, string.Empty, _legacyHint);

        // ---- 操作 ----
        var actionsRow = NewRow();
        _testFeishu = new Button { Text = "测试连接", Width = 100, Height = 28, Margin = new Padding(0, 3, 8, 0) };
        _testFeishu.Click += async (_, _) => await TestFeishuAsync();
        actionsRow.Controls.Add(_testFeishu);

        var pushNow = new Button { Text = "立即写入…", Width = 100, Height = 28, Margin = new Padding(0, 3, 8, 0) };
        pushNow.Click += async (_, _) => await PushToFeishuNowAsync();
        actionsRow.Controls.Add(pushNow);

        _pendingHint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(4, 9, 0, 0) };
        actionsRow.Controls.Add(_pendingHint);

        _feishuHint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(12, 9, 0, 0) };
        actionsRow.Controls.Add(_feishuHint);
        var actions = NewSection("操作");
        AddRow(actions, "操作", actionsRow);

        root.Controls.Add(grid);
        root.Controls.Add(connection);
        root.Controls.Add(actions);
        return root;
    }

    protected override void LoadFromOptions()
    {
        var feishu = Options.Feishu;

        _feishuEnabled.Checked = feishu.Enabled;
        _feishuTime.Text = feishu.ScheduleTimeOfDay;
        _feishuAfterSummary.Checked = feishu.PushAfterSummary;
        _feishuLookback.Value = Math.Clamp(feishu.PushLookbackDays, 1, 365);
        _feishuAppId.Text = feishu.AppId;
        _feishuSecret.Text = feishu.AppSecret;
        _feishuAppToken.Text = feishu.AppToken;
        _feishuTableId.Text = feishu.TableId;

        RefreshFieldMappingList();
        UpdateEnabledState();
        UpdateFeishuHint();
        UpdateLegacyHint();
        _ = RefreshPendingHintAsync();
    }

    /// <summary>
    /// 旧配置里的映射指向的是抓取记录，加载时已被换成总结字段的默认映射。
    /// 这件事必须说出来——列名很可能对不上用户的表，静默换掉会让人一头雾水。
    /// </summary>
    private void UpdateLegacyHint()
    {
        if (!Options.Feishu.LegacyMappingsReplaced)
        {
            _legacyHint.Visible = false;
            return;
        }

        _legacyHint.ForeColor = Color.OrangeRed;
        _legacyHint.Visible = true;
        _legacyHint.Text =
            "原配置的字段映射基于抓取记录，已自动替换为总结字段的默认映射。"
            + Environment.NewLine
            + "请按表内实际列名核对后再保存。";
    }

    protected override void WriteToOptions()
    {
        var feishu = Options.Feishu;

        feishu.Enabled = _feishuEnabled.Checked;
        feishu.ScheduleTimeOfDay = _feishuTime.Text.Trim();
        feishu.PushAfterSummary = _feishuAfterSummary.Checked;
        feishu.PushLookbackDays = (int)_feishuLookback.Value;
        feishu.AppId = _feishuAppId.Text.Trim();
        feishu.AppSecret = _feishuSecret.Text.Trim();
        feishu.AppToken = _feishuAppToken.Text.Trim();
        feishu.TableId = _feishuTableId.Text.Trim();

        // 用户确认过这一页的映射了，迁移提示不用再挂在那儿。
        feishu.LegacyMappingsReplaced = false;
    }

    private void UpdateEnabledState()
    {
        _feishuTime.Enabled = _feishuEnabled.Checked;
        _feishuAfterSummary.Enabled = _feishuEnabled.Checked;
        _feishuLookback.Enabled = _feishuEnabled.Checked;
    }

    private void UpdateFeishuHint()
    {
        var variableName = FeishuBitablePublisher.SecretVariableName(Options.Feishu);
        var fromEnvironment = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName));

        _feishuHint.ForeColor = SystemColors.GrayText;
        _feishuHint.Text = fromEnvironment
            ? $"已从环境变量 {variableName} 读取"
            : _feishuSecret.Text.Length > 0
                ? "使用此处填写的明文配置"
                : $"尚未配置（可设置环境变量 {variableName}）";
    }

    /// <summary>查一下库里有多少条总结等着写，让用户点按钮之前心里有数。</summary>
    private async Task RefreshPendingHintAsync()
    {
        try
        {
            var count = await new FeishuWriter(Store, Log)
                .CountPendingAsync(Options, CancellationToken.None)
                .ConfigureAwait(true);

            _pendingHint.Text = $"待写入 {count} 条";
        }
        catch (Exception ex)
        {
            _pendingHint.Text = string.Empty;
            Log.Warn($"查询待写入总结条数失败：{ex.Message}");
        }
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

    private void AddMapping()
    {
        using var dialog = new FeishuFieldMappingEditForm(new FeishuFieldMapping(), isNew: true, _knownFields);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        Options.Feishu.FieldMappings.Add(dialog.Result);
        MarkDirty();
        RefreshFieldMappingList();
        _fieldMappingList.SelectedIndex = _fieldMappingList.Items.Count - 1;
    }

    private void EditSelectedMapping()
    {
        if (_fieldMappingList.SelectedItem is not FeishuFieldMapping mapping)
        {
            MessageBox.Show("请先选择一条映射。", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new FeishuFieldMappingEditForm(mapping, isNew: false, _knownFields);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        var index = _fieldMappingList.SelectedIndex;
        Options.Feishu.FieldMappings[index] = dialog.Result;
        MarkDirty();
        RefreshFieldMappingList();
        _fieldMappingList.SelectedIndex = index;
    }

    private void RemoveSelectedMapping()
    {
        var index = _fieldMappingList.SelectedIndex;
        if (index < 0 || index >= Options.Feishu.FieldMappings.Count)
        {
            MessageBox.Show("请先选择一条映射。", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var mapping = Options.Feishu.FieldMappings[index];
        var confirm = MessageBox.Show(
            $"确认删除这条映射？{Environment.NewLine}{Environment.NewLine}{mapping}{Environment.NewLine}{Environment.NewLine}"
            + "删除仅表示不再写入该列，飞书表中的现有数据不受影响。",
            "SnapLog", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        Options.Feishu.FieldMappings.RemoveAt(index);
        MarkDirty();
        RefreshFieldMappingList();

        if (_fieldMappingList.Items.Count > 0)
        {
            _fieldMappingList.SelectedIndex = Math.Clamp(index, 0, _fieldMappingList.Items.Count - 1);
        }
    }

    private void ResetMappings()
    {
        var confirm = MessageBox.Show(
            "将字段映射恢复为默认的 5 条？" + Environment.NewLine + Environment.NewLine
            + "飞书列名将重置为“时间 / 触发来源 / 模型 / 记录条数 / 总结”，已添加的映射会被覆盖。",
            "SnapLog", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        Options.Feishu.FieldMappings = FeishuFieldMapping.CreateDefault();
        MarkDirty();
        RefreshFieldMappingList();
    }

    // ---------------------------------------------------------------- 操作

    private async Task TestFeishuAsync()
    {
        WriteToOptions();

        var original = _testFeishu.Text;
        _testFeishu.Enabled = false;
        _testFeishu.Text = "测试中…";
        _pendingHint.ForeColor = SystemColors.GrayText;
        _pendingHint.Text = "正在换取令牌并核对字段…";

        try
        {
            var publisher = new FeishuBitablePublisher(Options.Feishu, Log);
            var result = await publisher.TestAsync(CancellationToken.None);

            _pendingHint.ForeColor = result.Success ? Color.SeaGreen : Color.OrangeRed;
            _pendingHint.Text = Flatten(result.Message);

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

    /// <summary>
    /// 只读一次表里的列名，填进字段映射编辑窗体的下拉。
    /// 这一步的意义是不让用户手打列名——差一个空格就是 1254045，还很难自查。
    /// </summary>
    private async Task ReadTableFieldsAsync()
    {
        WriteToOptions();

        var original = _readFields.Text;
        _readFields.Enabled = false;
        _readFields.Text = "读取中…";
        _pendingHint.ForeColor = SystemColors.GrayText;
        _pendingHint.Text = "正在读取数据表字段名…";

        try
        {
            var (fields, error) = await new FeishuBitablePublisher(Options.Feishu, Log)
                .FetchFieldsAsync(CancellationToken.None);

            if (fields is null)
            {
                _pendingHint.ForeColor = Color.OrangeRed;
                _pendingHint.Text = Flatten(error ?? "读取字段失败");
                MessageBox.Show(
                    error ?? "读取字段失败", "读取表字段名", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _knownFields = fields;

            // 字段清单只列列名：写成"列名（类型）"时，用户复制粘贴容易把类型说明一起带进映射，
            // 而飞书按名称精确匹配，多这几个字就会报字段名不匹配。类型在映射编辑窗体的提示里单独说明。
            var names = string.Join("、", fields.Select(f => f.Name));
            _pendingHint.ForeColor = Color.SeaGreen;
            _pendingHint.Text = $"已读取 {fields.Count} 个字段";

            var mapped = Options.Feishu.FieldMappings
                .Where(m => !string.IsNullOrWhiteSpace(m.FeishuField))
                .Select(m => m.FeishuField)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var missing = mapped.Where(name => fields.All(f => !string.Equals(f.Name, name, StringComparison.Ordinal))).ToList();

            var message = $"表内共 {fields.Count} 个字段：{Environment.NewLine}{names}"
                          + Environment.NewLine + Environment.NewLine
                          + (missing.Count == 0
                              ? "当前映射的列名均可匹配。"
                              : $"以下映射在表内不存在对应列：{string.Join("、", missing)}"
                                + Environment.NewLine
                                + "编辑映射时可直接从下拉中选择表内列名。");

            MessageBox.Show(
                message,
                missing.Count == 0 ? "读取表字段名成功" : "存在无法匹配的映射",
                MessageBoxButtons.OK,
                missing.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            _readFields.Enabled = true;
            _readFields.Text = original;
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

        int pending;
        try
        {
            pending = await new FeishuWriter(Store, Log).CountPendingAsync(Options, CancellationToken.None);
        }
        catch (Exception ex)
        {
            pending = 0;
            Log.Warn($"查询待写入总结条数失败：{ex.Message}");
        }

        if (pending == 0)
        {
            MessageBox.Show(
                "没有待写入的总结。" + Environment.NewLine + Environment.NewLine
                + $"仅写入 {FeishuWriter.GetEarliestRunTime(Options.Feishu):yyyy-MM-dd} 之后生成且尚未写入的总结。"
                + "请先生成总结，或将“写入范围”调大。",
                "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"将向飞书多维表格写入 {pending} 条总结：" + Environment.NewLine + Environment.NewLine
            + $"app_token：{Options.Feishu.AppToken}" + Environment.NewLine
            + $"table_id：{Options.Feishu.TableId}" + Environment.NewLine + Environment.NewLine
            + "总结正文可能包含屏幕上识别出的内容，将上传至飞书。确认继续？",
            "写入飞书", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        _pendingHint.ForeColor = SystemColors.GrayText;
        _pendingHint.Text = "正在写入…";

        var result = await new FeishuWriter(Store, Log).WritePendingAsync(Options, CancellationToken.None);

        _pendingHint.ForeColor = result.Success ? Color.SeaGreen : Color.OrangeRed;
        _pendingHint.Text = Flatten(result.Message);

        MessageBox.Show(
            result.Message,
            result.Success ? "写入完成" : "写入失败",
            MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

        await RefreshPendingHintAsync();
    }
}
