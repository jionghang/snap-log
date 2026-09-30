namespace SnapLog.Summarization;

public interface ISummarizer
{
    /// <summary>给界面和日志用的说明，例如"主模型 · gpt-4o-mini @ api.openai.com（共 2 个模型）"。</summary>
    string Description { get; }

    /// <summary>
    /// 发送请求。实现负责顺序尝试多个模型配置、按策略重试，
    /// 全部失败时抛 <see cref="SummaryFailedException"/>（里面带着每个模型的失败原因）。
    /// </summary>
    Task<SummaryCompletion> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken);
}
