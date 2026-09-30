using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Summarization;

namespace SnapLog.Ui;

/// <summary>编辑系统提示词。能一键载入内置模板，改坏了也能回来。</summary>
internal sealed class SystemPromptEditForm : Form
{
    private readonly SummarizationOptions _options;
    private readonly TextBox _editor = new();
    private readonly Label _hint = new();

    public SystemPromptEditForm(SummarizationOptions options)
    {
        _options = options;

        Text = "系统提示词";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 560);
        Size = new Size(880, 660);
        Icon = IconFactory.AppIcon;
        ShowInTaskbar = false;

        BuildLayout();
    }

    private void BuildLayout()
    {
        _editor.Multiline = true;
        _editor.ScrollBars = ScrollBars.Both;
        _editor.WordWrap = true;
        _editor.Dock = DockStyle.Fill;
        _editor.Font = new Font("Consolas", 9.5f);
        _editor.Text = _options.SystemPromptOverride;

        _hint.Dock = DockStyle.Top;
        _hint.AutoSize = true;
        _hint.Padding = new Padding(10, 8, 10, 6);
        _hint.ForeColor = SystemColors.GrayText;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(10, 8, 10, 10),
        };

        var save = new Button { Text = "保存", Width = 88, Height = 32 };
        save.Click += (_, _) => Confirm();

        var loadTemplate = new Button { Text = "填入内置模板", Width = 120, Height = 32 };
        loadTemplate.Click += (_, _) => LoadBuiltInTemplate();

        var restore = new Button { Text = "清空（改用内置模板）", Width = 150, Height = 32 };
        restore.Click += (_, _) =>
        {
            _editor.Clear();
            UpdateHint();
        };

        var cancel = new Button { Text = "取消", Width = 88, Height = 32, DialogResult = DialogResult.Cancel };

        buttons.Controls.Add(save);
        buttons.Controls.Add(loadTemplate);
        buttons.Controls.Add(restore);
        buttons.Controls.Add(cancel);

        Controls.Add(_editor);
        Controls.Add(buttons);
        Controls.Add(_hint);

        AcceptButton = null;
        CancelButton = cancel;

        _editor.TextChanged += (_, _) => UpdateHint();
        UpdateHint();
    }

    private void UpdateHint()
    {
        var overridden = _editor.Text.Trim().Length > 0;
        var projects = _options.WorkProjects.Count(p => !string.IsNullOrWhiteSpace(p.Name));

        _hint.Text = (overridden
                ? "当前使用下方提示词，完全替换内置模板。"
                : "当前留空，使用内置模板。")
            + (projects > 0
                ? $"　另自动附上 {projects} 条工作项目清单，该部分为结构化数据，不受此处内容影响。"
                : string.Empty);
    }

    private void LoadBuiltInTemplate()
    {
        // 载入的是"当前配置下的内置模板"，所以措辞会跟着传参模式和输出语言变，
        // 用户改的时候看到的就是程序实际会用的那份。
        _editor.Text = Prompts.BuildDefaultTemplate(_options);
    }

    private void Confirm()
    {
        _options.SystemPromptOverride = _editor.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}
