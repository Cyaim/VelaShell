using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.Tests.TestSupport;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 资源管理器的过滤框按设计高 28,与快捷命令区、工具窗口的搜索框一致(DESIGN.md §4.2)。
/// </summary>
/// <remarks>
/// 原先只写了 <c>Height="26"</c>,Fluent 的 TextBox 最小高度 32 压过它,实际渲染成 32 —— 照着 XAML 读不出来,
/// 只有真排一次版才看得见;同类问题由 <see cref="Design.InputMinHeightTests" /> 按源码扫。
/// </remarks>
[TestClass]
[TestCategory("UI")]
public sealed class SidebarSearchBoxHeightUiTests
{
    private static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SidebarSearchBoxHeightUiTests).Assembly);

    [TestMethod]
    public Task ExplorerFilterBox_RendersAtTheSearchBoxHeight() => Session.RunOnUiAsync(() =>
    {
        var filter = new SessionTreeFilterBox();
        var window = new Window { Width = 280, Height = 200, Content = new StackPanel { Children = { filter } } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(28, filter.Bounds.Height, 0.5, "过滤框应按 28 渲染,不能被 Fluent 的最小高度撑到 32。");
        }
        finally
        {
            window.Close();
        }
    });
}
