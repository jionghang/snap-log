using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SnapLog.Configuration;

/// <summary>整个程序的配置，与 appsettings.json 一一对应。</summary>
public sealed class AppOptions
{
    public CaptureOptions Capture { get; set; } = new();

    public TriggerOptions Triggers { get; set; } = new();

    public OcrOptions Ocr { get; set; } = new();

    public StorageOptions Storage { get; set; } = new();

    public SummarizationOptions Summarization { get; set; } = new();

    public LoggingOptions Logging { get; set; } = new();

    public UiOptions Ui { get; set; } = new();

    public FeishuOptions Feishu { get; set; } = new();
}

/// <summary>抓取到内存中的图像如何缩放与留档。</summary>
public sealed class CaptureOptions
{
    [Category("抓取"), DisplayName("图像最长边上限(像素)"),
     Description("截图按最长边等比缩放，超过此值将被缩小。图像过大会拖慢识别，并可能超出引擎上限。")]
    public int MaxImageDimension { get; set; } = 2560;

    [Category("抓取"), DisplayName("保存截图文件"),
     Description("开启后每次抓取保存一张截图，可在记录查看器中直接打开；关闭后记录仅保留文字。默认开启。")]
    public bool SaveImages { get; set; } = true;

    [Category("抓取"), DisplayName("截图文件目录"),
     Description("留空则使用数据目录下的 images 子目录。支持环境变量，例如 %USERPROFILE%\\Pictures\\SnapLog。")]
    public string ImageDirectory { get; set; } = "";

    [Category("抓取"), DisplayName("截图保留天数"),
     Description("超过该天数的截图文件将被自动删除，记录本身不受影响。填 0 表示永久保留。截图占用磁盘较大，建议设置上限。")]
    public int ImageRetentionDays { get; set; } = 30;

    [Category("抓取"), DisplayName("截图格式"),
     Description("仅在开启“保存截图文件”时生效。Png 无损、体积较大；Jpeg 体积约为 Png 的三分之一。")]
    public ImageFormatKind ImageFormat { get; set; } = ImageFormatKind.Png;
}

public enum ImageFormatKind
{
    Png,
    Jpeg,
}

/// <summary>什么时候触发一次抓取。</summary>
public sealed class TriggerOptions
{
    [Category("触发"), DisplayName("触发模式"),
     Description("ForegroundWindowChange=跟随前台窗口切换；Interval=仅按固定间隔；Both=两者都启用。")]
    public TriggerMode Mode { get; set; } = TriggerMode.Both;

    [Category("触发"), DisplayName("延时截图时长(毫秒)"),
     Description("窗口首次出现后等待此时长；期间切换窗口会重新计时，等待结束后仍为同一窗口才截图。默认 5000 毫秒。")]
    public int ForegroundSettleMilliseconds { get; set; } = 5000;

    [Category("触发"), DisplayName("抓取周期(分钟)"),
     Description("同一窗口在每个周期内仅抓取一次。设为 10 时：窗口在 10 分钟内反复切回不会重复抓取，下一周期再次打开才会记录。")]
    public int CaptureCycleMinutes { get; set; } = 10;

    [Category("触发"), DisplayName("两次抓取最小间隔(秒)"),
     Description("限流值，避免快速连续切换窗口时频繁抓取。")]
    public int MinSecondsBetweenCaptures { get; set; } = 20;

    [Category("触发"), DisplayName("定时抓取间隔(秒)"),
     Description("定时抓取的周期，默认 300 秒（5 分钟）。")]
    public int IntervalSeconds { get; set; } = 300;

    [Category("触发"), DisplayName("排除的进程名"),
     Description("不记录这些进程的窗口（不含 .exe 后缀，不区分大小写）。")]
    public string[] ExcludedProcesses { get; set; } = ["snaplog"];

    [Category("触发"), DisplayName("排除的窗口标题"),
     Description("标题完全匹配这些字符串时跳过。空标题窗口始终会被跳过。")]
    public string[] ExcludedWindowTitles { get; set; } = ["Program Manager", "Windows Default Lock Screen"];
}

public enum TriggerMode
{
    ForegroundWindowChange,
    Interval,
    Both,
}

