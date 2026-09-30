using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Core;

/// <summary>一次推送的结果。</summary>
public sealed record FeishuPushResult(bool Success, string Message, int Written, int Skipped)
{
    /// <summary>写成功的那些总结的 id。调用方拿它去库里打“已写入”标记，下次就不会重复写。</summary>
    public IReadOnlyList<long> WrittenIds { get; init; } = [];

    public static FeishuPushResult Fail(string message) => new(false, message, 0, 0);
}

/// <summary>
/// 把大模型生成的总结写进飞书多维表格：一条总结 = 表里一行。
///
/// 分三层：
///   ① 数据层：app_token（整张多维表格）+ table_id（一张数据表）
///   ② 通道层：App ID + App Secret → tenant_access_token → 多维表格开放接口
///   ③ 触发层：生成总结后自动 / 定时任务 / 手动（由 SummaryRunner、ScheduledJobsService 与界面按钮负责）
///
/// 三个"踩过坑"的点都在这里落实了：
///   1. 用应用身份 tenant_access_token，令牌缓存并提前 5 分钟刷新，不会每条记录都换令牌。
///   2. 权限要两层同时满足：API 权限范围（bitable:app）+ 文档级协作授权。
///      缺前者飞书报 99991672（会列出缺哪个权限），缺后者报 91403 Forbidden —— 这两个码在本类里被翻译成人话。
///   3. 写入前先调“列出字段”核对字段名：飞书按名称精确匹配，
///      差一个空格就报 1254045 FieldNameNotFound。核对之后能在推送前就说清是哪一列对不上。
/// </summary>
public sealed class FeishuBitablePublisher
{
    private const string ApiRoot = "https://open.feishu.cn/open-apis";

    /// <summary>令牌提前刷新的余量。飞书令牌有效期 7200 秒，留 5 分钟余量避免边界失效。</summary>
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>令牌缓存。静态的，让同一个进程里多个推送器共用一份，不会重复换取。</summary>
    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    private static string? _cachedToken;
    private static DateTime _cachedTokenExpiresUtc = DateTime.MinValue;

    private readonly FeishuOptions _options;
    private readonly FileLogger _log;

    public FeishuBitablePublisher(FeishuOptions options, FileLogger log)
    {
        _options = options;
        _log = log;
    }

    public static string SecretVariableName(FeishuOptions options) =>
        string.IsNullOrWhiteSpace(options.AppSecretEnvironmentVariable)
            ? "SNAPLOG_FEISHU_APP_SECRET"
            : options.AppSecretEnvironmentVariable.Trim();

