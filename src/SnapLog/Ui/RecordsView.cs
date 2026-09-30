using System.ComponentModel;
using System.Runtime.InteropServices;
using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 记录查看器：筛选 + 分页 + 详情 + 导出 CSV。
///
/// 低开销的两个关键点：
/// - 列表查询只取摘要（substr(text,1,160)），不把整段 OCR 文字拉进内存；
///   选中某一行时才按 id 单独取全文，所以翻页和筛选都是常量级开销。
/// - 数据库操作全部在后台线程（存储层内部已放到线程池），UI 不会卡。
/// </summary>
internal sealed class RecordsView : UserControl
{
    private static readonly int[] PageSizes = [50, 100, 200, 500, 1000];

    private readonly AppOptions _options;
    private readonly IActivityStore _store;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;

    private readonly DateTimePicker _from = new();
    private readonly DateTimePicker _to = new();
    private readonly CheckBox _useDateRange = new();
    private readonly ScrollSafeComboBox _process = new();
    private readonly ScrollSafeComboBox _status = new();
    private readonly TextBox _keyword = new();
    private readonly DataGridView _grid = new();
    private readonly RichTextBox _detail = new();
    private readonly ScrollSafeComboBox _pageSize = new();
    private Button _delete = null!;
    private readonly Label _pageInfo = new();
    private readonly Button _firstPage = new();
    private readonly Button _previousPage = new();
    private readonly Button _nextPage = new();
    private readonly Button _lastPage = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly SplitContainer _split = new();
    private readonly Button _openScreenshot = new();
    private readonly Button _openImageFolder = new();

    private int _offset;
    private int _totalCount;
    private bool _busy;

    /// <summary>当前详情里显示的记录，供“打开截图”按钮使用。</summary>
    private ActivityRecord? _selectedRecord;

