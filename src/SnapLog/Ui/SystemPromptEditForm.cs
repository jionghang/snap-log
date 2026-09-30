using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Summarization;

namespace SnapLog.Ui;

/// <summary>编辑系统提示词。改坏了可以一键恢复默认模板。</summary>
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
        _editor.Text = ToEditorText(_options.SystemPromptOverride);

        _hint.Dock = DockStyle.Top;
        _hint.AutoSize = true;
        _hint.MaximumSize = new Size(820, 0);
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

        var restore = new Button { Text = "恢复默认模板", Width = 120, Height = 32 };
        restore.Click += (_, _) => LoadDefaultTemplate();

        var cancel = new Button { Text = "取消", Width = 88, Height = 32, DialogResult = DialogResult.Cancel };

        buttons.Controls.Add(save);
        buttons.Controls.Add(restore);
        buttons.Controls.Add(cancel);

        Controls.Add(_editor);
        Controls.Add(buttons);
        Controls.Add(_hint);

        AcceptButton = null;
        CancelButton = cancel;

        _editor.TextChanged += (_, _) => UpdateHint();
        UpdateHint();

        // TextBox 在"先设文本、后第一次获得焦点"时会把全文选中（WinForms 的老行为），
        // 打开就把光标放到开头，免得整段蓝底看着像被误选。
        Shown += (_, _) => _editor.Select(0, 0);
    }

    private void UpdateHint()
    {
        var projects = _options.WorkProjects.Count(p => !string.IsNullOrWhiteSpace(p.Name));

        _hint.Text = "下面这段就是实际发给模型的提示词。"
                     + "发送内容说明、输出语言、汇报类文档的处理规则、附加要求与工作项目清单在发送时自动追加，不受此处内容影响。"
                     + (projects > 0 ? $"（当前会附上 {projects} 条工作项目）" : string.Empty);
    }

    private void LoadDefaultTemplate()
    {
        _editor.Text = ToEditorText(Prompts.DefaultTemplate);
        UpdateHint();
    }

    /// <summary>
    /// 多行 TextBox 底下的 EDIT 控件只认 CRLF：喂给它纯 LF 的文本会显示成一整行。
    /// 内置模板来自源码里的原始字符串字面量（文件是 LF 行尾），所以这里统一转成 CRLF 再显示。
    /// </summary>
    private static string ToEditorText(string text) =>
        text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private void Confirm()
    {
        _options.SystemPromptOverride = _editor.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}