/// <summary>OCR 相关设置。</summary>
public sealed class OcrOptions
{
    [Category("OCR"), DisplayName("OCR 引擎"),
     Description("PaddleOcr = 离线 PaddleOCR（PP-OCR 模型，中文准确率明显更高，代价是体积和内存）。"
                 + "WindowsMedia = 系统内置 OCR（零额外依赖、最省内存，但中文错字较多）。"
                 + "Disabled = 仅记录时间与窗口标题，不进行文字识别。")]
    public OcrEngineKind Engine { get; set; } = OcrEngineKind.PaddleOcr;

    [Category("OCR"), DisplayName("识别方式"),
     Description("Realtime = 抓取后立即识别，结果即时可用，识别期间占用 CPU（默认）。"
                 + "ScheduledBatch = 白天只截图，到设定时间统一补识别，把 CPU 占用集中到空闲时段，"
                 + "代价是记录中的文字需等批次完成后才出现。定时模式必须保存截图，程序会自动开启该开关。")]
    public OcrRunMode Mode { get; set; } = OcrRunMode.Realtime;

    [Category("OCR"), DisplayName("定时识别时间"),
     Description("仅“识别方式 = ScheduledBatch”时生效，格式 HH:mm，例如 02:00。"
                 + "程序在那个时间点之后第一次运行时执行，当天只跑一次。")]
    public string BatchTimeOfDay { get; set; } = "02:00";

    [Category("OCR"), DisplayName("识别前目标分辨率(最长边)"),
     Description("仅对 Windows 内置 OCR 生效。界面字号较小时，放大对识别率提升明显："
                 + "实测 1650px 宽窗口提高到 2400px 后，常见词组正确数由 4/17 增至 13/17。"
                 + "PaddleOCR 的检测模型会自行缩放，无需预先放大。填 0 表示使用引擎允许的最大值。")]
    public int TargetLongestSide { get; set; } = 2400;

    [Category("OCR"), DisplayName("放大倍数上限"),
     Description("仅对 Windows 内置 OCR 生效。小于目标分辨率的图最多放大这么多倍。")]
    public double MaxUpscale { get; set; } = 3.0;

    [Category("OCR"), DisplayName("最小文本长度"),
     Description("识别结果长度小于该值视为无有效内容。")]
    public int MinTextLength { get; set; } = 4;

    [Category("OCR"), DisplayName("丢弃空记录"),
     Description("开启后，未识别到文字的记录不写入数据库，避免产生大量空记录。")]
    public bool DropEmptyRecords { get; set; } = true;

    // ---------------------------------------------------------------- PaddleOCR

    [Category("OCR · PaddleOCR"), DisplayName("模型"),
     Description("都是随程序离线发布的中文模型。"
                 + "V6Tiny = PP-OCRv6 tiny（推荐：实测命中率 22/24，最快）。"
                 + "V6Small = PP-OCRv6 small（同样 22/24，慢 3 倍）。"
                 + "V5Mobile = PP-OCRv5 mobile（21/24，慢 6 倍，不推荐）。")]
    public PaddleModelKind PaddleModel { get; set; } = PaddleModelKind.V6Tiny;

    [Category("OCR · PaddleOCR"), DisplayName("CPU 线程数"),
     Description("限制推理线程数，避免占满 CPU。实测 4 线程已接近收益上限。")]
    public int PaddleThreads { get; set; } = 4;

    [Category("OCR · PaddleOCR"), DisplayName("启用 MKLDNN 加速"),
     Description("关闭（默认）：一张 1650x925 截图约 4 秒、稳定占用约 145MB，适合内存有限的机器。"
                 + "开启：同样内容约 1.4 秒、稳定占用约 640MB，速度提高约 3 至 4 倍。"
                 + "两者识别准确率一致（实测均为 22/24）。")]
    public bool PaddleEnableMkldnn { get; set; }

    [Category("OCR · PaddleOCR"), DisplayName("检测模型最长边"),
     Description("传给 PaddleOCR 检测模型的 max_side_len。越小越快、越省内存，小字越容易漏。")]
    public int PaddleMaxSideLength { get; set; } = 2048;