    public RecordsView(AppOptions options, IActivityRepository store, AppPaths paths, FileLogger log)
    {
        _options = options;
        _store = store;
        _paths = paths;
        _log = log;

        Dock = DockStyle.Fill;
        BackColor = SystemColors.Control;

        BuildLayout();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // SplitterDistance 只能在控件拿到最终尺寸之后设，否则会越界抛异常。
        TrySetSplitterDistance(380);

        await ReloadProcessListAsync();
        await RunQueryAsync(resetOffset: true);
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
            // 布局还没稳定，用默认值即可。
        }
    }

    // ---------------------------------------------------------------- 布局

    private void BuildLayout()
    {
        var filters = BuildFilterBar();
        var pager = BuildPagerBar();

        DoubleBuffer.Enable(_grid);
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;   // 批量删除需要一次选多条
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "时间", FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "进程", FillWeight = 9 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "窗口标题", FillWeight = 24 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字数", FillWeight = 6 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "耗时(ms)", FillWeight = 8 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", FillWeight = 7 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "识别文字摘要", FillWeight = 33 });
        _grid.SelectionChanged += async (_, _) =>
        {
            _delete.Enabled = _grid.SelectedRows.Count > 0;
            await ShowSelectedDetailAsync();
        };

        _detail.Dock = DockStyle.Fill;
        _detail.ReadOnly = true;
        _detail.Font = new Font("Consolas", 9.5f);
        _detail.BackColor = SystemColors.Window;
        _detail.WordWrap = true;
        _detail.Text = "请在上方选择一行查看完整记录。";

        var detailHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 4),
        };
        detailHeader.Controls.Add(new Label
        {
            Text = "详情",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 12, 0),
        });

        _openScreenshot.Text = "打开截图";
        _openScreenshot.Width = 96;
        _openScreenshot.Height = 26;
        _openScreenshot.Enabled = false;
        _openScreenshot.Margin = new Padding(0, 1, 4, 0);
        _openScreenshot.Click += (_, _) => OpenSelectedScreenshot();
        detailHeader.Controls.Add(_openScreenshot);

        _openImageFolder.Text = "打开截图目录";
        _openImageFolder.Width = 110;
        _openImageFolder.Height = 26;
        _openImageFolder.Margin = new Padding(0, 1, 0, 0);
        _openImageFolder.Click += (_, _) => OpenPath(ResolveImageDirectory());
        detailHeader.Controls.Add(_openImageFolder);

        var detailPanel = new Panel { Dock = DockStyle.Fill };
        detailPanel.Controls.Add(_detail);
        detailPanel.Controls.Add(detailHeader);

        // 上下分割：上面列表，下面详情。用户可以拖动分隔条。
        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Horizontal;
        _split.Panel1MinSize = 160;
        _split.Panel2MinSize = 120;
        _split.Panel1.Controls.Add(_grid);
        _split.Panel2.Controls.Add(detailPanel);

        var statusStrip = new StatusStrip();
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusStrip.Items.Add(_statusLabel);

        Controls.Add(_split);
        Controls.Add(pager);
        Controls.Add(filters);


    }

    private Control BuildFilterBar()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10, 8, 10, 4),
        };

        // 第一行：时间范围 + 是否启用
        var timeRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };

        _useDateRange.Text = "按时间筛选";
        _useDateRange.AutoSize = true;
        _useDateRange.Checked = true;
        _useDateRange.Margin = new Padding(0, 7, 10, 0);
        _useDateRange.CheckedChanged += (_, _) => UpdateFilterEnabledState();
        timeRow.Controls.Add(_useDateRange);

        _from.Format = DateTimePickerFormat.Custom;
        _from.CustomFormat = "yyyy-MM-dd HH:mm";
        _from.ShowUpDown = true;
        _from.Width = 150;
        _from.Value = ActivityQuery.DefaultFrom;
        timeRow.Controls.Add(_from);

        timeRow.Controls.Add(new Label { Text = "→", AutoSize = true, Margin = new Padding(6, 7, 6, 0) });

        _to.Format = DateTimePickerFormat.Custom;
        _to.CustomFormat = "yyyy-MM-dd HH:mm";
        _to.ShowUpDown = true;
        _to.Width = 150;
        _to.Value = DateTime.Now.AddMinutes(1);
        timeRow.Controls.Add(_to);

        panel.Controls.Add(timeRow, 0, 0);

        // 第二行：进程 / 状态 / 关键词 / 按钮
        var filterRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };

        filterRow.Controls.Add(new Label { Text = "进程", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        _process.DropDownStyle = ComboBoxStyle.DropDownList;
        _process.Width = 130;
        filterRow.Controls.Add(_process);

        filterRow.Controls.Add(new Label { Text = "状态", AutoSize = true, Margin = new Padding(12, 8, 4, 0) });
        _status.DropDownStyle = ComboBoxStyle.DropDownList;
        _status.Width = 100;
        filterRow.Controls.Add(_status);

        filterRow.Controls.Add(new Label { Text = "关键词", AutoSize = true, Margin = new Padding(12, 8, 4, 0) });
        _keyword.Width = 200;
        _keyword.PlaceholderText = "匹配窗口标题与识别文字";
        _keyword.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await RunQueryAsync(resetOffset: true);
            }
        };
        filterRow.Controls.Add(_keyword);

        var search = new Button { Text = "查询", Width = 76, Height = 28, Margin = new Padding(12, 3, 4, 0) };
        search.Click += async (_, _) => await RunQueryAsync(resetOffset: true);
        filterRow.Controls.Add(search);

        var reset = new Button { Text = "重置", Width = 76, Height = 28, Margin = new Padding(4, 3, 4, 0) };
        reset.Click += async (_, _) => await ResetFiltersAsync();
        filterRow.Controls.Add(reset);

        var export = new Button { Text = "导出 CSV", Width = 92, Height = 28, Margin = new Padding(4, 3, 4, 0) };
        export.Click += async (_, _) => await ExportAsync();
        filterRow.Controls.Add(export);

        // 删除：二次确认，并可选择连截图文件一起删。
        _delete = new Button { Text = "删除", Width = 76, Height = 28, Margin = new Padding(4, 3, 4, 0), Enabled = false };
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        filterRow.Controls.Add(_delete);

        panel.Controls.Add(filterRow, 0, 1);

        _status.Items.Clear();
        _status.Items.Add("全部");
        foreach (var name in Enum.GetNames<RecordStatus>())
        {
            _status.Items.Add(name);
        }

        _status.SelectedIndex = 0;
        UpdateFilterEnabledState();

        return panel;
    }

    private Control BuildPagerBar()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(10, 4, 10, 4),
        };

        panel.Controls.Add(new Label { Text = "每页", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });

        _pageSize.DropDownStyle = ComboBoxStyle.DropDownList;
        _pageSize.Width = 80;
        foreach (var size in PageSizes)
        {
            _pageSize.Items.Add(size);
        }

        _pageSize.SelectedItem = 100;
        _pageSize.SelectedIndexChanged += async (_, _) => await RunQueryAsync(resetOffset: true);
        panel.Controls.Add(_pageSize);

        panel.Controls.Add(new Label { Text = "条", AutoSize = true, Margin = new Padding(4, 8, 14, 0) });

        ConfigurePageButton(_firstPage, "|< 首页", async () => await RunQueryAsync(resetOffset: true, absoluteOffset: 0));
        ConfigurePageButton(_previousPage, "< 上一页", async () => await StepPageAsync(-1));
        ConfigurePageButton(_nextPage, "下一页 >", async () => await StepPageAsync(1));
        ConfigurePageButton(_lastPage, "末页 >|", async () => await RunQueryAsync(resetOffset: true, absoluteOffset: LastPageOffset()));

        panel.Controls.Add(_firstPage);
        panel.Controls.Add(_previousPage);
        panel.Controls.Add(_nextPage);
        panel.Controls.Add(_lastPage);

        _pageInfo.AutoSize = true;
        _pageInfo.Margin = new Padding(14, 8, 0, 0);
        panel.Controls.Add(_pageInfo);

        return panel;
    }

    private static void ConfigurePageButton(Button button, string text, Func<Task> onClick)
    {
        button.Text = text;
        button.Width = 92;
        button.Height = 28;
        button.Margin = new Padding(4, 3, 0, 0);
        button.Click += async (_, _) => await onClick();
    }

    // ---------------------------------------------------------------- 查询

    private ActivityQuery BuildFilterQuery()
    {
        RecordStatus? status = _status.SelectedIndex <= 0 ? null : Enum.Parse<RecordStatus>(_status.Text);
        var process = _process.SelectedIndex <= 0 ? null : _process.Text;

        DateTime? from = null;
        DateTime? to = null;

        if (_useDateRange.Checked)
        {
            // DateTimePicker 精度到分钟，秒统一向内收，避免"到 10:00"漏掉 10:00:30 的记录。
            from = _from.Value.Date.AddHours(_from.Value.Hour).AddMinutes(_from.Value.Minute);
            to = _to.Value.Date.AddHours(_to.Value.Hour).AddMinutes(_to.Value.Minute).AddSeconds(59);
        }

        return new ActivityQuery
        {
            From = from,
            To = to,
            ProcessName = process,
            Status = status,
            Keyword = string.IsNullOrWhiteSpace(_keyword.Text) ? null : _keyword.Text,
        };
    }

    private ActivityQuery BuildPagedQuery(int limit, int offset) =>
        BuildFilterQuery() with { Limit = limit, Offset = offset };

    internal async Task RunQueryAsync(bool resetOffset, int? absoluteOffset = null)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetPageButtonsEnabled(false);
        UseWaitCursor = true;

        try
        {
            if (resetOffset)
            {
                _offset = absoluteOffset ?? 0;
            }

            var limit = (int)(_pageSize.SelectedItem ?? 100);
            PagedResult<ActivityListItem> page;

            // 筛选条件变了以后当前偏移可能越界（比如原来在第 5 页，新条件只够 2 页），
            // 这时回退到最后一个有效页，而不是给用户一个空列表。
            while (true)
            {
                page = await _store.QueryAsync(BuildPagedQuery(limit, _offset), CancellationToken.None);

                if (page.Items.Count > 0 || page.TotalCount == 0 || _offset == 0)
                {
                    break;
                }

                _offset = Math.Max(0, (page.PageCount - 1) * limit);
            }

            _offset = page.Offset;
            _totalCount = page.TotalCount;

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var item in page.Items)
            {
                var index = _grid.Rows.Add(
                    item.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    item.ProcessName,
                    item.WindowTitle,
                    item.TextLength,
                    item.OcrMilliseconds,
                    item.Status,
                    item.Preview);
                _grid.Rows[index].Tag = item.Id;
            }

            _grid.ResumeLayout();

            _pageInfo.Text = page.TotalCount == 0
                ? "没有匹配的记录"
                : $"第 {page.PageNumber} / {page.PageCount} 页，共 {page.TotalCount} 条";

            _statusLabel.Text = page.TotalCount == 0
                ? "没有匹配的记录"
                : $"已载入本页 {page.Items.Count} 条（共 {page.TotalCount} 条）";

            _firstPage.Enabled = page.HasPrevious;
            _previousPage.Enabled = page.HasPrevious;
            _nextPage.Enabled = page.HasNext;
            _lastPage.Enabled = page.HasNext;

            if (_grid.Rows.Count > 0)
            {
                _grid.Rows[0].Selected = true;
                _grid.CurrentCell = _grid.Rows[0].Cells[0];
            }
            else
            {
                _detail.Text = "没有匹配的记录，可放宽时间范围或清空关键词。";
            }
        }
        catch (Exception ex)
        {
            _log.Error("查询记录失败", ex);
            _statusLabel.Text = $"查询失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
        }
    }

    private async Task StepPageAsync(int delta)
    {
        var limit = (int)(_pageSize.SelectedItem ?? 100);
        var target = _offset + (delta * limit);
        await RunQueryAsync(resetOffset: true, absoluteOffset: Math.Max(0, target));
    }

    private int LastPageOffset()
    {
        var limit = (int)(_pageSize.SelectedItem ?? 100);
        if (_totalCount <= 0)
        {
            return 0;
        }

        var pages = (int)Math.Ceiling(_totalCount / (double)limit);
        return Math.Max(0, (pages - 1) * limit);
    }

    private void SetPageButtonsEnabled(bool enabled)
    {
        _firstPage.Enabled = enabled;
        _previousPage.Enabled = enabled;
        _nextPage.Enabled = enabled;
        _lastPage.Enabled = enabled;
    }

    internal async Task ReloadProcessListAsync()
    {
        try
        {
            var names = await _store.GetProcessNamesAsync(CancellationToken.None);

            var previous = _process.SelectedIndex > 0 ? _process.Text : null;

            _process.Items.Clear();
            _process.Items.Add("全部");
            foreach (var name in names)
            {
                _process.Items.Add(name);
            }

            // 尽量保住用户原先的选择，避免刷新后筛选条件被悄悄重置。
            var restored = previous is null ? -1 : _process.Items.IndexOf(previous);
            _process.SelectedIndex = restored >= 0 ? restored : 0;
        }
        catch (Exception ex)
        {
            _log.Error("载入进程列表失败", ex);
        }
    }

    private async Task ResetFiltersAsync()
    {
        _useDateRange.Checked = true;
        _from.Value = ActivityQuery.DefaultFrom;
        _to.Value = DateTime.Now.AddMinutes(1);
        _keyword.Clear();
        _status.SelectedIndex = 0;
        _process.SelectedIndex = 0;

        await RunQueryAsync(resetOffset: true);
    }

    private void UpdateFilterEnabledState()
    {
        var enabled = _useDateRange.Checked;
        _from.Enabled = enabled;
        _to.Enabled = enabled;
    }

    private async Task ShowSelectedDetailAsync()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            return;
        }

        // 多选时不去逐条查详情：这时用户是要批量操作，不是要看某一条。
        if (_grid.SelectedRows.Count > 1)
        {
            _detail.Text = $"已选中 {_grid.SelectedRows.Count} 条记录。点“删除”可批量删除。";
            return;
        }

        var tag = _grid.SelectedRows[0].Tag;
        if (tag is not long id)
        {
            return;
        }

        try
        {
            var record = await _store.GetByIdAsync(id, CancellationToken.None);
            if (record is null)
            {
                _selectedRecord = null;
                _detail.Text = "该记录已不存在。";
                return;
            }

            _selectedRecord = record;
            _openScreenshot.Enabled = File.Exists(_paths.ResolveStoredImagePath(record.ImagePath));
            _detail.Text = RenderDetail(record);
            _detail.SelectionStart = 0;
            _detail.ScrollToCaret();
        }
        catch (Exception ex)
        {
            _log.Error("读取记录详情失败", ex);
            _detail.Text = $"读取详情失败：{ex.Message}";
        }
    }

    private string RenderDetail(ActivityRecord record)
    {
        var stored = _paths.ResolveStoredImagePath(record.ImagePath);
        var imageState = stored.Length == 0
            ? "（未留档，可在设置中开启“保存截图文件”）"
            : File.Exists(stored) ? stored : $"{stored}（文件已不存在，可能已被保留策略清理）";

        return $"""
                # 记录 #{record.Id}

                时间      ：{record.Timestamp:yyyy-MM-dd HH:mm:ss}
                进程      ：{(record.ProcessName.Length == 0 ? "(未知)" : record.ProcessName)}
                窗口标题  ：{record.WindowTitle}
                窗口类名  ：{(record.WindowClass.Length == 0 ? "(未采集)" : record.WindowClass)}
                状态      ：{record.Status}
                字数      ：{record.TextLength}
                OCR 耗时  ：{record.OcrMilliseconds} ms
                抓取方式  ：{record.CaptureMethod}
                图像尺寸  ：{record.ImageWidth}x{record.ImageHeight}
                截图文件  ：{imageState}
                提示/错误 ：{(record.Error.Length == 0 ? "(无)" : record.Error)}

                --- 识别文字 ---
                {(record.OcrText.Length == 0 ? "(无)" : record.OcrText)}
                """;
    }

    // ---------------------------------------------------------------- 导出

    /// <summary>
    /// 批量删除选中的记录。二次确认里可以勾选"同时删除截图文件"——
    /// 删库是常规操作，删文件不可撤销，所以默认不勾。
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
            MessageBox.Show("请先在列表里选择要删除的记录。", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 先数一下这些记录里有多少张截图，好在确认框里说清楚。
        var screenshots = 0;
        foreach (var id in ids)
        {
            var record = await _store.GetByIdAsync(id, CancellationToken.None).ConfigureAwait(true);
            if (record is not null && !string.IsNullOrWhiteSpace(record.ImagePath) && ResolveImage(record.ImagePath) is { } path)
            {
                screenshots++;
            }
        }

        using var dialog = new DeleteRecordsDialog(ids.Count, screenshots);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var paths = await _store.DeleteByIdsAsync(ids, CancellationToken.None).ConfigureAwait(true);

            var removedFiles = 0;
            long freedBytes = 0;

            if (dialog.DeleteScreenshots)
            {
                foreach (var stored in paths)
                {
                    var path = ResolveImage(stored);
                    if (path is null)
                    {
                        continue;
                    }

                    try
                    {
                        var info = new FileInfo(path);
                        var size = info.Exists ? info.Length : 0;
                        info.Delete();
                        removedFiles++;
                        freedBytes += size;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _log.Warn($"删除截图失败：{path} —— {ex.Message}");
                    }
                }
            }

            var message = $"已删除 {ids.Count} 条记录"
                          + (dialog.DeleteScreenshots ? $"，同时删除 {removedFiles} 张截图（释放 {DescribeBytes(freedBytes)}）" : string.Empty);
            _statusLabel.Text = message;
            _log.Info(message);

            _detail.Text = message;
            await RunQueryAsync(resetOffset: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error("删除记录失败", ex);
            _statusLabel.Text = $"删除失败：{ex.Message}";
            MessageBox.Show($"删除失败：{ex.Message}", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string DescribeBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    /// <summary>把库里存的截图路径解析成本机绝对路径；解析不出来或文件不在就返回 null。</summary>
    private string? ResolveImage(string storedPath)
    {
        var path = _paths.ResolveStoredImagePath(storedPath);
        return path.Length > 0 && File.Exists(path) ? path : null;
    }

    private async Task ExportAsync()
    {
        if (_totalCount == 0)
        {
            MessageBox.Show("当前筛选条件下没有可导出的记录。", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "导出筛选结果",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            DefaultExt = "csv",
            FileName = $"snaplog-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            InitialDirectory = Directory.Exists(_paths.DataDirectory) ? _paths.DataDirectory : null,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        UseWaitCursor = true;
        _statusLabel.Text = "正在导出…";
        SetPageButtonsEnabled(false);

        try
        {
            // 导出的是"当前筛选条件下的全部结果"，不只是当前这一页——
            // 分页只是为了不让界面卡住，用户点导出想要的显然是完整结果。
            var count = await CsvActivityTransfer.ExportAsync(
                _store, BuildFilterQuery(), dialog.FileName, _log, CancellationToken.None);

            _statusLabel.Text = $"已导出 {count} 条到 {dialog.FileName}";

            var open = MessageBox.Show(
                $"已导出 {count} 条记录到：\n{dialog.FileName}\n\n是否打开所在位置？",
                "SnapLog",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (open == DialogResult.Yes)
            {
                OpenPath(dialog.FileName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Error("导出失败", ex);
            MessageBox.Show($"导出失败：{ex.Message}", "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _statusLabel.Text = "导出失败";
        }
        finally
        {
            UseWaitCursor = false;
            _firstPage.Enabled = _offset > 0;
            _previousPage.Enabled = _offset > 0;
            _nextPage.Enabled = _offset + _grid.Rows.Count < _totalCount;
            _lastPage.Enabled = _nextPage.Enabled;
        }
    }

    // ---------------------------------------------------------------- 杂项

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.F5:
                e.SuppressKeyPress = true;
                await RunQueryAsync(resetOffset: false);
                break;
            case Keys.Escape:
                // 控件里不处理关闭：由内嵌的独立窗口负责（KeyPreview 在包装窗口上）。
                break;
            case Keys.PageDown when e.Control:
                e.SuppressKeyPress = true;
                await StepPageAsync(1);
                break;
            case Keys.PageUp when e.Control:
                e.SuppressKeyPress = true;
                await StepPageAsync(-1);
                break;
        }
    }

    private string ResolveImageDirectory() =>
        _paths.ResolveImageDirectory(_options.Capture.ImageDirectory);

    private void OpenSelectedScreenshot()
    {
        var record = _selectedRecord;
        if (record is null)
        {
            return;
        }

        var path = _paths.ResolveStoredImagePath(record.ImagePath);
        if (!File.Exists(path))
        {
            MessageBox.Show(
                "截图文件不存在：" + Environment.NewLine + path + Environment.NewLine + Environment.NewLine
                + "可能已被“截图保留天数”的清理删除。",
                "SnapLog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _openScreenshot.Enabled = false;
            return;
        }

        OpenPath(path);
    }

    private void OpenPath(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            _log.Warn($"打开路径失败：{ex.Message}");
        }
    }
}
