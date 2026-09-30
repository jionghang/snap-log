namespace SnapLog.Storage;

/// <summary>CSV 里的一行：某一时刻前台窗口 + 屏幕上识别出的文字。</summary>
public sealed class ActivityRecord
{
    /// <summary>数据库主键。从 CSV 导入或新增时为 0，由存储层赋值。</summary>
    public long Id { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string ProcessName { get; set; } = string.Empty;

    public string WindowTitle { get; set; } = string.Empty;

    /// <summary>窗口类名（如 Chrome_WidgetWin_1）。比标题稳定，用来区分窗口类型。</summary>
    public string WindowClass { get; set; } = string.Empty;

    /// <summary>
    /// 派生自 <see cref="OcrText"/>。setter 是空实现，只为让 CSV 反序列化能正常构造映射。
    /// </summary>
    public int TextLength
    {
        get => OcrText.Length;
        set { }
    }

    public long OcrMilliseconds { get; set; }

    public string CaptureMethod { get; set; } = string.Empty;

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    /// <summary>截图文件相对数据目录的路径，只有开启"保存截图文件"时才有值。</summary>
    public string ImagePath { get; set; } = string.Empty;

    public RecordStatus Status { get; set; } = RecordStatus.Ok;

    public string Error { get; set; } = string.Empty;

    public string OcrText { get; set; } = string.Empty;

    /// <summary>CSV 单行里的文字预览，供界面表格显示。</summary>
    public string Preview
    {
        get
        {
            var flat = OcrText.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length <= 80 ? flat : flat[..80] + "…";
        }
    }
}