    /// <summary>是否要做文字识别。留着这个属性，让引擎侧少一处判断。</summary>
    public bool Enabled => Engine != OcrEngineKind.Disabled;
}

public enum OcrEngineKind
{
    /// <summary>离线 PaddleOCR（PP-OCR 模型）。</summary>
    PaddleOcr,

    /// <summary>系统内置 Windows.Media.Ocr。</summary>
    WindowsMedia,

    /// <summary>关闭识别。</summary>
    Disabled,
}

/// <summary>PaddleOCR 的模型选择。数值不参与序列化，配置里存的是名字。</summary>
public enum PaddleModelKind
{
    /// <summary>PP-OCRv6 tiny（随包发布，实测最优）。</summary>
    V6Tiny,

    /// <summary>PP-OCRv6 small（随包发布）。</summary>
    V6Small,

    /// <summary>PP-OCRv5 mobile（随包发布）。</summary>
    V5Mobile,
}

/// <summary>文字识别什么时候做。</summary>
public enum OcrRunMode
{
    /// <summary>抓到时立刻识别。</summary>
    Realtime,

    /// <summary>白天只截图，到设定时间统一补识别。</summary>
    ScheduledBatch,
}

/// <summary>落盘位置。</summary>
public sealed class StorageOptions
{
    [Category("存储"), DisplayName("数据目录"),
     Description("留空则使用 %LOCALAPPDATA%\\SnapLog。数据库、总结、日志、截图都在这个目录下。")]
    public string DataDirectory { get; set; } = "";

    [Category("存储"), DisplayName("数据库文件名"),
     Description("SQLite 数据库文件，相对于数据目录。")]
    public string DatabaseFileName { get; set; } = "activity.db";

    [Category("存储"), DisplayName("首次启动导入旧版 CSV"),
     Description("开启后，如果数据目录下存在旧版的 activity.csv 且数据库里还没有记录，会把它导入进来。"
                 + "导入不会删除原 CSV 文件。")]
    public bool ImportLegacyCsv { get; set; } = true;

    [Category("存储"), DisplayName("记录保留天数"),
     Description("超过该天数的记录将从数据库删除。填 0 表示永久保留。记录含文字内容，长期积累后体积较大，建议按需设置上限。")]
    public int RecordRetentionDays { get; set; }
}

/// <summary>调用大模型做总结的设置。</summary>
public sealed class SummarizationOptions
{
    [Category("总结"), DisplayName("启用大模型总结"),
     Description("关闭时不发起任何网络请求，仅保留本地记录。")]
    public bool Enabled { get; set; }

    [Browsable(false)]
    public bool ConsentGranted { get; set; }

    /// <summary>
    /// 模型列表，按顺序尝试：第一个失败（重试也用尽）就换下一个。
    /// 可以配多个网关做冗余，或者主用便宜的、备用能力更强的。
    /// </summary>
    [Category("总结"), DisplayName("模型列表"),
     Description("按列表顺序调用：前一个重试用尽仍失败时，自动切换到下一个。")]
    public List<LlmProviderOptions> Providers { get; set; } = [];

    // ---- 以下四项是旧版单模型配置，仅用于把老配置迁移进 Providers，界面上不显示 ----

    [Browsable(false)]
    public string Endpoint { get; set; } = "";

    [Browsable(false)]
    public string Model { get; set; } = "";

    [Browsable(false)]
    public string ApiKey { get; set; } = "";

    [Browsable(false)]
    public string ApiKeyEnvironmentVariable { get; set; } = "";

    // ---------------------------------------------------------------- 发送内容

    [Category("总结"), DisplayName("发送内容"),
     Description("TextOnly = 仅发送识别文字（消耗最低）。"
                 + "ImageOnly = 仅发送截图，由模型直接读图（适合识别效果不佳的界面，消耗更高）。"
                 + "TextAndImage = 文字与截图一并发送（效果最好、消耗最高，需模型支持视觉）。")]
    public LlmPayloadMode PayloadMode { get; set; } = LlmPayloadMode.TextOnly;

    [Category("总结"), DisplayName("附带图片最多张数"),
     Description("仅对 ImageOnly / TextAndImage 生效。图片计费较高，用于限制单次请求的成本上限。")]
    public int MaxImages { get; set; } = 6;

