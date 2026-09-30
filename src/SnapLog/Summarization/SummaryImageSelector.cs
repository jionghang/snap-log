using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SnapLog.Configuration;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Summarization;

/// <summary>
/// 挑选要发给模型的截图，并做编码。
///
/// 采样规则（对应配置里的“同窗口图片最小间隔”）：
/// 按时间从新往旧走，同一个窗口在一个间隔内只挑一张。
/// 一个窗口停在那里半小时、期间被定时抓了 6 次，就只发 1 张，而不是 6 张几乎一样的图。
/// 挑完再按时间正序排列，让模型看到的是自然的时间顺序。
///
/// 编码统一用 JPEG（质量 85）：全屏 PNG 通常 150KB+，转 JPEG 后约 100KB，
/// 而视觉模型不需要无损——省下的是请求体积和 token。
/// </summary>
public static class SummaryImageSelector
{
    private const long JpegQuality = 85L;

    /// <summary>
    /// 从最近的记录里挑出要发送的截图。文件不存在或读不出来的会被跳过（并记日志）。
    /// </summary>
    public static IReadOnlyList<SummaryImage> Select(
        IReadOnlyList<ActivityRecord> records,
        SummarizationOptions options,
        AppPaths paths,
        FileLogger log)
    {
        if (options.MaxImages <= 0)
        {
            return [];
        }

        var sampleSeconds = options.ImageSampleSeconds;
        var picked = new List<SummaryImage>();
        var lastSeenByWindow = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // 从最新往回走：优先保留最近的画面，而且采样时"最近的那张"才代表这个窗口的当前状态。
        for (var i = records.Count - 1; i >= 0; i--)
        {
            if (picked.Count >= options.MaxImages)
            {
                break;
            }

            var record = records[i];
            if (string.IsNullOrWhiteSpace(record.ImagePath))
            {
                continue;
            }

            var windowKey = $"{record.ProcessName}\u0001{record.WindowTitle}";
            if (sampleSeconds > 0
                && lastSeenByWindow.TryGetValue(windowKey, out var lastPicked)
                && (lastPicked - record.Timestamp).TotalSeconds < sampleSeconds)
            {
                continue;
            }

            var fullPath = paths.ResolveStoredImagePath(record.ImagePath);
            if (!File.Exists(fullPath))
            {
                log.Debug($"总结跳过截图（文件不存在）：{fullPath}");
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(fullPath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            picked.Add(new SummaryImage(
                fullPath, record.Timestamp, record.ProcessName, record.WindowTitle, size));
            lastSeenByWindow[windowKey] = record.Timestamp;
        }

        // 反回时间正序
        picked.Reverse();
        return picked;
    }

    /// <summary>把图片读出来转成 JPEG 字节。失败返回 null。</summary>
    public static byte[]? EncodeAsJpeg(string path, FileLogger log)
    {
        try
        {
            using var source = new Bitmap(path);
            using var buffer = new MemoryStream();

            var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            if (encoder is null)
            {
                source.Save(buffer, ImageFormat.Jpeg);
            }
            else
            {
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
                source.Save(buffer, encoder, parameters);
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            log.Warn($"读取截图失败，已跳过该图：{path} —— {ex.Message}");
            return null;
        }
    }
}
