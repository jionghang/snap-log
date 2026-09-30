using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SnapLog.Imaging;
using SnapLog.Interop;

namespace SnapLog.Capture;

/// <summary>一次截图尝试的结果。<see cref="Image"/> 非空时由调用方负责释放。</summary>
public sealed record CaptureResult(Bitmap? Image, string Method, string? Error) : IDisposable
{
    public bool Success => Image is not null;

    public static CaptureResult Failed(string error) => new(null, "none", error);

    public void Dispose() => Image?.Dispose();
}

/// <summary>单个抓取策略的体检结果，用于 --selftest 定位"为什么抓不到画面"。</summary>
public sealed record CaptureAttempt(
    string Name,
    bool Produced,
    bool Blank,
    int Width,
    int Height,
    int DistinctColors,
    string? Error);

/// <summary>
/// 抓取指定窗口的画面。
/// 依次尝试 PrintWindow → BitBlt → 屏幕拷贝，并对"纯色画面"做兜底判断：
/// 前两者对受保护内容（DRM、部分硬件加速窗口）会返回一张全黑图，
/// 这种情况必须继续往下试，否则 OCR 会一直拿到空结果。
/// </summary>
public static class WindowCapturer
{
    /// <summary>
    /// 逐个跑一遍所有策略并回报统计信息，不返回位图（内部已释放）。
    /// 只给诊断用，正常运行走 <see cref="Capture"/>。
    /// </summary>
    public static IReadOnlyList<CaptureAttempt> Diagnose(IntPtr hwnd)
    {
        var bounds = GetWindowBounds(hwnd);
        if (bounds.Width < 8 || bounds.Height < 8)
        {
            return [new CaptureAttempt("bounds", false, false, bounds.Width, bounds.Height, 0, "窗口尺寸过小或句柄无效")];
        }

        var attempts = new List<CaptureAttempt>();

        foreach (var (name, produce) in BuildStrategies(hwnd, bounds))
        {
            try
            {
                using var produced = produce();
                if (produced is null)
                {
                    attempts.Add(new CaptureAttempt(name, false, false, 0, 0, 0, "没有产出位图"));
                    continue;
                }

                attempts.Add(new CaptureAttempt(
                    name,
                    true,
                    LooksBlank(produced),
                    produced.Width,
                    produced.Height,
                    CountSampledColors(produced),
                    null));
            }
            catch (Exception ex) when (ex is ExternalException or OutOfMemoryException or ArgumentException)
            {
                attempts.Add(new CaptureAttempt(name, false, false, 0, 0, 0, ex.Message));
            }
        }

        return attempts;
    }

