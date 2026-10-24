using System.Text.Json;
using VelaShell.Core.Layouts;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 布局模板树的剪枝:保存时去掉没法重新打开的标签、打开时去掉配置已删除的标签,
/// 剩下的必须还是一棵合法的树(没有空组、没有单子分栏),选中下标与固定顺序跟着对上。
/// </summary>
[TestClass]
public sealed class LayoutTreeTests
{
    private static LayoutDocument Doc(string title, bool pinned = false) =>
        new() { Kind = LayoutDocumentKind.Terminal, ProfileId = Guid.NewGuid(), Title = title, IsPinned = pinned };

    private static LayoutNode Group(int active, params LayoutDocument[] documents) =>
        new() { Documents = [.. documents], ActiveIndex = active };

    private static string Titles(LayoutNode group) => string.Join(",", group.Documents.Select(d => d.Title));

    [TestMethod]
    public void Prune_DropsEmptiedGroups_AndHoistsTheOnlyChildWithTheSplitsShare()
    {
        LayoutDocument a = Doc("a"), b = Doc("b"), c = Doc("c");
        var inner = new LayoutNode
        {
            Orientation = LayoutOrientation.Vertical,
            Proportion = 0.25,
            Children = [Group(0, b), Group(0, c)],
        };
        var root = new LayoutNode { Orientation = LayoutOrientation.Horizontal, Children = [Group(0, a), inner] };
        var dropped = new List<LayoutDocument>();

        LayoutNode pruned = LayoutTree.Prune(root, d => d != b, dropped)!;

        Assert.AreSequenceEqual([b], dropped.ToArray());
        Assert.HasCount(2, pruned.Children);
        LayoutNode hoisted = pruned.Children[1];
        Assert.IsFalse(hoisted.IsSplit, "只剩 c 的分栏被它自己替换");
        Assert.AreEqual("c", Titles(hoisted));
        Assert.AreEqual(0.25, hoisted.Proportion, "接手分栏的份额");
        Assert.HasCount(2, inner.Children, "不改输入");
    }

    [TestMethod]
    public void Prune_NothingLeft_ReturnsNull()
    {
        var root = new LayoutNode { Orientation = LayoutOrientation.Horizontal, Children = [Group(0, Doc("a")), new()] };

        Assert.IsNull(LayoutTree.Prune(root, _ => false));
        Assert.IsNull(LayoutTree.Prune(null, _ => true));
    }

    [TestMethod]
    public void Prune_ActiveIndexFollowsTheSurvivors()
    {
        LayoutDocument a = Doc("a"), b = Doc("b"), c = Doc("c");

        LayoutNode keptActive = LayoutTree.Prune(Group(2, a, b, c), d => d != a)!;
        Assert.AreEqual(1, keptActive.ActiveIndex, "c 往前挪了一位,下标跟着挪");

        LayoutNode droppedActive = LayoutTree.Prune(Group(2, a, b, c), d => d != c)!;
        Assert.AreEqual(-1, droppedActive.ActiveIndex, "选中的那个没了:不猜,交给还原时选第一个到的");
    }

    [TestMethod]
    public void Prune_PutsPinnedTabsFirst_EvenWhenTheDataSaysOtherwise()
    {
        LayoutDocument a = Doc("a"), b = Doc("b", pinned: true);

        LayoutNode pruned = LayoutTree.Prune(Group(0, a, b), _ => true)!;

        Assert.AreEqual("b,a", Titles(pruned), "固定的排在最前是停靠层的不变式");
        Assert.AreEqual(1, pruned.ActiveIndex, "选中的 a 挪到了第二位");
    }

    [TestMethod]
    public void Prune_SanitizesProportionsAndOrientation()
    {
        var root = new LayoutNode
        {
            Orientation = "diagonal",
            Children =
            [
                new() { Proportion = double.PositiveInfinity, Documents = [Doc("a")] },
                new() { Proportion = -1, Documents = [Doc("b")] },
                new() { Proportion = 2, Documents = [Doc("c")] },
            ],
        };

        LayoutNode pruned = LayoutTree.Prune(root, _ => true)!;

        Assert.AreEqual(LayoutOrientation.Horizontal, pruned.Orientation, "不认识的方向当左右排");
        Assert.AreSequenceEqual([null, null, 2.0], pruned.Children.Select(child => child.Proportion).ToArray());
    }

    [TestMethod]
    public void Prune_TreeDeeperThanTheLimit_IsCutInsteadOfOverflowingTheStack()
    {
        LayoutNode node = Group(0, Doc("deep"));
        for (int i = 0; i < 5000; i++)
        {
            node = new() { Orientation = LayoutOrientation.Horizontal, Children = [node, Group(0, Doc($"n{i}"))] };
        }

        LayoutNode pruned = LayoutTree.Prune(node, _ => true)!;

        Assert.IsNotNull(pruned);
        Assert.IsLessThanOrEqualTo(LayoutTree.MaxDepth, LayoutTree.Documents(pruned).Count());
        Assert.IsGreaterThan(0, LayoutTree.GroupCount(pruned));
    }

    [TestMethod]
    public void Documents_AndGroupCount_WalkDepthFirst()
    {
        var root = new LayoutNode
        {
            Orientation = LayoutOrientation.Horizontal,
            Children =
            [
                Group(0, Doc("a"), Doc("b")),
                new() { Orientation = LayoutOrientation.Vertical, Children = [Group(0, Doc("c")), Group(0, Doc("d"))] },
            ],
        };

        Assert.AreEqual("a,b,c,d", string.Join(",", LayoutTree.Documents(root).Select(d => d.Title)));
        Assert.AreEqual(3, LayoutTree.GroupCount(root));
    }

    [TestMethod]
    public void Template_SurvivesJsonWithNullsAndUnknownFields()
    {
        // 手改过的文件、更新的版本写出的形状:集合是 null、多了不认识的字段,都不该把整份读坏。
        const string json = """
            {
              "schemaVersion": 9,
              "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
              "name": "prod",
              "colour": "teal",
              "root": {
                "orientation": "vertical",
                "children": [
                  { "documents": null },
                  { "documents": [ null, { "kind": "terminal", "profileId": "1b4e28ba-2fa1-11d2-883f-0016d3cca427", "title": "web", "isPinned": true } ] }
                ]
              }
            }
            """;
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

        LayoutTemplate template = JsonSerializer.Deserialize<LayoutTemplate>(json, options)!;
        LayoutNode pruned = LayoutTree.Prune(template.Root, _ => true)!;

        Assert.AreEqual("prod", template.Name);
        Assert.IsFalse(pruned.IsSplit);
        Assert.AreEqual("web", pruned.Documents.Single().Title);
        Assert.IsTrue(pruned.Documents.Single().IsPinned);
    }
}
