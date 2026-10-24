namespace VelaShell.Docking.Model;

/// <summary>
/// 一个新文档该落在哪(见 <see cref="DockWorkspace.PlacementResolver" />)。
/// </summary>
/// <param name="Group">落进哪个组。</param>
/// <param name="Index">插到第几个标签前面(按插入前计);会被收进固定区 / 普通区的合法区间。</param>
/// <param name="Pinned">放下时是否固定。</param>
/// <param name="SelectInGroup">是否在组里选中它(不抢全局焦点)。</param>
/// <param name="Activate">是否让它成为整个工作区的活动文档。</param>
public readonly record struct DockPlacement(
    DockGroup Group,
    int Index,
    bool Pinned,
    bool SelectInGroup,
    bool Activate);
