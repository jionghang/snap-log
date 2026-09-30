using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;
using SnapLog.Ocr;

namespace SnapLog.Ui;

/// <summary>OCR 配置页：引擎、模型、识别方式、识别测试。</summary>
internal sealed class OcrSettingsView : SettingsViewBase
{
    private ComboBox _ocrEngine = null!;
    private ComboBox _paddleModel = null!;
    private CheckBox _paddleMkldnn = null!;
    private NumericUpDown _paddleThreads = null!;
    private ComboBox _ocrMode = null!;
    private TextBox _ocrBatchTime = null!;
    private Label _engineHint = null!;
    private Button _testOcr = null!;

    public OcrSettingsView(SettingsContext context)
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

        var grid = NewSection("文字识别（OCR）");

        _ocrEngine = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 420,
        };
        _ocrEngine.Items.AddRange(
        [
            new EngineChoice(OcrEngineKind.PaddleOcr, "PaddleOCR（离线 PP-OCR 模型，中文更准）"),
            new EngineChoice(OcrEngineKind.WindowsMedia, "系统内置 Windows OCR（最省内存，中文错字多）"),
            new EngineChoice(OcrEngineKind.Disabled, "关闭文字识别（只记录窗口标题）"),
        ]);
        _ocrEngine.SelectedIndexChanged += (_, _) =>
        {
            UpdateEnabledState();
            UpdateEngineHint();
        };
        AddRow(grid, "OCR 引擎", _ocrEngine);

        _paddleModel = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 420,
        };
        _paddleModel.Items.AddRange(
        [
            new PaddleModelChoice(PaddleModelKind.V6Tiny, "PP-OCRv6 tiny（推荐）"),
            new PaddleModelChoice(PaddleModelKind.V6Small, "PP-OCRv6 small（精度相当，耗时约为 tiny 的 3 倍）"),
            new PaddleModelChoice(PaddleModelKind.V5Mobile, "PP-OCRv5 mobile（更慢，精度低于 v6）"),
        ]);
        _paddleModel.SelectedIndexChanged += (_, _) => UpdateEngineHint();
        AddRow(grid, "PaddleOCR 模型", _paddleModel);

        var optionsRow = NewRow();
        _paddleMkldnn = new CheckBox
        {
            Text = "启用 MKLDNN 加速（速度提高约 3 至 4 倍，内存占用由约 145MB 增至约 640MB）",
            AutoSize = true,
            Margin = new Padding(3, 6, 12, 0),
        };
        optionsRow.Controls.Add(_paddleMkldnn);

        optionsRow.Controls.Add(new Label { Text = "CPU 线程", AutoSize = true, Margin = new Padding(0, 9, 4, 0) });
        _paddleThreads = new NumericUpDown { Minimum = 1, Maximum = 32, Width = 60 };
        optionsRow.Controls.Add(_paddleThreads);
        AddRow(grid, "识别选项", optionsRow);

        var modeRow = NewRow();
        _ocrMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        _ocrMode.Items.AddRange(
        [
            new OcrModeChoice(OcrRunMode.Realtime, "实时识别（抓取后立即识别）"),
            new OcrModeChoice(OcrRunMode.ScheduledBatch, "每日定时批量识别（降低日常占用）"),
        ]);
        _ocrMode.SelectedIndexChanged += (_, _) =>
        {
            UpdateEnabledState();
            UpdateEngineHint();
        };
        modeRow.Controls.Add(_ocrMode);

        modeRow.Controls.Add(new Label { Text = "时间", AutoSize = true, Margin = new Padding(12, 9, 4, 0) });
        _ocrBatchTime = new TextBox { Width = 70, PlaceholderText = "HH:mm" };
        modeRow.Controls.Add(_ocrBatchTime);
        AddRow(grid, "识别方式", modeRow);

        var testRow = NewRow();
        _testOcr = new Button { Text = "测试识别", Width = 100, Height = 28, Margin = new Padding(0, 3, 8, 0) };
        _testOcr.Click += async (_, _) => await TestOcrAsync();
        testRow.Controls.Add(_testOcr);
        testRow.Controls.Add(new Label
        {
            Text = "抓取当前窗口执行一次完整识别，用于验证引擎与模型是否可用",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 9, 0, 0),
        });
        AddRow(grid, "识别测试", testRow);

        _engineHint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 4, 0, 6),
        };
        AddRow(grid, string.Empty, _engineHint);

        root.Controls.Add(grid);
        return root;
    }

    protected override void LoadFromOptions()
    {
        var ocr = Options.Ocr;

        SelectByKind(_ocrEngine, ocr.Engine);
        SelectByKind(_paddleModel, ocr.PaddleModel);
        _paddleMkldnn.Checked = ocr.PaddleEnableMkldnn;
        _paddleThreads.Value = Math.Clamp(ocr.PaddleThreads, 1, 32);
        SelectByKind(_ocrMode, ocr.Mode);
        _ocrBatchTime.Text = ocr.BatchTimeOfDay;

        UpdateEnabledState();
        UpdateEngineHint();
    }

    protected override void WriteToOptions()
    {
        var ocr = Options.Ocr;

        ocr.Engine = SelectedKind(_ocrEngine, OcrEngineKind.PaddleOcr);
        ocr.PaddleModel = SelectedKind(_paddleModel, PaddleModelKind.V6Tiny);
        ocr.PaddleEnableMkldnn = _paddleMkldnn.Checked;
        ocr.PaddleThreads = (int)_paddleThreads.Value;
        ocr.Mode = SelectedKind(_ocrMode, OcrRunMode.Realtime);
        ocr.BatchTimeOfDay = _ocrBatchTime.Text.Trim();
    }

    private void UpdateEnabledState()
    {
        var paddle = SelectedKind(_ocrEngine, OcrEngineKind.PaddleOcr) == OcrEngineKind.PaddleOcr;
        _paddleModel.Enabled = paddle;
        _paddleMkldnn.Enabled = paddle;
        _paddleThreads.Enabled = paddle;

        var batch = SelectedKind(_ocrMode, OcrRunMode.Realtime) == OcrRunMode.ScheduledBatch;
        _ocrBatchTime.Enabled = batch;
    }

    private void UpdateEngineHint()
    {
        var kind = SelectedKind(_ocrEngine, OcrEngineKind.PaddleOcr);

        switch (kind)
        {
            case OcrEngineKind.Disabled:
                _engineHint.Text = "关闭后仅记录时间、进程名与窗口标题，不进行文字识别。";
                return;

            case OcrEngineKind.PaddleOcr:
                // 刻意不在这里加载 PaddleOCR：一次加载要 400ms 和 100MB，
                // 说明文字不该付这个代价。这里只讲清楚配置和代价。
                var model = SelectedKind(_paddleModel, PaddleModelKind.V6Tiny) switch
                {
                    PaddleModelKind.V6Small => "PP-OCRv6 small",
                    PaddleModelKind.V5Mobile => "PP-OCRv5 mobile",
                    _ => "PP-OCRv6 tiny",
                };
                _engineHint.Text = $"{model}：首次识别时加载（约 400ms、100MB），之后常驻内存，全程离线运行。";
                return;

            default:
                try
                {
                    // 系统 OCR 的探测很便宜（只是问一下系统语言包），可以直接报可用性。
                    using var probe = new WindowsMediaOcrEngine();
                    _engineHint.Text = probe.IsAvailable
                        ? $"引擎可用：{probe.Description}"
                        : $"引擎不可用：{probe.Description}";
                }
                catch (Exception ex)
                {
                    _engineHint.Text = $"检测引擎失败：{ex.Message}";
                }

                return;
        }
    }

    /// <summary>
    /// 抓一次窗口走一遍真实识别链路。前台窗口拿不到时退回面积最大的可见窗口，
    /// 这样在锁屏 / 无人值守时也能验证引擎是否可用。
    /// </summary>
    private async Task TestOcrAsync()
    {
        var original = _testOcr.Text;
        _testOcr.Enabled = false;
        _testOcr.Text = "识别中…";
        _engineHint.ForeColor = SystemColors.GrayText;
        _engineHint.Text = "正在抓取并识别…";

        try
        {
            WriteToOptions();

            var (snapshot, isFallback) = Interop.WindowEnumerator.FindCaptureTarget();
            if (!snapshot.IsUsable)
            {
                _engineHint.ForeColor = Color.OrangeRed;
                _engineHint.Text = "未找到可抓取的窗口，请先将目标窗口置于最前。";
                return;
            }

            using var capture = SnapLog.Capture.WindowCapturer.Capture(snapshot.Handle, Options.Capture.MaxImageDimension);
            if (!capture.Success || capture.Image is null)
            {
                _engineHint.ForeColor = Color.OrangeRed;
                _engineHint.Text = $"抓取失败：{capture.Error}";
                return;
            }

            using var engine = OcrEngineFactory.Create(Options.Ocr, Log);
            var outcome = await engine.RecognizeAsync(capture.Image, CancellationToken.None);

            if (!outcome.Success)
            {
                _engineHint.ForeColor = Color.OrangeRed;
                _engineHint.Text = $"识别失败：{outcome.Error}";
                return;
            }

            _engineHint.ForeColor = Color.SeaGreen;
            _engineHint.Text = $"识别成功：{engine.Description}"
                               + (isFallback ? "（无前台窗口，已改用最大可见窗口）" : string.Empty);

            using var preview = new OcrPreviewForm(snapshot, capture.Method, capture.Image, outcome);
            preview.ShowDialog(FindForm());
        }
        catch (Exception ex)
        {
            _engineHint.ForeColor = Color.OrangeRed;
            _engineHint.Text = $"测试失败：{ex.GetType().Name}: {ex.Message}";
            Log.Error("OCR 识别测试失败", ex);
        }
        finally
        {
            _testOcr.Enabled = true;
            _testOcr.Text = original;
        }
    }
}
