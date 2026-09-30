using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using PaddleOCRSharp;
using SnapLog.Configuration;
using SnapLog.Diagnostics;

namespace SnapLog.Ocr;

/// <summary>
/// 离线 PaddleOCR（PP-OCR 系列）引擎。
///
/// 三个必须知道的实现约束，都来自实测：
///
/// 1. <b>免费社区版限制单次调用检测到的文本框数必须小于 100</b>。
///    密集界面（网页、IDE）整屏轻松超过，会直接抛
///    "The free community edition only support box sizes &lt;100"。
///    所以这里做自适应分块：先整图试一次，撞到该限制就拆成 2x2 递归重试，
///    最多拆到 4x4，并把每块的结果按坐标合并回正确的阅读顺序。
///
/// 2. <b>原生库不是线程安全的</b>，用信号量把所有识别串行化。
///
/// 3. <b>原生内存释放不彻底</b>。实测（MKLDNN 关闭）：加载后约 98MB，识别后约 130MB，
///    Dispose 后回落到约 92MB —— 真正释放的是推理用的那部分，
///    原生 DLL 已映射的约 90MB 要等进程退出才会还。
///    所以这里只做"首次使用时延迟加载"，不做"空闲卸载"：
///    实测空闲卸载只能省约 38MB，却要在下次识别时付出约 400ms 的重新加载代价，
///    不划算。要彻底回收只能把 OCR 放到独立进程里（未实现）。
///
/// 模型说明：随包发布的是中文模型（PP-OCRv5 mobile 与 PP-OCRv6 tiny/small）。
/// 实测 PP-OCRv6 tiny 命中率最高（22/24）且最快，是默认选择。
/// 这个工具只服务中文场景，所以语言不做成配置项，也不开放自定义模型目录；
/// 要挂别的模型就在 <see cref="PaddleModelKind"/> 加一个枚举值。
/// </summary>
public sealed class PaddleOcrEngine : IOcrEngine
{
    /// <summary>免费版单次调用的文本框上限。留一点余量，不要顶到 100。</summary>
    private const int BoxCountLimit = 100;

    /// <summary>最大分块深度：2 表示最细拆到 4x4 = 16 块，避免病态输入把耗时拉爆。</summary>
    private const int MaxSplitDepth = 2;

    /// <summary>纵向差小于这个像素数的文本块视为同一行（用于跨块合并后恢复阅读顺序）。</summary>
    private const double SameLineTolerance = 12.0;

    private readonly OcrOptions _options;
    private readonly FileLogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PaddleOCREngine? _engine;
    private string? _initializationError;
    private bool _disposed;

    public PaddleOcrEngine(OcrOptions options, FileLogger log)
    {
        _options = options;
        _log = log;
    }

