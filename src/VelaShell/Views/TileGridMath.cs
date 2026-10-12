using Avalonia.Input;

namespace VelaShell.Views;

/// <summary>
/// 图标视图(等尺寸格子从左到右、从上到下排)的纯几何:每行几格、哪些格子落在一段高度里、
/// 一个矩形框中了哪些格子、方向键从哪一格走到哪一格。
/// <para>
/// 格子尺寸统一,所以一切都能由下标直接算出来,用不着去问已经实现出来的容器 ——
/// 与 <see cref="MarqueeSelectionMath" /> 同一个理由:列表是虚拟化的,视口外的格子根本没有容器。
/// 抽出来也是为了能脱离 UI 单测。
/// </para>
/// </summary>
internal static class TileGridMath
{
    /// <summary>宽度 <paramref name="width" /> 里能并排放几格(至少一格:窗口再窄也得把东西排出来)。</summary>
    /// <param name="width">可用宽度;非有限值时按一格算。</param>
    /// <param name="tileWidth">格子宽度,必须为正。</param>
    public static int Columns(double width, double tileWidth)
    {
        if (tileWidth <= 0 || !double.IsFinite(width))
        {
            return 1;
        }
        // 留一点容差:宽度正好是格宽的整数倍时,浮点误差不该让最后一格挤到下一行。
        return Math.Max(1, (int)Math.Floor((width + 0.001) / tileWidth));
    }

    /// <summary><paramref name="count" /> 项排成每行 <paramref name="columns" /> 格要几行。</summary>
    public static int Rows(int count, int columns) =>
        count <= 0 || columns <= 0 ? 0 : ((count - 1) / columns) + 1;

    /// <summary>
    /// 与内容坐标区间 <paramref name="top" />..<paramref name="bottom" /> 相交的那几行里的全部下标(闭区间)。
    /// 一项都不沾时返回 <c>(-1, -1)</c>。
    /// </summary>
    public static (int First, int Last) IndexRange(double top, double bottom, double tileHeight, int columns, int count)
    {
        int rows = Rows(count, columns);
        if (rows == 0 || tileHeight <= 0 || bottom <= top || bottom <= 0 || top >= rows * tileHeight)
        {
            return (-1, -1);
        }
        int firstRow = Math.Clamp((int)Math.Floor(Math.Max(top, 0) / tileHeight), 0, rows - 1);
        int lastRow = Math.Clamp((int)Math.Ceiling(bottom / tileHeight) - 1, firstRow, rows - 1);
        return (firstRow * columns, Math.Min(count - 1, ((lastRow + 1) * columns) - 1));
    }

    /// <summary>
    /// 内容坐标里的矩形(两角可任意顺序给出)碰到的格子下标,按下标升序。
    /// 矩形整个落在格子区域之外(最后一列右边的空白、最后一行下面)时一项都不选。
    /// </summary>
    public static IReadOnlyList<int> IndicesInRect(
        double x1, double y1, double x2, double y2,
        double tileWidth, double tileHeight, int columns, int count)
    {
        int rows = Rows(count, columns);
        if (rows == 0 || tileWidth <= 0 || tileHeight <= 0)
        {
            return [];
        }
        (double left, double right) = x1 <= x2 ? (x1, x2) : (x2, x1);
        (double top, double bottom) = y1 <= y2 ? (y1, y2) : (y2, y1);
        if (right <= 0 || bottom <= 0 || left >= columns * tileWidth || top >= rows * tileHeight)
        {
            return [];
        }

        int firstColumn = Math.Clamp((int)Math.Floor(Math.Max(left, 0) / tileWidth), 0, columns - 1);
        int lastColumn = Math.Clamp((int)Math.Ceiling(right / tileWidth) - 1, firstColumn, columns - 1);
        int firstRow = Math.Clamp((int)Math.Floor(Math.Max(top, 0) / tileHeight), 0, rows - 1);
        int lastRow = Math.Clamp((int)Math.Ceiling(bottom / tileHeight) - 1, firstRow, rows - 1);

        var hits = new List<int>();
        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                int index = (row * columns) + column;
                if (index >= count)
                {
                    // 最后一行没排满:这一行再往右已经没有格子了。
                    break;
                }
                hits.Add(index);
            }
        }
        return hits;
    }

    /// <summary>
    /// 从第 <paramref name="from" /> 格按方向键走到哪一格,规矩照资源管理器:左右逐格走、到行尾接下一行;
    /// 上下按整行走、保持在同一列;正下方没有格子(最后一行没排满)但下面还有一行时落到最后一项;
    /// 走不动时原地不动。PageUp / PageDown 一次走 <paramref name="pageRows" /> 行。
    /// </summary>
    /// <returns>目标下标;方向不归格子管(Tab 之类)时返回 -1。</returns>
    public static int Navigate(int from, NavigationDirection direction, int columns, int count, int pageRows, bool wrap)
    {
        if (count <= 0 || columns <= 0)
        {
            return -1;
        }
        from = Math.Clamp(from, 0, count - 1);
        pageRows = Math.Max(1, pageRows);
        return direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Left or NavigationDirection.Previous => Step(from, -1, count, wrap),
            NavigationDirection.Right or NavigationDirection.Next => Step(from, 1, count, wrap),
            NavigationDirection.Up => RowsUp(from, 1, columns),
            NavigationDirection.Down => RowsDown(from, 1, columns, count),
            NavigationDirection.PageUp => RowsUp(from, pageRows, columns),
            NavigationDirection.PageDown => RowsDown(from, pageRows, columns, count),
            _ => -1
        };
    }

    private static int Step(int from, int delta, int count, bool wrap)
    {
        int to = from + delta;
        if (to >= 0 && to < count)
        {
            return to;
        }
        return wrap ? (to + count) % count : from;
    }

    private static int RowsUp(int from, int rows, int columns) =>
        from - (columns * Math.Min(rows, from / columns));

    private static int RowsDown(int from, int rows, int columns, int count)
    {
        int steps = Math.Min(rows, (count - 1 - from) / columns);
        if (steps > 0)
        {
            return from + (columns * steps);
        }
        return from / columns < (count - 1) / columns ? count - 1 : from;
    }
}
