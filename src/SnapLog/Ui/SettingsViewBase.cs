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

    /// <summary>载入配置期间触发的控件事件不算用户改动。</summary>
    private bool _loading;

    private bool _dirty;

    private Button _saveButton = null!;
    private Button _discardButton = null!;

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
        DoubleBuffered = true;

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

            // 建页期间挂起的布局到这里一次性恢复：反着恢复，让最外层最后重排。
            ResumeDeferredLayout(content);

            var footer = BuildFooter();
            footer.Dock = DockStyle.Bottom;

            Controls.Add(content);
            Controls.Add(footer);

            WireDirtyTracking(content);
        }
        finally
        {
            ResumeLayout(true);
        }

        ReloadFromOptions();
    }

    /// <summary>构建这一页的内容。</summary>
    protected abstract Control BuildContent();

    /// <summary>把配置读出来填到控件上。</summary>
    protected abstract void LoadFromOptions();

    /// <summary>把控件上的值写回配置对象（不写盘）。</summary>
    protected abstract void WriteToOptions();

    /// <summary>外部改过配置后要刷新显示，调用这个。</summary>
    public void Reload() => ReloadFromOptions();

    // ---------------------------------------------------------------- 改动跟踪

    /// <summary>
    /// 载入配置：这期间控件触发的事件不算用户改动，载完把"有改动"清掉，
    /// 于是"保存设置/放弃修改"在没有任何修改时是灰的，改一处才亮起来。
    /// </summary>
    private void ReloadFromOptions()
    {
        _loading = true;
        try
        {
            LoadFromOptions();
        }
        finally
        {
            _loading = false;
            SetDirty(false);
        }
    }

    /// <summary>标记这一页有未保存的改动。列表增删这类不走控件事件的改动要自己调。</summary>
    protected void MarkDirty()
    {
        if (_loading)
        {
            return;
        }

        SetDirty(true);
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        _saveButton.Enabled = dirty;
        _discardButton.Enabled = dirty;
    }

    /// <summary>递归订阅各输入控件的变更事件。选中列表项不算改动，所以 ListBox/DataGridView 不算在内。</summary>
    private void WireDirtyTracking(Control root)
    {
        foreach (Control child in root.Controls)
        {
            switch (child)
            {
                case CheckBox check:
                    check.CheckedChanged += OnControlChanged;
                    break;
                case TextBox text:
                    text.TextChanged += OnControlChanged;
                    break;
                case NumericUpDown number:
                    number.ValueChanged += OnControlChanged;
                    break;
                case ComboBox combo:
                    combo.SelectedIndexChanged += OnControlChanged;
                    combo.TextChanged += OnControlChanged;
                    break;
            }

            WireDirtyTracking(child);
        }
    }

    private void OnControlChanged(object? sender, EventArgs e) => MarkDirty();

    // ---------------------------------------------------------------- 建页时的布局

    /// <summary>建页期间挂起布局的容器。</summary>
    private readonly List<Control> _deferredLayout = [];

    /// <summary>
    /// 挂起一个容器的布局，等整页建完再统一恢复。
    /// 不这么做的话，每加一行控件都会让整棵 AutoSize 容器树重新量一遍、排一遍，
    /// 一个设置页能因此多花几百毫秒。
    /// </summary>
    private T DeferLayout<T>(T control) where T : Control
    {
        control.SuspendLayout();
        _deferredLayout.Add(control);
        return control;
    }

    private void ResumeDeferredLayout(Control content)
    {
        for (var i = _deferredLayout.Count - 1; i >= 0; i--)
        {
            _deferredLayout[i].ResumeLayout(false);
        }

        _deferredLayout.Clear();

        DoubleBuffer.Enable(content);
        content.PerformLayout();
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

        _saveButton = new Button
        {
            Text = "保存设置",
            Width = 110,
            Height = 32,
            Enabled = false,
        };
        _saveButton.Click += (_, _) => Save();

        _discardButton = new Button { Text = "放弃修改", Width = 100, Height = 32, Enabled = false };
        _discardButton.Click += (_, _) => DiscardChanges();

        // 灰掉的原因要说清楚，否则用户会以为是坏了。
        var tip = new ToolTip();
        tip.SetToolTip(_saveButton, "没有任何改动");
        tip.SetToolTip(_discardButton, "没有任何改动");

        panel.Controls.Add(_saveButton);
        panel.Controls.Add(_discardButton);
        return panel;
    }

    private void Save()
    {
        WriteToOptions();

        // 界面自检里 --ui-smoke 会用 _ => true 的回调，这里不真写盘。
        if (_onSave())
        {
            ReloadFromOptions();
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
        ReloadFromOptions();
    }

    // ---------------------------------------------------------------- 共享布局

    /// <summary>一个带标题的分组卡片。</summary>
    protected TableLayoutPanel NewSection(string title)
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
        // 够放下最长的行标题（"PaddleOCR 模型："），避免标题折成两行。
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
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

        // 标题下压一条分隔线：光靠加粗文字，几组内容容易糊成一片。
        var separator = new Panel
        {
            Height = 1,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            BackColor = SystemColors.ControlDark,
            Margin = new Padding(0, 2, 6, 8),
        };
        grid.Controls.Add(separator, 0, 1);
        grid.SetColumnSpan(separator, 2);
        grid.RowCount = 2;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 用 Tag 记下一个空行号，避免和标题、分隔线冲突。
        grid.Tag = 2;

        DoubleBuffer.Enable(grid);
        return DeferLayout(grid);
    }

    protected static void AddRow(TableLayoutPanel grid, string caption, Control control)
    {
        var row = (int)(grid.Tag ?? 2);
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
        // 限宽让它换行：不限宽的长说明会一路顶出窗口，窄窗口下就看不全了。
        MaximumSize = new Size(500, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(4, 8, 0, 0),
    };

    protected static Button NewSmallButton(string text, Action onClick)
    {
        var button = new Button { Text = text, Width = 84, Height = 26, Margin = new Padding(0, 0, 0, 4) };
        button.Click += (_, _) => onClick();
        return button;
    }

    protected FlowLayoutPanel NewRow()
    {
        return DeferLayout(new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            // 窄窗口下让行内控件换到下一行，而不是被裁掉。
            WrapContents = true,
        });
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
