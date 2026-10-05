using SnapLog.Configuration;

namespace SnapLog.Summarization;

/// <summary>要随请求一起发出去的一张截图。</summary>
public sealed record SummaryImage(
    string Path,
    DateTime Timestamp,
    string ProcessName,
    string WindowTitle,
    long SizeBytes);

/// <summary>
/// 一次总结请求。<see cref="Images"/> 为空就是纯文字请求。
/// 是不是要发文字、发图，由 <see cref="SummaryRunner"/> 按配置决定后组装成这个对象，
/// 总结器本身只负责"把给它的东西发出去"，不关心策略。
/// </summary>
public sealed record SummaryRequest(
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyList<SummaryImage> Images,
    LlmImageDetail ImageDetail)
{
    public bool HasImages => Images.Count > 0;

    public long TotalImageBytes => Images.Sum(image => image.SizeBytes);

    /// <summary>这次请求对应的发送模式。总结器用它区分"图片可选"与"只发图"：
    /// 后者遇到不收图的模型不能降级成纯文字（纯文字版没有正文可发）。</summary>
    public LlmPayloadMode Mode { get; init; } = LlmPayloadMode.TextOnly;
}

/// <summary>一次成功的总结，带上是谁完成的、试了几次。</summary>
public sealed record SummaryCompletion(string Text, string ProviderDescription, int Attempts);

/// <summary>每个模型配置轮到自己时的尝试记录，失败时用来告诉用户到底卡在哪。</summary>
public sealed record SummaryAttempt(string Provider, int Attempt, bool Transient, string Error);

public sealed class SummaryFailedException(string message, IReadOnlyList<SummaryAttempt> attempts)
    : Exception(message)
{
    public IReadOnlyList<SummaryAttempt> Attempts { get; } = attempts;

    /// <summary>按模型分组的失败原因，给界面和日志用。</summary>
    public string DescribeAttempts() =>
        Attempts.Count == 0
            ? "(没有可用的模型配置)"
            : string.Join("\n", Attempts.Select(a => $"  · {a.Provider} 第 {a.Attempt} 次：{a.Error}"));
}