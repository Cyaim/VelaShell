using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 文件栏的列宽与分栏比例跨重启记忆(维护者 2026-10-25 定):全局一份,存文档存储的 windowLayout 集合
/// (见 <see cref="FilePaneLayout" />),与上传选择框记尺寸同一做法,不进设置页。
/// </summary>
/// <remarks>
/// <para>
/// 列宽:远端栏的视图拿到面板时套上记下的宽度(终端侧栏的视图随标签换面板,每换一次都套 —— 全局一份,
/// 刚在别处调过的宽度在这里也该是那个样子);拖完拖拽条、双击自适应之后写回。本地栏的三列是定宽,没有可记的。
/// </para>
/// <para>
/// 分栏比例:单栏文档(本地 + 远程)与双栏远程文档共用一个比例 —— 都是「左右两栏」这一件事;拖完分隔条写回。
/// </para>
/// </remarks>
internal static class FilePaneLayoutMemory
{
    /// <summary>左栏比例的取值范围:两栏各有 280 的最小宽度,记下的比例太偏只会在窄窗口里被最小宽度顶回去。</summary>
    private const double MinRatio = 0.15;

    private const double MaxRatio = 0.85;

    /// <summary>
    /// 取存储;测试里换成内存存储。生产里是应用容器里那一个单例(没有容器时为 null,一切退化为空操作)。
    /// </summary>
    internal static Func<WindowLayoutStore?> StoreResolver { get; set; } = static () =>
        (Application.Current as App)?.Services?.GetService<WindowLayoutStore>();

    /// <summary>给远端栏面板套上记下的列宽;还没读进内存时读完再套(那时面板还是这个视图的就套)。</summary>
    /// <param name="view">远端栏视图。</param>
    /// <param name="pane">刚交给它的面板。</param>
    public static void RestoreColumns(Control view, FileBrowserViewModel pane)
    {
        if (StoreResolver() is not { } store)
        {
            return;
        }
        if (store.FilePanes is { } cached)
        {
            ApplyColumns(pane, cached);
            return;
        }
        _ = ApplyColumnsWhenLoadedAsync(store, view, pane);
    }

    private static async Task ApplyColumnsWhenLoadedAsync(WindowLayoutStore store, Control view, FileBrowserViewModel pane)
    {
        FilePaneLayout layout = await store.LoadFilePanesAsync();
        if (ReferenceEquals(view.DataContext, pane))
        {
            ApplyColumns(pane, layout);
        }
    }

    private static void ApplyColumns(FileBrowserViewModel pane, FilePaneLayout layout)
    {
        foreach ((string column, double width) in layout.ColumnWidths ?? [])
        {
            // 认不出的列键(将来删掉的列)不套;太窄的由面板按各列下限钳住。
            if (width > 0 && double.IsFinite(width) && column is "name" or "size" or "permissions" or "owner" or "group" or "type")
            {
                pane.SetColumnWidth(column, width);
            }
        }
    }

    /// <summary>用户调完列宽(拖完、双击自适应):把这一栏六个可调列的宽度写回。</summary>
    public static void SaveColumns(FileBrowserViewModel pane)
    {
        if (StoreResolver() is not { } store)
        {
            return;
        }
        Dictionary<string, double> widths = new(StringComparer.Ordinal);
        foreach (string column in (string[])["name", "size", "permissions", "owner", "group", "type"])
        {
            widths[column] = pane.GetColumnWidth(column).Value;
        }
        _ = store.SaveFilePanesAsync(layout => layout.ColumnWidths = widths);
    }

    /// <summary>
    /// 给「左栏 | 分隔条 | 右栏」三列的网格挂上分栏比例记忆:套上记下的比例,拖完分隔条写回。
    /// </summary>
    /// <param name="grid">三列网格(第 0、2 列是两栏,第 1 列是分隔条)。</param>
    /// <param name="splitter">中间那根分隔条。</param>
    public static void AttachSplit(Grid grid, GridSplitter splitter)
    {
        if (StoreResolver() is not { } store)
        {
            return;
        }
        if (store.FilePanes is { } cached)
        {
            ApplySplit(grid, cached.SplitRatio);
        }
        else
        {
            _ = ApplySplitWhenLoadedAsync(store, grid);
        }
        splitter.DragCompleted += (_, _) =>
        {
            double left = grid.ColumnDefinitions[0].ActualWidth;
            double right = grid.ColumnDefinitions[2].ActualWidth;
            if (left + right <= 0)
            {
                return;
            }
            double ratio = Math.Clamp(left / (left + right), MinRatio, MaxRatio);
            _ = store.SaveFilePanesAsync(layout => layout.SplitRatio = ratio);
        };
    }

    private static async Task ApplySplitWhenLoadedAsync(WindowLayoutStore store, Grid grid)
    {
        FilePaneLayout layout = await store.LoadFilePanesAsync();
        ApplySplit(grid, layout.SplitRatio);
    }

    private static void ApplySplit(Grid grid, double? ratio)
    {
        if (ratio is not { } value || !double.IsFinite(value))
        {
            return;
        }
        value = Math.Clamp(value, MinRatio, MaxRatio);
        // 两栏都用星号宽度:窗口再变宽变窄,比例照旧。
        grid.ColumnDefinitions[0].Width = new GridLength(value, GridUnitType.Star);
        grid.ColumnDefinitions[2].Width = new GridLength(1 - value, GridUnitType.Star);
    }
}