    public static CaptureResult Capture(IntPtr hwnd, int maxDimension)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return CaptureResult.Failed("窗口句柄无效");
        }

        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            return CaptureResult.Failed("窗口不可见");
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            return CaptureResult.Failed("窗口已最小化");
        }

        var bounds = GetWindowBounds(hwnd);
        if (bounds.Width < 8 || bounds.Height < 8)
        {
            return CaptureResult.Failed($"窗口尺寸过小 {bounds.Width}x{bounds.Height}");
        }

        Bitmap? chosen = null;
        var method = "none";
        string? note = null;

        foreach (var (name, produce) in BuildStrategies(hwnd, bounds))
        {
            Bitmap? attempt;
            try
            {
                attempt = produce();
            }
            catch (Exception ex) when (ex is ExternalException or OutOfMemoryException or ArgumentException)
            {
                note = $"{name} 失败：{ex.Message}";
                continue;
            }

            if (attempt is null)
            {
                continue;
            }

            if (!LooksBlank(attempt))
            {
                chosen?.Dispose();
                chosen = attempt;
                method = name;
                note = null;
                break;
            }

            // 先留一张纯色图兜底，但如果后面有更好的就丢掉它。
            if (chosen is null)
            {
                chosen = attempt;
                method = $"{name}(纯色)";
                note = "抓到的画面是单一颜色，可能是受保护或尚未渲染的窗口";
            }
            else
            {
                attempt.Dispose();
            }
        }

        if (chosen is null)
        {
            return CaptureResult.Failed(note ?? "所有抓取方式都失败");
        }

        if (maxDimension > 0 && Math.Max(chosen.Width, chosen.Height) > maxDimension)
        {
            try
            {
                var scaled = BitmapScaler.Downscale(chosen, maxDimension);
                chosen.Dispose();
                chosen = scaled;
            }
            catch (Exception ex) when (ex is ExternalException or OutOfMemoryException or ArgumentException)
            {
                // 缩放失败不等于抓取失败：退回原始尺寸，让记录不受影响。
                note = (note is null ? string.Empty : note + "；") + $"缩放失败，保留原尺寸：{ex.Message}";
            }
        }

        return new CaptureResult(chosen, method, note);
    }

    private static IEnumerable<(string Name, Func<Bitmap?> Produce)> BuildStrategies(IntPtr hwnd, Rectangle bounds)
    {
        yield return ("PrintWindow", () => TryPrintWindow(hwnd, bounds));
        yield return ("BitBlt", () => TryBitBlt(hwnd, bounds));
        yield return ("ScreenCopy", () => TryScreenCopy(bounds));
    }

    /// <summary>
    /// PW_RENDERFULLCONTENT 让 DWM 把合成后的内容画出来，
    /// 因此被遮挡、甚至不在最前的窗口也能抓到正确画面。
    /// </summary>
    private static Bitmap? TryPrintWindow(IntPtr hwnd, Rectangle bounds)
        => TryWithDib(bounds.Width, bounds.Height,
            hdc => NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT));

    private static Bitmap? TryBitBlt(IntPtr hwnd, Rectangle bounds)
    {
        var windowDc = NativeMethods.GetWindowDC(hwnd);
        if (windowDc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return TryWithDib(bounds.Width, bounds.Height,
                hdc => NativeMethods.BitBlt(hdc, 0, 0, bounds.Width, bounds.Height, windowDc, 0, 0, NativeMethods.SRCCOPY));
        }
        finally
        {
            NativeMethods.ReleaseDC(hwnd, windowDc);
        }
    }

    /// <summary>兜底方案：直接抄屏幕。只在前台窗口可见时才有意义，窗口离屏部分会被裁掉。</summary>
    private static Bitmap? TryScreenCopy(Rectangle bounds)
    {
        var visible = Rectangle.Intersect(bounds, SystemInformation.VirtualScreen);
        if (visible.Width < 8 || visible.Height < 8)
        {
            return null;
        }

        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                visible.Left,
                visible.Top,
                visible.Left - bounds.Left,
                visible.Top - bounds.Top,
                visible.Size,
                CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 建一个自上而下的 32bpp DIB 让 GDI 往上画。
    /// 显式用 Format32bppRgb 声明"没有 alpha 通道"，否则存 PNG 会出现整张透明。
    /// </summary>
    private static Bitmap? TryWithDib(int width, int height, Func<IntPtr, bool> draw)
    {
        if (width < 1 || height < 1)
        {
            return null;
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                return null;
            }

            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // 负高度表示自上而下，省掉一次整图翻转
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            };

            dib = NativeMethods.CreateDIBSection(screenDc, ref header, NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            previous = NativeMethods.SelectObject(memoryDc, dib);
            if (!draw(memoryDc))
            {
                return null;
            }

            return CopyOut(bits, width, height);
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previous);
            }

            if (dib != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(dib);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>把 DIB 像素逐行搬进托管位图，搬完就能安全释放 DIB。</summary>
    private static Bitmap CopyOut(IntPtr sourceBits, int width, int height)
    {
        var target = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var targetData = target.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);

        try
        {
            var rowBytes = width * 4;
            var row = new byte[rowBytes];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(sourceBits, y * rowBytes), row, 0, rowBytes);
                Marshal.Copy(row, 0, IntPtr.Add(targetData.Scan0, y * targetData.Stride), rowBytes);
            }
        }
        finally
        {
            target.UnlockBits(targetData);
        }

        return target;
    }

    /// <summary>16x16 采样网格判断是否整幅只有一个颜色。</summary>
    private static bool LooksBlank(Bitmap bitmap)
    {
        var reference = bitmap.GetPixel(0, 0);
        foreach (var (x, y) in SampleGrid(bitmap))
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.R != reference.R || pixel.G != reference.G || pixel.B != reference.B)
            {
                return false;
            }
        }

        return true;
    }

    private static int CountSampledColors(Bitmap bitmap)
    {
        var seen = new HashSet<int>();
        foreach (var (x, y) in SampleGrid(bitmap))
        {
            seen.Add(bitmap.GetPixel(x, y).ToArgb());
        }

        return seen.Count;
    }

    private static IEnumerable<(int X, int Y)> SampleGrid(Bitmap bitmap)
    {
        var stepX = Math.Max(1, bitmap.Width / 16);
        var stepY = Math.Max(1, bitmap.Height / 16);

        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                yield return (x, y);
            }
        }
    }

    private static Rectangle GetWindowBounds(IntPtr hwnd)
    {
        // DWM 的扩展边框去掉了 Win10 那圈看不见的拖拽调整边框，坐标更贴合肉眼所见。
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var dwmBounds,
                Marshal.SizeOf<RECT>()) == 0
            && dwmBounds.Width > 0
            && dwmBounds.Height > 0)
        {
            return new Rectangle(dwmBounds.Left, dwmBounds.Top, dwmBounds.Width, dwmBounds.Height);
        }

        if (NativeMethods.GetWindowRect(hwnd, out var bounds) && bounds.Width > 0 && bounds.Height > 0)
        {
            return new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }

        return Rectangle.Empty;
    }
}
