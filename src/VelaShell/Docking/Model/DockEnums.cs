namespace VelaShell.Docking.Model;

/// <summary>分栏方向:Horizontal = 子节点左右排列,Vertical = 上下排列。</summary>
public enum DockOrientation
{
    /// <summary>水平方向,子节点自左向右排列。</summary>
    Horizontal,

    /// <summary>垂直方向,子节点自上向下排列。</summary>
    Vertical
}

/// <summary>标签条在组内的停靠位置(右键菜单“标签位置”,与原 Dock 的 Top/Left/Right 对应)。</summary>
public enum DockTabsPosition
{
    /// <summary>
    /// 标签条横排:默认在组的顶部;<see cref="DockWorkspace.TabsAtBottom" /> 打开时(设置「标签栏位置」)
    /// 在底部。顶 / 底是整个工作区一起定的,所以这里只有一个「横排」,没有单独的 Bottom。
    /// </summary>
    Top,

    /// <summary>标签条位于组的左侧。</summary>
    Left,

    /// <summary>标签条位于组的右侧。</summary>
    Right
}

/// <summary>拖放目标区:中心 = 并入该组,四边 = 在该组对应侧拆分。</summary>
public enum DockPosition
{
    /// <summary>中心区域,将拖放项并入该组。</summary>
    Center,

    /// <summary>左侧区域,在该组左侧拆分放置。</summary>
    Left,

    /// <summary>顶部区域,在该组上方拆分放置。</summary>
    Top,

    /// <summary>右侧区域,在该组右侧拆分放置。</summary>
    Right,

    /// <summary>底部区域,在该组下方拆分放置。</summary>
    Bottom
}

/// <summary>窗格移焦的方向(Alt+方向键)。</summary>
public enum DockDirection
{
    /// <summary>向左。</summary>
    Left,

    /// <summary>向右。</summary>
    Right,

    /// <summary>向上。</summary>
    Up,

    /// <summary>向下。</summary>
    Down
}
