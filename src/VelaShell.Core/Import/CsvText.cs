using System.Buffers;
using System.Text;

namespace VelaShell.Core.Import;

/// <summary>
/// 写 CSV 的单元格与行:RFC 4180 的引号规则,加上 CSV 注入防护。连接文件(<see cref="SessionCsv" />)
/// 与审计日志导出共用这一份口径。
/// </summary>
public static class CsvText
{
    /// <summary>以单引号开头即被 Excel 当作文本的那几个「公式起始字符」。</summary>
    internal const string FormulaLeads = "=+-@\t\r";

    /// <summary>字段里出现就得加引号的字符:三种分隔符、引号与换行。</summary>
    private static readonly SearchValues<char> QuoteTriggers = SearchValues.Create(",\"\r\n;\t");

    /// <summary>
    /// 把一个值写成 CSV 单元格:以 <c>= + - @</c>、制表符、回车开头的先补一个单引号(Excel 就把它当文本而不是公式),
    /// 再按需加引号。
    /// </summary>
    /// <remarks>
    /// 写进 CSV 的东西多半来自别处:连接名、备注可能来自别人给的文件,审计日志的详情里有远端主机的原话。
    /// 不补这个单引号,一个 <c>=HYPERLINK(…)</c> 用 Excel 打开就成了可点击的东西。
    /// </remarks>
    /// <param name="value">原值。</param>
    /// <returns>可以直接拼进一行的单元格文本。</returns>
    public static string Cell(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string escaped = value.Length > 0 && FormulaLeads.Contains(value[0], StringComparison.Ordinal) ? "'" + value : value;
        bool needs = escaped.Length > 0
                     && (escaped.AsSpan().IndexOfAny(QuoteTriggers) >= 0
                         || char.IsWhiteSpace(escaped[0])
                         || char.IsWhiteSpace(escaped[^1]));
        return needs ? "\"" + escaped.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : escaped;
    }

    /// <summary>追加一行(逗号分隔,CRLF 结尾)。</summary>
    /// <param name="builder">目标。</param>
    /// <param name="cells">各单元格的原值。</param>
    public static void AppendRow(StringBuilder builder, IReadOnlyList<string> cells)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(cells);
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append(Cell(cells[i]));
        }
        builder.Append("\r\n");
    }
}