    [Category("总结"), DisplayName("同窗口图片最小间隔(秒)"),
     Description("仅对 ImageOnly / TextAndImage 生效。同一窗口在该时间内的多张截图只发送一张，"
                 + "避免重复发送长时间静止的画面。填 0 表示不限制。")]
    public int ImageSampleSeconds { get; set; } = 300;

    [Category("总结"), DisplayName("图片清晰度"),
     Description("Auto = 由服务端按尺寸决定；Low = 固定低清（约 85 token/张，消耗最低）；High = 高清（消耗为数倍）。"
                 + "文字主要由识别文本提供时，Low 通常足够。")]
    public LlmImageDetail ImageDetail { get; set; } = LlmImageDetail.Auto;

    // ---------------------------------------------------------------- 提示词与容错

    [Category("总结"), DisplayName("送入模型的最大字符数"),
     Description("仅对文字部分生效。超出部分按时间倒序截断，只保留最近的记录。")]
    public int MaxInputCharacters { get; set; } = 24000;

    [Category("总结"), DisplayName("最多送入的记录条数"),
     Description("限制条数可以进一步控制 token 消耗。")]
    public int MaxRecords { get; set; } = 200;

    [Category("总结"), DisplayName("总结输出语言"),
     Description("直接写进提示词，例如 zh-CN 、en-US 。")]
    public string Language { get; set; } = "zh-CN";

    [Category("总结"), DisplayName("附加要求"),
     Description("追加到提示词末尾的自定义要求，例如“按项目分组”“只保留待办事项”。")]
    public string ExtraInstructions { get; set; } = "";

    [Category("总结"), DisplayName("失败重试次数"),
     Description("同一个模型内部的重试次数（不含首次尝试）。用指数退避，避免撞上速率限制时连环失败。")]
    public int RetryCount { get; set; } = 2;

    [Category("总结"), DisplayName("重试起始间隔(秒)"),
     Description("第一次重试等这么久，之后按 2 倍递增（3 秒 → 6 秒 → 12 秒）。")]
    public int RetryDelaySeconds { get; set; } = 3;

    [Category("总结"), DisplayName("单次请求超时(秒)"),
     Description("超过这个时间没返回就当作失败并进入重试。带图请求会慢一些，别设太小。")]
    public int RequestTimeoutSeconds { get; set; } = 120;

    // ---------------------------------------------------------------- 定时与提示词

    [Category("总结"), DisplayName("定时生成总结"),
     Description("开启后每天到设定时间自动生成一次，结果与失败原因均记入“总结历史”。"
                 + "生成会实际把内容发送到模型接口，请先确认隐私提示。")]
    public bool ScheduleEnabled { get; set; }

    [Category("总结"), DisplayName("定时生成时间"),
     Description("格式 HH:mm，例如 18:30。程序在那个时间点之后第一次运行时执行，当天只跑一次。")]
    public string ScheduleTimeOfDay { get; set; } = "18:30";

    [Category("总结"), DisplayName("系统提示词（留空用内置模板）"),
     Description("填写后以此作为 system 提示词，完全替换内置模板。"
                 + "“工作项目清单”仍会自动附加（属结构化数据，不受模板影响）。"
                 + "界面提供“填入内置模板”按钮，可先载入默认内容再修改。")]
    public string SystemPromptOverride { get; set; } = "";

    [Category("总结"), DisplayName("工作项目列表"),
     Description("自定义的工作任务清单。生成总结时随提示词发送，模型据此将活动按项目归类。"
                 + "留空也可使用，此时不做项目归类。")]
    public List<WorkProjectOptions> WorkProjects { get; set; } = [];
}

/// <summary>一条自己维护的工作项目，用来给活动归类。</summary>
public sealed class WorkProjectOptions
{
    [Category("工作项目"), DisplayName("项目名称"),
     Description("简短名称，会出现在总结的项目归类部分。")]
    public string Name { get; set; } = "";

    [Category("工作项目"), DisplayName("说明"),
     Description("描述该项目的工作内容、涉及的系统或关键词。描述越具体，模型归类越准确。")]
    public string Description { get; set; } = "";

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Description) ? Name : $"{Name} —— {Description}";

    public WorkProjectOptions Clone() => new() { Name = Name, Description = Description };
}

