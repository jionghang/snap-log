using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapLog.Configuration;

public sealed record OptionsLoadResult(AppOptions Options, string? SourcePath, string? Warning);

/// <summary>配置的加载与保存。加载时会补齐缺失字段，保存时写回用户级配置。</summary>
public static class OptionsStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 配置里枚举写成 "Both" 这种字符串，默认反序列化只认数字，必须显式打开字符串枚举。
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 查找顺序：命令行显式指定 → %LOCALAPPDATA%\SnapLog\appsettings.json → 程序目录\appsettings.json。
    /// 读取失败不会抛出，而是退回内置默认值并通过 <see cref="OptionsLoadResult.Warning"/> 说明原因——
    /// 配置坏了不应该让程序起不来，但也不能悄悄忽略。
    /// </summary>
    public static OptionsLoadResult Load(string? explicitPath)
    {
        foreach (var candidate in EnumerateCandidates(explicitPath))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(candidate);
                var options = JsonSerializer.Deserialize<AppOptions>(json, ReadOptions);
                if (options is not null)
                {
                    Normalize(options);
                    return new OptionsLoadResult(options, candidate, null);
                }

                return Fallback($"{candidate} 内容为空，已使用内置默认配置");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return Fallback($"读取配置失败（{candidate}）：{ex.Message}，已使用内置默认配置");
            }
        }

        return Fallback(null);
    }

    private static OptionsLoadResult Fallback(string? warning)
    {
        var options = new AppOptions();
        Normalize(options);
        return new OptionsLoadResult(options, null, warning);
    }

    public static void Save(AppOptions options, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(options, WriteOptions));
    }

    /// <summary>
    /// 深拷贝。设置窗体用它来编辑副本，这样点“取消”时不会污染正在运行的实例。
    /// 走一次 JSON 往返而不是手写克隆：加字段时不会漏掉。
    /// </summary>
    public static AppOptions Clone(AppOptions options)
    {
        var json = JsonSerializer.Serialize(options, WriteOptions);
        var clone = JsonSerializer.Deserialize<AppOptions>(json, ReadOptions) ?? new AppOptions();
        Normalize(clone);
        return clone;
    }

    /// <summary>
    /// 把 <paramref name="source"/> 的各分节对象整体换到 <paramref name="target"/> 上。
    /// 运行中的组件持有的是同一个 AppOptions 实例、按需读取分节，所以换引用即可生效。
    /// </summary>
    public static void CopyInto(AppOptions target, AppOptions source)
    {
        target.Capture = source.Capture;
        target.Triggers = source.Triggers;
        target.Ocr = source.Ocr;
        target.Storage = source.Storage;
        target.Summarization = source.Summarization;
        target.Logging = source.Logging;
        target.Ui = source.Ui;
    }

    private static IEnumerable<string> EnumerateCandidates(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath.Trim()));
        }

        yield return AppPaths.UserConfigPath;
        yield return AppPaths.ShippedConfigPath;
    }

    /// <summary>把明显不合理的值拉回可用范围，例如负数间隔、空数组 null。</summary>
    private static void Normalize(AppOptions options)
    {
        options.Capture ??= new CaptureOptions();
        options.Triggers ??= new TriggerOptions();
        options.Ocr ??= new OcrOptions();
        options.Storage ??= new StorageOptions();
        options.Summarization ??= new SummarizationOptions();
        options.Logging ??= new LoggingOptions();
        options.Ui ??= new UiOptions();

        options.Capture.MaxImageDimension = Math.Clamp(options.Capture.MaxImageDimension, 320, 16384);

        options.Triggers.ForegroundSettleMilliseconds = Math.Clamp(options.Triggers.ForegroundSettleMilliseconds, 0, 30_000);
        options.Triggers.MinSecondsBetweenCaptures = Math.Clamp(options.Triggers.MinSecondsBetweenCaptures, 0, 86_400);
        options.Triggers.CaptureCycleMinutes = Math.Clamp(options.Triggers.CaptureCycleMinutes, 1, 24 * 60);
        options.Triggers.ExcludedProcesses ??= [];
        options.Triggers.ExcludedWindowTitles ??= [];

        options.Ocr.MinTextLength = Math.Max(0, options.Ocr.MinTextLength);
        options.Ocr.TargetLongestSide = options.Ocr.TargetLongestSide <= 0 ? 0 : Math.Clamp(options.Ocr.TargetLongestSide, 400, 8000);
        options.Ocr.PaddleThreads = Math.Clamp(options.Ocr.PaddleThreads, 1, 32);
        options.Ocr.PaddleMaxSideLength = Math.Clamp(options.Ocr.PaddleMaxSideLength, 320, 8192);
        options.Ocr.MaxUpscale = Math.Clamp(options.Ocr.MaxUpscale, 1.0, 8.0);

        if (string.IsNullOrWhiteSpace(options.Storage.DatabaseFileName))
        {
            options.Storage.DatabaseFileName = "activity.db";
        }

        options.Summarization.MaxInputCharacters = Math.Clamp(options.Summarization.MaxInputCharacters, 500, 400_000);
        options.Summarization.MaxRecords = Math.Clamp(options.Summarization.MaxRecords, 1, 5000);
        options.Summarization.MaxImages = Math.Clamp(options.Summarization.MaxImages, 0, 50);
        options.Summarization.ImageSampleSeconds = Math.Clamp(options.Summarization.ImageSampleSeconds, 0, 86_400);
        options.Summarization.RetryCount = Math.Clamp(options.Summarization.RetryCount, 0, 10);
        options.Summarization.RetryDelaySeconds = Math.Clamp(options.Summarization.RetryDelaySeconds, 0, 300);
        options.Summarization.RequestTimeoutSeconds = Math.Clamp(options.Summarization.RequestTimeoutSeconds, 10, 900);

        options.Capture.ImageRetentionDays = Math.Clamp(options.Capture.ImageRetentionDays, 0, 3650);
        options.Storage.RecordRetentionDays = Math.Clamp(options.Storage.RecordRetentionDays, 0, 3650);

        options.Feishu ??= new FeishuOptions();
        options.Feishu.PushLookbackDays = Math.Clamp(options.Feishu.PushLookbackDays, 1, 365);
        options.Feishu.MaxTextLength = Math.Clamp(options.Feishu.MaxTextLength, 1, 100_000);
        options.Feishu.BatchSize = Math.Clamp(options.Feishu.BatchSize, 1, 500);
        if (!TimeOnly.TryParse(options.Feishu.ScheduleTimeOfDay, out _))
        {
            options.Feishu.ScheduleTimeOfDay = "19:00";
        }

        MigrateLegacyProvider(options.Summarization);
        MigrateLegacyFeishuMappings(options.Feishu);

        options.Logging.RetentionDays = Math.Clamp(options.Logging.RetentionDays, 0, 3650);
    }

    /// <summary>
    /// 写入飞书的内容从"抓取记录"改成了"大模型总结"，旧配置里的记录字段（Timestamp、OcrText…）
    /// 在总结上取不到任何值——留着的话每次写入都会被整条跳过，而且不会有报错。
    /// 所以这里整组换成总结字段的默认映射，并把这件事记在
    /// <see cref="FeishuOptions.LegacyMappingsReplaced"/> 上，由界面提示用户重新核对列名。
    /// </summary>
    private static void MigrateLegacyFeishuMappings(FeishuOptions feishu)
    {
        feishu.FieldMappings ??= [];

        var known = new HashSet<string>(FeishuFieldMapping.AvailableFields, StringComparer.Ordinal);

        var legacy = feishu.FieldMappings.Any(m => m is not null
            && !string.IsNullOrWhiteSpace(m.RecordField)
            && !known.Contains(m.RecordField));

        if (legacy)
        {
            feishu.FieldMappings = FeishuFieldMapping.CreateDefault();
            feishu.LegacyMappingsReplaced = true;
        }

        // 手改配置可能写进 null 条目，顺手清掉，免得后面每处都要判空。
        feishu.FieldMappings = [.. feishu.FieldMappings.Where(m => m is not null)];
    }

    /// <summary>
    /// 旧版只有一组单模型配置（Endpoint/Model/ApiKey 等）。升级后模型列表是空的就用旧值合成一条，
    /// 用户的配置不会丢。旧字段保留只用于迁移，不再参与运行。
    /// </summary>
    private static void MigrateLegacyProvider(SummarizationOptions summarization)
    {
        summarization.Providers ??= [];

        // 手改配置可能写进 null 条目，顺手清掉并补齐必填项，免得后面各处都要判空。
        summarization.Providers = [.. summarization.Providers
            .Where(p => p is not null)
            .Select(p =>
            {
                p.Name = string.IsNullOrWhiteSpace(p.Name) ? "未命名模型" : p.Name;
                p.Endpoint = string.IsNullOrWhiteSpace(p.Endpoint) ? "https://api.openai.com/v1" : p.Endpoint;
                p.Model = string.IsNullOrWhiteSpace(p.Model) ? "gpt-4o-mini" : p.Model;
                p.ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(p.ApiKeyEnvironmentVariable)
                    ? "SNAPLOG_OPENAI_API_KEY"
                    : p.ApiKeyEnvironmentVariable;
                return p;
            })];

        if (summarization.Providers.Count > 0)
        {
            return;
        }

        var hasLegacy = !string.IsNullOrWhiteSpace(summarization.Endpoint)
                        || !string.IsNullOrWhiteSpace(summarization.Model)
                        || !string.IsNullOrWhiteSpace(summarization.ApiKey);

        if (!hasLegacy)
        {
            return;
        }

        summarization.Providers.Add(new LlmProviderOptions
        {
            Name = "迁移自旧版配置",
            Enabled = true,
            Endpoint = string.IsNullOrWhiteSpace(summarization.Endpoint) ? "https://api.openai.com/v1" : summarization.Endpoint,
            Model = string.IsNullOrWhiteSpace(summarization.Model) ? "gpt-4o-mini" : summarization.Model,
            ApiKey = summarization.ApiKey,
            ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(summarization.ApiKeyEnvironmentVariable)
                ? "SNAPLOG_OPENAI_API_KEY"
                : summarization.ApiKeyEnvironmentVariable,
        });
    }
}
