using SnapLog.Configuration;
using SnapLog.Diagnostics;

namespace SnapLog.Ui;

/// <summary>
/// “全部选项（高级）”：把配置对象整棵丢给 PropertyGrid。
/// 常用项已经放在设置页里了，这里只服务于需要改冷门选项的场景。
/// </summary>
internal sealed class AdvancedOptionsForm : Form
{
    private readonly AppOptions _options;
    private readonly PropertyGrid _grid = new();
    private readonly FileLogger _log;
    private readonly Func<bool> _onSave;

    public AdvancedOptionsForm(AppOptions options, FileLogger log, Func<bool> onSave)
    {
        _options = options;
        _log = log;
        _onSave = onSave;

        Text = "全部选项（高级）";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 520);
        Size = new Size(880, 640);
        Icon = IconFactory.AppIcon;
        ShowInTaskbar = false;

        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10, 8, 10, 6),
            ForeColor = SystemColors.GrayText,
            Text = "修改后点“保存设置”写入磁盘并生效。本页直接修改内存中的配置对象，与设置页各项为同一份数据。",
        };

        _grid.Dock = DockStyle.Fill;
        _grid.SelectedObject = _options;
        _grid.PropertySort = PropertySort.Categorized;
        _grid.HelpVisible = true;
        _grid.ToolbarVisible = false;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(10, 8, 10, 10),
        };

        var save = new Button { Text = "保存设置", Width = 110, Height = 32 };
        save.Click += (_, _) => Save();

        var reload = new Button { Text = "从磁盘重新读取", Width = 130, Height = 32 };
        reload.Click += (_, _) => Reload();

        var close = new Button { Text = "关闭", Width = 88, Height = 32 };
        close.Click += (_, _) => Close();

        buttons.Controls.Add(save);
        buttons.Controls.Add(reload);
        buttons.Controls.Add(close);

        Controls.Add(_grid);
        Controls.Add(buttons);
        Controls.Add(hint);
    }

    private void Save()
    {
        _grid.Refresh();

        if (!_onSave())
        {
            return;
        }

        _log.Info("已通过“全部选项（高级）”保存配置");
        MessageBox.Show("已保存并生效。", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Reload()
    {
        var load = OptionsStore.Load(null);
        OptionsStore.CopyInto(_options, load.Options);
        _grid.Refresh();

        if (load.Warning is not null)
        {
            MessageBox.Show(load.Warning, "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