/// <summary>
/// 把大模型生成的总结写入飞书多维表格。
/// 分三层：数据（app_token + table_id）、通道（应用凭证 → tenant_access_token）、触发（定时/生成后/手动）。
/// </summary>
public sealed class FeishuOptions
{
    [Category("飞书"), DisplayName("启用飞书写入"),
     Description("开启后可按定时或手动方式把总结写入飞书多维表格。"
                 + "写入内容为总结正文及其元数据（时间、触发来源、模型、条数等），将上传至飞书，"
                 + "请确认该表仅本人或可信成员可访问。")]
    public bool Enabled { get; set; }

    [Category("飞书"), DisplayName("定时写入时间"),
     Description("格式 HH:mm，例如 19:00。程序在那个时间点之后第一次运行时执行，当天只跑一次。")]
    public string ScheduleTimeOfDay { get; set; } = "19:00";

    [Category("飞书"), DisplayName("生成总结后立即写入"),
     Description("每次成功生成总结后立即写入飞书；配合“定时生成总结”即为每天自动汇总。关闭后仅按定时写入或手动写入执行。")]
    public bool PushAfterSummary { get; set; }

    // ---------------------------------------------------------------- 通道层

    [Category("飞书 · 应用凭证"), DisplayName("App ID"),
     Description("开发者后台里自建应用的 App ID，形如 cli_xxxxxxxxxxxxx。")]
    public string AppId { get; set; } = "";

    [Category("飞书 · 应用凭证"), DisplayName("App Secret"),
     Description("自建应用的 App Secret。留空则从下方环境变量读取。该值等同于应用身份，建议仅保存在环境变量中。")]
    public string AppSecret { get; set; } = "";

    [Category("飞书 · 应用凭证"), DisplayName("读取 App Secret 的环境变量名"),
     Description("默认 SNAPLOG_FEISHU_APP_SECRET。该环境变量有值时优先于上面的明文配置。")]
    public string AppSecretEnvironmentVariable { get; set; } = "SNAPLOG_FEISHU_APP_SECRET";

    [Category("飞书 · 应用凭证"), DisplayName("鉴权方式"),
     Description("使用应用身份 tenant_access_token（默认）。令牌有效期 7200 秒，程序会缓存并提前 5 分钟刷新。")]
    public FeishuAuthMode AuthMode { get; set; } = FeishuAuthMode.TenantAccessToken;

    // ---------------------------------------------------------------- 数据层

    [Category("飞书 · 数据"), DisplayName("多维表格 app_token"),
     Description("整张多维表格的标识，取自多维表格 URL，形如 "
                 + "https://xxx.feishu.cn/base/xxxxxxxxxxxxxxxxxxxxxxxx 中的 xxxxxxxxxxxxxxxxxxxxxxxx。"
                 + "同一张多维表格下的多张数据表共用。")]
    public string AppToken { get; set; } = "";

    [Category("飞书 · 数据"), DisplayName("数据表 table_id"),
     Description("每张数据表各有一个，形如 tblxxxxxxxxxxxxxx。更换或新建数据表后需重新获取。")]
    public string TableId { get; set; } = "";

    [Category("飞书 · 数据"), DisplayName("字段映射"),
     Description("总结字段与飞书列名的对应关系，一条一行，可随时增删。"
                 + "无对应列的映射将“飞书字段名”留空即可跳过，不必逐条填写。"
                 + "飞书按字段名精确匹配，名称中含多余空格或换行会报 FieldNameNotFound（1254045）；"
                 + "写入前程序会调用“列出字段”核对并指出不匹配项。")]
    public List<FeishuFieldMapping> FieldMappings { get; set; } = FeishuFieldMapping.CreateDefault();

    [Category("飞书 · 数据"), DisplayName("写入范围（天）"),
     Description("仅写入该天数内生成且尚未写入飞书的总结。默认 1 表示只写当天，"
                 + "避免首次开启时一次性导入全部历史总结；需补录历史时将该值调大。")]
    public int PushLookbackDays { get; set; } = 1;

