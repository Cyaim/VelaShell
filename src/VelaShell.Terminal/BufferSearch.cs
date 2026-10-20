using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal;

/// <summary>
/// 终端缓冲区内的一次搜索命中,采用绝对行 + **屏幕列**坐标。
/// </summary>
/// <remarks>
/// <see cref="StartCol" /> 与 <see cref="Length" /> 都以列计,不是行文本里的字符下标 ——
/// 渲染层拿它们直接画高亮、拉选区,单位错了就是整体错位。两者的换算见
/// <see cref="TerminalRow.CopyTextTo(Span{char}, Span{int})" />。
/// </remarks>
/// <param name="Row">缓冲区内的绝对行号(含回滚区)。</param>
/// <param name="StartCol">命中起始屏幕列。</param>
/// <param name="Length">命中覆盖的屏幕列数(宽字符按 2 列计)。</param>
public readonly record struct BufferSearchHit(int Row, int StartCol, int Length);

/// <summary>
/// 对整个终端缓冲区(回滚历史 + 当前屏幕)进行不区分大小写的纯文本搜索,
/// 供终端内搜索栏使用(规范 §5.3)。纯逻辑实现,不依赖任何 UI。
/// </summary>
public static class BufferSearch
{
    /// <summary>
    /// 在整个终端缓冲区(回滚历史 + 当前屏幕)中返回 <paramref name="query" /> 的全部不区分大小写匹配项;
    /// 空查询不会产生任何命中。
    /// </summary>
    /// <param name="screen">要搜索的终端缓冲区。</param>
    /// <param name="query">要搜索的纯文本。</param>
    /// <returns>全部命中,以绝对行/字符坐标表示,按缓冲区顺序排列。</returns>
    public static IReadOnlyList<BufferSearchHit> FindAll(TerminalScreen screen, string query) =>
        FindCore(screen, query, [], allRows: true);

    /// <summary>只搜索给定的可见绝对行;-1 表示视口中的空行。</summary>
    /// <remarks>
    /// 与 <see cref="FindAll" /> 不同,自动换行折开、且在视口里紧挨着的几行先接成一段再找:
    /// 跨折行的命中按行切成几段,一个匹配可能对应多个 <see cref="BufferSearchHit" />。
    /// 只给「逐行上色」的选区匹配高亮用 —— 搜索栏要按命中计数、逐个跳转,不能这样拆。
    /// </remarks>
    internal static IReadOnlyList<BufferSearchHit> FindVisible(
        TerminalScreen screen, string query, ReadOnlySpan<int> visibleRows) =>
        FindCore(screen, query, visibleRows, allRows: false);

    private static List<BufferSearchHit> FindCore(
        TerminalScreen screen, string query, ReadOnlySpan<int> visibleRows, bool allRows)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [];
        }
        var hits = new List<BufferSearchHit>();
        int totalRows = screen.TotalRows;

        // 逐行文本写进一个复用缓冲,而不是每行 GetText() 造一个 string:本方法在搜索框
        // 每次按键时对整个缓冲区(回滚 + 屏幕,默认上限 1 万行)跑一遍,原写法每敲一个
        // 字符就分配上万个 StringBuilder + string(长会话下每次按键约 1.6 MB),
        // 直接表现为搜索框打字卡顿。现在整次搜索只有这一个缓冲。
        char[] buffer = new char[Math.Max(256, screen.Columns * 2)];
        // 与 buffer 等长的列映射:第 i 个字符来自第几屏幕列。命中是按字符找到的,
        // 而高亮与选区按列画,两者之间必须有一次显式换算(见 BufferSearchHit)。
        int[] columnOfChar = new int[buffer.Length];
        // 缓冲里这一段由哪几行拼成、各从第几个字符起;只有可见行模式接折行时才多于一行。
        var segment = new List<(int Row, int Start)>(1);
        int length = 0;
        int count = allRows ? totalRows : visibleRows.Length;
        for (int i = 0; i < count; i++)
        {
            int row = allRows ? i : visibleRows[i];
            if (row < 0 || row >= totalRows)
            {
                continue;
            }
            TerminalRow line = screen.ViewLine(row);
            int written;
            while ((written = line.CopyTextTo(buffer.AsSpan(length), columnOfChar.AsSpan(length))) < 0)
            {
                // 组合标记可以让一行的字符数超过列数,接折行也会让一段超过一行,故按需扩容。
                Array.Resize(ref buffer, buffer.Length * 2);
                Array.Resize(ref columnOfChar, buffer.Length);
            }
            segment.Add((row, length));
            length += written;
            // 折到下一行、而下一行就是视口里紧接着的那一行:先不找,接上再说。
            // 两行文本直接相接,与 GetLogicalLineEndingAt 同一口径(只裁从没写过的格)。
            if (!allRows && line.Wrapped && i + 1 < count && visibleRows[i + 1] == row + 1)
            {
                continue;
            }
            Collect(screen, query, buffer.AsSpan(0, length), columnOfChar, segment, hits);
            segment.Clear();
            length = 0;
        }
        return hits;
    }

    private static void Collect(
        TerminalScreen screen,
        string query,
        ReadOnlySpan<char> text,
        int[] columnOfChar,
        List<(int Row, int Start)> segment,
        List<BufferSearchHit> hits)
    {
        int index = 0;
        while (index < text.Length)
        {
            int found = text[index..].IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }
            found += index;
            int end = found + query.Length;
            int k = 0;
            while (k + 1 < segment.Count && segment[k + 1].Start <= found)
            {
                k++;
            }
            // 逐行切段:命中跨折行时,每一行各报一个列区间。
            for (int start = found; start < end; k++)
            {
                int rowEnd = k + 1 < segment.Count ? segment[k + 1].Start : text.Length;
                if (rowEnd <= start)
                {
                    continue;
                }
                int last = Math.Min(end, rowEnd) - 1;
                int row = segment[k].Row;
                int startCol = columnOfChar[start];
                // 末字符所在列的**整格**都要盖住:宽字符的尾格与它同属一个字符单元,
                // 只盖前半格会在中文命中项右边留下半格没上色。
                int endColExclusive = columnOfChar[last] + 1;
                if (screen.ViewLine(row)[endColExclusive].IsWideTrailing)
                {
                    endColExclusive++;
                }
                hits.Add(new(row, startCol, endColExclusive - startCol));
                start = last + 1;
            }
            index = found + Math.Max(1, query.Length);
        }
    }
}
