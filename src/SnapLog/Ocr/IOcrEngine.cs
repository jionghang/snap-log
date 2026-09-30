namespace SnapLog.Ocr;

public sealed record OcrOutcome(
    bool Success,
    string Text,
    string LanguageTag,
    int LineCount,
    TimeSpan Elapsed,
    string? Error,
    string? Warning = null)
{
    public static OcrOutcome Failed(string error) => new(false, string.Empty, string.Empty, 0, TimeSpan.Zero, error);

    public static OcrOutcome Skipped() => new(true, string.Empty, string.Empty, 0, TimeSpan.Zero, null);
}

public interface IOcrEngine : IDisposable
{
    /// <summary>引擎是否可用；不可用时 <see cref="Description"/> 里有原因。</summary>
    bool IsAvailable { get; }

    /// <summary>给界面和诊断用的说明，例如"Windows OCR (zh-Hans-CN)"。</summary>
    string Description { get; }

    Task<OcrOutcome> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken);
}
