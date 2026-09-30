using System.Drawing;
using System.Drawing.Imaging;
using SnapLog.Capture;

namespace SnapLog.Cli;

/// <summary>
/// 界面截图：把每个窗口、主窗口的每个标签页存成 PNG，用来人工复查布局与文案。
///
/// 用窗口自身的捕获（PrintWindow 优先）而不是屏幕拷贝：窗口被别的窗口挡住也能拿到内容，
/// 也不要求它一定在最前面。走独立的短消息循环，每个窗口显示一会儿、布局稳定后再拍。
/// </summary>
internal static class WindowShots
{
    /// <summary>拍一张窗口图。窗口还没显示或拍出来是空的都返回 null（并把原因写进 error）。</summary>
    public static Bitmap? CaptureWindow(Control control, out string? error)
    {
        error = null;

        if (!control.IsHandleCreated)
        {
            error = "窗口句柄还没建好";
            return null;
        }

        var result = WindowCapturer.Capture(control.Handle, maxDimension: 0);
        if (!result.Success || result.Image is null)
        {
            error = result.Error ?? "捕获失败";
            return null;
        }

        return result.Image;
    }

    /// <summary>存成 PNG（目录不存在就建）。</summary>
    public static string Save(Bitmap image, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".png");
        image.Save(path, ImageFormat.Png);
        return path;
    }
}
