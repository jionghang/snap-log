using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 各设置页的公共底座：分组卡片、行布局、底部的保存/放弃按钮。
///
/// 数据模型：直接编辑传入的 <see cref="AppOptions"/> 实例。底部“保存设置”写盘并生效，
/// “放弃修改”从磁盘重新读回来丢弃改动——这两个行为对所有设置页是一致的，
/// 所以放在基类里，每个子类只管自己的字段。
/// </summary>
internal abstract class SettingsViewBase : UserControl
{
    protected readonly AppOptions Options;
    protected readonly AppPaths Paths;
    protected readonly FileLogger Log;
    protected readonly IActivityRepository Store;
    protected readonly SummaryRunner SummaryRunner;

    private readonly Func<bool> _onSave;

    /// <summary>完整上下文，供子类把它传给子窗口（比如总结历史）。</summary>
    protected SettingsContext Context { get; }

    protected SettingsViewBase(SettingsContext context)
    {
        Context = context;
        Options = context.Options;
        Paths = context.Paths;
        Log = context.Log;
        Store = context.Store;
        SummaryRunner = context.SummaryRunner;
        _onSave = context.OnSave;

        AutoScroll = true;
        Dock = DockStyle.Fill;
        BackColor = SystemColors.Control;

        SuspendLayout();
        try
        {
            var content = BuildContent();
            content.Dock = DockStyle.Top;
            if (content is TableLayoutPanel tlp)
            {
                tlp.AutoSize = true;
                tlp.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            }
            else if (content is FlowLayoutPanel flp)
            {
                flp.AutoSize = true;
                flp.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            }

            var footer = BuildFooter();
            footer.Dock = DockStyle.Bottom;

            Controls.Add(content);
            Controls.Add(footer);
        }
        finally
        {
            ResumeLayout(true);
        }

        LoadFromOptions();
    }

    /// <summary>构建这一页的内容。</summary>
    protected abstract Control BuildContent();

    /// <summary>把配置读出来填到控件上。</summary>
    protected abstract void LoadFromOptions();

    /// <summary>把控件上的值写回配置对象（不写盘）。</summary>
    protected abstract void WriteToOptions();

    /// <summary>外部改过配置后要刷新显示，调用这个。</summary>
    public void Reload()
    {
        LoadFromOptions();
    }

    // ---------------------------------------------------------------- 页脚

    private Control BuildFooter()
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(4, 8, 4, 6),
        };

        var save = new Button { Text = "保存设置", Width = 110, Height = 32 };
        save.Click += (_, _) => Save();

        var discard = new Button { Text = "放弃修改", Width = 100, Height = 32 };
        discard.Click += (_, _) => DiscardChanges();

        panel.Controls.Add(save);
        panel.Controls.Add(discard);
        return panel;
    }

    private void Save()
    {
        WriteToOptions();

        // 界面自检里 --ui-smoke 会用 _ => true 的回调，这里不真写盘。
        if (_onSave())
        {
            LoadFromOptions();
        }
    }

    private void DiscardChanges()
    {
        var confirm = MessageBox.Show(
            "放弃本页改动并从配置文件重新读取？",
            "SnapLog",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        var load = OptionsStore.Load(null);
        OptionsStore.CopyInto(Options, load.Options);
        LoadFromOptions();
    }

    // ---------------------------------------------------------------- 共享布局

    /// <summary>一个带标题的分组卡片。</summary>
    protected static TableLayoutPanel NewSection(string title)
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(0, 0, 0, 6),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var header = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6),
        };
        grid.Controls.Add(header, 0, 0);
        grid.SetColumnSpan(header, 2);
        grid.RowCount = 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 用 Tag 记下一个空行号，避免和标题行冲突。
        grid.Tag = 1;
        return grid;
    }

    protected static void AddRow(TableLayoutPanel grid, string caption, Control control)
    {
        var row = (int)(grid.Tag ?? 1);
        grid.Tag = row + 1;
        grid.RowCount = row + 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = caption.Length == 0 ? string.Empty : caption + "：",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(4, 8, 6, 0),
        };

        control.Margin = new Padding(0, 3, 0, 3);
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(control, 1, row);
    }

    protected static Label NewHint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(4, 8, 0, 0),
    };

    protected static Button NewSmallButton(string text, Action onClick)
    {
        var button = new Button { Text = text, Width = 84, Height = 26, Margin = new Padding(0, 0, 0, 4) };
        button.Click += (_, _) => onClick();
        return button;
    }

    protected static FlowLayoutPanel NewRow()
    {
        return new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
        };
    }

    // ---------------------------------------------------------------- 通用小工具

    protected static void SelectByKind<T>(ComboBox combo, T kind) where T : struct, Enum
    {
        foreach (var item in combo.Items)
        {
            var value = item switch
            {
                EngineChoice e => (object)e.Kind,
                PaddleModelChoice p => p.Kind,
                PayloadChoice p => p.Kind,
                DetailChoice d => d.Kind,
                OcrModeChoice o => o.Kind,
                _ => null,
            };

            if (value is T typed && EqualityComparer<T>.Default.Equals(typed, kind))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    protected static T SelectedKind<T>(ComboBox combo, T fallback) where T : struct, Enum
    {
        var value = combo.SelectedItem switch
        {
            EngineChoice e => (object)e.Kind,
            PaddleModelChoice p => p.Kind,
            PayloadChoice p => p.Kind,
            DetailChoice d => d.Kind,
            OcrModeChoice o => o.Kind,
            _ => null,
        };

        return value is T typed ? typed : fallback;
    }

    protected static string Flatten(string value)
    {
        var flat = value
            .Replace((char)13, ' ')
            .Replace((char)10, ' ')
            .Trim();

        return flat.Length <= 140 ? flat : flat[..140] + "…";
    }

    protected static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    // ---------------------------------------------------------------- 选项载体

    protected sealed record EngineChoice(OcrEngineKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    protected sealed record PaddleModelChoice(PaddleModelKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    protected sealed record PayloadChoice(LlmPayloadMode Kind, string Label)
    {
        public override string ToString() => Label;
    }

    protected sealed record DetailChoice(LlmImageDetail Kind, string Label)
    {
        public override string ToString() => Label;
    }

    protected sealed record OcrModeChoice(OcrRunMode Kind, string Label)
    {
        public override string ToString() => Label;
    }
}
