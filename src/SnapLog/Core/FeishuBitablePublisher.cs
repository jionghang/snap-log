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
    public static FeishuPushResult Fail(string message) => new(false, message, 0, 0);
}

/// <summary>
/// 把记录写进飞书多维表格。
///
/// 分三层：
///   ① 数据层：app_token（整张多维表格）+ table_id（一张数据表）
///   ② 通道层：App ID + App Secret → tenant_access_token → 多维表格开放接口
///   ③ 触发层：定时任务 / 手动（由 ScheduledJobsService 与界面按钮负责）
///
/// 三个"踩过坑"的点都在这里落实了：
///   1. 用应用身份 tenant_access_token，令牌缓存并提前 5 分钟刷新，不会每条记录都换令牌。
///   2. 权限要两层同时满足：API 权限范围（bitable:app）+ 文档级协作授权。
///      缺前者飞书报 99991672（会列出缺哪个权限），缺后者报 91403 Forbidden —— 这两个码在本类里被翻译成人话。
///   3. 写入前先调「列出字段」核对字段名：飞书按名称精确匹配，
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
            return "没有填飞书应用的 App ID。";
        }

        if (ResolveSecret(options) is null)
        {
            return $"拿不到 App Secret：请填 App Secret，或先设置环境变量 {SecretVariableName(options)}。";
        }

        if (string.IsNullOrWhiteSpace(options.AppToken))
        {
            return "没有填多维表格的 app_token。";
        }

        if (string.IsNullOrWhiteSpace(options.TableId))
        {
            return "没有填数据表的 table_id。";
        }

        if (!TimeOnly.TryParse(options.ScheduleTimeOfDay, out _))
        {
            return $"推送时间「{options.ScheduleTimeOfDay}」不是合法的 HH:mm 格式。";
        }

        return null;
    }

    /// <summary>
    /// 只做验证，不写入：换一次令牌 + 调「列出字段」，顺便核对字段映射。
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
                lines.Add("⚠ 这些映射在表里找不到同名字段，推送会失败：" + string.Join("、", missing));
                lines.Add("　请按表里的实际列名改「字段映射」，注意空格和换行也要一致。");
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

    /// <summary>把一批记录写进多维表格。返回写成功、跳过（字段留空或值无意义）的条数。</summary>
    public async Task<FeishuPushResult> PushAsync(
        IReadOnlyList<ActivityRecord> records,
        CancellationToken cancellationToken)
    {
        var problem = Validate(_options);
        if (problem is not null)
        {
            return FeishuPushResult.Fail(problem);
        }

        if (records.Count == 0)
        {
            return new FeishuPushResult(true, "没有需要推送的记录", 0, 0);
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
                "数据表里找不到这些字段：" + string.Join("、", missing)
                + Environment.NewLine
                + "飞书按字段名精确匹配（差一个空格都会报 1254045），"
                + "请按表里实际的列名修改「字段映射」，或先在表里加上这些列。");
        }

        var activeMappings = _options.FieldMappings
            .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.RecordField) && !string.IsNullOrWhiteSpace(m.FeishuField))
            .ToList();

        var typeByName = fields.ToDictionary(f => f.Name, f => f.Type, StringComparer.Ordinal);

        var written = 0;
        var skipped = 0;
        var batchSize = Math.Clamp(_options.BatchSize, 1, 500);
        var failures = new List<string>();

        for (var offset = 0; offset < records.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = records.Skip(offset).Take(batchSize).ToList();
            var payloadRecords = new List<FeishuRecordPayload>(batch.Count);

            foreach (var record in batch)
            {
                var fieldsPayload = BuildFields(record, activeMappings, typeByName, out var hasValue);
                if (!hasValue)
                {
                    skipped++;
                    continue;
                }

                payloadRecords.Add(new FeishuRecordPayload { Fields = fieldsPayload });
            }

            if (payloadRecords.Count == 0)
            {
                continue;
            }

            var (ok, error) = await BatchCreateAsync(http, token, payloadRecords, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                written += payloadRecords.Count;
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
                $"写入未全部成功：已写 {written} 条，失败 {failures.Count} 批 —— " + string.Join("；", failures),
                written,
                skipped);
        }

        var summary = $"已写入 {written} 条到飞书多维表格";
        if (skipped > 0)
        {
            summary += $"（{skipped} 条因为映射字段全空被跳过）";
        }

        return new FeishuPushResult(true, summary, written, skipped);
    }

    /// <summary>把一条记录按映射和表里字段类型组装成飞书的 fields 对象。</summary>
    private Dictionary<string, object?> BuildFields(
        ActivityRecord record,
        IReadOnlyList<FeishuFieldMapping> mappings,
        IReadOnlyDictionary<string, int> typeByName,
        out bool hasValue)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        hasValue = false;

        foreach (var mapping in mappings)
        {
            var raw = ReadRecordField(record, mapping.RecordField);
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

    /// <summary>按名字取记录里的字段值。返回 null 表示这个字段不存在或没值。</summary>
    private object? ReadRecordField(ActivityRecord record, string fieldName) => fieldName switch
    {
        nameof(ActivityRecord.Timestamp) => record.Timestamp,
        nameof(ActivityRecord.ProcessName) => Blank(record.ProcessName),
        nameof(ActivityRecord.WindowTitle) => Blank(record.WindowTitle),
        nameof(ActivityRecord.WindowClass) => Blank(record.WindowClass),
        nameof(ActivityRecord.TextLength) => record.TextLength,
        nameof(ActivityRecord.OcrMilliseconds) => record.OcrMilliseconds,
        nameof(ActivityRecord.CaptureMethod) => Blank(record.CaptureMethod),
        nameof(ActivityRecord.Status) => record.Status.ToString(),
        nameof(ActivityRecord.Error) => Blank(record.Error),
        nameof(ActivityRecord.OcrText) => Blank(Truncate(record.OcrText, _options.MaxTextLength)),
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
        return value.Length <= limit ? value : value[..limit] + "…(已截断)";
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
            .Select(m => m.RecordField)
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
            91403 => "文档级授权不足：把这个应用加为目标多维表格的可编辑协作者（只开 API 权限不够）",
            1254045 => "字段名对不上：飞书按字段名精确匹配，请核对「字段映射」里的名字是否与表里完全一致",
            1254005 => "数据表不存在或 app_token/table_id 不对",
            1254302 or 1254303 => "应用不是这张多维表格的协作者，或表格权限里没给编辑权",
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
    private sealed record FeishuTableField(string Name, int Type);
}
