using SnapLog.Interop;
using SnapLog.Ocr;

namespace SnapLog.Ui;

/// <summary>
/// 识别测试的结果预览：左边是抓到的图，右边是识别出的文字。
/// 光看一句"识别成功"没用——用户真正想确认的是"这段文字认得对不对"。
/// </summary>
internal sealed class OcrPreviewForm : Form
{
    private readonly Bitmap _image;

    public OcrPreviewForm(WindowSnapshot snapshot, string captureMethod, Bitmap image, OcrOutcome outcome)
    {
        // 自己留一份副本：调用方用的是 using，出了这个方法图就被释放了。
        // 克隆失败不崩——照样能看文字，只是看不到图。
        _image = Clone(image);

        Text = $"识别测试 · {snapshot.ProcessName}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 520);
        Size = new Size(1100, 700);
        Icon = IconFactory.AppIcon;
        ShowInTaskbar = false;

        BuildLayout(snapshot, captureMethod, outcome);
    }

    private void BuildLayout(WindowSnapshot snapshot, string captureMethod, OcrOutcome outcome)
    {
        var headerText = $"{snapshot.ProcessName} | {snapshot.ClassName} | {snapshot.WindowTitle}"
                         + Environment.NewLine
                         + $"抓取方式 {captureMethod}　图像 {_image.Width}×{_image.Height}　"
                         + $"识别 {outcome.LineCount} 行 / {outcome.Text.Length} 字　耗时 {outcome.Elapsed.TotalMilliseconds:0} ms";

        if (outcome.Warning is not null)
        {
            headerText += Environment.NewLine + "提示：" + outcome.Warning;
        }

        var header = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10, 8, 10, 6),
            Text = headerText,
        };

        var picture = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = _image,
            BackColor = SystemColors.ControlDarkDark,
        };

        var text = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Consolas", 10f),
            WordWrap = true,
            BackColor = SystemColors.Window,
            Text = outcome.Text.Length == 0 ? "(没有识别到文字)" : outcome.Text,
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Panel1MinSize = 200,
            Panel2MinSize = 200,
        };
        split.Panel1.Controls.Add(picture);
        split.Panel2.Controls.Add(text);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 6, 10, 10),
        };

        var close = new Button { Text = "关闭", Width = 88, Height = 30, DialogResult = DialogResult.OK };
        var copy = new Button { Text = "复制文字", Width = 100, Height = 30 };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(outcome.Text);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // 剪贴板被别的进程占用，忽略。
            }
        };

        buttons.Controls.Add(close);
        buttons.Controls.Add(copy);

        Controls.Add(split);
        Controls.Add(buttons);
        Controls.Add(header);

        AcceptButton = close;
        CancelButton = close;

        Shown += (_, _) =>
        {
            try
            {
                split.SplitterDistance = Math.Max(split.Panel1MinSize, split.Width / 2);
            }
            catch (InvalidOperationException)
            {
                // 布局还没稳定，用默认值。
            }
        };
    }

    private static Bitmap Clone(Bitmap image)
    {
        try
        {
            return new Bitmap(image);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException
                                   or OutOfMemoryException
                                   or ArgumentException)
        {
            return BlankFallback(image);
        }
    }

    private static Bitmap BlankFallback(Bitmap source)
    {
        var width = Math.Max(1, Math.Min(source.Width, 1600));
        var height = Math.Max(1, Math.Min(source.Height, 900));
        var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Black);
            using var pen = new Pen(Color.Gray, 2f);
            graphics.DrawRectangle(pen, 0, 0, width - 1, height - 1);
            graphics.DrawLine(pen, 0, 0, width, height);
            graphics.DrawLine(pen, width, 0, 0, height);
        }

        return bitmap;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _image.Dispose();
        }

        base.Dispose(disposing);
    }
}
