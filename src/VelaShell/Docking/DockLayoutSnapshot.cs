using VelaShell.Core.Layouts;
using VelaShell.Docking.Model;

namespace VelaShell.Docking;

/// <summary>
/// 停靠布局与布局模板树(<see cref="LayoutNode" />)之间的两个方向:把眼前的布局拍下来,
/// 以及按模板树搭出一副空的分屏骨架。
/// </summary>
/// <remarks>
/// 模型层(<see cref="DockWorkspace" /> 及其节点)不认识模板 —— 它只管树的结构操作;
/// 文档是什么、怎么重新打开,是宿主的事,由调用方以 <c>describe</c> 委托交进来。
/// </remarks>
public static class DockLayoutSnapshot
{
    /// <summary>
    /// 把工作区当前的分屏树拍成模板树:方向、比例、各组的标签顺序、选中的标签、固定状态、标签条位置,
    /// 以及整个工作区的活动标签。
    /// </summary>
    /// <param name="workspace">要拍的工作区。</param>
    /// <param name="describe">
    /// 一个文档在模板里记成什么;返回 null 表示这个文档没法重新打开(插件面板、快速连接…),跳过。
    /// 固定状态、是否活动与标题由这里补,委托不必管。
    /// </param>
    /// <param name="skipped">收集被跳过的文档;不关心可以不传。</param>
    /// <returns>模板树(没有标签的组已去掉、单子分栏已提升);一个能记的标签都没有时为 null。</returns>
    public static LayoutNode? Capture(
        DockWorkspace workspace,
        Func<DockDocument, LayoutDocument?> describe,
        ICollection<DockDocument>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(describe);
        LayoutNode raw = CaptureNode(workspace.Root, workspace.ActiveDocument, describe, skipped);
        LayoutNode? root = LayoutTree.Prune(raw, static _ => true);
        // 根在父分栏里没有份额可言,比例记了也没人读。
        root?.Proportion = null;
        return root;
    }

    private static LayoutNode CaptureNode(
        DockNode node,
        DockDocument? active,
        Func<DockDocument, LayoutDocument?> describe,
        ICollection<DockDocument>? skipped)
    {
        switch (node)
        {
            case DockSplit split:
                return new()
                {
                    Orientation = split.Orientation == DockOrientation.Vertical
                                      ? LayoutOrientation.Vertical
                                      : LayoutOrientation.Horizontal,
                    Proportion = ToProportion(split.Proportion),
                    Children = [.. split.Children.Select(child => CaptureNode(child, active, describe, skipped))],
                };
            case DockGroup group:
                var documents = new List<LayoutDocument>(group.Documents.Count);
                int activeIndex = -1;
                foreach (DockDocument document in group.Documents)
                {
                    if (describe(document) is not { } described)
                    {
                        skipped?.Add(document);
                        continue;
                    }
                    described.IsPinned = document.IsPinned;
                    described.IsActive = ReferenceEquals(document, active);
                    if (string.IsNullOrEmpty(described.Title))
                    {
                        described.Title = document.Title;
                    }
                    if (ReferenceEquals(document, group.ActiveDocument))
                    {
                        activeIndex = documents.Count;
                    }
                    documents.Add(described);
                }
                return new()
                {
                    Proportion = ToProportion(group.Proportion),
                    Documents = documents,
                    ActiveIndex = activeIndex,
                    // 置顶是默认值,不写;其余按名字存(见 LayoutNode.TabsPosition 为什么不存数字)。
                    TabsPosition = group.TabsPosition == DockTabsPosition.Top ? null : group.TabsPosition.ToString(),
                };
            default:
                // 不认识的节点类型记成一个空组,随后被剪掉。
                return new();
        }
    }

    /// <summary>
    /// 按模板树(应已剪过枝,见 <see cref="LayoutTree.Prune" />)搭出一副全是空组的分屏骨架。
    /// </summary>
    /// <param name="root">模板树。</param>
    /// <param name="groupCreated">每建出一个组调一次(模板里的组节点, 对应的停靠组),按树上的顺序。</param>
    /// <returns>骨架的根,尚未挂到任何工作区上(见 <see cref="DockWorkspace.InsertLayout" />)。</returns>
    public static DockNode BuildSkeleton(LayoutNode root, Action<LayoutNode, DockGroup> groupCreated)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(groupCreated);
        if (root.IsSplit)
        {
            var split = new DockSplit(
                string.Equals(root.Orientation, LayoutOrientation.Vertical, StringComparison.OrdinalIgnoreCase)
                    ? DockOrientation.Vertical
                    : DockOrientation.Horizontal)
            {
                Proportion = FromProportion(root.Proportion),
            };
            foreach (LayoutNode child in root.Children)
            {
                split.Children.Add(BuildSkeleton(child, groupCreated));
            }
            return split;
        }
        var group = new DockGroup
        {
            Proportion = FromProportion(root.Proportion),
            TabsPosition = ParseTabsPosition(root.TabsPosition),
        };
        groupCreated(root, group);
        return group;
    }

    /// <summary>
    /// 标签条位置按名字读;不认识的名字(更新的版本加了新位置)与数字一律按置顶处理 ——
    /// <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)" /> 会把 "7" 读成一个不存在的枚举值,
    /// 所以还要 <see cref="Enum.IsDefined{TEnum}(TEnum)" /> 把一道关。
    /// </summary>
    internal static DockTabsPosition ParseTabsPosition(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !char.IsAsciiDigit(value.Trim()[0])
        && Enum.TryParse(value, ignoreCase: true, out DockTabsPosition position)
        && Enum.IsDefined(position)
            ? position
            : DockTabsPosition.Top;

    private static double? ToProportion(double proportion) =>
        double.IsFinite(proportion) && proportion > 0 ? proportion : null;

    private static double FromProportion(double? proportion) =>
        proportion is { } value && double.IsFinite(value) && value > 0 ? value : double.NaN;
}