    [Category("飞书 · 数据"), DisplayName("总结正文最大长度"),
     Description("总结正文可能较长，而飞书单元格有长度上限。超出部分将被截断并标注。")]
    public int MaxTextLength { get; set; } = 2000;

    [Category("飞书 · 数据"), DisplayName("每批写入条数"),
     Description("飞书单次批量写入上限为 500 条，分批提交可降低失败时的影响范围。")]
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// 载入配置时发现字段映射还是旧版的记录字段、已被自动换成总结字段的默认映射。
    /// 只是用来提示用户重新核对列名，不写进配置文件。
    /// </summary>
    [JsonIgnore]
    public bool LegacyMappingsReplaced { get; set; }
}

/// <summary>鉴权方式。当前只实现应用身份；留成枚举是为了以后要加用户身份时不用改配置结构。</summary>
public enum FeishuAuthMode
{
    /// <summary>应用身份 tenant_access_token（推荐，不需要用户授权）。</summary>
    TenantAccessToken,
}

/// <summary>
/// 一条字段映射：把总结的哪个字段写到飞书表的哪一列。
/// 两条都可以留空其一：飞书字段名留空 = 这条不写（表里没有那一列时就这么处理）。
/// </summary>
public sealed class FeishuFieldMapping
{
    [Category("字段映射"), DisplayName("总结字段"),
     Description("SnapLog 侧的字段，从下拉列表中选择。")]
    public string RecordField { get; set; } = "";

    [Category("字段映射"), DisplayName("飞书字段名"),
     Description("飞书数据表中的列名，必须完全一致（含空格与符号）。留空表示不写入该列。")]
    public string FeishuField { get; set; } = "";

    public override string ToString() =>
        string.IsNullOrWhiteSpace(FeishuField)
            ? $"{DescribeField(RecordField)}（未指定飞书列，跳过）"
            : $"{DescribeField(RecordField)} → {FeishuField}";

    public FeishuFieldMapping Clone() => new() { RecordField = RecordField, FeishuField = FeishuField };

    /// <summary>可映射的总结字段清单。全部可选，默认映射只是一份常见写法。</summary>
    public static IReadOnlyList<string> AvailableFields { get; } =
    [
        nameof(Storage.SummaryRun.StartedAt),
        nameof(Storage.SummaryRun.Trigger),
        nameof(Storage.SummaryRun.Provider),
        nameof(Storage.SummaryRun.RecordCount),
        nameof(Storage.SummaryRun.ImageCount),
        nameof(Storage.SummaryRun.ElapsedMilliseconds),
        nameof(Storage.SummaryRun.Attempts),
        nameof(Storage.SummaryRun.Markdown),
        nameof(Storage.SummaryRun.Preview),
        nameof(Storage.SummaryRun.SavedPath),
        nameof(Storage.SummaryRun.Message),
    ];

    /// <summary>字段的中文说明。下拉和列表里显示它，免得用户对着 StartedAt 猜意思。</summary>
    public static string DescribeField(string field) => field switch
    {
        nameof(Storage.SummaryRun.StartedAt) => "总结生成时间",
        nameof(Storage.SummaryRun.Trigger) => "触发来源",
        nameof(Storage.SummaryRun.Provider) => "模型",
        nameof(Storage.SummaryRun.RecordCount) => "记录条数",
        nameof(Storage.SummaryRun.ImageCount) => "截图张数",
        nameof(Storage.SummaryRun.ElapsedMilliseconds) => "耗时(毫秒)",
        nameof(Storage.SummaryRun.Attempts) => "尝试次数",
        nameof(Storage.SummaryRun.Markdown) => "总结正文",
        nameof(Storage.SummaryRun.Preview) => "总结摘要(前120字)",
        nameof(Storage.SummaryRun.SavedPath) => "本地文件路径",
        nameof(Storage.SummaryRun.Message) => "结果说明",
        _ => field,
    };

