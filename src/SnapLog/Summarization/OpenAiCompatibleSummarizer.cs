using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;
using OpenAI.Chat;
using SnapLog.Configuration;
using SnapLog.Diagnostics;

namespace SnapLog.Summarization;

/// <summary>
/// 走 OpenAI 官方 .NET 客户端，接口地址可替换，因此任何 OpenAI 兼容网关都能用。
///
/// 容错策略（配置里的“模型列表”+“失败重试次数”）：
///   外层：按顺序遍历启用中的模型配置，前一个彻底失败就换下一个；
///   内层：同一个模型内部按指数退避重试若干次。
///
/// 重试只针对"可能自愈"的错误（超时、429、5xx、网络故障）。
/// 401/400/404 这类改不了的事实性问题重试没有意义，直接跳到下一个模型——
/// 换个网关可能就正常了（比如某个模型在 A 网关没有、在 B 网关有）。
/// </summary>
public sealed class OpenAiCompatibleSummarizer : ISummarizer
{
    private readonly SummarizationOptions _options;
    private readonly FileLogger _log;
    private readonly List<ProviderRuntime> _providers;

    private OpenAiCompatibleSummarizer(
        SummarizationOptions options,
        List<ProviderRuntime> providers,
        FileLogger log)
    {
        _options = options;
        _providers = providers;
        _log = log;

        Description = providers.Count == 1
            ? providers[0].Describe()
            : $"{providers[0].Describe()} 等 {providers.Count} 个模型";
    }

    public string Description { get; }

