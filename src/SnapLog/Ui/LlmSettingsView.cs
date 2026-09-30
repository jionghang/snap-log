using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;
using SnapLog.Summarization;

namespace SnapLog.Ui;

/// <summary>大模型配置页：启用、模型列表、发送内容、定时、提示词、工作项目。</summary>
internal sealed class LlmSettingsView : SettingsViewBase
{
    private CheckBox _llmEnabled = null!;
    private ListBox _providerList = null!;
    private ComboBox _payloadMode = null!;
    private ComboBox _imageDetail = null!;
    private NumericUpDown _imageSampleSeconds = null!;
    private NumericUpDown _maxImages = null!;
    private NumericUpDown _retryCount = null!;
    private NumericUpDown _retryDelaySeconds = null!;
    private CheckBox _summarySchedule = null!;
    private TextBox _summaryScheduleTime = null!;
    private Button _editPrompt = null!;
    private Label _promptState = null!;
    private ListBox _projectList = null!;
    private Button _openHistory = null!;
    private Label _payloadHint = null!;

    public LlmSettingsView(SettingsContext context)
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

        var grid = NewSection("大模型总结");

        var enableRow = NewRow();
        _llmEnabled = new CheckBox
        {
            Text = "启用（关闭时不发起任何网络请求）",
            AutoSize = true,
            Margin = new Padding(3, 6, 0, 0),
        };
        _llmEnabled.CheckedChanged += (_, _) => UpdateEnabledState();
        enableRow.Controls.Add(_llmEnabled);
        AddRow(grid, "总结", enableRow);

