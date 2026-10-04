using SnapLog.Configuration;

namespace SnapLog.Core;

/// <summary>
/// 把飞书表的真实列名和我们的总结字段对上号。
///
/// 存在的意义：让用户"确认"而不是"从零填"。手打列名错一个空格就是 1254045，
/// 而这种错在写入之前完全看不出来。
/// 纯函数：不联网、不碰配置，列名由调用方（飞书"列出字段"接口）给。
/// </summary>
public static class FeishuFieldMatcher
{
    /// <summary>每个总结字段可能对应的飞书列名关键词，按优先级排。</summary>
    private static readonly (string Field, string[] Keywords)[] Rules =
    [
        (nameof(Storage.SummaryRun.StartedAt), ["生成时间", "总结时间", "时间", "日期"]),
        (nameof(Storage.SummaryRun.CoveredDay), ["覆盖", "归属", "日报日期"]),
        (nameof(Storage.SummaryRun.Trigger), ["触发", "来源", "方式"]),
        (nameof(Storage.SummaryRun.Provider), ["模型", "大模型"]),
        (nameof(Storage.SummaryRun.RecordCount), ["记录条数", "条数", "记录数"]),
        (nameof(Storage.SummaryRun.ImageCount), ["截图", "图片", "张数"]),
        (nameof(Storage.SummaryRun.ElapsedMilliseconds), ["耗时", "用时"]),
        (nameof(Storage.SummaryRun.Attempts), ["尝试", "次数"]),
        (nameof(Storage.SummaryRun.Markdown), ["总结正文", "工作内容", "日报内容", "正文", "总结", "内容"]),
        (nameof(Storage.SummaryRun.Preview), ["摘要", "预览"]),
        (nameof(Storage.SummaryRun.SavedPath), ["路径", "文件"]),
        (nameof(Storage.SummaryRun.Message), ["结果", "说明", "备注"]),
    ];

    /// <summary>
    /// 按列名重新匹配一份映射表。规则：
    ///   1. 用户原来指定的列如果还在表里 → 原样保留（尊重手工改动）；
    ///   2. 否则按关键词找第一个还没被占用的列；
    ///   3. 都没找到 → 该条留空（写入时跳过，不报错）。
    /// </summary>
    public static List<FeishuFieldMapping> Match(
        IReadOnlyList<FeishuBitablePublisher.FeishuTableField> fields,
        IReadOnlyList<FeishuFieldMapping> current)
    {
        var available = fields
            .Select(field => field.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FeishuFieldMapping>();

        foreach (var (field, keywords) in Rules)
        {
            var mapping = new FeishuFieldMapping { RecordField = field };

            // 1. 原来指定的列还在 → 保留
            var previous = current.FirstOrDefault(m =>
                string.Equals(m.RecordField, field, StringComparison.Ordinal));

            if (previous is not null
                && previous.FeishuField.Length > 0
                && available.Any(name => string.Equals(name, previous.FeishuField, StringComparison.Ordinal)))
            {
                mapping.FeishuField = previous.FeishuField;
                used.Add(previous.FeishuField);
                result.Add(mapping);
                continue;
            }

            // 2. 关键词命中第一个没被占用的列
            foreach (var keyword in keywords)
            {
                var hit = available.FirstOrDefault(name =>
                    !used.Contains(name)
                    && name.Contains(keyword, StringComparison.OrdinalIgnoreCase));

                if (hit is not null)
                {
                    mapping.FeishuField = hit;
                    used.Add(hit);
                    break;
                }
            }

            result.Add(mapping);
        }

        return result;
    }
}
