namespace VelaShell.Core.Layouts;

/// <summary>
/// 布局树(<see cref="LayoutNode" />)上的纯函数:剪枝、遍历、计数。
/// </summary>
/// <remarks>
/// 保存与打开两头都要「去掉一部分标签之后,树还得是一棵合法的树」:保存时去掉没法重新打开的标签
/// (插件面板、快速连接),打开时去掉配置已经删掉的标签。两头用同一个剪枝,规则只有一份:
/// 没有标签的组整个去掉、只剩一个子节点的分栏把那个子节点提上来 —— 与停靠层自己的
/// 「空组折叠、单子分栏提升」同一口径,于是还原出来的布局与用户当时看到的(少了那几个标签之后)一致。
/// </remarks>
public static class LayoutTree
{
    /// <summary>
    /// 树的最大深度。正常的分屏不会超过个位数;文档库里的数据被改坏(或手写)成一条极深的链时,
    /// 递归会把栈打穿,而 .NET 接不住栈溢出 —— 超出的部分按损坏丢掉。
    /// </summary>
    public const int MaxDepth = 32;

    /// <summary>
    /// 返回只保留 <paramref name="keep" /> 认可的标签之后的新树(不改输入);一个标签都不剩时返回 null。
    /// </summary>
    /// <param name="node">要剪的树;null 视作空树。</param>
    /// <param name="keep">标签留不留。</param>
    /// <param name="dropped">收集被去掉的标签(按树上的顺序);不关心可以不传。</param>
    /// <returns>剪完的树。</returns>
    public static LayoutNode? Prune(
        LayoutNode? node,
        Func<LayoutDocument, bool> keep,
        ICollection<LayoutDocument>? dropped = null)
    {
        ArgumentNullException.ThrowIfNull(keep);
        return Prune(node, keep, dropped, depth: 0);
    }

    private static LayoutNode? Prune(
        LayoutNode? node,
        Func<LayoutDocument, bool> keep,
        ICollection<LayoutDocument>? dropped,
        int depth)
    {
        if (node is null || depth >= MaxDepth)
        {
            return null;
        }
        if (node.IsSplit)
        {
            var children = new List<LayoutNode>(node.Children?.Count ?? 0);
            foreach (LayoutNode? child in node.Children ?? [])
            {
                if (Prune(child, keep, dropped, depth + 1) is { } kept)
                {
                    children.Add(kept);
                }
            }
            switch (children.Count)
            {
                case 0:
                    return null;
                case 1:
                    // 单子分栏:子节点接手分栏在父分栏里的那份比例(与停靠层的 HoistIfSingle 一致)。
                    // children[0] 是剪枝新建的副本,改它不碰输入。
                    children[0].Proportion = Sanitize(node.Proportion);
                    return children[0];
                default:
                    return new()
                    {
                        Orientation = NormalizeOrientation(node.Orientation),
                        Proportion = Sanitize(node.Proportion),
                        Children = children,
                    };
            }
        }

        // 反序列化来的数据里集合与元素都可能是 null(手改过的文件、更新的版本写出的形状),一律当空。
        List<LayoutDocument?> source = node.Documents is null ? [] : [.. node.Documents];
        var documents = new List<LayoutDocument>(source.Count);
        int active = -1;
        for (int i = 0; i < source.Count; i++)
        {
            if (source[i] is not { } document)
            {
                continue;
            }
            if (!keep(document))
            {
                dropped?.Add(document);
                continue;
            }
            if (i == node.ActiveIndex)
            {
                active = documents.Count;
            }
            documents.Add(document);
        }
        if (documents.Count == 0)
        {
            return null;
        }
        // 「固定的排在最前」是停靠层的不变式;数据被改乱时在这里扶正,而不是让还原出来的组违反它。
        List<LayoutDocument> ordered = [.. documents.Where(d => d.IsPinned), .. documents.Where(d => !d.IsPinned)];
        if (active >= 0)
        {
            active = ordered.IndexOf(documents[active]);
        }
        return new()
        {
            Proportion = Sanitize(node.Proportion),
            Documents = ordered,
            ActiveIndex = active,
            TabsPosition = node.TabsPosition,
        };
    }

    /// <summary>按树上的顺序(深度优先、组内从左到右)列出全部标签。</summary>
    /// <param name="node">树;null 视作空树。</param>
    /// <returns>全部标签。</returns>
    public static IEnumerable<LayoutDocument> Documents(LayoutNode? node) => Documents(node, depth: 0);

    private static IEnumerable<LayoutDocument> Documents(LayoutNode? node, int depth)
    {
        if (node is null || depth >= MaxDepth)
        {
            yield break;
        }
        if (node.IsSplit)
        {
            foreach (LayoutNode? child in node.Children ?? [])
            {
                foreach (LayoutDocument document in Documents(child, depth + 1))
                {
                    yield return document;
                }
            }
            yield break;
        }
        foreach (LayoutDocument? document in node.Documents ?? [])
        {
            if (document is not null)
            {
                yield return document;
            }
        }
    }

    /// <summary>树里有几个标签组(= 窗格数)。</summary>
    /// <param name="node">树;null 视作空树。</param>
    /// <returns>标签组个数。</returns>
    public static int GroupCount(LayoutNode? node) => GroupCount(node, depth: 0);

    private static int GroupCount(LayoutNode? node, int depth)
    {
        if (node is null || depth >= MaxDepth)
        {
            return 0;
        }
        return node.IsSplit
                   ? (node.Children ?? []).Sum(child => GroupCount(child, depth + 1))
                   : 1;
    }

    /// <summary>不认识的方向一律当左右排:比把整棵子树丢掉更接近用户当时的样子。</summary>
    private static string NormalizeOrientation(string? orientation) =>
        string.Equals(orientation, LayoutOrientation.Vertical, StringComparison.OrdinalIgnoreCase)
            ? LayoutOrientation.Vertical
            : LayoutOrientation.Horizontal;

    /// <summary>比例只接受有限的正数;别的(0、负数、无穷)当「均分」。</summary>
    private static double? Sanitize(double? proportion) =>
        proportion is { } value && double.IsFinite(value) && value > 0 ? value : null;
}
