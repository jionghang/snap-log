using System.Drawing.Drawing2D;
using SnapLog.Interop;

namespace SnapLog.Ui;

/// <summary>
/// 运行时画托盘图标，避免为了一个 16x16 的图标往仓库里塞二进制资源。
/// 全进程共用同一个实例：窗体开开关关不会反复创建 GDI 句柄。
/// </summary>
internal static class IconFactory
{
    private static readonly Lazy<Icon> SharedAppIcon = new(CreateAppIcon, isThreadSafe: true);

    /// <summary>应用图标。生命周期跟随进程，不归任何窗体所有，窗体不要释放它。</summary>
    public static Icon AppIcon => SharedAppIcon.Value;

    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(255, 30, 92, 168));
            graphics.FillEllipse(background, 1, 1, 30, 30);

            using var pen = new Pen(Color.White, 2.6f);
            graphics.DrawArc(pen, 7, 7, 18, 18, -55, 110);
            graphics.DrawLine(pen, 16, 16, 16, 8);

            using var center = new SolidBrush(Color.White);
            graphics.FillEllipse(center, 12, 12, 8, 8);
        }

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
}
