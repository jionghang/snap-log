using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapLog.Configuration;

/// <summary>
/// 程序自己的运行状态：界面标记、定时任务上次跑的时间。
///
/// 为什么不放进 appsettings.json：那份文件是**用户配置**，用户目录里的版本会覆盖
/// 随程序发布的默认值。程序为了记一个"提示看过了"就去写整份配置，会导致
/// 以后每次升级改默认值都被这份旧快照挡住——开发过程中被这件事坑了两次
/// （换了 OCR 引擎却发现没生效）。所以拆开：程序只写这个状态文件，
/// appsettings.json 只在用户点“保存设置”时才写。
/// </summary>
public sealed class AppState
{
    [JsonPropertyName("firstRunNoticeShown")]
    public bool FirstRunNoticeShown { get; set; }

    /// <summary>
    /// 各定时任务最后一次执行的本地时间，键是任务的稳定标识。
    /// 记下来是为了"一天只跑一次"能跨重启保持——否则电脑一天重启三次就会总结三次。
    /// </summary>
    [JsonPropertyName("jobLastRunLocal")]
    public Dictionary<string, DateTime> JobLastRunLocal { get; set; } = [];
}

public static class AppStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), Options);
                if (state is not null)
                {
                    state.JobLastRunLocal ??= [];
                    return state;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 状态文件坏了不值得打扰用户：最多是多弹一次首次提示、或者定时任务补跑一次。
        }

        return new AppState();
    }

    public static void Save(string path, AppState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记状态失败不影响使用。
        }
    }
}
