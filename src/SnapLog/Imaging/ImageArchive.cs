using System.Drawing.Imaging;
using SnapLog.Configuration;

namespace SnapLog.Imaging;

/// <summary>
/// 可选地把截图留档成图片文件。抓取主流程和 --selftest 都用这里，
/// 避免两处各写一份导致行为不一致。
/// </summary>
public static class ImageArchive
{
    /// <summary>
    /// 保存截图到指定目录，返回完整路径。失败时返回空字符串——留档失败不该影响记录本身。
    /// </summary>
    public static string Save(
        Bitmap image,
        string directory,
        ImageFormatKind format,
        DateTime timestamp,
        string processName)
    {
        Directory.CreateDirectory(directory);

        var safeProcess = string.IsNullOrWhiteSpace(processName) ? "unknown" : Sanitize(processName);
        var extension = format == ImageFormatKind.Jpeg ? ".jpg" : ".png";

        // 精确到毫秒：同一秒内可能既被手动抓取又被定时抓取，不加毫秒会互相覆盖。
        var fileName = $"{timestamp:yyyyMMdd-HHmmssfff}-{safeProcess}{extension}";
        var fullPath = Path.Combine(directory, fileName);

        image.Save(
            fullPath,
            format == ImageFormatKind.Jpeg ? System.Drawing.Imaging.ImageFormat.Jpeg : System.Drawing.Imaging.ImageFormat.Png);

        return fullPath;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. value.Where(c => !invalid.Contains(c))]);
    }
}
