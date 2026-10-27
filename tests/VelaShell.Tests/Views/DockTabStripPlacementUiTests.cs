using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Docking;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Tests.Views;

/// <summary>
/// 标签条停在哪一边:设置「标签栏位置」把横排的标签条停到顶部或底部(所有窗格一起变),
/// 右键菜单停到左 / 右侧的窗格不受它影响。
/// </summary>
/// <remarks>
/// 设了 <c>VELASHELL_VISUAL_QA_DIR</c> 时各存一帧,供人眼复核(与 <see cref="DockTabStripUiTests" /> 同款)。
/// </remarks>
[TestClass]
[TestCategory("DockPaneUi")]
public sealed class DockTabStripPlacementUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DockTabStripPlacementUiTests).Assembly);

    [TestMethod]
    public void TabsAtBottom_MovesEveryHorizontalStripBelowItsContent_AndBack()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            PluginDocument[] docs = Docs("alpha", "beta", "gamma");
            Add(workspace, docs);
            workspace.SplitDocument(docs[2], DockOrientation.Horizontal);
            (Window window, DockWorkspaceControl dock) = Show(workspace, 900, 400);
            DockGroupControl[] groups = Groups(dock);
            Assert.HasCount(2, groups, "拆分之后得有两个窗格,才测得出「所有窗格一起变」");
            foreach (DockGroupControl group in groups)
            {
                AssertStripAt(group, Dock.Top);
            }

            workspace.TabsAtBottom = true;
            Pump(window);
            foreach (DockGroupControl group in groups)
            {
                AssertStripAt(group, Dock.Bottom);
                // 标签自己的样子不变:强调线仍贴激活标签的上缘 —— 在底部,那正是朝着内容区的一边。
                Control active = Container(group, group.Group!.ActiveDocument!);
                Assert.AreEqual(active.TranslatePoint(default, TabsOverlay(group))!.Value.Y, IndicatorTarget(group).Y, 0.5);
            }
            SaveOptionalFrame(window, "dock-tabs-bottom.png");

            workspace.TabsAtBottom = false;
            Pump(window);
            foreach (DockGroupControl group in groups)
            {
                AssertStripAt(group, Dock.Top);
            }

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void TabsAtBottom_LeavesSideStripsAlone_UntilThePaneReturnsToHorizontal()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            Add(workspace, Docs("alpha", "beta"));
            workspace.PrimaryGroup.TabsPosition = DockTabsPosition.Left;
            (Window window, DockWorkspaceControl dock) = Show(workspace, 800, 400);
            DockGroupControl group = Groups(dock).Single();

            workspace.TabsAtBottom = true;
            Pump(window);
            AssertStripAt(group, Dock.Left, "停到左侧是按窗格单独挑的,全局的顶 / 底开关不去动它");

            workspace.PrimaryGroup.TabsPosition = DockTabsPosition.Top;
            Pump(window);
            AssertStripAt(group, Dock.Bottom, "回到横排时落在「标签栏位置」定的那一边");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void TabsAtBottom_WithMultipleRows_GrowsUpFromTheBottomEdge()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace { TabsAtBottom = true, MultiRowTabs = true };
            Add(workspace, Docs([.. Enumerable.Range(1, 8).Select(i => $"server-{i}.example.com")]));
            (Window window, DockWorkspaceControl dock) = Show(workspace, 640, 400);
            DockGroupControl group = Groups(dock).Single();

            Rect strip = BoundsIn(group, group.TabStripPanel);
            Assert.IsGreaterThan(40d, strip.Height, "放不下就换行,标签条长高");
            AssertStripAt(group, Dock.Bottom);
            SaveOptionalFrame(window, "dock-tabs-bottom-multirow.png");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void RightClickMenu_HorizontalItem_NamesTheEdgeItWillLandOn()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            PluginWorkspaceDocument document = WorkspaceDocument("local-redis");
            workspace.AddDocument(document);
            (Window window, DockWorkspaceControl dock) = Show(workspace, 800, 400);
            DockGroupControl group = Groups(dock).Single();

            Assert.AreEqual(Strings.Get("Dock_TabsTop"), OpenHorizontalItem(window, group, document).Header);
            CloseMenu(group, document);

            workspace.TabsAtBottom = true;
            workspace.PrimaryGroup.TabsPosition = DockTabsPosition.Right;
            Pump(window);
            MenuItem item = OpenHorizontalItem(window, group, document);
            Assert.AreEqual(Strings.Get("Dock_TabsBottom"), item.Header, "设成底部之后还写「顶部」,点下去却落到底部,菜单就是在说谎");

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            CloseMenu(group, document);
            Pump(window);
            Assert.AreEqual(DockTabsPosition.Top, workspace.PrimaryGroup.TabsPosition);
            AssertStripAt(group, Dock.Bottom);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void SideStrip_ShowsOverflowControlsAtItsFoot_AndScrollsVertically()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            Add(workspace, Docs([.. Enumerable.Range(1, 15).Select(i => $"host-{i:00}")]));
            workspace.PrimaryGroup.TabsPosition = DockTabsPosition.Left;
            // 窗口够宽、不够高:横排放得下,纵排放不下 —— 两条轴各自判定才分得出来。
            (Window window, DockWorkspaceControl dock) = Show(workspace, 2400, 300);
            DockGroupControl group = Groups(dock).Single();
            ScrollViewer strip = group.TabScrollViewer;
            StackPanel controls = Named<StackPanel>(group, "OverflowControls");

            Assert.IsGreaterThan(strip.Viewport.Height + 0.5, strip.Extent.Height, "纵排得真的放不下");
            Assert.IsTrue(controls.IsVisible, "纵排溢出时三连钮要出现(以前按宽度判定,永远不出现)");
            Rect stripRect = BoundsIn(group, group.TabStripPanel);
            Rect controlsRect = BoundsIn(group, controls);
            Assert.AreEqual(stripRect.Bottom - 6, controlsRect.Bottom, 0.5, "停在标签条底端");
            Assert.AreEqual(stripRect.Center.X, controlsRect.Center.X, 1, "在标签条里居中排成一行");
            Assert.AreEqual(90d, Rotation(group, "ScrollBackIcon"), "滚动箭头转成上 / 下");
            Assert.AreEqual(-90d, Rotation(group, "TabListIcon"), "下拉箭头指向列表弹出的那一边(左侧条 → 朝右)");

            Button back = Named<Button>(group, "ScrollBackButton");
            Button forward = Named<Button>(group, "ScrollForwardButton");
            Assert.IsFalse(back.IsEnabled, "还在最上面,往回滚的那一枚置灰");
            Assert.IsTrue(forward.IsEnabled);
            ClickCenter(window, forward);
            Pump(window);
            Assert.IsGreaterThan(0d, strip.Offset.Y, "纵排滚的是 Offset.Y");
            Assert.AreEqual(0d, strip.Offset.X, 0.5);
            Assert.IsTrue(back.IsEnabled);
            SaveOptionalFrame(window, "dock-tabs-side-overflow.png");

            ClickCenter(window, back);
            Pump(window);
            Assert.AreEqual(0d, strip.Offset.Y, 0.5);

            // 回到横排:这么宽的窗口一排放得下,三连钮跟着消失,箭头转回去。
            workspace.PrimaryGroup.TabsPosition = DockTabsPosition.Top;
            Pump(window);
            Assert.IsFalse(controls.IsVisible, "横排按宽度判定:放得下就不出现,哪怕内容比视口高");
            Assert.AreEqual(0d, Rotation(group, "ScrollBackIcon"));

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void BottomStrip_TabListOpensUpward()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace { TabsAtBottom = true };
            Add(workspace, Docs([.. Enumerable.Range(1, 12).Select(i => $"host-{i:00}")]));
            (Window window, DockWorkspaceControl dock) = Show(workspace, 480, 300);
            DockGroupControl group = Groups(dock).Single();

            Assert.IsTrue(Named<StackPanel>(group, "OverflowControls").IsVisible, "横排放不下,三连钮照常出现");
            Assert.AreEqual(180d, Rotation(group, "TabListIcon"), "停在底部时列表往上弹,箭头朝上");
            Assert.AreEqual(0d, Rotation(group, "ScrollBackIcon"), "横排的滚动箭头不转");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void NewTabButton_StaysHidden_UntilTheHostHandlesIt()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            Add(workspace, Docs("alpha"));
            (Window window, DockWorkspaceControl dock) = Show(workspace, 800, 300);
            DockGroupControl group = Groups(dock).Single();
            Button plus = NewTabButton(group);
            Assert.IsFalse(plus.IsVisible, "没人接的「+」点了什么也不会发生,不该摆出来");
            Assert.AreEqual(default, group.TabScrollViewer.Margin, "也不该为它让出位置");

            static void Handler(DockGroup _)
            {
            }
            dock.NewTabRequested += Handler;
            Pump(window);
            Assert.IsTrue(plus.IsVisible);

            dock.NewTabRequested -= Handler;
            Pump(window);
            Assert.IsFalse(plus.IsVisible);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void NewTabButton_FollowsTheLastTab_ThenPinsToTheViewportEnd()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            PluginDocument[] docs = Docs("alpha", "beta");
            Add(workspace, docs);
            (Window window, DockWorkspaceControl dock) = Show(workspace, 900, 300);
            dock.NewTabRequested += _ => { };
            Pump(window);
            DockGroupControl group = Groups(dock).Single();
            Button plus = NewTabButton(group);
            ScrollViewer strip = group.TabScrollViewer;

            Rect last = BoundsIn(group, Container(group, docs[1]));
            Rect plusRect = BoundsIn(group, plus);
            Assert.AreEqual(last.Right + 2, plusRect.Left, 0.5, "放得下时紧跟在最后一个标签后面");
            Assert.AreEqual(last.Center.Y, plusRect.Center.Y, 0.5, "与标签同一排、垂直居中");
            SaveOptionalFrame(window, "dock-tabs-new-button.png");

            foreach (PluginDocument doc in Docs([.. Enumerable.Range(1, 12).Select(i => $"host-{i:00}")]))
            {
                workspace.AddDocument(doc);
            }
            Pump(window);
            Assert.IsGreaterThan(strip.Viewport.Width + 0.5, strip.Extent.Width, "这么多标签得溢出了");
            plusRect = BoundsIn(group, plus);
            Rect viewport = BoundsIn(group, strip);
            Assert.AreEqual(viewport.Left + strip.Viewport.Width + 2, plusRect.Left, 0.5, "溢出时钉在视口末端");
            Assert.IsLessThanOrEqualTo(BoundsIn(group, Named<StackPanel>(group, "OverflowControls")).Left, plusRect.Right,
                                       "不压到溢出三连钮上");

            strip.Offset = strip.Offset.WithX(0);
            Pump(window);
            Assert.AreEqual(plusRect.Left, BoundsIn(group, plus).Left, 0.5, "滚到哪儿它都留在原地,不随标签滚走");
            SaveOptionalFrame(window, "dock-tabs-new-button-overflow.png");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void NewTabButton_SitsBelowTheLastTab_OnASideStrip()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            PluginDocument[] docs = Docs("alpha", "beta");
            Add(workspace, docs);
            workspace.PrimaryGroup.TabsPosition = DockTabsPosition.Left;
            (Window window, DockWorkspaceControl dock) = Show(workspace, 900, 400);
            dock.NewTabRequested += _ => { };
            Pump(window);
            DockGroupControl side = Groups(dock).Single();

            Rect last = BoundsIn(side, Container(side, docs[1]));
            Rect plusRect = BoundsIn(side, NewTabButton(side));
            Assert.AreEqual(last.Bottom + 2, plusRect.Top, 0.5, "纵排时在最后一个标签下面");
            Assert.AreEqual(14d, plusRect.Left + 5, 0.5, "「+」字形与标签内容的左缘对齐");
            SaveOptionalFrame(window, "dock-tabs-new-button-side.png");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void NewTabButton_InAnEmptyRootPane_SitsAtTheStartOfTheStrip()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            (Window window, DockWorkspaceControl dock) = Show(workspace, 800, 300);
            dock.NewTabRequested += _ => { };
            Pump(window);
            DockGroupControl group = Groups(dock).Single();

            Rect plusRect = BoundsIn(group, NewTabButton(group));
            Rect strip = BoundsIn(group, group.TabStripPanel);
            Assert.AreEqual(2d, plusRect.Left, 0.5, "一个标签都没有时就在标签条的起点 —— 那正是「一个会话都没开」时最该点的地方");
            Assert.AreEqual(strip.Center.Y, plusRect.Center.Y, 0.5);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void NewTabButton_WithMultipleRows_FollowsTheLastTabOnTheLastRow()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace { MultiRowTabs = true };
            PluginDocument[] docs = Docs([.. Enumerable.Range(1, 7).Select(i => $"server-{i}.example.com")]);
            Add(workspace, docs);
            (Window window, DockWorkspaceControl dock) = Show(workspace, 640, 400);
            dock.NewTabRequested += _ => { };
            Pump(window);
            DockGroupControl group = Groups(dock).Single();

            Rect last = BoundsIn(group, Container(group, docs[^1]));
            Rect first = BoundsIn(group, Container(group, docs[0]));
            Rect plusRect = BoundsIn(group, NewTabButton(group));
            Assert.IsGreaterThan(first.Top, last.Top, "得真的换了行");
            Assert.AreEqual(last.Right + 2, plusRect.Left, 0.5);
            Assert.AreEqual(last.Center.Y, plusRect.Center.Y, 0.5, "落在最后一排");
            Assert.IsLessThanOrEqualTo(group.Bounds.Width, plusRect.Right, "换行时已经给它留了位置,不会伸出窗格");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void NewTabButton_AsksTheHostForItsOwnPane_ByMouseAndByKeyboard()
    {
        _session.Dispatch(() =>
        {
            var workspace = new DockWorkspace();
            PluginDocument[] docs = Docs("alpha", "beta", "gamma");
            Add(workspace, docs);
            workspace.SplitDocument(docs[2], DockOrientation.Horizontal);
            workspace.ActivateDocument(docs[0]);
            (Window window, DockWorkspaceControl dock) = Show(workspace, 900, 400);
            List<DockGroup> requested = [];
            dock.NewTabRequested += requested.Add;
            Pump(window);
            DockGroupControl left = Groups(dock).Single(g => ReferenceEquals(g.Group, workspace.PrimaryGroup));
            DockGroupControl right = Groups(dock).Single(g => !ReferenceEquals(g.Group, workspace.PrimaryGroup));

            ClickCenter(window, NewTabButton(right));
            Pump(window);
            Assert.HasCount(1, requested);
            Assert.AreSame(right.Group, requested[0], "报的是按下「+」的那一格");
            Assert.AreSame(docs[2], workspace.ActiveDocument, "那一格成了活动窗格,新标签照落点规则落进它");

            Button plus = NewTabButton(left);
            Assert.IsTrue(plus.Focus(), "「+」要能拿到键盘焦点");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump(window);
            Assert.HasCount(2, requested, "回车也能按下它");
            Assert.AreSame(left.Group, requested[1]);
            Assert.AreSame(docs[0], workspace.ActiveDocument, "键盘按下时没有指针那一下顺手激活,得自己把那一格设为活动窗格");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ---- 脚手架 ----

    private static Button NewTabButton(DockGroupControl group) => Named<Button>(group, "NewTabButton");

    private static double Rotation(DockGroupControl group, string iconName) =>
        Named<Control>(group, iconName).RenderTransform is RotateTransform rotate ? rotate.Angle : 0;

    private static void ClickCenter(Window window, Control control)
    {
        Point spot = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                     ?? throw new AssertFailedException("控件不在可视树上。");
        window.MouseDown(spot, MouseButton.Left);
        window.MouseUp(spot, MouseButton.Left);
    }

    private static PluginDocument[] Docs(params string[] titles) =>
        [.. titles.Select((title, i) => new PluginDocument($"doc-{i}", title, "acme.demo", new Border()))];

    private static void Add(DockWorkspace workspace, IEnumerable<DockDocument> documents)
    {
        DockDocument? first = null;
        foreach (DockDocument document in documents)
        {
            workspace.AddDocument(document);
            first ??= document;
        }
        if (first is not null)
        {
            workspace.ActivateDocument(first);
        }
    }

    private static (Window Window, DockWorkspaceControl Dock) Show(DockWorkspace workspace, double width, double height)
    {
        var dock = new DockWorkspaceControl { Workspace = workspace };
        var window = new Window { Width = width, Height = height, Content = dock };
        window.Show();
        Pump(window);
        return (window, dock);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static DockGroupControl[] Groups(DockWorkspaceControl dock) =>
        [.. dock.GetVisualDescendants().OfType<DockGroupControl>()];

    private static T Named<T>(DockGroupControl group, string name) where T : Control =>
        group.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static Panel TabsOverlay(DockGroupControl group) => Named<Panel>(group, "TabsOverlay");

    private static Control Container(DockGroupControl group, DockDocument document) =>
        group.TabsItemsControl.ContainerFromItem(document) ?? throw new AssertFailedException("标签容器没有实体化。");

    private static Rect BoundsIn(Visual root, Visual visual) =>
        new(visual.TranslatePoint(default, root) ?? throw new AssertFailedException("控件不在可视树上。"), visual.Bounds.Size);

    /// <summary>强调线落位的目标(读基值:换位走 180ms 过渡,headless 里时钟不走)。</summary>
    private static Point IndicatorTarget(DockGroupControl group)
    {
        ITransform target = Named<Border>(group, "ActiveTabIndicator").GetBaseValue(Visual.RenderTransformProperty).Value!;
        return new Point(target.Value.M31, target.Value.M32);
    }

    /// <summary>标签条贴着窗格的哪一边,分割线夹在它与内容区之间,内容区在另一侧。</summary>
    private static void AssertStripAt(DockGroupControl group, Dock edge, string? message = null)
    {
        Rect strip = BoundsIn(group, group.TabStripPanel);
        Rect separator = BoundsIn(group, Named<Border>(group, "StripSeparator"));
        // 量内容区的那层面板,而不是 ContentHost 本身:后者切内容时带着一段 translateY 的落定动画,
        // headless 里时钟不走,TranslatePoint 会把那几像素的位移也算进来。
        Rect content = BoundsIn(group, Named<Control>(group, "ContentHost").GetVisualParent()!);
        Size size = group.Bounds.Size;
        string because = message ?? $"标签条应当停在{edge}";
        switch (edge)
        {
            case Dock.Top:
                Assert.AreEqual(0d, strip.Top, 0.5, because);
                Assert.AreEqual(strip.Bottom, separator.Top, 0.5, because);
                Assert.AreEqual(separator.Bottom, content.Top, 0.5, because);
                Assert.AreEqual(size.Width, strip.Width, 0.5, because);
                break;
            case Dock.Bottom:
                Assert.AreEqual(size.Height, strip.Bottom, 0.5, because);
                Assert.AreEqual(strip.Top, separator.Bottom, 0.5, because);
                Assert.AreEqual(separator.Top, content.Bottom, 0.5, because);
                Assert.AreEqual(0d, content.Top, 0.5, because);
                Assert.AreEqual(size.Width, strip.Width, 0.5, because);
                break;
            case Dock.Left:
                Assert.AreEqual(0d, strip.Left, 0.5, because);
                Assert.AreEqual(size.Height, strip.Height, 0.5, because);
                Assert.AreEqual(strip.Right, separator.Left, 0.5, because);
                Assert.AreEqual(separator.Right, content.Left, 0.5, because);
                break;
            case Dock.Right:
                Assert.AreEqual(size.Width, strip.Right, 0.5, because);
                Assert.AreEqual(size.Height, strip.Height, 0.5, because);
                Assert.AreEqual(strip.Left, separator.Right, 0.5, because);
                Assert.AreEqual(separator.Left, content.Right, 0.5, because);
                break;
        }
    }

    /// <summary>在标签上真点一下右键(走 ContextRequested,与用户操作同一条路),返回「标签位置」里回到横排的那一项。</summary>
    private static MenuItem OpenHorizontalItem(Window window, DockGroupControl group, DockDocument document)
    {
        Control container = Container(group, document);
        Point spot = container.TranslatePoint(new Point(6, container.Bounds.Height / 2), window)!.Value;
        window.MouseDown(spot, MouseButton.Right);
        window.MouseUp(spot, MouseButton.Right);
        Pump(window);
        ContextMenu menu = Menu(group, document);
        Assert.IsTrue(menu.IsOpen, "右键应当打开标签的菜单");
        return FindMenuItem(menu.Items, DockTabItemBase.HorizontalTabsMenuItemName)
               ?? throw new AssertFailedException("「标签位置」子菜单里没有回到横排的那一项。");
    }

    private static void CloseMenu(DockGroupControl group, DockDocument document) => Menu(group, document).Close();

    private static ContextMenu Menu(DockGroupControl group, DockDocument document) =>
        Container(group, document).GetVisualDescendants().OfType<DockTabItemBase>().Single().ContextMenu!;

    private static MenuItem? FindMenuItem(IEnumerable<object?> items, string name)
    {
        foreach (MenuItem item in items.OfType<MenuItem>())
        {
            if (item.Name == name)
            {
                return item;
            }
            if (FindMenuItem(item.Items, name) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    private static void SaveOptionalFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "Skia headless renderer should produce a visual-QA frame.");
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }

    /// <summary>
    /// 插件工作台文档:五种标签里带「标签位置」子菜单、又不用起终端的那一种。
    /// </summary>
    private static PluginWorkspaceDocument WorkspaceDocument(string name) =>
        new(new SessionProfile
        {
            Name = name,
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = "redis",
            Host = "127.0.0.1",
            Port = 6379
        }, Guid.NewGuid(), "Redis", new FakeWorkspace());

    private sealed class FakeWorkspace : IWorkspaceDocument
    {
        public WorkspaceStatus Status { get; } = new(ProtocolSessionState.Connected);

        public event EventHandler<WorkspaceStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public object CreateView() => new TextBlock { Text = "panel" };

        public Task ReconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
