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
/// 采样分两轮：
/// 第一轮每个窗口全天只挑一张（从新往旧，取该窗口最近的一张）——一个窗口停在那里
/// 半小时被定时抓了 6 次，只发 1 张代表这个窗口，而不是 6 张几乎一样的图。
/// 当天窗口数不足图片上限时进入第二轮：把剩下的名额用来拉开时间覆盖，
/// 每次挑"离已挑画面时间最远"的一条（同窗口与已挑画面间隔小于配置间隔的候选不参与，
/// 避免近似重复）。这样窗口多的日子每个窗口一张，窗口少的日子全天铺开。
/// 挑完按时间正序排列，让模型看到的是自然的时间顺序。
///
/// 编码统一用 JPEG（质量 85）：全屏 PNG 通常 150KB+，转 JPEG 后约 100KB，
/// 而视觉模型不需要无损——省下的是请求体积和 token。
/// </summary>
public static class SummaryImageSelector
{
    private const long JpegQuality = 85L;

    /// <summary>从最近的记录里挑出要发送的截图。文件不存在或读不出来的会被跳过（并记日志）。</summary>
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

        // 先收集全部可用候选（有路径、文件存在、大小可读），由新到旧。
        var candidates = new List<Candidate>();
        foreach (var record in Enumerable.Reverse(records))
        {
            if (string.IsNullOrWhiteSpace(record.ImagePath))
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

            candidates.Add(new Candidate(
                new SummaryImage(fullPath, record.Timestamp, record.ProcessName, record.WindowTitle, size),
                $"{record.ProcessName}\u0001{record.WindowTitle}"));
        }

        var picked = new List<Candidate>();
        var taken = new bool[candidates.Count];

        // 第一轮：每个窗口全天只挑一张。
        var seenWindows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < candidates.Count && picked.Count < options.MaxImages; i++)
        {
            if (seenWindows.Add(candidates[i].WindowKey))
            {
                picked.Add(candidates[i]);
                taken[i] = true;
            }
        }

        // 第二轮：窗口数不够上限（当天就这几个窗口）时补足名额，尽量拉开时间覆盖。
        var sampleSeconds = options.ImageSampleSeconds;
        while (picked.Count < options.MaxImages)
        {
            var bestIndex = -1;
            var bestDistance = TimeSpan.MinValue;

            for (var i = 0; i < candidates.Count; i++)
            {
                if (taken[i])
                {
                    continue;
                }

                var nearest = TimeSpan.MaxValue;
                var blocked = false;

                foreach (var chosen in picked)
                {
                    var gap = (candidates[i].Image.Timestamp - chosen.Image.Timestamp).Duration();

                    if (sampleSeconds > 0
                        && gap.TotalSeconds < sampleSeconds
                        && string.Equals(candidates[i].WindowKey, chosen.WindowKey, StringComparison.OrdinalIgnoreCase))
                    {
                        blocked = true;             // 同窗口、太近：近似重复，不补
                        break;
                    }

                    if (gap < nearest)
                    {
                        nearest = gap;
                    }
                }

                if (!blocked && nearest > bestDistance)
                {
                    bestDistance = nearest;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                break;
            }

            picked.Add(candidates[bestIndex]);
            taken[bestIndex] = true;
        }

        // 反回时间正序
        return picked
            .OrderBy(candidate => candidate.Image.Timestamp)
            .Select(candidate => candidate.Image)
            .ToList();
    }

    private sealed record Candidate(SummaryImage Image, string WindowKey);

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
