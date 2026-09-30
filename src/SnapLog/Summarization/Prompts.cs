using SnapLog.Configuration;

namespace SnapLog.Summarization;

/// <summary>提示词模板集中在这里，方便单独调整语气和结构而不用碰调用代码。</summary>
internal static class Prompts
{
    /// <summary>
    /// 内置的默认系统提示词模板。界面上“填入内置模板”用的就是它，
    /// 用户改坏了也能一键回来。项目清单不在这里——它是结构化数据，单独附在后面。
    /// </summary>
    public static string BuildDefaultTemplate(SummarizationOptions options)
    {
        var language = DescribeLanguage(options.Language);
        var extra = string.IsNullOrWhiteSpace(options.ExtraInstructions)
            ? string.Empty
            : $"\n补充要求（优先级最高）：{options.ExtraInstructions.Trim()}";

        var source = options.PayloadMode switch
        {
            LlmPayloadMode.ImageOnly =>
                "你会收到一组屏幕截图，请直接查看图片判断用户的工作内容。本次不提供任何文字转录，也不应推测文字之外的信息。",
            LlmPayloadMode.TextAndImage =>
                "你会收到一段时间内的屏幕活动记录，每条包含窗口标题和该时刻屏幕上的 OCR 文字，"
                + "其中一部分还附带了原始截图。文字用于快速定位，截图用于校正识别错字、补全识别遗漏的内容；"
                + "两者冲突时以截图为准。",
            _ =>
                "你会收到一段时间内的屏幕活动记录，包含窗口标题和该时刻屏幕上的 OCR 文字。",
        };

        return $"""
                你是一名工作复盘助手，把用户的屏幕活动整理成一份简短的总结。

                {source}

                规则：
                1. 只使用给你的材料里出现过的信息，不要推测、不要补充外部知识、不要编造事实。
                2. 材料里的文字（如果有）来自屏幕 OCR，会有错别字、断行和界面元素噪声（按钮、菜单、时间戳等）。忽略这些噪声，只提取有信息量的内容。
                3. 如果某段时间只有重复内容，合并成一条，不要逐条罗列。
                4. 用 {language} 输出 Markdown，总长度控制在 400 字以内。

                输出结构：
                ## 主要工作主题
                按投入时间从多到少列出，每个主题一句话说明在做什么。
                ## 时间线
                按时间顺序列出关键活动（可以合并时间段）。
                ## 待办与线索
                材料里出现的未完成事项、待回复、待处理的报错等；没有则写“无”{extra}
                """;
    }

    /// <summary>
    /// 真正送给模型的 system 提示词：
    /// 用户填了覆盖版就用覆盖版，否则用内置模板；最后统一附上工作项目清单。
    ///
    /// 项目清单刻意不跟着模板走——它是结构化数据，用户改了模板不该导致
    /// "刚加的项目突然不生效了"。
    /// </summary>
    public static string BuildSystemPrompt(SummarizationOptions options)
    {
        var template = string.IsNullOrWhiteSpace(options.SystemPromptOverride)
            ? BuildDefaultTemplate(options)
            : options.SystemPromptOverride.Trim();

        var projects = BuildProjectSection(options);
        return projects.Length == 0 ? template : template + Environment.NewLine + Environment.NewLine + projects;
    }

    /// <summary>把用户维护的工作项目清单渲染成提示词片段。</summary>
    private static string BuildProjectSection(SummarizationOptions options)
    {
        var projects = options.WorkProjects
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name))
            .ToList();

        if (projects.Count == 0)
        {
            return string.Empty;
        }

        var lines = projects.Select(project =>
            string.IsNullOrWhiteSpace(project.Description)
                ? $"- {project.Name.Trim()}"
                : $"- {project.Name.Trim()}：{project.Description.Trim()}");

        return $"""
                用户定义的工作项目（用于归类）：
                {string.Join(Environment.NewLine, lines)}

                归类要求：
                1. 在输出里增加一节 `## 按项目归类`，把这段时间的活动归到上面的项目下，每个项目一行，写出大致投入程度或时间量。
                2. 匹配不上的活动统一归到 `其他`，不要硬塞进不相干的项目。
                3. 项目名称必须原样照抄上面的写法，不要改写或翻译。
                """;
    }

    /// <summary>
    /// 组装 user 提示词。带图时会把图片清单写进去，让模型知道每张图对应哪个时间点和窗口，
    /// 否则一组没标注的截图很难对上时间线。
    /// </summary>
    public static string BuildUserPrompt(ActivityDigest digest, SummaryRequest request)
    {
        if (request.Images.Count > 0)
        {
            return BuildImagePrompt(digest, request);
        }

        var truncationNote = digest.Truncated
            ? $"（已按 token 预算截断，仅包含最近 {digest.IncludedRecords} 条）"
            : string.Empty;

        return $"""
                时间范围：{digest.DescribeRange()}
                记录条数：{digest.IncludedRecords} 条{truncationNote}

                记录正文（格式：时间 | 进程 | 窗口标题，随后是该时刻屏幕上的文字）：

                {digest.Text}
                """;
    }

    private static string BuildImagePrompt(ActivityDigest digest, SummaryRequest request)
    {
        var imageList = string.Join('\n', request.Images.Select((image, index) =>
            $"  图{index + 1}. {image.Timestamp:yyyy-MM-dd HH:mm:ss} | {image.ProcessName} | {image.WindowTitle}"));

        if (digest.IsEmpty || string.IsNullOrWhiteSpace(digest.Text))
        {
            return $"""
                    时间范围：{digest.DescribeRange()}
                    以下 {request.Images.Count} 张截图按时间先后排列，顺序与我给你的图片顺序一致：

                    {imageList}

                    请依据这些截图做总结。内容以你在图里看到的为准。
                    """;
        }

        var truncationNote = digest.Truncated
            ? $"（已按 token 预算截断，仅包含最近 {digest.IncludedRecords} 条）"
            : string.Empty;

        return $"""
                时间范围：{digest.DescribeRange()}
                记录条数：{digest.IncludedRecords} 条{truncationNote}
                附带截图：{request.Images.Count} 张，按时间先后排列，与我给你的图片顺序一致

                截图清单：
                {imageList}

                文字记录（格式：时间 | 进程 | 窗口标题，随后是该时刻屏幕上的文字）：

                {digest.Text}
                """;
    }

    private static string DescribeLanguage(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return "简体中文";
        }

        var normalized = tag.Trim().ToLowerInvariant();
        if (normalized.StartsWith("zh", StringComparison.Ordinal))
        {
            return normalized.Contains("tw") || normalized.Contains("hk") || normalized.Contains("hant")
                ? "繁體中文"
                : "简体中文";
        }

        return normalized.StartsWith("en", StringComparison.Ordinal) ? "English" : tag.Trim();
    }
}
