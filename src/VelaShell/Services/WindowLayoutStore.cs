using Avalonia;
using Avalonia.Controls;
using VelaShell.Core.Data;

namespace VelaShell.Services;

/// <summary>一个辅助窗口记住的几何信息(存进 SonnetDB 的 windowLayout 集合)。</summary>
public sealed class WindowLayout
{
    /// <summary>上次的普通态宽度(逻辑像素)。最大化时不写入,否则还原后会变成整屏尺寸。</summary>
    public double Width { get; set; }

    /// <summary>上次的普通态高度(逻辑像素)。</summary>
    public double Height { get; set; }

    /// <summary>关闭时是否处于最大化。</summary>
    public bool Maximized { get; set; }

    /// <summary>窗口里内容的视图形态(上传选择框:<c>List</c> / <c>Icons</c>);没有视图之分的窗口留空。</summary>
    public string? View { get; set; }

    /// <summary>
    /// 上次普通态的左上角横坐标(物理像素,与 <see cref="Window.Position" /> 同一口径);没记过时为 null。
    /// 用可空而不是 0 当「没记」:多屏时 (0, 0) 与负坐标都是正当的位置。
    /// </summary>
    public int? X { get; set; }

    /// <summary>上次普通态的左上角纵坐标(物理像素);没记过时为 null。</summary>
    public int? Y { get; set; }
}

/// <summary>
/// 文件栏记住的几何(windowLayout 集合里 <c>filePanes</c> 那一条):远端栏各列的宽度与双栏文件标签的分栏比例。
/// </summary>
/// <remarks>
/// 全局一份,不按连接分 —— 用户调的是「文件栏」这件东西,不是「这台服务器的文件栏」(上传选择框同一口径)。
/// 纯属使用习惯,与辅助窗口的尺寸一样不进设置页。
/// </remarks>
public sealed class FilePaneLayout
{
    /// <summary>
    /// 远端栏拖过的列宽(列键 → 逻辑像素;列键见 <c>FileBrowserViewModel.SetColumnWidth</c>)。
    /// 没拖过的列不在其中,按出厂宽度。
    /// </summary>
    public Dictionary<string, double>? ColumnWidths { get; set; }

    /// <summary>双栏文件标签(本地 + 远程、远程 + 远程)左栏占两栏的比例(0–1);没拖过分隔条时为 null,各占一半。</summary>
    public double? SplitRatio { get; set; }

    /// <summary>深拷贝(写库用一份快照,内存里那份接着改)。</summary>
    internal FilePaneLayout Clone() => new()
    {
        ColumnWidths = ColumnWidths is null ? null : new(ColumnWidths, StringComparer.Ordinal),
        SplitRatio = SplitRatio,
    };
}

/// <summary>
/// 辅助窗口(任务管理器等)的尺寸与位置持久化。主窗口的尺寸走设置里的
/// <c>Appearance.LastWindowWidth</c>,那是用户可见的一项设置;辅助窗口纯属使用习惯,
/// 不该塞进设置页,因此落在文档存储里。
/// </summary>
/// <remarks>
/// 位置只给非模态工具窗口用(<see cref="RememberAsync" />):多屏用户把审计日志、回放中心、任务管理器摆到副屏上,
/// 下次打开还该在那儿。模态框仍跟着属主居中 —— 它是当前操作的一部分,开在别处反而找不着。
/// </remarks>
/// <param name="store">文档存储;为 null(无头测试)时所有操作退化为空操作。</param>
public sealed class WindowLayoutStore(IAppDataStore? store)
{
    private const string Collection = "windowLayout";

    /// <summary>文件栏几何在 windowLayout 集合里的键(见 <see cref="FilePaneLayout" />)。</summary>
    private const string FilePanesKey = "filePanes";
    private Task<FilePaneLayout>? _filePanesLoad;

    /// <summary>
    /// 已经读进内存的文件栏几何;还没读过(或第一次读库还没回来)时为 null。
    /// 视图建好时同步套用它,免得每开一个文件栏都先按出厂宽度画一帧再跳。
    /// </summary>
    public FilePaneLayout? FilePanes { get; private set; }

    /// <summary>读文件栏几何:只在第一次读库,之后都用内存里那份(改动经 <see cref="SaveFilePanesAsync" /> 先改内存)。</summary>
    public Task<FilePaneLayout> LoadFilePanesAsync() => _filePanesLoad ??= LoadFilePanesCoreAsync();

    private async Task<FilePaneLayout> LoadFilePanesCoreAsync()
    {
        FilePaneLayout? stored = null;
        if (store is not null)
        {
            try
            {
                stored = await store.GetAsync<FilePaneLayout>(Collection, FilePanesKey).ConfigureAwait(false);
            }
            catch
            {
                // 记不住列宽不值得打扰用户:读不出来就按出厂宽度。
            }
        }
        return FilePanes ??= stored ?? new FilePaneLayout();
    }

    /// <summary>改一处文件栏几何,写回存储;失败静默忽略。</summary>
    /// <param name="change">对内存里那份的修改。在调用方的线程上执行(视图在 UI 线程上调)。</param>
    public async Task SaveFilePanesAsync(Action<FilePaneLayout> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        // 不加 ConfigureAwait(false):修改要回到调用方的线程上做,视图同步读的就是这一份。
        FilePaneLayout layout = await LoadFilePanesAsync();
        change(layout);
        if (store is null)
        {
            return;
        }
        try
        {
            await store.UpsertAsync(Collection, FilePanesKey, layout.Clone()).ConfigureAwait(false);
        }
        catch
        {
            // 同上。
        }
    }

