namespace SnapLog.Storage;

/// <summary>
/// 把 \r\n 和单独的 \r 统一成 \n 之后再交给 CsvHelper。
///
/// 为什么需要它：CsvHelper 读取时用的是 <c>CsvConfiguration.NewLine</c> 作为行终止符，
/// 而不是自动识别换行——两种做法都有实测踩到的坑：
///   - 按 CRLF 匹配 → LF 换行的文件被当成一行，250 条记录导入结果为 0 条；
///   - 按 LF 匹配   → CRLF 文件里带引号的多行字段被切坏，整段文字丢失（字数变成 0）。
/// 在流入解析器之前统一换行，两种文件都变成"只有 \n"的规范形态，
/// 解析器按 \n 断行即可同时正确切分记录和保留多行字段。
///
/// 逐块转换而不是逐字符，几十万行的历史 CSV 也不会有明显开销。
/// </summary>
internal sealed class NormalizedTextReader : TextReader
{
    private readonly TextReader _inner;
    private bool _disposed;

    public NormalizedTextReader(TextReader inner)
    {
        _inner = inner;
    }

    public override int Peek()
    {
        var c = _inner.Peek();
        return c == '\r' ? '\n' : c;
    }

    public override int Read()
    {
        var c = _inner.Read();
        if (c != '\r')
        {
            return c;
        }

        // \r\n 算一个换行，把 \n 一起吃掉。
        if (_inner.Peek() == '\n')
        {
            _inner.Read();
        }

        return '\n';
    }

    /// <summary>块读取：一次拿一块就地转换，避免逐字符调用的开销。</summary>
    public override int Read(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (count <= 0)
        {
            return 0;
        }

        var read = _inner.Read(buffer, index, count);
        if (read == 0)
        {
            return 0;
        }

        var written = 0;
        for (var i = 0; i < read; i++)
        {
            var c = buffer[index + i];
            if (c != '\r')
            {
                buffer[index + written++] = c;
                continue;
            }

            if (i == read - 1)
            {
                // 块尾的 \r 可能是被切开的 \r\n，看一眼下一字符再决定要不要吞。
                if (_inner.Peek() == '\n')
                {
                    _inner.Read();
                }
            }
            else if (buffer[index + i + 1] == '\n')
            {
                i++;
            }

            buffer[index + written++] = '\n';
        }

        return written;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