    /// <summary>推送前的基本配置校验。返回 null 表示没问题。</summary>
    public static string? Validate(FeishuOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AppId))
        {
            return "未填写飞书应用的 App ID。";
        }

        if (ResolveSecret(options) is null)
        {
            return $"未获取到 App Secret：请填写 App Secret，或设置环境变量 {SecretVariableName(options)}。";
        }

        if (string.IsNullOrWhiteSpace(options.AppToken))
        {
            return "未填写多维表格的 app_token。";
        }

        if (string.IsNullOrWhiteSpace(options.TableId))
        {
            return "未填写数据表的 table_id。";
        }

        if (!TimeOnly.TryParse(options.ScheduleTimeOfDay, out _))
        {
            return $"写入时间“{options.ScheduleTimeOfDay}”不是合法的 HH:mm 格式。";
        }

        return null;
    }

    /// <summary>
    /// 只读表里的字段清单，给界面上的“读取表字段名”用：
    /// 让用户从真实列名里挑，而不是手打——手打错一个空格就是 1254045。
    /// 只做一次“列出字段”请求，不写任何数据。
    /// </summary>
    public async Task<(IReadOnlyList<FeishuTableField>? Fields, string? Error)> FetchFieldsAsync(
        CancellationToken cancellationToken)
    {
        var problem = Validate(_options);
        if (problem is not null)
        {
            return (null, problem);
        }

        using var http = CreateClient();

        try
        {
            var token = await GetTokenAsync(http, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return (null, _lastError ?? "换取 tenant_access_token 失败。");
            }

            var (fields, error) = await ListFieldsAsync(http, token, cancellationToken).ConfigureAwait(false);
            return fields is null ? (null, error ?? "读取数据表字段失败。") : (fields, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, $"连接失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 只做验证，不写入：换一次令牌 + 调“列出字段”，顺便核对字段映射。
    /// 这是最有用的一步——权限两层是否都配好、字段名对不对，一次全查出来。
    /// </summary>
    public async Task<FeishuPushResult> TestAsync(CancellationToken cancellationToken)
    {
        var problem = Validate(_options);
        if (problem is not null)
        {
            return FeishuPushResult.Fail(problem);
        }

        using var http = CreateClient();

        try
        {
            var token = await GetTokenAsync(http, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return FeishuPushResult.Fail(_lastError ?? "换取 tenant_access_token 失败。");
            }

            var (fields, error) = await ListFieldsAsync(http, token, cancellationToken).ConfigureAwait(false);
            if (fields is null)
            {
                return FeishuPushResult.Fail(error ?? "读取数据表字段失败。");
            }

            var lines = new List<string>
            {
                $"连接成功：令牌已获取，数据表读到 {fields.Count} 个字段。",
                "表内字段：" + string.Join("、", fields.Select(f => f.Name)),
            };

            var (missing, unused) = CheckMapping(fields);
            if (missing.Count > 0)
            {
                lines.Add("以下映射在表内找不到同名字段，写入会失败：" + string.Join("、", missing));
                lines.Add("　请按表内实际列名修改“字段映射”，注意空格与换行需完全一致。");
            }
            else
            {
                lines.Add("字段映射核对通过。");
            }

            if (unused.Count > 0)
            {
                lines.Add($"（映射里有 {unused.Count} 条目标字段留空，会被跳过）");
            }

            return new FeishuPushResult(missing.Count == 0, string.Join(Environment.NewLine, lines), 0, 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return FeishuPushResult.Fail($"连接失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把一批总结写进多维表格。返回写成功、跳过（这条的映射恰好都没值）的条数。</summary>
    public async Task<FeishuPushResult> PushAsync(
        IReadOnlyList<SummaryRun> runs,
        CancellationToken cancellationToken)
    {
        var problem = Validate(_options);
        if (problem is not null)
        {
            return FeishuPushResult.Fail(problem);
        }

        if (runs.Count == 0)
        {
            return new FeishuPushResult(true, "没有需要写入的总结", 0, 0);
        }

        // 字段映射全部留空时，写进去就是一堆空行——直接拦下来说清楚，别浪费一次请求。
        var activeMappings = _options.FieldMappings
            .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.RecordField) && !string.IsNullOrWhiteSpace(m.FeishuField))
            .ToList();

        if (activeMappings.Count == 0)
        {
            return FeishuPushResult.Fail(
                "字段映射中没有任何一条填写了“飞书字段名”，没有可写入的列。"
                + Environment.NewLine
                + "请在“推送配置”中为至少一条映射填写表内列名。");
        }

        using var http = CreateClient();

        var token = await GetTokenAsync(http, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return FeishuPushResult.Fail(_lastError ?? "换取 tenant_access_token 失败。");
        }

        var (fields, fieldError) = await ListFieldsAsync(http, token, cancellationToken).ConfigureAwait(false);
        if (fields is null)
        {
            return FeishuPushResult.Fail(fieldError ?? "读取数据表字段失败。");
        }

        // 先核对字段名再写：这样"哪一列对不上"能在推送前一次说清，而不是写一条报一次错。
        var (missing, _) = CheckMapping(fields);
        if (missing.Count > 0)
        {
            return FeishuPushResult.Fail(
                "数据表中不存在以下字段：" + string.Join("、", missing)
                + Environment.NewLine
                + "飞书按字段名精确匹配（含空格差异会返回 1254045），"
                + "请按表内实际列名修改“字段映射”，或先在表中添加这些列。");
        }

        var typeByName = fields.ToDictionary(f => f.Name, f => f.Type, StringComparer.Ordinal);

        var written = 0;
        var skipped = 0;
        var writtenIds = new List<long>();
        var batchSize = Math.Clamp(_options.BatchSize, 1, 500);
        var failures = new List<string>();

        for (var offset = 0; offset < runs.Count; offset += batchSize)
        {
            // 取消不直接抛：已经把前几批写进去了，得把"写了哪些"带回去打标记，
            // 否则下次重试会在表里刷出一批重复行。
            if (cancellationToken.IsCancellationRequested)
            {
                return new FeishuPushResult(
                    false,
                    $"写入被取消：已写 {written} 条，剩余 {runs.Count - offset} 条没写。已写入的已标记，下次接着写不会重复。",
                    written,
                    skipped)
                {
                    WrittenIds = writtenIds,
                };
            }

            var batch = runs.Skip(offset).Take(batchSize).ToList();
            var payloadRecords = new List<FeishuRecordPayload>(batch.Count);

            // 与 payloadRecords 一一对应的 SnapLog 总结 id：整批成功才算写上，
            // 所以拿它去打“已写入”标记，同一批里跳过的那几条不会被误标。
            var batchRunIds = new List<long>(batch.Count);

            foreach (var run in batch)
            {
                var fieldsPayload = BuildFields(run, activeMappings, typeByName, out var hasValue);
                if (!hasValue)
                {
                    skipped++;
                    continue;
                }

                payloadRecords.Add(new FeishuRecordPayload { Fields = fieldsPayload });
                batchRunIds.Add(run.Id);
            }

            if (payloadRecords.Count == 0)
            {
                continue;
            }

            bool ok;
            string? error;
            try
            {
                (ok, error) = await BatchCreateAsync(http, token, payloadRecords, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 这一批发出去没有、飞书收没收下，本地无法确定——如实说出来，别假装没发生。
                return new FeishuPushResult(
                    false,
                    $"写入中断：已写入 {written} 条。第 {offset / batchSize + 1} 批状态不确定（可能已写入表内），"
                    + "请核对后再决定是否重试。",
                    written,
                    skipped)
                {
                    WrittenIds = writtenIds,
                };
            }

            if (ok)
            {
                written += payloadRecords.Count;
                writtenIds.AddRange(batchRunIds);
            }
            else
            {
                failures.Add(error ?? "未知错误");
            }
        }

        if (failures.Count > 0)
        {
            return new FeishuPushResult(
                false,
                $"写入未全部成功：已写入 {written} 条，失败 {failures.Count} 批 —— " + string.Join("；", failures),
                written,
                skipped)
            {
                WrittenIds = writtenIds,
            };
        }

        var summary = $"已写入 {written} 条总结到飞书多维表格";
        if (skipped > 0)
        {
            summary += $"（{skipped} 条因映射字段均无值被跳过）";
        }

        return new FeishuPushResult(true, summary, written, skipped) { WrittenIds = writtenIds };
    }

    /// <summary>把一条总结按映射和表里字段类型组装成飞书的 fields 对象。</summary>
    private Dictionary<string, object?> BuildFields(
        SummaryRun run,
        IReadOnlyList<FeishuFieldMapping> mappings,
        IReadOnlyDictionary<string, int> typeByName,
        out bool hasValue)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        hasValue = false;

        foreach (var mapping in mappings)
        {
            var raw = ReadSummaryField(run, mapping.RecordField);
            if (raw is null)
            {
                continue;
            }

            var converted = ConvertForFieldType(raw, typeByName.GetValueOrDefault(mapping.FeishuField, 1));
            if (converted is null)
            {
                continue;
            }

            fields[mapping.FeishuField] = converted;
            hasValue = true;
        }

        return fields;
    }

    /// <summary>按名字取总结里的字段值。返回 null 表示这个字段不存在或这条没值。</summary>
    private object? ReadSummaryField(SummaryRun run, string fieldName) => fieldName switch
    {
        nameof(SummaryRun.StartedAt) => run.StartedAt,
        nameof(SummaryRun.FinishedAt) => run.FinishedAt,
        nameof(SummaryRun.Trigger) => Blank(run.Trigger),
        nameof(SummaryRun.Provider) => Blank(run.Provider),
        nameof(SummaryRun.RecordCount) => run.RecordCount,
        nameof(SummaryRun.ImageCount) => run.ImageCount,
        nameof(SummaryRun.ElapsedMilliseconds) => run.ElapsedMilliseconds,
        nameof(SummaryRun.Attempts) => run.Attempts,
        nameof(SummaryRun.Markdown) => Blank(Truncate(run.Markdown, _options.MaxTextLength)),
        nameof(SummaryRun.Preview) => Blank(run.Preview),
        nameof(SummaryRun.SavedPath) => Blank(run.SavedPath),
        nameof(SummaryRun.Message) => Blank(run.Message),
        _ => null,
    };

    /// <summary>
    /// 按飞书字段类型转换值。飞书要求值的类型和列类型匹配，否则报错。
    /// 类型码：1 文本、2 数字、3 单选、4 多选、5 日期、7 复选框（其他类型按文本发）。
    /// </summary>
    private static object? ConvertForFieldType(object raw, int fieldType) => fieldType switch
    {
        // 日期列要毫秒时间戳
        5 when raw is DateTime time => new DateTimeOffset(time).ToUnixTimeMilliseconds(),

        // 数字列要数字
        2 when raw is int or long => raw,
        2 when raw is string text && long.TryParse(text, out var parsed) => parsed,

        // 复选框要布尔
        7 when raw is bool flag => flag,

        // 其余（文本/单选/多选等）都用字符串
        _ => raw switch
        {
            DateTime time => time.ToString("yyyy-MM-dd HH:mm:ss"),
            bool flag => flag ? "是" : "否",
            _ => raw.ToString(),
        },
    };

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Truncate(string value, int maxLength)
    {
        var limit = Math.Clamp(maxLength, 1, 100_000);
        return value.Length <= limit ? value : value[..limit] + "…（已截断）";
    }

    /// <summary>核对映射里的字段名在不在表里。返回（表里没有的、被跳过不写的）。</summary>
    private (List<string> Missing, List<string> Unused) CheckMapping(IReadOnlyList<FeishuTableField> fields)
    {
        var existing = new HashSet<string>(fields.Select(f => f.Name), StringComparer.Ordinal);

        var missing = _options.FieldMappings
            .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.FeishuField))
            .Select(m => m.FeishuField)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !existing.Contains(name))
            .ToList();

        var unused = _options.FieldMappings
            .Where(m => m is not null && string.IsNullOrWhiteSpace(m.FeishuField))
            .Select(m => FeishuFieldMapping.DescribeField(m.RecordField))
            .ToList();

        return (missing, unused);
    }

    // ---------------------------------------------------------------- 通道层

    private string? _lastError;

    /// <summary>换取 tenant_access_token，带缓存。失败时把原因写进 <see cref="_lastError"/>。</summary>
    private async Task<string?> GetTokenAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (_cachedToken is not null && DateTime.UtcNow < _cachedTokenExpiresUtc - TokenRefreshMargin)
        {
            return _cachedToken;
        }

        await TokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 另一个推送器可能刚换过，再确认一次。
            if (_cachedToken is not null && DateTime.UtcNow < _cachedTokenExpiresUtc - TokenRefreshMargin)
            {
                return _cachedToken;
            }

            var payload = new FeishuTokenPayload
            {
                AppId = _options.AppId.Trim(),
                AppSecret = ResolveSecret(_options)!,
            };

            using var response = await http
                .PostAsJsonAsync($"{ApiRoot}/auth/v3/tenant_access_token/internal", payload, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content
                .ReadFromJsonAsync<FeishuTokenResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (body is null)
            {
                _lastError = $"换取令牌失败：HTTP {(int)response.StatusCode}，响应无法解析";
                _log.Warn(_lastError);
                return null;
            }

            if (body.Code != 0 || string.IsNullOrWhiteSpace(body.TenantAccessToken))
            {
                _lastError = DescribeTokenFailure(body.Code, body.Msg);
                _log.Warn(_lastError);
                return null;
            }

            _cachedToken = body.TenantAccessToken;

            // expire 是秒；拿不到就按飞书默认的 7200 秒算。
            var lifetime = body.Expire > 0 ? TimeSpan.FromSeconds(body.Expire) : TimeSpan.FromHours(2);
            _cachedTokenExpiresUtc = DateTime.UtcNow + lifetime;

            _log.Info($"已获取飞书 tenant_access_token，有效期 {lifetime.TotalMinutes:0} 分钟（到期前 5 分钟自动刷新）");
            return _cachedToken;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _lastError = $"换取令牌失败：{ex.GetType().Name}: {ex.Message}";
            _log.Warn(_lastError);
            return null;
        }
        finally
        {
            TokenGate.Release();
        }
    }

    /// <summary>把换取令牌的失败码翻译成人能看懂的话。</summary>
    private static string DescribeTokenFailure(int code, string? msg) => code switch
    {
        10003 => $"换取令牌失败：参数不合法（App ID 格式可能不对）—— {msg}",
        10014 => $"换取令牌失败：App Secret 不正确 —— {msg}",
        10012 => $"换取令牌失败：App ID 不存在 —— {msg}",
        _ => $"换取令牌失败：飞书返回 code={code} {msg}",
    };

    // ---------------------------------------------------------------- 接口调用

    private async Task<(List<FeishuTableField>? Fields, string? Error)> ListFieldsAsync(
        HttpClient http,
        string token,
        CancellationToken cancellationToken)
    {
        var url = $"{ApiRoot}/bitable/v1/apps/{Uri.EscapeDataString(_options.AppToken.Trim())}"
                  + $"/tables/{Uri.EscapeDataString(_options.TableId.Trim())}/fields?page_size=200";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content
            .ReadFromJsonAsync<FeishuFieldListResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            return (null, $"读取字段失败：HTTP {(int)response.StatusCode}，响应无法解析");
        }

        if (body.Code != 0)
        {
            return (null, DescribeApiFailure("读取数据表字段", body.Code, body.Msg));
        }

        var fields = body.Data?.Items?
            .Where(f => !string.IsNullOrWhiteSpace(f.FieldName))
            .Select(f => new FeishuTableField(f.FieldName!, f.Type))
            .ToList() ?? [];

        return (fields, null);
    }

    private async Task<(bool Ok, string? Error)> BatchCreateAsync(
        HttpClient http,
        string token,
        IReadOnlyList<FeishuRecordPayload> records,
        CancellationToken cancellationToken)
    {
        var url = $"{ApiRoot}/bitable/v1/apps/{Uri.EscapeDataString(_options.AppToken.Trim())}"
                  + $"/tables/{Uri.EscapeDataString(_options.TableId.Trim())}/records/batch_create";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new FeishuBatchCreatePayload { Records = records }, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        FeishuBaseResponse? body;
        try
        {
            body = await response.Content
                .ReadFromJsonAsync<FeishuBaseResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return (false, $"写入返回 HTTP {(int)response.StatusCode}，响应无法解析");
        }

        if (body is null)
        {
            return (false, $"写入返回 HTTP {(int)response.StatusCode}，响应为空");
        }

        if (body.Code == 0)
        {
            return (true, null);
        }

        return (false, DescribeApiFailure($"写入 {records.Count} 条", body.Code, body.Msg));
    }

    /// <summary>
    /// 把多维表格接口的错误码翻译成人话。
    /// 99991672 和 91403 是权限配置的两个必踩点，分开说清"缺哪一层"。
    /// </summary>
    private static string DescribeApiFailure(string action, int code, string? msg)
    {
        var hint = code switch
        {
            99991672 => "缺少 API 权限范围：到开发者后台给这个应用开通多维表格的读写权限（bitable:app）",
            91403 => "文档级授权不足：需将该应用添加为目标多维表格的可编辑协作者（仅开通 API 权限不足）",
            1254045 => "字段名不匹配：飞书按字段名精确匹配，请核对“字段映射”中的名称是否与表内完全一致",
            1254005 => "数据表不存在，或 app_token / table_id 不正确",
            1254302 or 1254303 => "应用不是该多维表格的协作者，或未授予编辑权限",
            _ => null,
        };

        var message = $"{action}失败：飞书返回 code={code}";
        if (hint is not null)
        {
            message += $" —— {hint}";
        }

        if (!string.IsNullOrWhiteSpace(msg))
        {
            message += $"（{msg}）";
        }

        return message;
    }

    private HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SnapLog", "1.0"));
        return client;
    }

    private static string? ResolveSecret(FeishuOptions options)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(SecretVariableName(options));
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        return string.IsNullOrWhiteSpace(options.AppSecret) ? null : options.AppSecret.Trim();
    }

    // ---------------------------------------------------------------- 传输模型

    private sealed class FeishuTokenPayload
    {
        [JsonPropertyName("app_id")]
        public string AppId { get; set; } = string.Empty;

        [JsonPropertyName("app_secret")]
        public string AppSecret { get; set; } = string.Empty;
    }

    private sealed class FeishuTokenResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("msg")]
        public string? Msg { get; set; }

        [JsonPropertyName("tenant_access_token")]
        public string? TenantAccessToken { get; set; }

        [JsonPropertyName("expire")]
        public int Expire { get; set; }
    }

    private class FeishuBaseResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("msg")]
        public string? Msg { get; set; }
    }

    private sealed class FeishuFieldListResponse : FeishuBaseResponse
    {
        [JsonPropertyName("data")]
        public FeishuFieldListData? Data { get; set; }
    }

    private sealed class FeishuFieldListData
    {
        [JsonPropertyName("items")]
        public List<FeishuFieldItem>? Items { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }
    }

    private sealed class FeishuFieldItem
    {
        [JsonPropertyName("field_name")]
        public string? FieldName { get; set; }

        [JsonPropertyName("field_id")]
        public string? FieldId { get; set; }

        [JsonPropertyName("type")]
        public int Type { get; set; }
    }

    private sealed class FeishuBatchCreatePayload
    {
        [JsonPropertyName("records")]
        public IReadOnlyList<FeishuRecordPayload> Records { get; set; } = [];
    }

    private sealed class FeishuRecordPayload
    {
        [JsonPropertyName("fields")]
        public Dictionary<string, object?> Fields { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>表里的一个字段（名字 + 类型码）。</summary>
    public sealed record FeishuTableField(string Name, int Type)
    {
        /// <summary>把类型码说成人话，界面下拉里显示它，方便用户判断这一列能不能放数字/日期。</summary>
        public string TypeName => Type switch
        {
            1 => "文本",
            2 => "数字",
            3 => "单选",
            4 => "多选",
            5 => "日期",
            7 => "复选框",
            11 => "人员",
            13 => "电话",
            15 => "超链接",
            17 => "附件",
            18 => "关联",
            19 => "公式",
            20 => "创建时间",
            21 => "修改时间",
            1001 => "创建时间",
            1002 => "修改时间",
            _ => $"类型 {Type}",
        };
    }
}
