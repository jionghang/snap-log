using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SnapLog.Interop;

namespace SnapLog.Ui;

/// <summary>
/// 运行时画图标（托盘与窗口用）。exe 文件图标走编译期嵌入：Assets/snaplog.ico，
/// 与下面的 Draw 是同一枚快门——改了这边的几何参数，要同步重新生成那个 .ico。
/// 全进程共用同一个实例：窗口开开关关不会反复创建 GDI 句柄。
/// </summary>
internal static class IconFactory
{
    private static readonly Lazy<Icon> SharedAppIcon = new(CreateAppIcon, isThreadSafe: true);
    private static readonly Lazy<Avalonia.Controls.WindowIcon> SharedWindowIcon = new(CreateWindowIcon, isThreadSafe: true);

    /// <summary>托盘用的图标。生命周期跟随进程，不归任何窗体所有，窗体不要释放它。</summary>
    public static Icon AppIcon => SharedAppIcon.Value;

    /// <summary>窗口左上角用的图标（Avalonia 需要自己的类型）。</summary>
    public static Avalonia.Controls.WindowIcon WindowIcon => SharedWindowIcon.Value;

    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        Draw(bitmap);

        // GetHicon 拿到的是需要手动销毁的句柄，先克隆成托管 Icon 再销毁，避免句柄泄漏。
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static Avalonia.Controls.WindowIcon CreateWindowIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        Draw(bitmap);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        return new Avalonia.Controls.WindowIcon(stream);
    }

    /// <summary>一枚蓝色的"快门"：圆底 + 表盘弧线 + 中心点。</summary>
    private static void Draw(Bitmap bitmap)
    {
        var size = bitmap.Width;
        var k = size / 32f;   // 全部尺寸按 32 像素的基准等比换算，32 和 64 都能用

        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var background = new SolidBrush(Color.FromArgb(255, 37, 99, 235));
        graphics.FillEllipse(background, 0, 0, size - 1, size - 1);

        using var pen = new Pen(Color.White, 2.6f * k);
        graphics.DrawArc(pen, 7 * k, 7 * k, 18 * k, 18 * k, -55, 110);
        graphics.DrawLine(pen, size / 2f, size / 2f, size / 2f, 8 * k);

        using var center = new SolidBrush(Color.White);
        graphics.FillEllipse(center, 12 * k, 12 * k, 8 * k, 8 * k);
    }
}
