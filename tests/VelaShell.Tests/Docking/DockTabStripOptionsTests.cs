using VelaShell.Docking.Model;

namespace VelaShell.Tests.Docking;

/// <summary>
/// 标签条的工作区级开关:标签栏位置(顶 / 底)。它与「多行显示标签页」一样整个工作区一个值,
/// 停靠层只负责通知,摆法由 <c>DockGroupControl</c> 按它重排(见 <c>DockTabStripPlacementUiTests</c>)。
/// </summary>
[TestClass]
[TestCategory("Docking")]
public sealed class DockTabStripOptionsTests
{
    [TestMethod]
    public void TabsAtBottom_DefaultsOff_AndNotifiesOnlyOnRealChanges()
    {
        var workspace = new DockWorkspace();
        Assert.IsFalse(workspace.TabsAtBottom, "出厂在顶部");
        List<string?> changed = [];
        workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        workspace.TabsAtBottom = true;
        workspace.TabsAtBottom = true;

        Assert.AreSequenceEqual([nameof(DockWorkspace.TabsAtBottom)], changed.ToArray(), "只在值真的变了时通知一次");
    }

    [TestMethod]
    public void TabsAtBottom_DoesNotTouchPerPaneTabPositions()
    {
        var workspace = new DockWorkspace();
        workspace.PrimaryGroup.TabsPosition = DockTabsPosition.Left;

        workspace.TabsAtBottom = true;

        Assert.AreEqual(DockTabsPosition.Left, workspace.PrimaryGroup.TabsPosition,
                        "停到左 / 右侧是按窗格单独挑的,全局的顶 / 底开关不改写它");
    }
}