        // ---- 模型列表 ----
        var listRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
        };
        listRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 440));
        listRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _providerList = new ListBox { Height = 104, Width = 440, IntegralHeight = false };
        _providerList.DoubleClick += (_, _) => EditSelectedProvider();
        listRow.Controls.Add(_providerList, 0, 0);

        var listButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(8, 0, 0, 0),
        };
        listButtons.Controls.Add(NewSmallButton("添加…", AddProvider));
        listButtons.Controls.Add(NewSmallButton("编辑…", EditSelectedProvider));
        listButtons.Controls.Add(NewSmallButton("删除", RemoveProvider));
        listButtons.Controls.Add(NewSmallButton("上移", () => MoveProvider(-1)));
        listButtons.Controls.Add(NewSmallButton("下移", () => MoveProvider(1)));
        listRow.Controls.Add(listButtons, 1, 0);

        AddRow(grid, "模型列表", listRow);
        AddRow(grid, string.Empty, NewHint("按列表顺序调用：前一个重试用尽仍失败时自动切换到下一个。双击可编辑。"));

        // ---- 发送内容 ----
        _payloadMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        _payloadMode.Items.AddRange(
        [
            new PayloadChoice(LlmPayloadMode.TextOnly, "仅发送识别文字（消耗最低）"),
            new PayloadChoice(LlmPayloadMode.TextAndImage, "文字与截图（效果最好，需视觉模型）"),
            new PayloadChoice(LlmPayloadMode.ImageOnly, "仅发送截图（不发送识别文字，消耗更高）"),
        ]);
        _payloadMode.SelectedIndexChanged += (_, _) =>
        {
            UpdateEnabledState();
            UpdatePayloadHint();
        };
        AddRow(grid, "发送内容", _payloadMode);

        var imageOptionsRow = NewRow();
        imageOptionsRow.Controls.Add(new Label { Text = "同窗口图片间隔", AutoSize = true, Margin = new Padding(0, 9, 4, 0) });
        _imageSampleSeconds = new NumericUpDown { Minimum = 0, Maximum = 86_400, Increment = 30, Width = 80 };
        imageOptionsRow.Controls.Add(_imageSampleSeconds);
        imageOptionsRow.Controls.Add(new Label { Text = "秒", AutoSize = true, Margin = new Padding(4, 9, 14, 0) });

        imageOptionsRow.Controls.Add(new Label { Text = "最多", AutoSize = true, Margin = new Padding(0, 9, 4, 0) });
        _maxImages = new NumericUpDown { Minimum = 0, Maximum = 50, Width = 60 };
        imageOptionsRow.Controls.Add(_maxImages);
        imageOptionsRow.Controls.Add(new Label { Text = "张", AutoSize = true, Margin = new Padding(4, 9, 14, 0) });

        _imageDetail = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        _imageDetail.Items.AddRange(
        [
            new DetailChoice(LlmImageDetail.Auto, "清晰度 自动"),
            new DetailChoice(LlmImageDetail.Low, "清晰度 低（节省）"),
            new DetailChoice(LlmImageDetail.High, "清晰度 高（消耗更高）"),
        ]);
        imageOptionsRow.Controls.Add(_imageDetail);
        AddRow(grid, "图片选项", imageOptionsRow);

        _payloadHint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 4, 0, 6),
        };
        AddRow(grid, string.Empty, _payloadHint);

        // ---- 重试 ----
        var retryRow = NewRow();
        retryRow.Controls.Add(new Label { Text = "每个模型重试", AutoSize = true, Margin = new Padding(0, 9, 4, 0) });
        _retryCount = new NumericUpDown { Minimum = 0, Maximum = 10, Width = 56 };
        retryRow.Controls.Add(_retryCount);
        retryRow.Controls.Add(new Label { Text = "次，起始间隔", AutoSize = true, Margin = new Padding(4, 9, 4, 0) });
        _retryDelaySeconds = new NumericUpDown { Minimum = 0, Maximum = 300, Width = 60 };
        retryRow.Controls.Add(_retryDelaySeconds);
        retryRow.Controls.Add(new Label { Text = "秒（指数退避）", AutoSize = true, Margin = new Padding(4, 9, 0, 0) });
        AddRow(grid, "失败重试", retryRow);

        // ---- 定时生成 ----
        var scheduleRow = NewRow();
        _summarySchedule = new CheckBox
        {
            Text = "每天定时生成",
            AutoSize = true,
            Margin = new Padding(3, 6, 12, 0),
        };
        _summarySchedule.CheckedChanged += (_, _) => UpdateEnabledState();
        scheduleRow.Controls.Add(_summarySchedule);

        scheduleRow.Controls.Add(new Label { Text = "时间", AutoSize = true, Margin = new Padding(0, 9, 4, 0) });
        _summaryScheduleTime = new TextBox { Width = 70, PlaceholderText = "HH:mm" };
        scheduleRow.Controls.Add(_summaryScheduleTime);
        scheduleRow.Controls.Add(new Label { Text = "(HH:mm)", AutoSize = true, Margin = new Padding(4, 9, 0, 0) });
        AddRow(grid, "定时生成", scheduleRow);

        // ---- 系统提示词 ----
        var promptRow = NewRow();
        _editPrompt = new Button { Text = "编辑提示词…", Width = 120, Height = 28, Margin = new Padding(0, 3, 8, 0) };
        _editPrompt.Click += (_, _) => EditSystemPrompt();
        promptRow.Controls.Add(_editPrompt);

        _promptState = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 9, 0, 0) };
        promptRow.Controls.Add(_promptState);
        AddRow(grid, "系统提示词", promptRow);

        // ---- 工作项目 ----
        var projectRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
        };
        projectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 440));
        projectRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _projectList = new ListBox { Height = 104, Width = 440, IntegralHeight = false };
        _projectList.DoubleClick += (_, _) => EditSelectedProject();
        projectRow.Controls.Add(_projectList, 0, 0);

        var projectButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(8, 0, 0, 0),
        };
        projectButtons.Controls.Add(NewSmallButton("添加…", AddProject));
        projectButtons.Controls.Add(NewSmallButton("编辑…", EditSelectedProject));
        projectButtons.Controls.Add(NewSmallButton("删除", RemoveProject));
        projectButtons.Controls.Add(NewSmallButton("上移", () => MoveProject(-1)));
        projectButtons.Controls.Add(NewSmallButton("下移", () => MoveProject(1)));
        projectRow.Controls.Add(projectButtons, 1, 0);

        AddRow(grid, "工作项目", projectRow);
        AddRow(grid, string.Empty, NewHint(
            "填写自己的工作项目（项目名与说明）。生成总结时会随提示词发送，模型据此将活动按项目归类。双击可编辑。"));

        // ---- 总结历史 ----
        var historyRow = NewRow();
        _openHistory = new Button { Text = "查看总结历史…", Width = 130, Height = 28, Margin = new Padding(0, 3, 0, 0) };
        _openHistory.Click += (_, _) => OpenHistory();
        historyRow.Controls.Add(_openHistory);
        historyRow.Controls.Add(new Label
        {
            Text = "成功与失败均会记录，便于事后排查",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(8, 9, 0, 0),
        });
        AddRow(grid, "总结历史", historyRow);

        root.Controls.Add(grid);
        return root;
    }

    protected override void LoadFromOptions()
    {
        var summary = Options.Summarization;

        _llmEnabled.Checked = summary.Enabled;
        SelectByKind(_payloadMode, summary.PayloadMode);
        SelectByKind(_imageDetail, summary.ImageDetail);
        _imageSampleSeconds.Value = Math.Clamp(summary.ImageSampleSeconds, 0, (int)_imageSampleSeconds.Maximum);
        _maxImages.Value = Math.Clamp(summary.MaxImages, 0, (int)_maxImages.Maximum);
        _retryCount.Value = Math.Clamp(summary.RetryCount, 0, 10);
        _retryDelaySeconds.Value = Math.Clamp(summary.RetryDelaySeconds, 0, 300);
        _summarySchedule.Checked = summary.ScheduleEnabled;
        _summaryScheduleTime.Text = summary.ScheduleTimeOfDay;

        RefreshProviderList();
        RefreshProjectList();
        UpdatePromptState();
        UpdateEnabledState();
        UpdatePayloadHint();
    }

    protected override void WriteToOptions()
    {
        var summary = Options.Summarization;

        summary.Enabled = _llmEnabled.Checked;
        summary.PayloadMode = SelectedKind(_payloadMode, LlmPayloadMode.TextOnly);
        summary.ImageDetail = SelectedKind(_imageDetail, LlmImageDetail.Auto);
        summary.ImageSampleSeconds = (int)_imageSampleSeconds.Value;
        summary.MaxImages = (int)_maxImages.Value;
        summary.RetryCount = (int)_retryCount.Value;
        summary.RetryDelaySeconds = (int)_retryDelaySeconds.Value;
        summary.ScheduleEnabled = _summarySchedule.Checked;
        summary.ScheduleTimeOfDay = _summaryScheduleTime.Text.Trim();
    }

    private void UpdateEnabledState()
    {
        var images = SelectedKind(_payloadMode, LlmPayloadMode.TextOnly) != LlmPayloadMode.TextOnly;
        _imageSampleSeconds.Enabled = images;
        _maxImages.Enabled = images;
        _imageDetail.Enabled = images;

        _providerList.Enabled = _llmEnabled.Checked;
        _summaryScheduleTime.Enabled = _summarySchedule.Checked;
    }

    private void UpdatePayloadHint()
    {
        var mode = SelectedKind(_payloadMode, LlmPayloadMode.TextOnly);

        _payloadHint.Text = mode switch
        {
            LlmPayloadMode.ImageOnly =>
                "仅发送截图，由模型直接读图，需要视觉模型（如 gpt-4o、qwen-vl-max）。"
                + "截图原图会上传，画面中的全部内容都会发送出去。",
            LlmPayloadMode.TextAndImage =>
                "文字与截图一并发送，效果最好、消耗最高。同一窗口的截图按上方间隔采样，"
                + "避免重复发送长时间未变化的画面。",
            _ =>
                "仅发送识别文字，不上传截图，消耗与隐私暴露面都最小。"
                + "识别文字仍可能包含工作内容，请注意排除列表之外的窗口。",
        };
    }

    // ---------------------------------------------------------------- 模型列表

    private void RefreshProviderList()
    {
        var selected = _providerList.SelectedIndex;

        _providerList.Items.Clear();
        foreach (var provider in Options.Summarization.Providers)
        {
            _providerList.Items.Add(provider);
        }

        if (_providerList.Items.Count > 0)
        {
            _providerList.SelectedIndex = Math.Clamp(selected, 0, _providerList.Items.Count - 1);
        }
    }

    private void AddProvider()
    {
        var provider = new LlmProviderOptions { Name = $"模型 {Options.Summarization.Providers.Count + 1}" };
        using var dialog = new LlmProviderEditForm(provider, Options.Summarization.Providers);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        Options.Summarization.Providers.Add(dialog.Result);
        RefreshProviderList();
        _providerList.SelectedIndex = _providerList.Items.Count - 1;
    }

    private void EditSelectedProvider()
    {
        if (_providerList.SelectedItem is not LlmProviderOptions provider)
        {
            MessageBox.Show("先选中一个模型。", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new LlmProviderEditForm(provider, Options.Summarization.Providers);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        var index = _providerList.SelectedIndex;
        Options.Summarization.Providers[index] = dialog.Result;
        RefreshProviderList();
        _providerList.SelectedIndex = index;
    }

    private void RemoveProvider()
    {
        if (_providerList.SelectedItem is not LlmProviderOptions provider)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"删除模型配置“{provider.Name}”？", "SnapLog", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        var index = _providerList.SelectedIndex;
        Options.Summarization.Providers.RemoveAt(index);
        RefreshProviderList();
        _providerList.SelectedIndex = Math.Clamp(index - 1, 0, Math.Max(0, _providerList.Items.Count - 1));
    }

    private void MoveProvider(int delta)
    {
        var index = _providerList.SelectedIndex;
        var target = index + delta;
        var providers = Options.Summarization.Providers;

        if (index < 0 || target < 0 || target >= providers.Count)
        {
            return;
        }

        (providers[index], providers[target]) = (providers[target], providers[index]);
        RefreshProviderList();
        _providerList.SelectedIndex = target;
    }

    // ---------------------------------------------------------------- 工作项目

    private void RefreshProjectList()
    {
        var selected = _projectList.SelectedIndex;

        _projectList.Items.Clear();
        foreach (var project in Options.Summarization.WorkProjects)
        {
            _projectList.Items.Add(project);
        }

        if (_projectList.Items.Count > 0)
        {
            _projectList.SelectedIndex = Math.Clamp(selected, 0, _projectList.Items.Count - 1);
        }

        UpdatePromptState();
    }

    private void AddProject()
    {
        using var dialog = new WorkProjectEditForm(new WorkProjectOptions());
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        Options.Summarization.WorkProjects.Add(dialog.Result);
        RefreshProjectList();
        _projectList.SelectedIndex = _projectList.Items.Count - 1;
    }

    private void EditSelectedProject()
    {
        if (_projectList.SelectedItem is not WorkProjectOptions project)
        {
            MessageBox.Show("先选中一个工作项目。", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new WorkProjectEditForm(project);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        var index = _projectList.SelectedIndex;
        Options.Summarization.WorkProjects[index] = dialog.Result;
        RefreshProjectList();
        _projectList.SelectedIndex = index;
    }

    private void RemoveProject()
    {
        if (_projectList.SelectedItem is not WorkProjectOptions project)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"删除工作项目“{project.Name}”？", "SnapLog", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        var index = _projectList.SelectedIndex;
        Options.Summarization.WorkProjects.RemoveAt(index);
        RefreshProjectList();
        _projectList.SelectedIndex = Math.Clamp(index - 1, 0, Math.Max(0, _projectList.Items.Count - 1));
    }

    private void MoveProject(int delta)
    {
        var index = _projectList.SelectedIndex;
        var target = index + delta;
        var projects = Options.Summarization.WorkProjects;

        if (index < 0 || target < 0 || target >= projects.Count)
        {
            return;
        }

        (projects[index], projects[target]) = (projects[target], projects[index]);
        RefreshProjectList();
        _projectList.SelectedIndex = target;
    }

    // ---------------------------------------------------------------- 提示词与历史

    private void EditSystemPrompt()
    {
        using var dialog = new SystemPromptEditForm(Options.Summarization);
        dialog.ShowDialog(FindForm());
        UpdatePromptState();
    }

    private void UpdatePromptState()
    {
        var projects = Options.Summarization.WorkProjects.Count(p => !string.IsNullOrWhiteSpace(p.Name));
        var overridden = Options.Summarization.SystemPromptOverride.Trim().Length > 0;

        _promptState.Text = (overridden ? "已自定义（替换内置模板）" : "使用内置模板")
                            + (projects > 0 ? $"，另附 {projects} 条工作项目" : string.Empty);
    }

    private void OpenHistory()
    {
        using var dialog = new SummaryHistoryForm(Context);
        var parent = FindForm();
        if (parent is null)
        {
            dialog.ShowDialog();
        }
        else
        {
            dialog.ShowDialog(parent);
        }
    }

    private async Task GenerateSummaryNowAsync()
    {
        var result = await SummaryRunner.RunAsync(Options, "手动", CancellationToken.None);
        if (result.Success)
        {
            return;
        }

        MessageBox.Show(
            "生成失败：" + Environment.NewLine + Environment.NewLine + result.Message,
            "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
