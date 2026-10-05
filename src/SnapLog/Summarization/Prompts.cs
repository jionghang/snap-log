using SnapLog.Configuration;

namespace SnapLog.Summarization;

/// <summary>提示词模板集中在这里，方便单独调整语气和结构而不用碰调用代码。</summary>
internal static class Prompts
{
    /// <summary>
    /// 默认系统提示词模板：只写"怎么总结"，不含任何随配置变化的内容
    /// （发送内容、输出语言、附加要求、工作项目都在发送时结构化追加，见 BuildSystemPrompt）。
    ///
    /// 这样它就能直接固化进用户配置：首次运行会把它填进 SystemPromptOverride，
    /// 用户在界面上看到、编辑的就是实际会用到的那份，不存在"留空 = 用内置模板"的第二种状态。
    /// </summary>
    public static string DefaultTemplate => """
        你是一名工作复盘助手，把用户的屏幕活动整理成一份总结。

        规则：
        1. 只使用给你的材料里出现过的信息，不要推测、不要补充外部知识、不要编造事实。
        2. 材料里的文字（如果有）来自屏幕 OCR，会有错别字、断行和界面元素噪声（按钮、菜单、时间戳等）。忽略这些噪声，只提取有信息量的内容。
        3. 如果某段时间只有重复内容，合并成一条，不要逐条罗列。
        4. 用纯文本输出，不要使用任何 Markdown 标记（不要井号、星号、反引号、竖线等）。篇幅从简，全文一般控制在 400 字以内：当天做过的事都要出现，保证覆盖全面；但不展开过程和细节，每条只写一句概括、一般不超过 30 字，不要逐条复述材料。

        输出结构：只写“主要工作主题”一节，标题单独占一行；正文按投入时间从多到少用阿拉伯数字分点列出，每条一句话说明在做什么。
        """;

    /// <summary>400 字那条字数规则配上三节结构的那一版（“时间线”“待办与线索”移除之前的默认模板）。</summary>
    private static string LegacyThreeSectionTemplate => """
        你是一名工作复盘助手，把用户的屏幕活动整理成一份总结。

        规则：
        1. 只使用给你的材料里出现过的信息，不要推测、不要补充外部知识、不要编造事实。
        2. 材料里的文字（如果有）来自屏幕 OCR，会有错别字、断行和界面元素噪声（按钮、菜单、时间戳等）。忽略这些噪声，只提取有信息量的内容。
        3. 如果某段时间只有重复内容，合并成一条，不要逐条罗列。
        4. 用纯文本输出，不要使用任何 Markdown 标记（不要井号、星号、反引号、竖线等）。篇幅从简，全文一般控制在 400 字以内：当天做过的事都要出现，保证覆盖全面；但不展开过程和细节，每条只写一句概括、一般不超过 30 字，不要逐条复述材料。

        输出结构（每节标题单独占一行，正文用阿拉伯数字编号）：

        主要工作主题
        1. 按投入时间从多到少列出，每条一句话说明在做什么。

        时间线
        1. 按时间顺序列出主要时间段，每个时间段一句概括，不重复上面已写过的细节。

        待办与线索
        列出材料里出现的未完成事项、待回复、待处理的报错等；没有则写“无”。
        """;

    /// <summary>
    /// 早期版本内置模板要求输出 Markdown，正文里全是井号与星号，读起来不友好。
    /// 仅用于迁移，见 <see cref="LegacyDefaultTemplates"/>。
    /// </summary>
    private static string LegacyMarkdownTemplate => """
        你是一名工作复盘助手，把用户的屏幕活动整理成一份简短的总结。

        规则：
        1. 只使用给你的材料里出现过的信息，不要推测、不要补充外部知识、不要编造事实。
        2. 材料里的文字（如果有）来自屏幕 OCR，会有错别字、断行和界面元素噪声（按钮、菜单、时间戳等）。忽略这些噪声，只提取有信息量的内容。
        3. 如果某段时间只有重复内容，合并成一条，不要逐条罗列。
        4. 输出 Markdown，总长度控制在 400 字以内。

        输出结构：
        ## 主要工作主题
        按投入时间从多到少列出，每个主题一句话说明在做什么。
        ## 时间线
        按时间顺序列出关键活动（可以合并时间段）。
        ## 待办与线索
        材料里出现的未完成事项、待回复、待处理的报错等；没有则写“无”。
        """;

