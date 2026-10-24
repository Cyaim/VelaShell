using System.Globalization;
using Avalonia;
using VelaShell.Converters;
using VelaShell.Docking.Model;

namespace VelaShell.Tests.Docking;

/// <summary>
/// 标签条的工作区级开关:标签栏位置(顶 / 底)。它与「多行显示标签页」一样整个工作区一个值,
/// 停靠层只负责通知,摆法由 <c>DockGroupControl</c> 按它重排(见 <c>DockTabStripPlacementUiTests</c>)。
/// 另有溢出判定的取轴:横排比宽度、纵排比高度。
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
    public void OverflowConverter_MeasuresAlongTheAxisTheTabsRunOn()
    {
        // 横排:内容比视口宽才算溢出;同样的尺寸放到纵排条上,比的是高度。
        Assert.IsTrue(Overflowing(new Size(900, 35), new Size(600, 35), DockTabsPosition.Top));
        Assert.IsFalse(Overflowing(new Size(900, 35), new Size(600, 35), DockTabsPosition.Left));
        // 纵排:内容宽度永远等于视口宽度 —— 只比宽度的旧判定在这里永远是 false。
        Assert.IsTrue(Overflowing(new Size(150, 800), new Size(150, 300), DockTabsPosition.Left));
        Assert.IsTrue(Overflowing(new Size(150, 800), new Size(150, 300), DockTabsPosition.Right));
        Assert.IsFalse(Overflowing(new Size(150, 800), new Size(150, 300), DockTabsPosition.Top));
        Assert.IsFalse(Overflowing(new Size(600.4, 35), new Size(600, 35), DockTabsPosition.Top), "0.5px 容差");
        Assert.IsFalse((bool)TabStripOverflowConverter.Instance.Convert(
                           [new Size(900, 35), new Size(600, 35)], typeof(bool), null, CultureInfo.InvariantCulture),
                       "输入不全(绑定还没就位)时不出现");
    }

    private static bool Overflowing(Size extent, Size viewport, DockTabsPosition position) =>
        (bool)TabStripOverflowConverter.Instance.Convert([extent, viewport, position], typeof(bool), null,
                                                        CultureInfo.InvariantCulture);

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
