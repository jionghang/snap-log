using SnapLog.Configuration;
using SnapLog.Diagnostics;

namespace SnapLog.Ocr;

/// <summary>
/// OCR 引擎的唯一构造入口。以前是在 Program/CliRunner/MainForm 里各写一份
/// <c>new WindowsMediaOcrEngine(...)</c>，加第二个引擎时这种散落就没法维护了。
/// </summary>
public static class OcrEngineFactory
{
    public static IOcrEngine Create(OcrOptions options, FileLogger log) => options.Engine switch
    {
        OcrEngineKind.PaddleOcr => new PaddleOcrEngine(options, log),
        OcrEngineKind.Disabled => new DisabledOcrEngine(),
        _ => new WindowsMediaOcrEngine(options.TargetLongestSide, options.MaxUpscale),
    };
}

/// <summary>
/// 关闭识别时用的空实现。存在的意义是让"关闭 OCR"这条路也走工厂，
/// 而不是让调用方去构造一个用不上的真引擎。
/// </summary>
internal sealed class DisabledOcrEngine : IOcrEngine
{
    public bool IsAvailable => true;

    public string Description => "已关闭（只记录时间戳和窗口标题）";

    public Task<OcrOutcome> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken) =>
        Task.FromResult(OcrOutcome.Skipped());

    public void Dispose()
    {
    }
}