    /// <summary>
    /// 构造。返回 false 时 <paramref name="error"/> 说明原因（没有启用中的模型、或所有模型都缺密钥）。
    /// </summary>
    public static bool TryCreate(
        SummarizationOptions options,
        FileLogger log,
        out OpenAiCompatibleSummarizer? summarizer,
        out string? error)
    {
        summarizer = null;
        error = null;

        var providers = new List<ProviderRuntime>();
        var problems = new List<string>();

        foreach (var configured in options.Providers)
        {
            if (!configured.Enabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(configured.Model))
            {
                problems.Add($"{ConfiguredName(configured)}：没有配置模型名称");
                continue;
            }

            var apiKey = ResolveApiKey(configured);
            if (apiKey is null)
            {
                var variableName = string.IsNullOrWhiteSpace(configured.ApiKeyEnvironmentVariable)
                    ? "SNAPLOG_OPENAI_API_KEY"
                    : configured.ApiKeyEnvironmentVariable;
                problems.Add($"{ConfiguredName(configured)}：找不到 API Key（环境变量 {variableName} 或配置里的明文）");
                continue;
            }

            Uri endpoint;
            try
            {
                endpoint = NormalizeEndpoint(configured.Endpoint);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"{ConfiguredName(configured)}：{ex.Message}");
                continue;
            }

            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = endpoint,
                NetworkTimeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 10, 900)),

                // 关掉 SDK 自带的重试，重试策略统一由本类负责。
                // 否则两层重试会相乘（配置 2 次 × SDK 4 次 = 单次请求最多 8 次），
                // 实测过：配置写"重试 1 次"却因为 SDK 内部重试跑了 17 秒。
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            };

            var client = new ChatClient(configured.Model.Trim(), new ApiKeyCredential(apiKey), clientOptions);
            providers.Add(new ProviderRuntime(configured, client, endpoint.Host));
        }

        if (providers.Count == 0)
        {
            error = options.Providers.All(p => !p.Enabled)
                ? "“模型列表”里没有启用中的模型。请至少启用一个。"
                : "没有可用的模型配置：\n" + string.Join("\n", problems.Select(p => "  · " + p));
            return false;
        }

        if (problems.Count > 0)
        {
            log.Warn($"以下模型配置被跳过：\n{string.Join("\n", problems.Select(p => "  · " + p))}");
        }

        summarizer = new OpenAiCompatibleSummarizer(options, providers, log);
        return true;
    }

    public async Task<SummaryCompletion> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken)
    {
        var attempts = new List<SummaryAttempt>();

        foreach (var provider in _providers)
        {
            var maxAttempts = Math.Clamp(_options.RetryCount, 0, 10) + 1;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var text = await SendOnceAsync(provider, request, cancellationToken).ConfigureAwait(false);
                    return new SummaryCompletion(text, provider.Describe(), attempt);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var transient = IsTransient(ex, cancellationToken);
                    attempts.Add(new SummaryAttempt(provider.Describe(), attempt, transient, Describe(ex)));

                    // 不是"等一等就会好"的错误，重试同一个模型没意义，直接换下一个。
                    if (!transient)
                    {
                        _log.Warn($"{provider.Describe()} 第 {attempt} 次失败（不重试）：{Describe(ex)}");
                        break;
                    }

                    if (attempt >= maxAttempts)
                    {
                        _log.Warn($"{provider.Describe()} 重试 {maxAttempts} 次仍失败：{Describe(ex)}");
                        break;
                    }

                    var delay = BackoffDelay(attempt);
                    _log.Warn($"{provider.Describe()} 第 {attempt} 次失败，{delay.TotalSeconds:0.#} 秒后重试：{Describe(ex)}");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var summary = new SummaryFailedException(
            $"所有模型都没能完成请求（共尝试 {attempts.Count} 次）。", attempts);

        _log.Error($"生成总结失败：{summary.Message}\n{summary.DescribeAttempts()}");
        throw summary;
    }

    /// <summary>指数退避：起始间隔 × 2^(次数-1)，最长 60 秒。</summary>
    private TimeSpan BackoffDelay(int attempt)
    {
        var baseSeconds = Math.Max(0, _options.RetryDelaySeconds);
        var seconds = baseSeconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, 60));
    }

    private async Task<string> SendOnceAsync(
        ProviderRuntime provider,
        SummaryRequest request,
        CancellationToken cancellationToken)
    {
        // 图片只编码一次：下面的降级重试（换参数）不重复做这件重活。
        var imageParts = new List<ChatMessageContentPart>();
        foreach (var image in request.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = SummaryImageSelector.EncodeAsJpeg(image.Path, _log);
            if (bytes is null)
            {
                continue;
            }

            imageParts.Add(ChatMessageContentPart.CreateImagePart(
                BinaryData.FromBytes(bytes),
                "image/jpeg",
                ToDetailLevel(request.ImageDetail)));
        }

        List<ChatMessage> BuildMessages(bool withImages)
        {
            var parts = new List<ChatMessageContentPart>
            {
                ChatMessageContentPart.CreateTextPart(request.UserPrompt),
            };

            if (withImages)
            {
                parts.AddRange(imageParts);
            }

            return
            [
                new SystemChatMessage(request.SystemPrompt),
                new UserChatMessage(parts),
            ];
        }

        // 两类"参数不被接受"的降级重试：去掉 temperature、去掉图片。
        // 每个方向只降一次（标志翻转后不复原），最多多试两轮，不会绕圈。
        float? temperature = DefaultTemperature;
        var withImages = imageParts.Count > 0;
        var imagesOptional = request.Mode != LlmPayloadMode.ImageOnly;

        System.ClientModel.ClientResult<ChatCompletion> response;

        while (true)
        {
            try
            {
                response = await provider.Client
                    .CompleteChatAsync(BuildMessages(withImages), BuildRequestOptions(temperature), cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            catch (ClientResultException ex) when (temperature is not null && MentionsTemperature(ex))
            {
                // 有的模型（例如 Kimi 的 k3）只接受 temperature=1，默认的 0.3 会被 400 拒掉。
                // 这属于"参数不被接受"，拿同样的参数重试没有意义；去掉温度、用服务端默认值再来一次。
                _log.Warn($"{provider.Describe()} 不接受 temperature={temperature}"
                          + $"（{Trim(ex.Message)}），改用服务端默认值重试一次");
                temperature = null;
            }
            catch (ClientResultException ex) when (withImages && imagesOptional && LooksLikeImageRejection(ex.Message))
            {
                // 有的模型只收文字：图片被拒时降级为纯文字，摘要照样能出，只是少了截图校正。
                // 只发图模式不降级：那个模式没有文字正文可发，降级等于发一份空材料，宁可如实报错。
                _log.Warn($"{provider.Describe()} 不接受图片输入（{Trim(ex.Message)}），本次改用纯文字重试");
                withImages = false;
            }
        }

        ChatCompletion completion = response;
        var text = string.Concat(completion.Content
            .Where(part => part.Kind == ChatMessageContentPartKind.Text)
            .Select(part => part.Text));

        if (string.IsNullOrWhiteSpace(text))
        {
            // 空内容是"网关/模型行为"，光看这句话没法判断原因，把判定依据一起写进日志：
            // 结束原因、返回了哪几类内容、以及原始响应体（截断）。
            var kinds = completion.Content.Count == 0
                ? "无内容块"
                : string.Join("、", completion.Content.Select(part => part.Kind.ToString()));
            var raw = DescribeRawResponse(response);
            _log.Warn($"{provider.Describe()} 返回空正文：结束原因={completion.FinishReason}，内容块={kinds}，原始响应={raw}");

            throw new InvalidOperationException(
                completion.FinishReason == ChatFinishReason.Length
                    ? "模型输出被长度上限截断，正文为空。"
                    : $"模型返回了空内容（结束原因：{completion.FinishReason}）。");
        }

        return text.Trim();
    }

    /// <summary>原始响应体，用于诊断网关返回了什么。取不到就返回说明文字，不影响主流程。</summary>
    private static string DescribeRawResponse(ClientResult<ChatCompletion> response)
    {
        try
        {
            var body = response.GetRawResponse().Content.ToString();
            if (string.IsNullOrWhiteSpace(body))
            {
                return "(原始响应为空)";
            }

            body = body.Replace('\r', ' ').Replace('\n', ' ');
            return body.Length <= 600 ? body : body[..600] + "…（已截断）";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ObjectDisposedException)
        {
            return $"(原始响应不可读：{ex.GetType().Name})";
        }
    }

    private static ChatImageDetailLevel ToDetailLevel(LlmImageDetail detail) => detail switch
    {
        LlmImageDetail.Low => ChatImageDetailLevel.Low,
        LlmImageDetail.High => ChatImageDetailLevel.High,
        _ => ChatImageDetailLevel.Auto,
    };

    /// <summary>环境变量优先于明文配置，避免把密钥写进文件。</summary>
    private static string? ResolveApiKey(LlmProviderOptions provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable))
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(provider.ApiKeyEnvironmentVariable.Trim());
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment.Trim();
            }
        }

        return string.IsNullOrWhiteSpace(provider.ApiKey) ? null : provider.ApiKey.Trim();
    }

    /// <summary>
    /// SDK 会在 Endpoint 后面拼 /chat/completions，所以路径要以 v1 结尾。
    /// 填 https://api.deepseek.com 会补成 /v1；填 .../compatible-mode/v1 保持原样。
    /// </summary>
    public static Uri NormalizeEndpoint(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("接口地址为空。");
        }

        var text = raw.Trim().TrimEnd('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"接口地址无效：{raw}");
        }

        if (uri.AbsolutePath.Contains("v1", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        return new Uri($"{text}/v1");
    }

    /// <summary>
    /// 判断这个错误"等一等重试"有没有意义。
    ///
    /// 一定要先拆 AggregateException：SDK 会把连接失败包在里面
    /// （"Retry failed after N tries. (由于目标计算机积极拒绝，无法连接。)"），
    /// 直接看外层类型会把最常见的网络故障判成"不可重试"，重试策略就形同虚设。
    /// </summary>
    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case ClientResultException clientError:
                // Status 0 表示根本没拿到 HTTP 响应（DNS 失败、连接被拒、TLS 握手失败……），
                // SDK 会把它包成 ClientResultException(0)。这类错误最该重试。
                return clientError.Status is 0 or 408 or 409 or 425 or 429 or >= 500;

            case HttpRequestException or TimeoutException or IOException:
                return true;

            // HttpClient 超时也抛 TaskCanceledException；只有外部真的取消时才不算可重试。
            case OperationCanceledException:
                return !cancellationToken.IsCancellationRequested;

            case AggregateException aggregate:
                return aggregate.Flatten().InnerExceptions.Any(inner => IsTransient(inner, cancellationToken));

            default:
                return false;
        }
    }

    /// <summary>默认温度；服务端不接受时去掉这个字段重试（见 SendOnceAsync）。</summary>
    private const float DefaultTemperature = 0.3f;

    /// <summary>请求参数。temperature 传 null 表示不发这个字段、用服务端默认值。</summary>
    private static ChatCompletionOptions BuildRequestOptions(float? temperature) => new()
    {
        Temperature = temperature,
        // 提示词里要求"写完整、写充实"，上限就得留够。
        // 推理型模型（网关把内容放在 reasoning 里）会先花大量 token 盘材料，
        // 上限给小了会出现"全程在推理、正文一个字没有"的截断，只能给足空间。
        MaxOutputTokenCount = 16000,
    };

    /// <summary>400 里明确提到 temperature 的：参数不被接受，换参数重试才有意义。</summary>
    private static bool MentionsTemperature(ClientResultException ex) =>
        ex.Message.Contains("temperature", StringComparison.OrdinalIgnoreCase);

    /// <summary>错误信息明确指向图片输入的：该模型不收图，降级为纯文字重试。
    /// 独立成静态方法，自检可以用样例消息验证判定，不依赖真实接口。</summary>
    internal static bool LooksLikeImageRejection(string message)
    {
        string[] hints = ["image", "vision", "multimodal", "多模态", "图片", "图像"];
        return hints.Any(hint => message.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(Exception exception) => exception switch
    {
        ClientResultException clientError => $"接口返回 {clientError.Status}：{Trim(clientError.Message)}",
        _ => $"{exception.GetType().Name}: {Trim(exception.Message)}",
    };

    private static string Trim(string message) =>
        message.Length <= 300 ? message.Replace('\n', ' ') : message[..300].Replace('\n', ' ') + "…";

    private static string ConfiguredName(LlmProviderOptions provider) =>
        string.IsNullOrWhiteSpace(provider.Name) ? "(未命名模型)" : provider.Name;

    /// <summary>一个模型配置 + 已经建好的客户端。</summary>
    private sealed record ProviderRuntime(LlmProviderOptions Options, ChatClient Client, string Host)
    {
        public string Describe() => $"{ConfiguredName(Options)} · {Options.Model} @ {Host}";
    }
}
