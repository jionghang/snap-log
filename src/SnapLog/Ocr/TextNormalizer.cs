using System.Text;

namespace SnapLog.Ocr;

/// <summary>
/// OCR 结果的文本清洗。
/// Windows 内置 OCR 对中文会在字与字之间插空格（"屏 幕 活 动"），
/// 这是它的分词方式，直接落进 CSV 会变成一片噪声，必须还原成连续中文。
/// </summary>
public static class TextNormalizer
{
    /// <summary>把多行识别结果拼成一段文本：逐行归一化、丢空行、去连续重复行。</summary>
    public static string ComposeFromLines(IEnumerable<string> lines)
    {
        var result = new List<string>();
        string? previous = null;

        foreach (var raw in lines)
        {
            var line = NormalizeLine(raw);
            if (line.Length == 0 || line == previous)
            {
                continue;
            }

            result.Add(line);
            previous = line;
        }

        return string.Join('\n', result);
    }

    /// <summary>去掉中日韩字符之间被 OCR 插入的空格，拉丁文之间的空格保持原样。</summary>
    public static string NormalizeLine(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(line.Length);
        var previous = '\0';

        for (var i = 0; i < line.Length; i++)
        {
            var current = line[i];
            if (current == ' '
                && IsCjkOrFullWidth(previous)
                && i + 1 < line.Length
                && IsCjkOrFullWidth(line[i + 1]))
            {
                continue;
            }

            builder.Append(current);
            previous = current;
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// 送入大模型前再压一遍：去掉纯符号行、合并连续空行、限制单条长度。
    /// 目的是省 token，不是为了好看。
    /// </summary>
    public static string Condense(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var kept = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || HasNoWordCharacters(line))
            {
                continue;
            }

            kept.Add(line);
        }

        var joined = string.Join('\n', kept);
        return joined.Length <= maxLength ? joined : joined[..maxLength] + "…(已截断)";
    }

    private static bool HasNoWordCharacters(string line)
    {
        foreach (var c in line)
        {
            if (char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCjkOrFullWidth(char c) =>
        c is >= '\u4E00' and <= '\u9FFF'   // CJK 统一表意文字
            or >= '\u3400' and <= '\u4DBF' // 扩展 A 区
            or >= '\uF900' and <= '\uFAFF' // 兼容表意文字
            or >= '\u3000' and <= '\u303F' // CJK 标点
            or >= '\uFF00' and <= '\uFFEF'; // 全角字符
}