    /// <summary>读取某个窗口上次的几何信息;没存过或读取失败时返回 null。</summary>
    /// <param name="key">窗口标识,如 "processManager"。</param>
    public async Task<WindowLayout?> LoadAsync(string key)
    {
        if (store is null)
        {
            return null;
        }
        try
        {
            return await store.GetAsync<WindowLayout>(Collection, key).ConfigureAwait(false);
        }
        catch
        {
            // 记住尺寸是锦上添花,读不出来就用默认尺寸开,不能因此打不开窗口。
            return null;
        }
    }

    /// <summary>
    /// 给非模态工具窗口挂上尺寸与位置记忆:读上次的几何信息套上去(位置要还在某块屏的工作区里才用,
    /// 否则照窗口自己的启动位置居中),关窗时写回。必须在 Show 之前调用。
    /// </summary>
    /// <param name="key">窗口标识;同类窗口(各会话的任务管理器)共用一条 —— 用户调的是「这个面板」,不是「这台服务器的面板」。</param>
    /// <param name="window">目标窗口。</param>
    public async Task RememberAsync(string key, Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Apply(window, await LoadAsync(key), restorePosition: true);
        window.Closing += (_, _) => _ = SaveAsync(key, window);
    }

    /// <summary>把窗口当前的几何信息写回存储;失败静默忽略。</summary>
    /// <param name="key">窗口标识。</param>
    /// <param name="window">要记录的窗口。</param>
    /// <param name="view">窗口里内容的视图形态(见 <see cref="WindowLayout.View" />);没有就不传。</param>
    public async Task SaveAsync(string key, Window window, string? view = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (store is null)
        {
            return;
        }
        // 窗口属性只能在 UI 线程上读:先在这里取完,再去异步读上次的记录。
        WindowLayout layout = Capture(window, view, previous: null);
        if (window.WindowState != WindowState.Normal)
        {
            // 最大化/最小化时 Width/Height 已不是用户调过的那个尺寸、Position 是最大化后的左上角
            // (最小化时 Win32 报 -32000),沿用上次记录的值。
            WindowLayout? previous = await LoadAsync(key).ConfigureAwait(false);
            layout.Width = previous?.Width ?? 0;
            layout.Height = previous?.Height ?? 0;
            layout.X = previous?.X;
            layout.Y = previous?.Y;
        }
        try
        {
            await store.UpsertAsync(Collection, key, layout).ConfigureAwait(false);
        }
        catch
        {
            // 同上:记不住尺寸不值得打扰用户。
        }
    }

    /// <summary>
    /// 取窗口此刻的几何信息:普通态读当下的尺寸与位置;不在普通态时尺寸与位置沿用 <paramref name="previous" />。
    /// </summary>
    /// <remarks>
    /// 只在关窗那一刻读,不像主窗口那样平时跟着记(<see cref="MainWindowPlacement" />):工具窗口从最大化态关掉的情形少,
    /// 沿用上一次的记录足够,省掉 X11 / macOS 上状态事件与坐标先后不定的那一摊。
    /// </remarks>
    internal static WindowLayout Capture(Window window, string? view, WindowLayout? previous)
    {
        WindowLayout layout = new() { Maximized = window.WindowState == WindowState.Maximized, View = view };
        if (window.WindowState == WindowState.Normal)
        {
            layout.Width = window.Width;
            layout.Height = window.Height;
            PixelPoint position = window.Position;
            layout.X = position.X;
            layout.Y = position.Y;
        }
        else
        {
            layout.Width = previous?.Width ?? 0;
            layout.Height = previous?.Height ?? 0;
            layout.X = previous?.X;
            layout.Y = previous?.Y;
        }
        return layout;
    }

    /// <summary>
    /// 把记录的几何信息套用到窗口,必须在 Show 之前调用。尺寸会夹取到窗口自身的最小值与
    /// 当前屏幕工作区之间 —— 换了小显示器之后,上次在大屏上存的尺寸会让窗口大半在屏幕外。
    /// </summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="layout">读到的几何信息;为 null 时不做任何改动。</param>
    /// <param name="restorePosition">
    /// 是否连位置一起摆回去。位置要过 <see cref="MainWindowPlacement.TryRestorePosition" /> 的核实
    /// (标题栏还在某块屏的工作区里抓得住);拔了副屏、换了排列时不动它,窗口照自己的启动位置居中打开。
    /// </param>
    public static void Apply(Window window, WindowLayout? layout, bool restorePosition = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (layout is null)
        {
            return;
        }
        if (layout is { Width: > 0, Height: > 0 })
        {
            double maxWidth = double.PositiveInfinity;
            double maxHeight = double.PositiveInfinity;
            try
            {
                if (window.Screens.Primary is { Scaling: > 0 } screen)
                {
                    maxWidth = screen.WorkingArea.Width / screen.Scaling;
                    maxHeight = screen.WorkingArea.Height / screen.Scaling;
                }
            }
            catch
            {
                // 屏幕信息在窗口显示前不一定可用(无头/远程桌面):拿不到就不夹取上界。
            }
            window.Width = Math.Clamp(layout.Width, window.MinWidth, maxWidth);
            window.Height = Math.Clamp(layout.Height, window.MinHeight, maxHeight);
        }
        // 尺寸定好之后再摆位置:核实「标题栏抓得住」要用到窗口宽度。
        if (restorePosition && layout is { X: { } x, Y: { } y })
        {
            MainWindowPlacement.TryRestorePosition(window, new PixelPoint(x, y));
        }
        if (layout.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }
}
