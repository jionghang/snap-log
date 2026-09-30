using System.Runtime.InteropServices;
using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Summarization;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 总结历史。成功和失败都列出来——定时任务半夜失败了，第二天得能查到原因。
/// 正文直接存在库里，所以历史查看不依赖 Markdown 文件还在不在。
/// </summary>
internal sealed class SummaryHistoryView : UserControl
{
    private static readonly int[] PageSizes = [20, 50, 100, 200];

    private readonly SettingsContext _context;
    private readonly FileLogger _log;
    private readonly Func<Task> _generateNow;

    private ISummaryHistoryStore Store => _context.Store;

    private readonly DataGridView _grid = new();
    private readonly RichTextBox _detail = new();
    private readonly ScrollSafeComboBox _pageSize = new();
    private readonly CheckBox _pickDay = new();
    private readonly DateTimePicker _dayPicker = new();
    private readonly Button _delete = new();
    private readonly Button _openSummaryFile = new();
    private readonly CheckBox _successOnly = new();
    private readonly Label _summary = new();
    private readonly SplitContainer _split = new();

    private IReadOnlyList<SummaryRun> _runs = [];

    public SummaryHistoryView(SettingsContext context)
    {
        _context = context;
        _log = context.Log;
        _generateNow = () => GenerateWithConsentAsync();

        Dock = DockStyle.Fill;
        BackColor = SystemColors.Control;

        BuildLayout();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        TrySetSplitterDistance(360);
        await ReloadAsync();
    }

    private void TrySetSplitterDistance(int distance)
    {
        try
        {
            var available = _split.Height - _split.SplitterWidth - _split.Panel2MinSize;
            if (available >= _split.Panel1MinSize)
            {
                _split.SplitterDistance = Math.Clamp(distance, _split.Panel1MinSize, available);
            }
        }
        catch (InvalidOperationException)
        {
            // 布局还没稳定，用默认值。
        }
    }

    private void BuildLayout()
    {
        // 单行工具条，用一段固定间距把"看"和"做"分开。
        // （不用"左右两列 + 百分比"：WrapContents 的流式面板放进 AutoSize 列里会折成竖条。）
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(10, 8, 10, 4),
        };

        var viewing = toolbar;
        var acting = toolbar;


        _successOnly.Text = "只看成功";
        _successOnly.AutoSize = true;
        _successOnly.Margin = new Padding(0, 7, 12, 0);
        _successOnly.CheckedChanged += async (_, _) => await ReloadAsync();
        viewing.Controls.Add(_successOnly);

