using SnapLog.Storage;

namespace SnapLog.Core;

/// <summary>某一天为什么需要生成总结。</summary>
public enum SummaryPlanReason
{
    /// <summary>这一天还没有生成过总结。</summary>
    Missing,

    /// <summary>已经生成过，但之后这一天又有了新的记录，需要重新生成并覆盖。</summary>
    Updated,
}

/// <summary>一天要生成的总结。</summary>
public sealed record SummaryPlanItem(DateOnly Day, SummaryPlanReason Reason, int RecordCount);

/// <summary>
/// 定时生成的"按天"规划：决定这一轮该生成哪几天的总结。
///
/// 两条规则（用户给定）：
///   1. 之前没生成过的天 → 生成；
///   2. 之前生成过、但那天后来又有了新记录 → 重新生成，覆盖旧的那份。
///
/// 纯函数，不碰数据库也不调模型，便于单独验证。
/// </summary>
public static class SummaryPlanner
{
    /// <summary>往前看几天。更早的历史不再补生成——定时任务应该保持"每天一小步"，而不是某天突然补几十天。</summary>
    public const int LookbackDays = 7;

    /// <summary>一次最多生成几天，避免积压很多天时一次跑很久、也避免一次花掉太多模型调用。</summary>
    public const int MaxDaysPerRun = 3;

    /// <summary>
    /// 规划这一轮要生成哪些天。
    /// </summary>
    /// <param name="today">今天（本地日期）。只处理今天之前的日子——今天还没过完。</param>
    /// <param name="marks">各天的记录统计（条数与当天最大记录 id）。</param>
    /// <param name="existingRuns">已有的总结历史（只用到 CoveredDay / CoveredMarks / Success）。</param>
    public static IReadOnlyList<SummaryPlanItem> Plan(
        DateOnly today,
        IReadOnlyList<DayMark> marks,
        IReadOnlyList<SummaryRun> existingRuns)
    {
        var earliest = today.AddDays(-LookbackDays);
        var yesterday = today.AddDays(-1);

        // 每一天已生成总结时的数据指纹：取该天最近一次"成功且按天生成"的记录。
        var covered = existingRuns
            .Where(run => run.Success && run.CoveredDay.Length > 0)
            .GroupBy(run => run.CoveredDay, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(run => run.Id).First().CoveredMarks,
                StringComparer.Ordinal);

        var plan = new List<SummaryPlanItem>();

        foreach (var mark in marks.OrderByDescending(m => m.Day))
        {
            var day = DateOnly.FromDateTime(mark.Day);

            if (day > yesterday || day < earliest || mark.Count == 0)
            {
                continue;
            }

            var key = day.ToString("yyyy-MM-dd");

            if (!covered.TryGetValue(key, out var marksAtGeneration))
            {
                plan.Add(new SummaryPlanItem(day, SummaryPlanReason.Missing, mark.Count));
            }
            else if (mark.MaxId > marksAtGeneration)
            {
                // 生成之后这一天又多了记录：重新生成，覆盖旧的那份。
                plan.Add(new SummaryPlanItem(day, SummaryPlanReason.Updated, mark.Count));
            }
        }

        return [.. plan.Take(MaxDaysPerRun)];
    }

    /// <summary>把规划结果说成人话，用于日志与托盘提示。</summary>
    public static string Describe(IReadOnlyList<SummaryPlanItem> plan) =>
        plan.Count == 0
            ? "没有需要生成的天"
            : string.Join("、", plan.Select(item =>
                $"{item.Day:MM-dd}（{(item.Reason == SummaryPlanReason.Missing ? "新增" : "有更新，覆盖")}，{item.RecordCount} 条）"));
}
