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
    private readonly ComboBox _pageSize = new();
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
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(10, 8, 10, 4),
        };

        _successOnly.Text = "只看成功";
        _successOnly.AutoSize = true;
        _successOnly.Margin = new Padding(0, 7, 12, 0);
        _successOnly.CheckedChanged += async (_, _) => await ReloadAsync();
        toolbar.Controls.Add(_successOnly);

        toolbar.Controls.Add(new Label { Text = "显示最近", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        _pageSize.DropDownStyle = ComboBoxStyle.DropDownList;
        _pageSize.Width = 70;
        foreach (var size in PageSizes)
        {
            _pageSize.Items.Add(size);
        }

        _pageSize.SelectedItem = 50;
        _pageSize.SelectedIndexChanged += async (_, _) => await ReloadAsync();
        toolbar.Controls.Add(_pageSize);

        toolbar.Controls.Add(new Label { Text = "条", AutoSize = true, Margin = new Padding(4, 8, 12, 0) });

        var refresh = new Button { Text = "刷新", Width = 76, Height = 28, Margin = new Padding(0, 3, 4, 0) };
        refresh.Click += async (_, _) => await ReloadAsync();
        toolbar.Controls.Add(refresh);

        var preview = new Button { Text = "预览要发送的内容", Width = 150, Height = 28, Margin = new Padding(0, 3, 4, 0) };
        preview.Click += async (_, _) => await PreviewPayloadAsync();
        toolbar.Controls.Add(preview);

        var generate = new Button { Text = "现在生成一次", Width = 110, Height = 28, Margin = new Padding(0, 3, 4, 0) };
        generate.Click += async (_, _) =>
        {
            await _generateNow();
            await ReloadAsync();
        };
        toolbar.Controls.Add(generate);

        var copy = new Button { Text = "复制小结", Width = 96, Height = 28, Margin = new Padding(0, 3, 4, 0) };
        copy.Click += (_, _) => CopySelectedSummary();
        toolbar.Controls.Add(copy);

        var openFolder = new Button { Text = "打开总结目录", Width = 110, Height = 28, Margin = new Padding(0, 3, 4, 0) };
        openFolder.Click += (_, _) => OpenPath(_context.Paths.SummariesDirectory);
        toolbar.Controls.Add(openFolder);

        _summary.AutoSize = true;
        _summary.Margin = new Padding(12, 8, 0, 0);
        _summary.ForeColor = SystemColors.GrayText;
        toolbar.Controls.Add(_summary);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "开始时间", FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "触发", FillWeight = 7 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "结果", FillWeight = 7 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "模型", FillWeight = 20 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "记录/图", FillWeight = 9 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "耗时", FillWeight = 7 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "内容摘要", FillWeight = 36 });
        _grid.SelectionChanged += (_, _) => ShowSelected();
        _grid.RowPrePaint += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < _grid.Rows.Count
                && _grid.Rows[e.RowIndex].Tag is false)
            {
                _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = Color.OrangeRed;
            }
        };

        _detail.Dock = DockStyle.Fill;
        _detail.ReadOnly = true;
        _detail.Font = new Font("Consolas", 9.5f);
        _detail.BackColor = SystemColors.Window;
        _detail.WordWrap = true;
        _detail.Text = "在上方选中一条查看完整小结（失败时显示失败原因）。";

        var detailHeader = new Label
        {
            Text = "详情",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 4),
            ForeColor = SystemColors.GrayText,
        };

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

    /// <summary>预览要发送的内容，不真的发请求。</summary>
    private async Task PreviewPayloadAsync()
    {
        try
        {
            var (preparation, error) = await _context.SummaryRunner.PrepareAsync(_context.Options, CancellationToken.None);

            _detail.Text = preparation is null
                ? $"[无法预览]" + Environment.NewLine + Environment.NewLine + error
                : preparation.RenderForDisplay();
        }
        catch (Exception ex)
        {
            _log.Error("预览失败", ex);
            _detail.Text = $"[预览失败] {ex.Message}";
        }
    }

    /// <summary>生成小结。第一次会走隐私确认。</summary>
    private async Task GenerateWithConsentAsync()
    {
        if (!_context.Options.Summarization.Enabled)
        {
            var enable = MessageBox.Show(
                "大模型总结当前是关闭状态。" + Environment.NewLine + Environment.NewLine
                + "要现在打开吗？打开后才会把记录发送到配置的接口。",
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
                "生成小结会把最近的活动记录（窗口标题 + 屏幕上识别出的文字"
                + (_context.Options.Summarization.PayloadMode == LlmPayloadMode.TextOnly ? string.Empty : "，以及截图")
                + "）发送到配置的模型接口。" + Environment.NewLine + Environment.NewLine
                + "记录里可能包含隐私内容（聊天记录、文档片段、账号信息等），请确认这条链路你能接受。",
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
            var result = await _context.SummaryRunner.RunAsync(_context.Options, "手动", CancellationToken.None);

            _detail.Text = result.Success
                ? $"{result.Markdown}" + Environment.NewLine + Environment.NewLine + "---" + Environment.NewLine + $"已保存：{result.SavedPath}"
                : $"[未生成]" + Environment.NewLine + Environment.NewLine + result.Message
                  + (result.Preparation is null ? string.Empty : Environment.NewLine + Environment.NewLine + result.Preparation.RenderForDisplay());
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void CopySelectedSummary()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            return;
        }

        var index = _grid.SelectedRows[0].Index;
        if (index < 0 || index >= _runs.Count)
        {
            return;
        }

        var run = _runs[index];
        var text = run.Markdown.Length > 0 ? run.Markdown : run.Message;
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // 剪贴板被别的进程占用，忽略。
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
                    run.Provider.Length > 0 ? run.Provider : "—",
                    $"{run.RecordCount}/{run.ImageCount}",
                    run.ElapsedText,
                    run.Preview);

                _grid.Rows[index].Tag = run.Success;
            }

            _grid.ResumeLayout();

            var total = runs.Count;
            var failed = runs.Count(r => !r.Success);
            _summary.Text = total == 0
                ? "还没有总结记录"
                : $"共 {total} 条" + (failed > 0 ? $"，其中失败 {failed} 条" : string.Empty);

            if (_grid.Rows.Count > 0)
            {
                _grid.Rows[0].Selected = true;
                _grid.CurrentCell = _grid.Rows[0].Cells[0];
            }
            else
            {
                _detail.Text = "还没有总结记录。点「现在生成一次」，或到设置里把「定时生成总结」打开。";
            }
        }
        catch (Exception ex)
        {
            _log.Error("读取总结历史失败", ex);
            _summary.Text = $"读取失败：{ex.Message}";
        }
    }

    private void ShowSelected()
    {
        if (_grid.SelectedRows.Count == 0)
        {
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
            $"文件      ：{(run.SavedPath.Length == 0 ? "(未落盘)" : run.SavedPath)}",
            $"说明      ：{run.Message}",
            string.Empty,
        };

        lines.Add(run.Markdown.Length > 0 ? "--- 小结正文 ---" : "--- 没有小结正文（这次没有成功生成）---");

        if (run.Markdown.Length > 0)
        {
            lines.Add(run.Markdown);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