    /// <summary>
    /// 是否可用。**这个属性会真的把原生引擎加载起来**（约 400ms、约 100MB），
    /// 所以只给 --diagnose 这类明确的可用性检查用；界面显示状态请用
    /// <see cref="Description"/>，它不会触发加载。
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                // 加载过程也会打 banner，同样要静音。
                using var _ = PaddleNativeOutputSilencer.Enter();
                return EnsureEngine() is not null;
            }
            catch (Exception ex)
            {
                _log.Warn($"PaddleOCR 不可用：{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 配置与加载状态的说明，**不触发原生加载**。
    /// 界面每次刷新状态都会读它，绝不能在这里初始化几百 MB 的原生库。
    /// </summary>
    public string Description
    {
        get
        {
            var model = DescribeModel();
            var mkldnn = _options.PaddleEnableMkldnn ? "MKLDNN 开" : "MKLDNN 关";

            if (_initializationError is not null)
            {
                return $"PaddleOCR 不可用：{_initializationError}";
            }

            return _engine is null
                ? $"PaddleOCR {model}（{mkldnn}，{_options.PaddleThreads} 线程）· 未加载"
                : $"PaddleOCR {model}（{mkldnn}，{_options.PaddleThreads} 线程）· 已加载";
        }
    }

    /// <summary>当前是否已经加载了原生引擎（延迟加载，没识别过就是 false）。</summary>
    public bool IsLoaded => _engine is not null;

    public async Task<OcrOutcome> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (bitmap.Width < 8 || bitmap.Height < 8)
        {
            return OcrOutcome.Failed($"图像尺寸过小 {bitmap.Width}x{bitmap.Height}");
        }

        // 原生库不支持并发调用，整体串行。
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 原生层会往 stdout 打 banner 和错误行，整个调用期间把 stdout 指向空设备。
            using var _ = PaddleNativeOutputSilencer.Enter();

            PaddleOCREngine engine;
            try
            {
                var loaded = EnsureEngine();
                if (loaded is null)
                {
                    return OcrOutcome.Failed(_initializationError ?? "PaddleOCR 引擎不可用");
                }

                engine = loaded;
            }
            catch (Exception ex)
            {
                return OcrOutcome.Failed($"初始化 PaddleOCR 失败：{ex.GetType().Name}: {ex.Message}");
            }

            var stopwatch = Stopwatch.StartNew();

            // 识别本身是同步的原生调用，丢到线程池上跑，别堵住 UI。
            var result = await Task
                .Run(() => RecognizeInternal(engine, bitmap, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            if (result.Lines.Count == 0 && result.Notes.Count > 0 && result.Tiles == 0)
            {
                return OcrOutcome.Failed(string.Join("；", result.Notes));
            }

            var text = TextNormalizer.ComposeFromLines(result.Lines);

            // notes 里的内容不是装饰：可能是"某个区域拆到最深仍超限、已跳过"，
            // 也就是真实的数据丢失。成功也要带出去，落进记录的“错误”列让用户看得见。
            var warning = result.Notes.Count > 0 ? string.Join("；", result.Notes) : null;

            if (warning is not null)
            {
                _log.Warn($"PaddleOCR 本次识别有需要留意的点：{warning}");
            }
            else
            {
                _log.Debug($"PaddleOCR 识别完成：{result.Lines.Count} 行，{text.Length} 字，"
                           + $"{stopwatch.ElapsedMilliseconds} ms，{result.Tiles} 块");
            }

            return new OcrOutcome(
                Success: true,
                Text: text,
                LanguageTag: "paddle-" + _options.PaddleModel,
                LineCount: result.Lines.Count,
                Elapsed: stopwatch.Elapsed,
                Error: null,
                Warning: warning);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OcrOutcome.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 整图优先，撞到免费版的文本框上限就递归分块。
    /// "块数"和"调用次数"分开统计：失败的整图尝试也算一次调用，但不算一块，
    /// 否则给用户的提示会说"已分 5 块"而实际只有 4 块。
    /// </summary>
    private RecognizeRun RecognizeInternal(
        PaddleOCREngine engine,
        Bitmap bitmap,
        CancellationToken cancellationToken)
    {
        var run = new RecognizeRun();

        run.Blocks = CollectBlocks(
            engine,
            bitmap,
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            depth: 0,
            run,
            cancellationToken);

        if (run.Blocks.Count == 0)
        {
            return run;
        }

        if (run.Tiles > 1)
        {
            run.Notes.Add($"内容超过免费版单次 {BoxCountLimit} 块上限，已拆分为 {run.Tiles} 块识别");
        }

        run.Lines = ToReadingOrder(run.Blocks);
        return run;
    }

    /// <summary>
    /// 识别一个矩形区域；如果因为文本框过多失败，就拆成 2x2 继续递归。
    /// 坐标统一换算成整图坐标，这样后续可以跨块按位置重排。
    /// </summary>
    private List<PositionedBlock> CollectBlocks(
        PaddleOCREngine engine,
        Bitmap source,
        Rectangle region,
        int depth,
        RecognizeRun run,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = new List<PositionedBlock>();
        Bitmap? tile = null;
        var isWholeImage = region.X == 0 && region.Y == 0 && region.Width == source.Width && region.Height == source.Height;

        try
        {
            tile = isWholeImage ? source : Crop(source, region);

            OCRResult? ocr;
            try
            {
                run.Calls++;
                ocr = engine.DetectText(tile);
                run.Tiles++;
            }
            catch (Exception ex) when (IsBoxCountLimitError(ex))
            {
                if (depth >= MaxSplitDepth)
                {
                    // 拆到上限还是超，只能放弃这一块，但不能让整次抓取失败。
                    run.Notes.Add($"部分区域拆分至 {depth} 层仍超出免费版单次上限，该区域文字已跳过");
                    return result;
                }

                foreach (var sub in SplitIntoQuads(region))
                {
                    result.AddRange(CollectBlocks(engine, source, sub, depth + 1, run, cancellationToken));
                }

                return result;
            }

            if (ocr?.TextBlocks is null)
            {
                return result;
            }

            foreach (var block in ocr.TextBlocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text))
                {
                    continue;
                }

                var (x, y) = TopLeft(block);
                result.Add(new PositionedBlock(x + region.X, y + region.Y, block.Text));
            }

            return result;
        }
        finally
        {
            if (!isWholeImage)
            {
                tile?.Dispose();
            }
        }
    }

    /// <summary>
    /// 把文本块按位置还原成阅读顺序（先上后下、同行内先左后右），
    /// 再把同一视觉行的块合并成一行。分块识别时跨块的行会因此重新拼在一起。
    /// </summary>
    private static List<string> ToReadingOrder(List<PositionedBlock> blocks)
    {
        var sorted = blocks
            .OrderBy(b => b.Y)
            .ThenBy(b => b.X)
            .ToList();

        var lines = new List<string>();
        var current = new System.Text.StringBuilder();
        var currentY = double.NaN;

        foreach (var block in sorted)
        {
            if (double.IsNaN(currentY) || Math.Abs(block.Y - currentY) > SameLineTolerance)
            {
                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                }

                current.Clear();
                currentY = block.Y;
            }

            current.Append(block.Text);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    /// <summary>取文本框左上角。拿不到坐标时退化为 (0,0)，只影响排序，不影响文字内容。</summary>
    private static (double X, double Y) TopLeft(TextBlock block)
    {
        if (block.BoxPoints is not { Count: > 0 })
        {
            return (0, 0);
        }

        var x = double.MaxValue;
        var y = double.MaxValue;

        foreach (var point in block.BoxPoints)
        {
            if (point.X < x)
            {
                x = point.X;
            }

            if (point.Y < y)
            {
                y = point.Y;
            }
        }

        return (x, y);
    }

    private static IEnumerable<Rectangle> SplitIntoQuads(Rectangle region)
    {
        // 用 w - w/2 处理奇数尺寸，保证不丢像素。
        var leftWidth = region.Width / 2;
        var topHeight = region.Height / 2;
        var rightWidth = region.Width - leftWidth;
        var bottomHeight = region.Height - topHeight;

        if (leftWidth < 8 || topHeight < 8 || rightWidth < 8 || bottomHeight < 8)
        {
            yield break;
        }

        yield return new Rectangle(region.X, region.Y, leftWidth, topHeight);
        yield return new Rectangle(region.X + leftWidth, region.Y, rightWidth, topHeight);
        yield return new Rectangle(region.X, region.Y + topHeight, leftWidth, bottomHeight);
        yield return new Rectangle(region.X + leftWidth, region.Y + topHeight, rightWidth, bottomHeight);
    }

    private static Bitmap Crop(Bitmap source, Rectangle region)
    {
        var target = new Bitmap(region.Width, region.Height, source.PixelFormat);
        using var graphics = Graphics.FromImage(target);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(source, new Rectangle(0, 0, region.Width, region.Height), region, GraphicsUnit.Pixel);
        return target;
    }

    /// <summary>免费社区版的文本框上限错误。只能靠消息文本识别——原生层没有给出错误码。</summary>
    private static bool IsBoxCountLimitError(Exception ex) =>
        ex.Message.Contains("only support box sizes", StringComparison.OrdinalIgnoreCase);

    private PaddleOCREngine? EnsureEngine()
    {
        if (_engine is not null || _initializationError is not null)
        {
            return _engine;
        }

        try
        {
            var modelConfig = BuildModelConfig(_options);

            _engine = new PaddleOCREngine(modelConfig, new OCRParameter
            {
                cpu_math_library_num_threads = Math.Clamp(_options.PaddleThreads, 1, 32),
                enable_mkldnn = _options.PaddleEnableMkldnn,
                max_side_len = Math.Clamp(_options.PaddleMaxSideLength, 320, 8192),
                det = true,
                rec = true,
                // 方向分类模型对我们的场景没用（截图里的文字基本是正向的），关掉省一遍推理。
                cls = false,
                use_angle_cls = false,
            });

            _log.Info($"PaddleOCR 引擎已加载（{DescribeModel()}，延迟加载，首次识别时才初始化）");
        }
        catch (Exception ex)
        {
            _initializationError = $"{ex.GetType().Name}: {ex.Message}";
            _log.Error("加载 PaddleOCR 失败", ex);
        }

        return _engine;
    }

    private static OCRModelConfig BuildModelConfig(OcrOptions options) => options.PaddleModel switch
    {
        PaddleModelKind.V6Small => OCRModelConfig.V6_Small,
        PaddleModelKind.V5Mobile => OCRModelConfig.V5_CN,
        _ => OCRModelConfig.V6_Tiny,
    };

    private string DescribeModel() => _options.PaddleModel switch
    {
        PaddleModelKind.V6Small => "PP-OCRv6 small",
        PaddleModelKind.V5Mobile => "PP-OCRv5 mobile",
        _ => "PP-OCRv6 tiny",
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 原生引擎不支持并发，等正在跑的识别结束后再释放。
        _gate.Wait();
        try
        {
            _engine?.Dispose();
            _engine = null;
        }
        catch (Exception ex)
        {
            _log.Warn($"释放 PaddleOCR 引擎时出错：{ex.Message}");
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private sealed record PositionedBlock(double X, double Y, string Text);

    /// <summary>一次识别过程的临时状态。“块”= 成功返回结果的调用，“调用”含失败重试。</summary>
    private sealed class RecognizeRun
    {
        public int Calls { get; set; }

        public int Tiles { get; set; }

        public List<PositionedBlock> Blocks { get; set; } = [];

        public List<string> Lines { get; set; } = [];

        public List<string> Notes { get; } = [];
    }
}
