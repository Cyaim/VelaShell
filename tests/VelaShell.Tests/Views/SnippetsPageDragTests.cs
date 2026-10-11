using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Localization;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;
using VelaShell.Views.Settings;

namespace VelaShell.Tests.Views;

/// <summary>
/// #555 代码片段设置页的拖动排序:走真实的指针事件(按下手柄 → 移动 → 松手),
/// 核对落点、插入线、拖动中的变淡,以及松手后视图模型里的顺序。
/// </summary>
[TestClass]
[TestCategory("QuickCommands")]
public sealed class SnippetsPageDragTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SnippetsPageDragTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void DraggingARowGrip_OntoAnotherRow_MovesTheCommandThere()
    {
        OnUi(() =>
        {
            (Window window, SnippetsPage page, QuickCommandsViewModel snippets) = Show();
            QuickCommandGroupViewModel group = snippets.FilteredGroups.First(g => g.Commands.Count >= 4);
            QuickCommandViewModel dragged = group.Commands[0];
            QuickCommandViewModel anchor = group.Commands[2];
            QuickCommandViewModel next = group.Commands[3];

            Point from = Center(Grip(page, dragged), window);
            // 落在第三行的下半截:离「第三行之后」那条线最近。
            Point to = At(Row(page, anchor), window, 0.8);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Point(0, 8), RawInputModifiers.LeftMouseButton);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);

            var drop = page.DropTarget as SnippetsPage.CommandDropTarget;
            Assert.IsNotNull(drop, "拖到别的行上应有落点");
            Assert.AreSame(group, drop.Group);
            Assert.AreSame(next, drop.Before);
            Assert.IsTrue(Indicator(page).IsVisible, "要画插入线");
            Assert.Contains("dragging", Row(page, dragged).Classes);

            window.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.AreSame(dragged, group.Commands[2]);
            Assert.AreSame(next, group.Commands[3]);
            Assert.IsFalse(Indicator(page).IsVisible);
            Assert.IsNull(page.DropTarget);
            Assert.DoesNotContain(
                b => b.Classes.Contains("dragging"), page.GetVisualDescendants().OfType<Border>(),
                "松手后不该有残留的变淡"
            );
        });
    }

    [TestMethod]
    public void DraggingARowIntoAnotherGroup_MovesItThere()
    {
        OnUi(() =>
        {
            (Window window, SnippetsPage page, QuickCommandsViewModel snippets) = Show();
            QuickCommandGroupViewModel source = snippets.FilteredGroups[0];
            QuickCommandGroupViewModel target = snippets.FilteredGroups[1];
            QuickCommandViewModel dragged = source.Commands[0];
            QuickCommandViewModel anchor = target.Commands[0];

            Point from = Center(Grip(page, dragged), window);
            Point to = At(Row(page, anchor), window, 0.2);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Point(0, 8), RawInputModifiers.LeftMouseButton);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            QuickCommandViewModel moved = target.Commands[0];
            Assert.AreEqual(dragged.Id, moved.Id);
            Assert.IsTrue(moved.IsBuiltInOverride, "拖进别的分组的内置命令应换成同标识的自定义命令");
            Assert.DoesNotContain(c => c.Id == dragged.Id, source.Commands);
        });
    }

    [TestMethod]
    public void DraggingAGroupGrip_ReordersTheGroups()
    {
        OnUi(() =>
        {
            (Window window, SnippetsPage page, QuickCommandsViewModel snippets) = Show();
            QuickCommandGroupViewModel dragged = snippets.FilteredGroups[0];
            QuickCommandGroupViewModel before = snippets.FilteredGroups[3];

            Point from = Center(Grip(page, dragged), window);
            Point to = At(Card(page, before), window, 0) + new Point(0, 2);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Point(0, 8), RawInputModifiers.LeftMouseButton);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);

            var drop = page.DropTarget as SnippetsPage.GroupDropTarget;
            Assert.IsNotNull(drop);
            Assert.AreSame(before, drop.Before);
            Assert.Contains("dragging", Card(page, dragged).Classes);

            window.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.AreSame(dragged, snippets.FilteredGroups[2]);
            Assert.AreSame(before, snippets.FilteredGroups[3]);
        });
    }

    /// <summary>拖出列表(这里是拖到下面的表单区)再松手 = 取消,顺序不变。</summary>
    [TestMethod]
    public void ReleasingFarFromTheList_CancelsTheDrag()
    {
        OnUi(() =>
        {
            (Window window, SnippetsPage page, QuickCommandsViewModel snippets) = Show();
            QuickCommandGroupViewModel group = snippets.FilteredGroups[0];
            QuickCommandViewModel[] order = [.. group.Commands];
            Control list = page.FindControl<Panel>("SnippetListHost")!;

            Point from = Center(Grip(page, order[0]), window);
            Point away = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height + 200), window)!.Value;
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Point(0, 8), RawInputModifiers.LeftMouseButton);
            window.MouseMove(away, RawInputModifiers.LeftMouseButton);

            Assert.IsNull(page.DropTarget);
            Assert.IsFalse(Indicator(page).IsVisible);

            window.MouseUp(away, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.AreSequenceEqual(order, [.. group.Commands]);
        });
    }

    /// <summary>按下没挪够阈值就松手是点击,不是拖动:不出插入线、不动顺序。</summary>
    [TestMethod]
    public void ClickingAGrip_DoesNotMoveAnything()
    {
        OnUi(() =>
        {
            (Window window, SnippetsPage page, QuickCommandsViewModel snippets) = Show();
            QuickCommandGroupViewModel group = snippets.FilteredGroups[0];
            QuickCommandViewModel[] order = [.. group.Commands];

            Point at = Center(Grip(page, order[0]), window);
            window.MouseDown(at, MouseButton.Left);
            window.MouseMove(at + new Point(0, 2), RawInputModifiers.LeftMouseButton);
            window.MouseUp(at + new Point(0, 2), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.IsFalse(Indicator(page).IsVisible);
            Assert.AreSequenceEqual(order, [.. group.Commands]);
            Assert.IsFalse(group.HasCustomOrder);
        });
    }

    /// <summary>「未分组」固定垫底:它的手柄不接指针(不显示、也拖不起来)。</summary>
    [TestMethod]
    public void UngroupedGroup_HasNoDraggableGrip()
    {
        OnUi(() =>
        {
            (_, SnippetsPage page, QuickCommandsViewModel snippets) = Show();
            QuickCommandGroupViewModel ungrouped = snippets.FilteredGroups.Single(g => g.IsDefault);

            Assert.IsFalse(Grip(page, ungrouped).IsHitTestVisible);
        });
    }

    private static (Window Window, SnippetsPage Page, QuickCommandsViewModel Snippets) Show()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        IThemeService theme = Substitute.For<IThemeService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var data = new QuickCommandData
        {
            Groups = QuickCommandGroupCatalog.CreateSystemGroups(),
            Commands = [new() { Name = "loose", CommandText = "pwd", GroupId = QuickCommandGroupCatalog.DefaultGroupId }],
        };
        repository.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new QuickCommandLoadResult(data)));

        var viewModel = new SettingsViewModel(settings, theme, quickCommandRepository: repository);
        viewModel.Snippets!.LoadAsync().GetAwaiter().GetResult();

        var page = new SnippetsPage { DataContext = viewModel };
        // 整张列表要都在窗口里:页面没有套滚动区,超出窗口的行收不到指针。
        var window = new Window { Width = 900, Height = 3200, Content = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, page, viewModel.Snippets);
    }

    private static Border Grip(SnippetsPage page, object item) =>
        page.GetVisualDescendants()
            .OfType<Border>()
            .First(b => b.Classes.Contains("drag-grip") && ReferenceEquals(b.DataContext, item));

    private static Border Row(SnippetsPage page, QuickCommandViewModel command) =>
        page.GetVisualDescendants()
            .OfType<Border>()
            .First(b => b.Classes.Contains("snippet-row") && ReferenceEquals(b.DataContext, command));

    private static Border Card(SnippetsPage page, QuickCommandGroupViewModel group) =>
        page.GetVisualDescendants()
            .OfType<Border>()
            .First(b => b.Classes.Contains("snippet-group") && ReferenceEquals(b.DataContext, group));

    private static Border Indicator(SnippetsPage page) => page.FindControl<Border>("DropIndicator")!;

    private static Point Center(Control control, Visual window) => At(control, window, 0.5);

    /// <summary>控件水平居中、纵向在 <paramref name="fraction" /> 处的点(窗口坐标)。</summary>
    private static Point At(Control control, Visual window, double fraction) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height * fraction), window)!.Value;

    private static void OnUi(Action action) =>
        _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}