    /// <summary>
    /// 默认映射。刻意只给几个最常用的字段：每多一条就多一个"表里可能没有这一列"的机会，
    /// 用户按自己表里的列名改、加、删都行。
    /// </summary>
    public static List<FeishuFieldMapping> CreateDefault() =>
    [
        new() { RecordField = nameof(Storage.SummaryRun.StartedAt), FeishuField = "时间" },
        new() { RecordField = nameof(Storage.SummaryRun.Trigger), FeishuField = "触发来源" },
        new() { RecordField = nameof(Storage.SummaryRun.Provider), FeishuField = "模型" },
        new() { RecordField = nameof(Storage.SummaryRun.RecordCount), FeishuField = "记录条数" },
        new() { RecordField = nameof(Storage.SummaryRun.Markdown), FeishuField = "总结" },
    ];
}

/// <summary>一个可用的模型配置。列表里按顺序尝试。</summary>
public sealed class LlmProviderOptions
{
    [Category("模型"), DisplayName("名称"),
     Description("便于识别的名称，会显示在日志与总结文件中。")]
    public string Name { get; set; } = "主模型";

    [Category("模型"), DisplayName("启用")]
    public bool Enabled { get; set; } = true;

    [Category("模型"), DisplayName("接口地址(OpenAI 兼容)"),
     Description("例如 https://api.openai.com/v1 、https://api.deepseek.com/v1 、"
                 + "https://dashscope.aliyuncs.com/compatible-mode/v1 。只填域名会自动补 /v1。")]
    public string Endpoint { get; set; } = "https://api.openai.com/v1";

    [Category("模型"), DisplayName("模型名称"),
     Description("例如 gpt-4o-mini / deepseek-chat / qwen-plus 。使用图片时需具备视觉能力（如 gpt-4o、qwen-vl-max）。")]
    public string Model { get; set; } = "gpt-4o-mini";

    [Category("模型"), DisplayName("API Key"),
     Description("留空则从下方环境变量读取。建议使用环境变量，避免明文写入磁盘。")]
    public string ApiKey { get; set; } = "";

    [Category("模型"), DisplayName("读取密钥的环境变量名"),
     Description("默认 SNAPLOG_OPENAI_API_KEY。该环境变量有值时优先于上面的明文配置。")]
    public string ApiKeyEnvironmentVariable { get; set; } = "SNAPLOG_OPENAI_API_KEY";

    /// <summary>列表和日志里的显示名。</summary>
    public override string ToString() =>
        $"{(Enabled ? "" : "[已停用] ")}{(string.IsNullOrWhiteSpace(Name) ? "(未命名)" : Name)} · {Model}";

    public LlmProviderOptions Clone() => new()
    {
        Name = Name,
        Enabled = Enabled,
        Endpoint = Endpoint,
        Model = Model,
        ApiKey = ApiKey,
        ApiKeyEnvironmentVariable = ApiKeyEnvironmentVariable,
    };
}

/// <summary>送给模型的载荷包含什么。</summary>
public enum LlmPayloadMode
{
    /// <summary>只发 OCR 文字。</summary>
    TextOnly,

    /// <summary>只发截图。</summary>
    ImageOnly,

    /// <summary>文字 + 截图。</summary>
    TextAndImage,
}

public enum LlmImageDetail
{
    Auto,
    Low,
    High,
}

/// <summary>日志设置。</summary>
public sealed class LoggingOptions
{
    [Category("日志"), DisplayName("写日志文件"),
     Description("关闭后只保留界面上的实时状态。")]
    public bool Enabled { get; set; } = true;

    [Category("日志"), DisplayName("日志级别"),
     Description("Trace/Debug/Info/Warn/Error 。排查问题时用 Debug 。")]
    public string Level { get; set; } = "Info";

    [Category("日志"), DisplayName("日志保留天数"),
     Description("超期日志在启动时清理。")]
    public int RetentionDays { get; set; } = 7;
}

/// <summary>界面行为。</summary>
public sealed class UiOptions
{
    [Category("界面"), DisplayName("启动后最小化到托盘"),
     Description("关闭则启动时直接弹出主窗口。")]
    public bool StartMinimizedToTray { get; set; } = true;

    [Category("界面"), DisplayName("关闭按钮收起而不退出"),
     Description("主窗口关闭时仅收起到托盘，进程继续记录；退出需通过主窗口或托盘菜单。")]
    public bool CloseToTrayInsteadOfExit { get; set; } = true;
}
