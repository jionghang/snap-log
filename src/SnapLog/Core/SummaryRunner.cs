using System.Text;
using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Storage;
using SnapLog.Summarization;

namespace SnapLog.Core;

/// <summary>送模型之前准备好的全部内容，界面可以拿它做"发送前预览"。</summary>
public sealed record SummaryPreparation(
    ActivityDigest Digest,
    SummaryRequest Request,
    string ProviderDescription,
    LlmPayloadMode PayloadMode,
    LlmImageDetail ImageDetail)
{
    public int TextCharacters => Request.SystemPrompt.Length + Request.UserPrompt.Length;

    public int ImageCount => Request.Images.Count;

    public long ImageBytes => Request.TotalImageBytes;

    /// <summary>
    /// 给用户看的预览。刻意**不**包含图片的 base64 内容（那会是一屏乱码），
    /// 只列出文件名、大小和时间点，让用户确认"要发哪些图"。
    /// </summary>
    public string RenderForDisplay()
    {
        var builder = new StringBuilder();
        builder.AppendLine("=== 将发送到的模型 ===");
        builder.AppendLine(ProviderDescription);
        var textLabel = PayloadMode == LlmPayloadMode.ImageOnly
            ? $"合计 {TextCharacters} 字符（只有提示词和图片清单，不含 OCR 文字）"
            : $"合计 {TextCharacters} 字符文字";

        builder.AppendLine($"发送内容：{DescribeMode(PayloadMode)}　{textLabel}"
                           + (ImageCount > 0 ? $" + {ImageCount} 张图（{FormatBytes(ImageBytes)}）" : string.Empty));
        builder.AppendLine();

        if (ImageCount > 0)
        {
            builder.AppendLine("=== 将附带的截图（图片本身不在此处显示）===");
            for (var i = 0; i < Request.Images.Count; i++)
            {
                var image = Request.Images[i];
                builder.AppendLine($"  图{i + 1}. {image.Timestamp:MM-dd HH:mm:ss} | {image.ProcessName} | {image.WindowTitle}");
                builder.AppendLine($"        {image.Path}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("=== system 提示词 ===");
        builder.AppendLine(Request.SystemPrompt);
        builder.AppendLine();
        builder.AppendLine("=== user 提示词 ===");
        builder.AppendLine(Request.UserPrompt);
        return builder.ToString();
    }

    public static string DescribeMode(LlmPayloadMode mode) => mode switch
    {
        LlmPayloadMode.ImageOnly => "仅发送截图",
        LlmPayloadMode.TextAndImage => "文字与截图",
        _ => "仅发送识别文字",
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}

public sealed record SummaryRunResult(
    bool Success,
    string? Markdown,
    string? SavedPath,
    string Message,
    SummaryPreparation? Preparation = null,
    int Attempts = 1);

/// <summary>
/// 总结流程编排：读记录 → 按配置挑截图 → 组装请求 → 调模型（多模型回退 + 重试）→ 存 Markdown。
/// 拆出 <see cref="PrepareAsync"/> 是为了让界面能先把"要发什么"展示给用户看，
/// 尤其是带图的模式下，用户必须先知道会发出去哪几张截图。
/// </summary>
public sealed class SummaryRunner
{
    private readonly IActivityRepository _store;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;

    public SummaryRunner(IActivityRepository store, AppPaths paths, FileLogger log)
    {
        _store = store;
        _paths = paths;
        _log = log;
    }

    /// <summary>
    /// 准备要发送的内容。<paramref name="day"/> 指定时只取那一天的记录，为空则取最近的记录。
    /// </summary>
    public async Task<(SummaryPreparation? Preparation, string? Error)> PrepareAsync(
        AppOptions options,
        DateOnly? day,
        CancellationToken cancellationToken)
    {
        var settings = options.Summarization;

        var records = day is { } targetDay
            ? await _store
                .GetByDayAsync(targetDay.ToDateTime(TimeOnly.MinValue), settings.MaxRecords, cancellationToken)
                .ConfigureAwait(false)
            : await _store
                .GetRecentAsync(settings.MaxRecords, cancellationToken)
                .ConfigureAwait(false);

        if (records.Count == 0)
        {
            return (null, day is { } emptyDay
                ? $"{emptyDay:yyyy-MM-dd} 没有记录可以总结。"
                : "暂无可用于总结的记录。请先运行一段时间，或点“立即抓取一次”。");
        }

        // 预览不要求密钥就绪：用户正是要先看清"会发什么"才决定配不配密钥，
        // 这里只报告当前有哪些模型、哪些缺密钥，不阻止预览。
        {
            var images = settings.PayloadMode == LlmPayloadMode.TextOnly
                ? []
                : SummaryImageSelector.Select(records, settings, _paths, _log);

            if (settings.PayloadMode != LlmPayloadMode.TextOnly && images.Count == 0)
            {
                return (null,
                    "当前配置为发送截图，但所选记录中没有可用的截图文件。"
                    + "请确认已开启“保存截图文件”，或将“发送内容”改回“仅发送识别文字”。");
            }

            // 只发图时，文字部分只留"时间范围"这种骨架，不把 OCR 内容发出去（否则就不叫只发图了）。
            var digest = settings.PayloadMode == LlmPayloadMode.ImageOnly
                ? BuildImageOnlyDigest(records)
                : ActivityDigestBuilder.Build(records, settings);

            if (settings.PayloadMode != LlmPayloadMode.ImageOnly && digest.IsEmpty)
            {
                return (null, $"读取到 {records.Count} 条记录，但其中没有可用的文字内容（可能过短或未通过最小长度判定）。");
            }

            var request = new SummaryRequest(
                Prompts.BuildSystemPrompt(settings),
                string.Empty,
                images,
                settings.ImageDetail);

            request = request with { UserPrompt = Prompts.BuildUserPrompt(digest, request) };

            return (new SummaryPreparation(
                digest, request, DescribeProviders(settings), settings.PayloadMode, settings.ImageDetail), null);
        }
    }

    /// <summary>
    /// 给预览用的模型清单描述：列出启用中的模型，并标出哪些拿不到密钥。
    /// 不做网络请求，也不需要客户端能构造成功。
    /// </summary>
    private static string DescribeProviders(SummarizationOptions settings)
    {
        var enabled = settings.Providers.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return "（模型列表中没有启用的模型，生成将直接失败）";
        }

        var lines = new List<string> { $"共 {enabled.Count} 个模型，按顺序调用：" };

        foreach (var provider in enabled)
        {
            var variableName = string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable)
                ? "SNAPLOG_OPENAI_API_KEY"
                : provider.ApiKeyEnvironmentVariable;

            var hasKey = !string.IsNullOrWhiteSpace(provider.ApiKey)
                         || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName));

            lines.Add($"  · {provider.Name} · {provider.Model} @ {provider.Endpoint}"
                      + (hasKey ? string.Empty : $"（缺少密钥：请填写 API Key 或设置环境变量 {variableName}）"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 只发图模式下的"摘要"：只保留时间范围和窗口清单骨架，不带 OCR 文字。
    /// 复用 ActivityDigest 是为了让时间范围/条数的展示继续工作。
    /// </summary>
    private static ActivityDigest BuildImageOnlyDigest(IReadOnlyList<ActivityRecord> records)
    {
        var withText = records.Count(r => !string.IsNullOrWhiteSpace(r.OcrText));
        return new ActivityDigest(
            Text: string.Empty,
            IncludedRecords: withText,
            AvailableRecords: records.Count,
            FirstTimestamp: records.Count > 0 ? records[0].Timestamp : null,
            LastTimestamp: records.Count > 0 ? records[^1].Timestamp : null,
            Truncated: false);
    }

    /// <summary>
    /// 生成总结。每次调用都会往“总结历史”里记一条（成功和失败都记），
    /// 这样定时任务半夜失败了第二天也查得到原因。
    /// </summary>
    /// <param name="trigger">触发来源，会显示在总结历史里：手动 / 定时 / 命令行。</param>
    /// <param name="day">只总结这一天的记录；为空表示总结最近的记录。</param>
    public async Task<SummaryRunResult> RunAsync(
        AppOptions options,
        string trigger,
        DateOnly? day,
        CancellationToken cancellationToken)
    {
        var settings = options.Summarization;
        var startedAt = DateTime.Now;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await RunCoreAsync(options, day, cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();

        // 连"因为没启用/没确认隐私而没跑"也记进去：否则定时任务静默不动，用户没法排查。
        await RecordAsync(result, trigger, startedAt, stopwatch.ElapsedMilliseconds, cancellationToken)
            .ConfigureAwait(false);

        // 记完历史才写飞书：这段时间刚好把这条总结落到库里，推送那边是按库里的待写入清单走的。
        if (result.Success)
        {
            await PushToFeishuAsync(options, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// 按配置把刚生成的总结写进飞书。推送失败不影响总结本身——正文已经落盘也落库了，
    /// 这里只记日志，剩下的交给定时写入或用户手动补一次。
    /// </summary>
    private async Task PushToFeishuAsync(AppOptions options, CancellationToken cancellationToken)
    {
        if (!options.Feishu.Enabled || !options.Feishu.PushAfterSummary)
        {
            return;
        }

        try
        {
            var writer = new FeishuWriter(_store, _log);
            var result = await writer.WritePendingAsync(options, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                _log.Info($"已按配置写入飞书：{result.Message}");
            }
            else
            {
                _log.Warn($"写入飞书未成功（总结本身已保存）：{result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"写入飞书失败（总结本身已保存）：{ex.Message}");
        }
    }

    private async Task<SummaryRunResult> RunCoreAsync(
        AppOptions options,
        DateOnly? day,
        CancellationToken cancellationToken)
    {
        var settings = options.Summarization;

        if (!settings.Enabled)
        {
            return new SummaryRunResult(false, null, null, "大模型总结当前为关闭状态，请先在设置中启用。");
        }

        if (!settings.ConsentGranted)
        {
            return new SummaryRunResult(false, null, null,
                "尚未确认“活动记录将发送至外部接口”的提示，已取消。在设置中生成一次并确认即可。");
        }

        var (preparation, prepareError) = await PrepareAsync(options, day, cancellationToken).ConfigureAwait(false);
        if (preparation is null)
        {
            return new SummaryRunResult(false, null, null, prepareError ?? "准备总结内容失败。");
        }

        if (!OpenAiCompatibleSummarizer.TryCreate(settings, _log, out var summarizer, out var createError))
        {
            return new SummaryRunResult(false, null, null, createError ?? "初始化模型客户端失败。", preparation);
        }

        {
            try
            {
                _log.Info($"开始生成总结：{summarizer!.Description}，"
                          + $"{SummaryPreparation.DescribeMode(settings.PayloadMode)}，"
                          + $"{preparation.TextCharacters} 字符文字 + {preparation.ImageCount} 张图");

                var completion = await summarizer
                    .SummarizeAsync(preparation.Request, cancellationToken)
                    .ConfigureAwait(false);

                var savedPath = Save(completion, preparation);
                _log.Info($"总结已保存：{savedPath}（由 {completion.ProviderDescription} 生成，"
                          + $"尝试 {completion.Attempts} 次）");

                var message = completion.Attempts > 1
                    ? $"已生成并保存到 {savedPath}（{completion.ProviderDescription}，第 {completion.Attempts} 次尝试成功）"
                    : $"已生成并保存到 {savedPath}";

                return new SummaryRunResult(true, completion.Text, savedPath, message, preparation, completion.Attempts);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SummaryFailedException ex)
            {
                // 把每个模型的失败原因都带出来，否则用户只看到"失败了"没法排查。
                return new SummaryRunResult(
                    false, null, null,
                    $"{ex.Message}\n\n{ex.DescribeAttempts()}",
                    preparation);
            }
            catch (Exception ex)
            {
                _log.Error("生成总结失败", ex);
                return new SummaryRunResult(false, null, null, $"生成失败：{ex.Message}", preparation);
            }
        }
    }

    /// <summary>把这次尝试写进总结历史。写不进去只记日志——历史记录不该影响总结本身。</summary>
    private async Task RecordAsync(
        SummaryRunResult result,
        string trigger,
        DateTime startedAt,
        long elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var run = new SummaryRun
            {
                StartedAt = startedAt,
                FinishedAt = DateTime.Now,
                Trigger = trigger,
                Success = result.Success,
                Provider = result.Success ? result.Message : string.Empty,
                Attempts = result.Attempts,
                Markdown = result.Markdown ?? string.Empty,
                SavedPath = result.SavedPath ?? string.Empty,
                Message = result.Message,
                RecordCount = result.Preparation?.Digest.IncludedRecords ?? 0,
                ImageCount = result.Preparation?.ImageCount ?? 0,
                ElapsedMilliseconds = elapsedMilliseconds,
            };

            await _store.AppendSummaryRunAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"写入总结历史失败（不影响本次结果）：{ex.Message}");
        }
    }

    private string Save(SummaryCompletion completion, SummaryPreparation preparation)
    {
        Directory.CreateDirectory(_paths.SummariesDirectory);

        var path = Path.Combine(_paths.SummariesDirectory, $"summary-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        var header = new StringBuilder()
            .AppendLine("# SnapLog 活动总结")
            .AppendLine()
            .AppendLine($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"- 覆盖范围：{preparation.Digest.DescribeRange()}")
            .AppendLine($"- 记录条数：{preparation.Digest.IncludedRecords} / {preparation.Digest.AvailableRecords}"
                        + (preparation.Digest.Truncated ? "（按预算截断）" : string.Empty))
            .AppendLine($"- 发送内容：{SummaryPreparation.DescribeMode(preparation.PayloadMode)}"
                        + (preparation.ImageCount > 0 ? $"，附带 {preparation.ImageCount} 张截图" : string.Empty))
            .AppendLine($"- 模型：{completion.ProviderDescription}"
                        + (completion.Attempts > 1 ? $"（第 {completion.Attempts} 次尝试成功）" : string.Empty))
            .AppendLine($"- 数据文件：{_store.Location}");

        // 带图时把图片清单留档：以后想知道"当时到底发了哪些画面"能查得到。
        if (preparation.ImageCount > 0)
        {
            header.AppendLine("- 附带截图：");
            foreach (var image in preparation.Request.Images)
            {
                header.AppendLine($"  - {image.Timestamp:yyyy-MM-dd HH:mm:ss} {image.ProcessName} `{image.Path}`");
            }
        }

        header.AppendLine()
            .AppendLine("---")
            .AppendLine()
            .AppendLine(completion.Text);

        // 带 BOM，方便记事本/VS Code 直接认成 UTF-8。
        File.WriteAllText(path, header.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}
