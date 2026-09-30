namespace SnapLog.Summarization;

/// <summary>
/// 认出周期性汇报类文档（日报、周报、月报、年度总结、述职等）。
///
/// 这类文档的正文写的是一整周甚至一整月的事，和本次总结覆盖的时间段对不上；
/// 直接采信会把更早做过的事情算成本时段的产出。所以摘要里给它打一个标记，
/// 由提示词里的规则告诉模型怎么区别对待——正文照样发送，只是解释方式不同。
/// </summary>
internal static class ReportDocuments
{
    /// <summary>打在摘要条目上的标记。提示词里的规则引用同一个字符串，避免两边写岔。</summary>
    public const string Marker = "周期性汇报文档：正文可能覆盖更早时间";

    private static readonly string[] Keywords =
    [
        "日报", "周报", "月报", "季报", "年报",
        "周总结", "月总结", "季度总结", "年度总结", "半年总结", "工作总结",
        "述职", "工作汇报", "汇报材料",
        "daily report", "weekly report", "monthly report",
    ];

    /// <summary>窗口标题里带汇报类字眼就算命中。拿不准的时候宁可标上，标记本身不影响事实。</summary>
    public static bool IsPeriodicReport(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        foreach (var keyword in Keywords)
        {
            if (windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
