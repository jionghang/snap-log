namespace SnapLog.Storage;

/// <summary>一条记录的结局。以字符串形式存进数据库，便于直接用 SQLite 工具查看。</summary>
public enum RecordStatus
{
    /// <summary>抓取并识别成功。</summary>
    Ok,

    /// <summary>抓到了画面但没识别出有效文字，按配置可能不落库。</summary>
    NoText,

    /// <summary>抓取或识别环节出错。</summary>
    Error,

    /// <summary>
    /// 只截了图、还没识别（“识别方式 = 每日定时”下的正常状态）。
    /// 批次任务跑完会变成 Ok / NoText / Error。
    /// </summary>
    Pending,
}
