using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using VelaShell.Docking.Model;

namespace VelaShell.Converters;

/// <summary>
/// 标签条溢出判定(设计 nunbT 的 Tab Overflow Controls 仅在放不下时出现),沿标签排列的那条轴比:
/// 输入 [Extent, Viewport, TabsPosition] —— 横排(顶部 / 底部)比宽度,停到左 / 右侧的纵排比高度,
/// 内容比视口长(留 0.5px 容差)时为 true。
/// </summary>
/// <remarks>
/// 以前只比宽度(WidthOverflowConverter):纵排的标签条内容宽度永远等于视口宽度,
/// 于是标签再多三连钮也不出现,只能靠滚轮去够溢出的那些。
/// </remarks>
public sealed class TabStripOverflowConverter : IMultiValueConverter
{
    /// <summary>可在 XAML 绑定中直接复用的共享转换器实例。</summary>
    public static readonly TabStripOverflowConverter Instance = new();

    /// <summary>按标签位置取轴:内容沿排列方向比视口长时返回 true;输入不全时为 false。</summary>
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [Size extent, Size viewport, DockTabsPosition position, ..]
        && IsOverflowing(extent, viewport, vertical: position != DockTabsPosition.Top);

    /// <summary>沿排列轴,内容是否比视口长。0.5px 容差:免得小数累加把溢出钮闪出来。</summary>
    public static bool IsOverflowing(Size extent, Size viewport, bool vertical) =>
        vertical ? extent.Height > viewport.Height + 0.5 : extent.Width > viewport.Width + 0.5;
}