        viewing.Controls.Add(new Label { Text = "显示最近", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        _pageSize.DropDownStyle = ComboBoxStyle.DropDownList;
        _pageSize.Width = 70;
        foreach (var size in PageSizes)
        {
            _pageSize.Items.Add(size);
        }

        _pageSize.SelectedItem = 50;
        _pageSize.SelectedIndexChanged += async (_, _) => await ReloadAsync();
        viewing.Controls.Add(_pageSize);

        viewing.Controls.Add(new Label { Text = "条", AutoSize = true, Margin = new Padding(4, 8, 12, 0) });

        var refresh = new Button { Text = "刷新", Width = 76, Height = 28, Margin = new Padding(0, 3, 4, 0) };
        refresh.Click += async (_, _) => await ReloadAsync();
        viewing.Controls.Add(refresh);

        AddGroupGap(toolbar);

        // 生成范围放在"现在生成一次"之前：先说范围，再说动作。
        // 勾上就只总结指定那一天的记录，不勾则总结最近的记录。
        acting.Controls.Add(new Label
        {
            Text = "生成范围",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(12, 8, 4, 0),
        });

        _pickDay.Text = "只总结指定日期";
        _pickDay.AutoSize = true;
        _pickDay.Margin = new Padding(0, 8, 4, 0);
        _pickDay.CheckedChanged += (_, _) => _dayPicker.Enabled = _pickDay.Checked;
        acting.Controls.Add(_pickDay);

        _dayPicker.Format = DateTimePickerFormat.Custom;
        _dayPicker.CustomFormat = "yyyy-MM-dd";
        _dayPicker.Width = 110;
        _dayPicker.Value = DateTime.Today.AddDays(-1);
        _dayPicker.Enabled = false;
        _dayPicker.Margin = new Padding(0, 3, 4, 0);
        DoubleBuffer.Enable(_dayPicker);
        acting.Controls.Add(_dayPicker);

        var generate = new Button { Text = "现在生成一次", Width = 110, Height = 28, Margin = new Padding(0, 3, 4, 0) };
        generate.Click += async (_, _) =>
        {
            await _generateNow();
            await ReloadAsync();
        };
        acting.Controls.Add(generate);

        _delete.Text = "删除";
        _delete.Width = 76;
        _delete.Height = 28;
        _delete.Enabled = false;
        _delete.Margin = new Padding(12, 3, 4, 0);
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        acting.Controls.Add(_delete);

        _summary.AutoSize = true;
        _summary.Margin = new Padding(12, 8, 0, 0);
        _summary.ForeColor = SystemColors.GrayText;
        acting.Controls.Add(_summary);

        DoubleBuffer.Enable(_grid);
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;   // 批量删除需要一次选多条
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "开始时间", FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "触发", FillWeight = 7 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "结果", FillWeight = 7 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "飞书", FillWeight = 8 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "模型", FillWeight = 20 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "记录/图", FillWeight = 9 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "耗时", FillWeight = 7 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "内容摘要", FillWeight = 34 });
        _grid.SelectionChanged += (_, _) =>
        {
            _delete.Enabled = _grid.SelectedRows.Count > 0;
            ShowSelected();
        };


        _detail.Dock = DockStyle.Fill;
        _detail.ReadOnly = true;
        _detail.Font = new Font("Consolas", 9.5f);
        _detail.BackColor = SystemColors.Window;
        _detail.WordWrap = true;
        _detail.Text = "请在上方选择一条记录查看完整总结（失败时显示原因）。";

