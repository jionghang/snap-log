using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Summarization;

namespace SnapLog.Ui;

/// <summary>
/// 编辑“模型列表”里的一个条目。
/// 顺便校验接口地址是否合法、密钥是否拿得到——这两个是最常见的配错点，
/// 让用户在这里就发现，而不是等到生成总结时才看到一句失败。
/// </summary>
internal sealed class LlmProviderEditForm : Form
{
    private readonly LlmProviderOptions _working;
    private readonly LlmProviderOptions _original;
    private readonly IReadOnlyList<LlmProviderOptions> _siblings;

    private readonly TextBox _name = new();
    private readonly CheckBox _enabled = new();
    private readonly TextBox _endpoint = new();
    private readonly TextBox _model = new();
    private readonly TextBox _apiKey = new();
    private readonly CheckBox _showApiKey = new();
    private readonly TextBox _keyVariable = new();
    private readonly Label _hint = new();

    public LlmProviderEditForm(LlmProviderOptions provider, IReadOnlyList<LlmProviderOptions> siblings)
    {
        _working = provider.Clone();
        // 保留原引用：重复检测要靠它把"正在编辑的这一条"排除掉，
        // 否则克隆体和列表里的自己比对，永远会被判成重复。
        _original = provider;
        _siblings = siblings;

        Text = "模型配置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = IconFactory.AppIcon;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(600, 320);

        BuildLayout();
    }

