using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SnapLog.Imaging;

/// <summary>位图缩放。截图和 OCR 都需要把图调到引擎好处理的尺寸。</summary>
public static class BitmapScaler
{
    /// <summary>按比例缩放（可放大）；返回新位图，调用方负责释放，原图不动。</summary>
    public static Bitmap Scale(Bitmap source, double factor)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * factor));
        var height = Math.Max(1, (int)Math.Round(source.Height * factor));

        var target = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(target);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return target;
    }

    /// <summary>按最长边等比缩小；返回新位图，调用方负责释放，原图不动。</summary>
    public static Bitmap Downscale(Bitmap source, int maxDimension) =>
        Scale(source, (double)maxDimension / Math.Max(source.Width, source.Height));

    /// <summary>
    /// 把最长边调整到 <paramref name="targetLongestSide"/>（可放大也可缩小）。
    /// 放大倍数上限 <paramref name="maxUpscale"/>，避免小窗口被拉成巨图白白浪费 OCR 时间。
    /// 尺寸已经足够接近时直接返回原图，跳过无意义的重采样。
    /// </summary>
    public static Bitmap ResizeToLongestSide(Bitmap source, int targetLongestSide, double maxUpscale)
    {
        var longest = Math.Max(source.Width, source.Height);
        var factor = (double)targetLongestSide / longest;

        if (factor > maxUpscale)
        {
            factor = maxUpscale;
        }

        // 缩放收益小于 2% 时不值得付出一次重采样。
        if (Math.Abs(factor - 1.0) < 0.02)
        {
            return source;
        }

        return Scale(source, factor);
    }
}