        var detailHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 4, 0, 4),
        };

        detailHeader.Controls.Add(new Label
        {
            Text = "详情",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 12, 0),
        });

        _openSummaryFile.Text = "打开总结文件";
        _openSummaryFile.Width = 116;
        _openSummaryFile.Height = 26;
        _openSummaryFile.Enabled = false;
        _openSummaryFile.Margin = new Padding(0, 1, 4, 0);
        _openSummaryFile.Click += (_, _) => OpenSelectedSummaryFile();
        detailHeader.Controls.Add(_openSummaryFile);

        var openFolder = new Button { Text = "打开总结目录", Width = 116, Height = 26, Margin = new Padding(0, 1, 0, 0) };
        openFolder.Click += (_, _) => OpenPath(_context.Paths.SummariesDirectory);
        detailHeader.Controls.Add(openFolder);

        var detailPanel = new Panel { Dock = DockStyle.Fill };
        detailPanel.Controls.Add(_detail);
        detailPanel.Controls.Add(detailHeader);

        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Horizontal;
        _split.Panel1MinSize = 150;
        _split.Panel2MinSize = 120;
        _split.Panel1.Controls.Add(_grid);
        _split.Panel2.Controls.Add(detailPanel);

        Controls.Add(_split);
        Controls.Add(toolbar);
    }

    /// <summary>工具条里的分组间隔：一段空白占位，换行时跟着走。</summary>
    private static void AddGroupGap(FlowLayoutPanel toolbar) =>
        toolbar.Controls.Add(new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Width = 22,
            Height = 1,
            Margin = new Padding(0, 0, 0, 0),
        });

    /// <summary>勾了"指定日期"就返回那一天，否则返回 null（表示总结最近的记录）。</summary>
    private DateOnly? SelectedDay =>
        _pickDay.Checked ? DateOnly.FromDateTime(_dayPicker.Value) : null;

    /// <summary>生成总结。第一次会走隐私确认。</summary>
    private async Task GenerateWithConsentAsync()
    {
        if (!_context.Options.Summarization.Enabled)
        {
            var enable = MessageBox.Show(
                "大模型总结当前为关闭状态。" + Environment.NewLine + Environment.NewLine
                + "是否现在启用？启用后才会把内容发送到所配置的接口。",
                "SnapLog",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (enable != DialogResult.Yes)
            {
                return;
            }

            _context.Options.Summarization.Enabled = true;
            _context.OnSave();
        }

        if (!_context.Options.Summarization.ConsentGranted)
        {
            var confirm = MessageBox.Show(
                "生成总结会把最近的活动记录（窗口标题与识别文字"
                + (_context.Options.Summarization.PayloadMode == LlmPayloadMode.TextOnly ? string.Empty : "，以及截图")
                + "）发送到所配置的模型接口。" + Environment.NewLine + Environment.NewLine
                + "内容可能包含隐私信息（聊天记录、文档片段、账号信息等），请确认可以接受。",
                "数据外发确认",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.OK)
            {
                return;
            }

            _context.Options.Summarization.ConsentGranted = true;
            _context.OnSave();
        }

        _detail.Text = "正在生成，请稍候…";
        UseWaitCursor = true;

        try
        {
            var result = await _context.SummaryRunner
                .RunAsync(_context.Options, "手动", SelectedDay, CancellationToken.None)
                .ConfigureAwait(true);

            _detail.Text = result.Success
                ? $"{result.Markdown}" + Environment.NewLine + Environment.NewLine + "---" + Environment.NewLine + $"已保存：{result.SavedPath}"
                : "[未生成]" + Environment.NewLine + Environment.NewLine + result.Message
                  + (result.Preparation is null ? string.Empty : Environment.NewLine + Environment.NewLine + result.Preparation.RenderForDisplay());
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            _log.Warn($"打开路径失败：{ex.Message}");
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            var limit = (int)(_pageSize.SelectedItem ?? 50);
            var runs = await Store.GetSummaryRunsAsync(limit, CancellationToken.None);

            if (_successOnly.Checked)
            {
                runs = [.. runs.Where(r => r.Success)];
            }

            _runs = runs;

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var run in runs)
            {
                var index = _grid.Rows.Add(
                    run.StartedAt.ToString("MM-dd HH:mm:ss"),
                    run.Trigger,
                    run.Success ? "成功" : "失败",
                    run.Pushed ? "已写入" : "—",
                    run.Provider.Length > 0 ? run.Provider : "—",
                    $"{run.RecordCount}/{run.ImageCount}",
                    run.ElapsedText,
                    run.Preview);

                _grid.Rows[index].Tag = run.Id;
                _grid.Rows[index].DefaultCellStyle.ForeColor = run.Success ? SystemColors.ControlText : Color.OrangeRed;
            }

            _grid.ResumeLayout();

            var total = runs.Count;
            var failed = runs.Count(r => !r.Success);
            _summary.Text = total == 0
                ? "暂无总结记录"
                : $"共 {total} 条" + (failed > 0 ? $"，其中失败 {failed} 条" : string.Empty);

            if (_grid.Rows.Count > 0)
            {
                _grid.Rows[0].Selected = true;
                _grid.CurrentCell = _grid.Rows[0].Cells[0];
            }
            else
            {
                _detail.Text = "暂无总结记录。可点“现在生成一次”，或在设置中启用“定时生成总结”。";
            }
        }
        catch (Exception ex)
        {
            _log.Error("读取总结历史失败", ex);
            _summary.Text = $"读取失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 批量删除选中的总结。二次确认里可以勾选"同时删除总结文件"——
    /// 删库是常规操作，删 .md 文件不可撤销，所以默认不勾。
    /// </summary>
    private async Task DeleteSelectedAsync()
    {
        var ids = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.Tag)
            .OfType<long>()
            .ToList();

        if (ids.Count == 0)
        {
            MessageBox.Show("请先在列表里选择要删除的总结。", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var files = _runs
            .Where(run => ids.Contains(run.Id))
            .Count(run => run.SavedPath.Length > 0 && File.Exists(run.SavedPath));

        using var dialog = new DeleteConfirmDialog(
            "总结",
            ids.Count,
            "总结文件（.md）",
            files,
            "这些总结没有落盘的 Markdown 文件（生成失败，或文件已被清理）。");

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var paths = await _context.Store.DeleteSummaryRunsAsync(ids, CancellationToken.None).ConfigureAwait(true);

            var removedFiles = 0;
            if (dialog.DeleteFiles)
            {
                foreach (var path in paths)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            removedFiles++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _log.Warn($"删除总结文件失败：{path} —— {ex.Message}");
                    }
                }
            }

            var message = $"已删除 {ids.Count} 条总结"
                          + (dialog.DeleteFiles ? $"，同时删除 {removedFiles} 个文件" : string.Empty);
            _log.Info(message);

            _detail.Text = message;
            _openSummaryFile.Enabled = false;
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error("删除总结失败", ex);
            MessageBox.Show($"删除失败：{ex.Message}", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>打开当前选中那条总结的 Markdown 文件。</summary>
    private void OpenSelectedSummaryFile()
    {
        if (_grid.SelectedRows.Count != 1)
        {
            return;
        }

        var index = _grid.SelectedRows[0].Index;
        if (index < 0 || index >= _runs.Count)
        {
            return;
        }

        OpenPath(_runs[index].SavedPath);
    }

    private void ShowSelected()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            return;
        }

        // 多选时用户是要批量删除，不是要看某一条。
        if (_grid.SelectedRows.Count > 1)
        {
            _detail.Text = $"已选中 {_grid.SelectedRows.Count} 条总结。点“删除”可批量删除。";
            _openSummaryFile.Enabled = false;
            return;
        }

        var index = _grid.SelectedRows[0].Index;
        if (index < 0 || index >= _runs.Count)
        {
            return;
        }

        var run = _runs[index];
        _detail.Text = RenderDetail(run);
        _detail.SelectionStart = 0;
        _detail.ScrollToCaret();

        _openSummaryFile.Enabled = run.SavedPath.Length > 0 && File.Exists(run.SavedPath);
    }

    private static string RenderDetail(SummaryRun run)
    {
        var lines = new List<string>
        {
            $"# 总结 #{run.Id}",
            string.Empty,
            $"开始时间  ：{run.StartedAt:yyyy-MM-dd HH:mm:ss}",
            $"结束时间  ：{run.FinishedAt:yyyy-MM-dd HH:mm:ss}",
            $"触发来源  ：{run.Trigger}",
            $"结果      ：{(run.Success ? "成功" : "失败")}",
            $"模型      ：{(run.Provider.Length == 0 ? "—" : run.Provider)}",
            run.Attempts > 1 ? $"尝试次数  ：第 {run.Attempts} 次成功" : "尝试次数  ：1",
            $"输入记录  ：{run.RecordCount} 条，附带截图 {run.ImageCount} 张",
            $"耗时      ：{run.ElapsedText}",
            $"飞书写入  ：{(run.PushedAt is { } pushedAt ? pushedAt.ToString("yyyy-MM-dd HH:mm:ss") : "尚未写入")}",
            $"覆盖范围  ：{(run.CoveredDay.Length == 0 ? "最近的记录" : run.CoveredDay + " 全天")}",
            $"文件      ：{(run.SavedPath.Length == 0 ? "（未保存）" : run.SavedPath)}",
            $"说明      ：{run.Message}",
            string.Empty,
        };

        lines.Add(run.Markdown.Length > 0 ? "--- 总结正文 ---" : "--- 无总结正文（本次未成功生成）---");

        if (run.Markdown.Length > 0)
        {
            lines.Add(run.Markdown);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