    /// <summary>改成纯文本输出后、放宽字数限制前的那一版（正文里还写着"400 字以内"）。</summary>
    private static string LegacyPlainTextTemplate => """
        你是一名工作复盘助手，把用户的屏幕活动整理成一份简短的总结。

        规则：
        1. 只使用给你的材料里出现过的信息，不要推测、不要补充外部知识、不要编造事实。
        2. 材料里的文字（如果有）来自屏幕 OCR，会有错别字、断行和界面元素噪声（按钮、菜单、时间戳等）。忽略这些噪声，只提取有信息量的内容。
        3. 如果某段时间只有重复内容，合并成一条，不要逐条罗列。
        4. 用纯文本输出，不要使用任何 Markdown 标记（不要井号、星号、反引号、竖线等），总长度控制在 400 字以内。

        输出结构（三节，每节标题单独占一行，正文用阿拉伯数字编号）：

        主要工作主题
        1. 按投入时间从多到少列出，每条一句话说明在做什么。

        时间线
        1. 按时间顺序列出关键活动，可以合并时间段。

        待办与线索
        列出材料里出现的未完成事项、待回复、待处理的报错等；没有则写“无”。
        """;

    /// <summary>放宽字数限制那一版（"一般控制在 600 字左右"），换成"核心工作、全面不抠细节"后进历史。</summary>
    private static string LegacyDetailedTemplate => """
        你是一名工作复盘助手，把用户的屏幕活动整理成一份总结。

        规则：
        1. 只使用给你的材料里出现过的信息，不要推测、不要补充外部知识、不要编造事实。
        2. 材料里的文字（如果有）来自屏幕 OCR，会有错别字、断行和界面元素噪声（按钮、菜单、时间戳等）。忽略这些噪声，只提取有信息量的内容。
        3. 如果某段时间只有重复内容，合并成一条，不要逐条罗列。
        4. 用纯文本输出，不要使用任何 Markdown 标记（不要井号、星号、反引号、竖线等）。篇幅按内容的多少来定，内容完整优先，一般控制在 600 字左右；不要为了压字数丢信息，也不要逐条复述材料。

        输出结构（每节标题单独占一行，正文用阿拉伯数字编号）：

        主要工作主题
        1. 按投入时间从多到少列出，每条一句话说明在做什么。

        时间线
        1. 按时间顺序列出关键活动，可以合并时间段。

        待办与线索
        列出材料里出现的未完成事项、待回复、待处理的报错等；没有则写“无”。
        """;

    /// <summary>600 字的字数规则配上两节结构的那一版（"时间线"小节加入之前的默认模板）。</summary>
    private static string LegacyTwoSectionTemplate => """
        你是一名工作复盘助手，把用户的屏幕活动整理成一份总结。

        规则：
        1. 只使用给你的材料里出现过的信息，不要推测、不要补充外部知识、不要编造事实。
        2. 材料里的文字（如果有）来自屏幕 OCR，会有错别字、断行和界面元素噪声（按钮、菜单、时间戳等）。忽略这些噪声，只提取有信息量的内容。
        3. 如果某段时间只有重复内容，合并成一条，不要逐条罗列。
        4. 用纯文本输出，不要使用任何 Markdown 标记（不要井号、星号、反引号、竖线等）。篇幅按内容的多少来定，内容完整优先，一般控制在 600 字左右；不要为了压字数丢信息，也不要逐条复述材料。

        输出结构：
        主要工作主题
        按投入时间从多到少列出，每个主题分点说明做了什么。
        待办与线索
        材料里出现的未完成事项、待回复、待处理的报错等；没有则写“无”。
        """;

    /// <summary>
    /// 历次内置模板。配置里存的提示词等于其中之一时，说明那只是当时自动填入的默认内容，
    /// 应当换成最新模板；用户自己改过的一律不动。
    /// </summary>
    public static IReadOnlyList<string> LegacyDefaultTemplates { get; } =
        [LegacyMarkdownTemplate, LegacyPlainTextTemplate, LegacyDetailedTemplate, LegacyTwoSectionTemplate, LegacyThreeSectionTemplate];