    /// <summary>编辑结果。只有点“确定”时有效。</summary>
    public LlmProviderOptions Result => _working;

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
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430));

        var row = 0;
        void AddRow(string caption, Control control)
        {
            grid.RowCount = row + 1;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            grid.Controls.Add(new Label
            {
                Text = caption + "：",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 8, 6, 0),
            }, 0, row);

            control.Margin = new Padding(0, 3, 0, 3);
            grid.Controls.Add(control, 1, row);
            row++;
        }

        _name.Dock = DockStyle.Fill;
        AddRow("名称", _name);

        _enabled.Text = "启用这个模型";
        _enabled.AutoSize = true;
        _enabled.Margin = new Padding(3, 6, 0, 0);
        AddRow("状态", _enabled);

        _endpoint.Dock = DockStyle.Fill;
        AddRow("接口地址", _endpoint);

        _model.Dock = DockStyle.Fill;
        AddRow("模型名称", _model);

        var keyRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        _apiKey.Dock = DockStyle.Fill;
        _apiKey.UseSystemPasswordChar = true;
        keyRow.Controls.Add(_apiKey, 0, 0);

        _showApiKey.Text = "显示";
        _showApiKey.AutoSize = true;
        _showApiKey.Margin = new Padding(6, 5, 0, 0);
        _showApiKey.CheckedChanged += (_, _) => _apiKey.UseSystemPasswordChar = !_showApiKey.Checked;
        keyRow.Controls.Add(_showApiKey, 1, 0);
        AddRow("API Key", keyRow);

        _keyVariable.Dock = DockStyle.Fill;
        AddRow("密钥环境变量", _keyVariable);

        _hint.AutoSize = true;
        _hint.MaximumSize = new Size(430, 0);
        _hint.ForeColor = SystemColors.GrayText;
        _hint.Margin = new Padding(3, 6, 0, 6);
        AddRow(string.Empty, _hint);

        // 边填边给反馈：接口地址是否合法、密钥从哪来，都不用等到点确定才知道。
        _name.TextChanged += (_, _) => UpdateHint();
        _endpoint.TextChanged += (_, _) => UpdateHint();
        _model.TextChanged += (_, _) => UpdateHint();
        _apiKey.TextChanged += (_, _) => UpdateHint();
        _keyVariable.TextChanged += (_, _) => UpdateHint();

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

        // 填完就能立刻验一下：真实发一次最小的请求，比事后看"生成失败"好排查得多。
        var test = new Button { Text = "测试连接", Width = 100, Height = 30 };
        test.Click += async (_, _) => await TestConnectionAsync(test);

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(test);

        var root = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, Dock = DockStyle.Top };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(grid, 0, 0);
        root.Controls.Add(buttons, 0, 1);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        LoadFromProvider();
        UpdateHint();
    }

    private void LoadFromProvider()
    {
        _name.Text = _working.Name;
        _enabled.Checked = _working.Enabled;
        _endpoint.Text = _working.Endpoint;
        _model.Text = _working.Model;
        _apiKey.Text = _working.ApiKey;
        _keyVariable.Text = _working.ApiKeyEnvironmentVariable;
    }

    private void Confirm()
    {
        _working.Name = string.IsNullOrWhiteSpace(_name.Text) ? "未命名模型" : _name.Text.Trim();
        _working.Enabled = _enabled.Checked;
        _working.Endpoint = _endpoint.Text.Trim();
        _working.Model = _model.Text.Trim();
        _working.ApiKey = _apiKey.Text.Trim();
        _working.ApiKeyEnvironmentVariable = _keyVariable.Text.Trim();

        if (_working.Model.Length == 0)
        {
            Warn("模型名称不能为空。");
            return;
        }

        try
        {
            OpenAiCompatibleSummarizer.NormalizeEndpoint(_working.Endpoint);
        }
        catch (ArgumentException ex)
        {
            Warn(ex.Message);
            return;
        }

        if (_working.Enabled && ResolveKey() is null)
        {
            var variableName = _working.ApiKeyEnvironmentVariable.Length == 0
                ? "SNAPLOG_OPENAI_API_KEY"
                : _working.ApiKeyEnvironmentVariable;

            Warn($"未获取到密钥：请填写 API Key，或先设置环境变量 {variableName}。\n\n"
                 + "（留空也可保存，但生成总结时会自动跳过该模型）");
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>实时提示：这个配置现在能不能用、密钥从哪来。</summary>
    private void UpdateHint()
    {
        var fromEnvironment = false;
        var variableName = _keyVariable.Text.Trim();

        if (variableName.Length > 0 && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName)))
        {
            fromEnvironment = true;
        }

        var endpointText = _endpoint.Text.Trim();
        string endpointNote;
        try
        {
            endpointNote = $"实际请求：{OpenAiCompatibleSummarizer.NormalizeEndpoint(endpointText)}";
        }
        catch (ArgumentException ex)
        {
            endpointNote = ex.Message;
        }

        var keyNote = fromEnvironment
            ? $"已从环境变量 {variableName} 读取，其优先级高于此处填写的明文。"
            : _apiKey.Text.Length > 0
                ? "使用此处填写的明文配置（将写入配置文件）。"
                : string.IsNullOrWhiteSpace(variableName)
                    ? "缺少密钥来源：请填写 API Key 或设置环境变量。"
                    : $"缺少密钥来源：请填写 API Key，或先设置环境变量 {variableName}。";

        var duplicate = _siblings.Any(other =>
            !ReferenceEquals(other, _original)
            && other.Enabled
            && string.Equals(other.Model, _model.Text.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(other.Endpoint.TrimEnd('/'), endpointText.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        var hint = new List<string> { endpointNote, keyNote, "使用图片时模型需具备视觉能力（如 gpt-4o、qwen-vl-max）。" };

        if (duplicate)
        {
            hint.Add("列表中已存在相同的接口与模型，重复配置无意义。");
        }

        if (BindingContext is not null)
        {
            Text = _name.Text.Trim().Length > 0 ? $"模型配置 · {_name.Text.Trim()}" : "模型配置";
        }

        _hint.Text = string.Join("\n", hint);
    }

    /// <summary>
    /// 用当前填的内容真实调一次模型，验证地址、密钥、模型名三件事是否都对得上。
    /// 会发一次很小的请求（几十 token），这是唯一能确认配置可用的办法。
    /// </summary>
    private async Task TestConnectionAsync(Button trigger)
    {
        var candidate = new LlmProviderOptions
        {
            Name = _name.Text.Trim(),
            Enabled = true,
            Endpoint = _endpoint.Text.Trim(),
            Model = _model.Text.Trim(),
            ApiKey = _apiKey.Text.Trim(),
            ApiKeyEnvironmentVariable = _keyVariable.Text.Trim(),
        };

        if (candidate.Model.Length == 0)
        {
            Warn("请先填模型名称。");
            return;
        }

        var original = trigger.Text;
        trigger.Enabled = false;
        trigger.Text = "测试中…";

        try
        {
            var options = new SummarizationOptions
            {
                Providers = [candidate],
                Enabled = true,
                ConsentGranted = true,
                RetryCount = 0,
                RetryDelaySeconds = 1,
                RequestTimeoutSeconds = 30,
                PayloadMode = LlmPayloadMode.TextOnly,
            };

            if (!OpenAiCompatibleSummarizer.TryCreate(options, FileLogger.Null, out var summarizer, out var createError))
            {
                Warn("配置不完整：" + Environment.NewLine + Environment.NewLine + createError);
                return;
            }

            var request = new SummaryRequest(
                "你是一个连通性测试。收到请求后只回复两个字：可用。",
                "连通性测试，请回复“可用”。",
                [],
                LlmImageDetail.Auto);

            var completion = await summarizer!.SummarizeAsync(request, CancellationToken.None);

            _hint.Text = $"测试通过：{completion.ProviderDescription}" + Environment.NewLine
                         + $"回复内容：{Trim(completion.Text)}" + Environment.NewLine
                         + "（已实际发送一次最小请求）";
            _hint.ForeColor = Color.SeaGreen;

            MessageBox.Show(
                "连接成功。" + Environment.NewLine + Environment.NewLine
                + $"模型：{completion.ProviderDescription}" + Environment.NewLine
                + $"回复：{Trim(completion.Text)}",
                "模型配置", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (SummaryFailedException ex)
        {
            _hint.Text = "测试失败：" + Environment.NewLine + ex.DescribeAttempts();
            _hint.ForeColor = Color.OrangeRed;
            Warn("测试失败：" + Environment.NewLine + Environment.NewLine + ex.DescribeAttempts());
        }
        catch (Exception ex)
        {
            _hint.Text = $"测试失败：{ex.GetType().Name}: {ex.Message}";
            _hint.ForeColor = Color.OrangeRed;
            Warn("测试失败：" + Environment.NewLine + Environment.NewLine
                 + $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            trigger.Enabled = true;
            trigger.Text = original;
        }
    }

    private static string Trim(string value)
    {
        var flat = value
            .Replace((char)13, ' ')
            .Replace((char)10, ' ')
            .Replace((char)9, ' ')
            .Trim();
        return flat.Length <= 80 ? flat : flat[..80] + "…";
    }

    private void Warn(string message) =>
        MessageBox.Show(message, "模型配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static string? ResolveKey(string? configured, string? variableName)
    {
        if (!string.IsNullOrWhiteSpace(variableName))
        {
            var value = Environment.GetEnvironmentVariable(variableName.Trim());
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }

    private string? ResolveKey() => ResolveKey(_apiKey.Text, _keyVariable.Text);
}
