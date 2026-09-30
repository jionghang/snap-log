using CsvHelper.Configuration;

namespace SnapLog.Storage;

/// <summary>
/// 列顺序和列名都在这里定义，读写共用同一份映射，避免两边不一致。
/// 表头用中文是为了直接双击 CSV 用 Excel 打开时能看懂。
/// </summary>
public sealed class ActivityRecordMap : ClassMap<ActivityRecord>
{
    public ActivityRecordMap()
    {
        Map(m => m.Timestamp).Index(0).Name("时间").TypeConverterOption.Format("yyyy-MM-dd HH:mm:ss");
        Map(m => m.ProcessName).Index(1).Name("进程");
        Map(m => m.WindowTitle).Index(2).Name("窗口标题");
        Map(m => m.WindowClass).Index(3).Name("窗口类名");
        Map(m => m.TextLength).Index(4).Name("字数");
        Map(m => m.OcrMilliseconds).Index(5).Name("识别耗时(ms)");
        Map(m => m.CaptureMethod).Index(6).Name("抓取方式");
        Map(m => m.ImageWidth).Index(7).Name("图像宽");
        Map(m => m.ImageHeight).Index(8).Name("图像高");
        Map(m => m.ImagePath).Index(9).Name("截图文件");
        Map(m => m.Status).Index(10).Name("状态");
        Map(m => m.Error).Index(11).Name("错误");
        Map(m => m.OcrText).Index(12).Name("识别文字");

        // 纯展示用的派生属性，不进 CSV。
        Map(m => m.Preview).Ignore();
    }
}