    /// <summary>
    /// 真正送给模型的 system 提示词：模板（用户配置里的那份）+ 结构化追加部分。
    /// 追加部分不跟着模板走——它是配置的结构化投影，用户改了模板也不该让
    /// "刚加的项目不生效"或"切到带图模式后说明还是旧的"。
    /// </summary>
    public static string BuildSystemPrompt(SummarizationOptions options)
    {
        var template = string.IsNullOrWhiteSpace(options.SystemPromptOverride)
            ? DefaultTemplate
            : options.SystemPromptOverride.Trim();

        var sections = new List<string> { template };

        var context = BuildContextSection(options);
        if (context.Length > 0)
        {
            sections.Add(context);
        }

        var projects = BuildProjectSection(options);
        if (projects.Length > 0)
        {
            sections.Add(projects);
        }

        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    /// <summary>本次输入与输出要求：随配置变化的部分都在这里，而不是写进用户的模板文本。</summary>
    private static string BuildContextSection(SummarizationOptions options)
    {
        var source = options.PayloadMode switch
        {
            LlmPayloadMode.ImageOnly =>
                "你会收到一组屏幕截图，请直接查看图片判断用户的工作内容；本次不提供文字转录，也不应推测画面之外的信息。",
            LlmPayloadMode.TextAndImage =>
                "你会收到一段时间内的屏幕活动记录，每条包含窗口标题和该时刻屏幕上的 OCR 文字，其中一部分还附带了原始截图；"
                + "文字用于快速定位，截图用于校正识别错字、补全遗漏，两者冲突时以截图为准。",
            _ =>
                "你会收到一段时间内的屏幕活动记录，每条包含窗口标题和该时刻屏幕上的 OCR 文字。",
        };

        var lines = new List<string>
        {
            "本次输入与要求：",
            $"1. {source}",
            $"2. 用 {DescribeLanguage(options.Language)} 输出。",
            $"3. 标了“（{ReportDocuments.Marker}）”的记录是日报、周报、月报、年度总结、述职这类周期性汇报，"
            + "正文讲的是某个周期的整体情况，可能包含更早时间做过的事。这类记录只把“在写或在看这份报告”记为一项工作；"
            + "报告里列的事项，要有本次材料的其他记录佐证，才算这段时间的工作。"
            + "只覆盖当天的日报不受这条限制，可以按当天内容采信。",
        };

        if (!string.IsNullOrWhiteSpace(options.ExtraInstructions))
        {
            lines.Add($"4. 补充要求（优先级最高）：{options.ExtraInstructions.Trim()}");
        }

        return string.Join(Environment.NewLine, lines);
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

        var lines = projects.Select((project, index) =>
            string.IsNullOrWhiteSpace(project.Description)
                ? $"{index + 1}. {project.Name.Trim()}"
                : $"{index + 1}. {project.Name.Trim()}：{project.Description.Trim()}");

        return $"""
                用户定义的工作项目：
                {string.Join(Environment.NewLine, lines)}

                按项目写作的要求：
                1.“主要工作主题”一节按项目分组，不再整体按投入时间排序：每个项目名单独占一行，下面用阿拉伯数字（1. 2. 3.）分点列出这段时间在该项目上做的事，组内按投入时间从多到少。
                2. 这段时间没有活动的项目不要出现。
                3. 归不进上面任何一个项目的活动，也要单独成组、放在最后，并且写成和项目一样的样子：起一个概括性的名字（例如“其他事务”“临时沟通”“会议与协调”）单独占一行，下面同样用阿拉伯数字分点列出；不要把这些活动塞进不相干的项目，也不要只用一句话带过。
                4. 项目名称必须原样照抄上面的写法，不要改写、翻译或补充。
                5. 不要另外增加“按项目归类”之类的小节，项目分组本身就写在“主要工作主题”里。
                6. 全篇纯文本，不加任何 Markdown 标记，项目名也不要加井号、方括号或星号。
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
