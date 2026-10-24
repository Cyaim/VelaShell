using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using VelaShell.Converters;
using VelaShell.Docking.Model;

namespace VelaShell.Docking.Controls;

/// <summary>
/// 标签组控件:标签条 + 分割线 + 内容区。DataContext 为 <see cref="DockGroup" />,
/// 由 <see cref="DockWorkspaceControl" /> 创建并注入工作区与视图解析器
/// (视图按文档缓存,切换标签复用同一控件实例)。
/// </summary>
public partial class DockGroupControl : UserControl
{
    /// <summary>一格滚轮翻过的像素数:约合半个标签,连滚几下能过一屏,又不会一下窜到头。</summary>
    private const double WheelStep = 80;

    /// <summary>溢出区两枚滚动钮点一下(或按住连发时每一下)滚过的像素数,约合一个收到下限的标签。</summary>
    private const double ScrollButtonStep = 120;

    /// <summary>标签条一排的高度(不含下方 1px 分割线);多排时每排也是这个高度。</summary>
    private const double TabStripRowHeight = 35;

    private Func<DockDocument, Control?>? _viewResolver;
    private int _contentMotionGeneration;
    private readonly Transitions _contentTransitions;
    private readonly Transitions _indicatorTransitions;
    private bool _indicatorPlaced;
    private (double X, double Y, double W, double H) _indicatorGeometry = (-1, -1, -1, -1);
    private Orientation _tabsOrientation = Orientation.Horizontal;
    private bool _multiRowTabs;
    private bool _revealActiveTab;

    /// <summary>初始化控件,订阅标签滚动变化并注册全组激活的指针处理器。</summary>
    public DockGroupControl()
    {
        InitializeComponent();
        _contentTransitions = ContentHost.Transitions
            ?? throw new InvalidOperationException("ContentHost transitions are not configured.");
        _indicatorTransitions = ActiveTabIndicator.Transitions
            ?? throw new InvalidOperationException("ActiveTabIndicator transitions are not configured.");
        // 面板按当前的排向与多排开关现造:模板只在面板(重新)生成时才调用,
        // 之后的切换直接改活面板的属性(ApplyTabStripLayout),不重建标签。
        TabsHost.ItemsPanel = new FuncTemplate<Panel?>(CreateTabPanel);
        TabScroll.ScrollChanged += (_, _) =>
        {
            UpdateScrollButtons();
            UpdateNewTabButton();
        };
        // 隧道阶段接管滚轮:ScrollViewer 自己会把滚轮当纵向滚动,而标签条是横排的,
        // 于是"在标签条上滚一下"什么也不会发生,标签一多就只能去够右端那两枚小箭头。
        TabScroll.AddHandler(PointerWheelChangedEvent, OnTabsWheel, RoutingStrategies.Tunnel);
        // 标签宽度随标题变化、容器随集合重建 —— 指示器与「+」的几何跟着布局走最省心;
        // 两者对相同几何都不重写属性,不会造成布局风暴。
        TabsHost.LayoutUpdated += (_, _) =>
        {
            UpdateActiveTabIndicator();
            UpdateNewTabButton();
        };
        // 点击本组任意位置(含终端内容区)即把本组的选中文档设为全局激活 —— 对应原
        // Dock 的 FocusedDockable 语义;缺了它,分屏后点另一个窗格输入,SFTP 面板与
        // 状态栏不会跟随切换。Tunnel + handledEventsToo:终端控件会吞掉
        // 指针事件,冒泡阶段收不到。点标签时本处理器先按组当前选中激活一次,随后
        // DockTabItem 再精确激活被点的标签,结果一致。
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>标签条上滚滚轮 = 沿标签排列方向翻标签(纵排标签条则是上下翻)。</summary>
    /// <remarks>没有溢出时不吞事件 —— 那一下该照常传给下面的人,而不是变成一次静默的空操作。</remarks>
    private void OnTabsWheel(object? sender, PointerWheelEventArgs e)
    {
        double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta == 0 || !TabStripOverflowConverter.IsOverflowing(TabScroll.Extent, TabScroll.Viewport, IsVerticalStrip))
        {
            return;
        }
        // 滚轮向前(远离自己)= 往标签条的前端翻,与列表滚动的方向感一致。
        ScrollTabsBy(-delta * WheelStep);
        e.Handled = true;
    }

    /// <summary>标签沿哪条轴排:停到左 / 右侧时纵排,否则横排(顶部或底部)。</summary>
    private bool IsVerticalStrip => (Group?.TabsPosition ?? DockTabsPosition.Top) != DockTabsPosition.Top;

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Workspace is { } workspace && Group is { ActiveDocument: { } active } && !ReferenceEquals(workspace.ActiveDocument, active))
        {
            workspace.ActivateDocument(active);
        }
    }

    /// <summary>当前 DataContext 承载的标签组;非 <see cref="DockGroup" /> 时为 null。</summary>
    public DockGroup? Group => DataContext as DockGroup;

    /// <summary>所属停靠工作区,由 <see cref="Initialize" /> 注入。</summary>
    public DockWorkspace? Workspace { get; private set; }

    /// <summary>所属工作区控件(拖拽控制器经由它取得)。</summary>
    public DockWorkspaceControl? WorkspaceControl { get; private set; }

    internal ScrollViewer TabScrollViewer => TabScroll;

    internal ItemsControl TabsItemsControl => TabsHost;

    internal Panel TabStripPanel => TabStripArea;

    /// <summary>注入工作区、工作区控件与视图解析器,订阅标签组变更并刷新内容区。</summary>
    public void Initialize(DockWorkspace workspace, DockWorkspaceControl workspaceControl, Func<DockDocument, Control?> viewResolver)
    {
        Workspace = workspace;
        WorkspaceControl = workspaceControl;
        _viewResolver = viewResolver;
        workspace.ActiveDocumentChanged += OnWorkspaceActiveDocumentChanged;
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        if (Group is { } group)
        {
            group.PropertyChanged += OnGroupPropertyChanged;
            group.Documents.CollectionChanged += OnDocumentsChanged;
        }
        ApplyTabStripLayout();
        UpdateContent();
        UpdateActivePaneState();
        _revealActiveTab = true;
    }

    private void OnDocumentsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateContent();
        // 标签换了个组住,而全局激活文档**没变**(把当前标签直接拖到隔壁窗格就是这样),
        // ActiveDocumentChanged 不会响;活动窗格的归属却已经变了,得在这里补一次。
        UpdateActivePaneState();
    }

    private void OnWorkspaceActiveDocumentChanged(DockDocument? document) => UpdateActivePaneState();

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DockWorkspace.MultiRowTabs) or nameof(DockWorkspace.TabsAtBottom))
        {
            ApplyTabStripLayout();
        }
    }

    /// <summary>本组是否是"活动窗格"(承载着全局激活文档的那一格),供样式弱化其余窗格的强调线。</summary>
    private void UpdateActivePaneState() =>
        PseudoClasses.Set(
            ":activepane",
            Workspace?.ActiveDocument is { } active && Group is { } group && group.Documents.Contains(active));

    /// <summary>
    /// (重新)挂到可视树时恢复订阅与内容。分离处理器为避免双父级会把 Target 置空并退订
    /// 全部事件——原设计假设"摘下的控件必被丢弃",但任何祖先级的摘挂(窗口内容重排、
    /// 宿主容器切换、托盘隐藏恢复)都会让**同一实例**重挂:不在这里自愈,重挂回来的
    /// 就是一个既收不到事件又没有内容的死控件,表现为"终端内容凭空消失"
    /// (2026-07-23 实证,DockContentBlankHuntTests 序列 4 锁定)。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Workspace is not { } workspace || Group is not { } group)
        {
            return; // 尚未 Initialize(首挂由 Initialize 完成接线),无从自愈。
        }
        // 先退订再订阅:首挂紧跟 Initialize 之后,不加防重会双份订阅。
        group.PropertyChanged -= OnGroupPropertyChanged;
        group.Documents.CollectionChanged -= OnDocumentsChanged;
        workspace.ActiveDocumentChanged -= OnWorkspaceActiveDocumentChanged;
        workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        group.PropertyChanged += OnGroupPropertyChanged;
        group.Documents.CollectionChanged += OnDocumentsChanged;
        workspace.ActiveDocumentChanged += OnWorkspaceActiveDocumentChanged;
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        // 离树期间多排开关可能被切过(另一个窗格的右键菜单、设置页),重挂时对齐一次。
        ApplyTabStripLayout();
        UpdateActivePaneState();
        if (ContentHost.Target is null)
        {
            UpdateContent();
            UpdateActiveTabIndicator();
        }
    }

    /// <summary>从可视树分离时退订标签组事件,并释放对缓存视图的引用以避免双父级。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _contentMotionGeneration++;
        RestoreContentMotion();
        if (Group is { } group)
        {
            group.PropertyChanged -= OnGroupPropertyChanged;
            group.Documents.CollectionChanged -= OnDocumentsChanged;
        }
        if (Workspace is { } workspace)
        {
            workspace.ActiveDocumentChanged -= OnWorkspaceActiveDocumentChanged;
            workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        }
        // 释放对缓存视图的引用,避免下一个宿主收养时双父级;若本实例被重挂,
        // OnAttachedToVisualTree 会自愈(重订阅 + 重填内容)。
        ContentHost.Target = null;
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DockGroup.ActiveDocument):
                _revealActiveTab = true;
                UpdateContent();
                UpdateActiveTabIndicator();
                break;
            case nameof(DockGroup.TabsPosition):
                ApplyTabStripLayout();
                break;
        }
    }

    /// <summary>
    /// 把滑动强调线对齐到激活标签:水平模式为贴顶的 2px 横线,垂直模式为贴内侧边的
    /// 2px 竖线(标签在左 → 线贴条带右缘,反之贴左缘)。首次落位不做动画(指示器
    /// 不该从别的组的旧位置飘进来),此后位置/宽度经 180ms 过渡滑动 —— 这比每个标签
    /// 自己的顶线瞬时跳变自然得多。
    /// </summary>
    private void UpdateActiveTabIndicator()
    {
        DockDocument? active = Group?.ActiveDocument;
        Control? container = active is null ? null : TabsHost.ContainerFromItem(active);
        if (container is null || container.Bounds.Width <= 0 || container.Bounds.Height <= 0)
        {
            ActiveTabIndicator.IsVisible = false;
            _indicatorPlaced = false;
            _indicatorGeometry = (-1, -1, -1, -1);
            return;
        }
        if (_revealActiveTab)
        {
            // 激活的标签滚进可视区(DESIGN.md「Tab scroll」):从「所有标签页」下拉里挑一个、
            // Ctrl+Tab 切到溢出区里的那个,都该让人看得见它。放在布局回调里做,容器这时才有边界;
            // 没有溢出(或多排)时 ScrollViewer 无处可滚,这一句什么也不做。
            _revealActiveTab = false;
            container.BringIntoView();
        }
        Point origin = container.TranslatePoint(default, TabsOverlay) ?? default;
        bool vertical = (Group?.TabsPosition ?? DockTabsPosition.Top) != DockTabsPosition.Top;
        (double X, double Y, double W, double H) geometry = vertical
            ? (Group?.TabsPosition == DockTabsPosition.Left ? Math.Max(0, TabsOverlay.Bounds.Width - 2) : 0,
               Math.Round(origin.Y), 2, Math.Round(container.Bounds.Height))
            // 横排贴激活标签的顶边;单排时那就是 0,多排时是它所在那一排的顶。
            : (Math.Round(origin.X), Math.Round(origin.Y), Math.Round(container.Bounds.Width), 2);
        if (geometry == _indicatorGeometry && ActiveTabIndicator.IsVisible)
        {
            return; // 布局回调高频触发;几何没变就绝不重写属性,避免过渡被反复重启。
        }
        _indicatorGeometry = geometry;

        bool animate = _indicatorPlaced;
        if (!animate)
        {
            ActiveTabIndicator.Transitions = null;
        }
        ActiveTabIndicator.Width = geometry.W;
        ActiveTabIndicator.Height = geometry.H;
        ActiveTabIndicator.RenderTransform = TransformOperations.Parse(
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"translate({geometry.X}px, {geometry.Y}px)"));
        ActiveTabIndicator.IsVisible = true;
        if (!animate)
        {
            _indicatorPlaced = true;
            Dispatcher.UIThread.Post(() => ActiveTabIndicator.Transitions ??= _indicatorTransitions,
                DispatcherPriority.Render);
        }
    }

    private void UpdateContent()
    {
        DockDocument? active = Group?.ActiveDocument;
        Control? view = active is null ? null : _viewResolver?.Invoke(active);
        int motionGeneration = ++_contentMotionGeneration;
        if (view is null || !ContentHost.IsEffectivelyVisible)
        {
            RestoreContentMotion();
            ContentHost.Target = view;
            UpdateEmptyState();
            return;
        }

        ContentHost.Transitions = null;
        ContentHost.Classes.Add("settling");
        ContentHost.Opacity = 0;
        ContentHost.RenderTransform = TransformOperations.Parse("translateY(2px)");
        ContentHost.Target = view;
        UpdateEmptyState();
        ContentHost.ClearValue(OpacityProperty);
        ContentHost.ClearValue(RenderTransformProperty);
        Dispatcher.UIThread.Post(() =>
        {
            if (motionGeneration == _contentMotionGeneration && ReferenceEquals(ContentHost.Target, view))
            {
                ContentHost.Transitions = _contentTransitions;
                ContentHost.Classes.Remove("settling");
            }
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// 空窗格的提示分两种:能撤掉的空窗格给"拖标签进来 / 关掉它",
    /// 整个工作区都空了则只说现状与下一步。
    /// </summary>
    /// <remarks>
    /// 判据是**有没有父分栏**,而不是"是不是主组":根窗格没有父分栏,
    /// <see cref="DockWorkspace.ClosePane" /> 对它是空操作 —— 在那儿摆一枚点了不会有
    /// 任何反应的按钮,比没有按钮更糟;同理,一个标签都没有的时候,
    /// "把标签拖到这里"指的是一件当下做不到的事(用户实测反馈)。
    /// </remarks>
    private void UpdateEmptyState()
    {
        bool empty = Group is { Documents.Count: 0 };
        bool removable = empty && Group?.Parent is not null;
        EmptyHint.IsVisible = empty;
        DropTargetHint.IsVisible = removable;
        NoSessionHint.IsVisible = empty && !removable;
    }

    private void RestoreContentMotion()
    {
        ContentHost.Transitions = null;
        ContentHost.Classes.Remove("settling");
        ContentHost.ClearValue(OpacityProperty);
        ContentHost.ClearValue(RenderTransformProperty);
        ContentHost.Transitions = _contentTransitions;
    }

    /// <summary>
    /// 标签条的摆法:本组的标签位置(右键菜单“标签位置”)、工作区的「标签栏位置」(顶 / 底)与
    /// 「多行显示标签页」三个开关合起来决定。横排(Top)为默认,停在顶部还是底部看工作区;
    /// Left/Right 时标签条纵排、分割线立起、滚动方向改为垂直(溢出三连钮随之按高度判定、挪到底端,
    /// 见 ApplyOverflowControlsLayout)。横排且开了多排时两个方向都不滚:面板拿到确定的宽度去换行,高度随排数长。
    /// </summary>
    /// <remarks>
    /// 停到底部只是把标签条连同分割线整体挪到内容区下方,标签自己的样子不变 —— 激活强调线
    /// 仍贴标签的上缘,在底部那正好是朝着内容区的一边,与纵排时强调线贴内侧边同一个道理。
    /// </remarks>
    private void ApplyTabStripLayout()
    {
        DockTabsPosition position = Group?.TabsPosition ?? DockTabsPosition.Top;
        bool vertical = position != DockTabsPosition.Top;
        bool multiRow = !vertical && Workspace?.MultiRowTabs == true;
        Dock edge = position switch
        {
            DockTabsPosition.Left => Dock.Left,
            DockTabsPosition.Right => Dock.Right,
            _ => Workspace?.TabsAtBottom == true ? Dock.Bottom : Dock.Top
        };
        // 两者按同一边停靠,先后次序决定了分割线永远夹在标签条与内容区之间。
        DockPanel.SetDock(TabStripArea, edge);
        DockPanel.SetDock(StripSeparator, edge);
        StripSeparator.Height = vertical ? double.NaN : 1;
        StripSeparator.Width = vertical ? 1 : double.NaN;
        TabStripArea.MinHeight = vertical ? 0 : TabStripRowHeight;
        TabStripArea.MinWidth = vertical ? TabStripRowHeight : 0;
        TabScroll.HorizontalScrollBarVisibility = vertical || multiRow
                                                      ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                                                      : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        TabScroll.VerticalScrollBarVisibility = vertical
                                                    ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden
                                                    : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        _tabsOrientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        _multiRowTabs = multiRow;
        if (TabsHost.ItemsPanelRoot is DockTabPanel panel)
        {
            panel.Orientation = _tabsOrientation;
            panel.MultiRow = _multiRowTabs;
        }
        ApplyOverflowControlsLayout(position, edge);
        UpdateNewTabButton();
        // 换了摆法,激活标签可能被挤到了视口外(比如从多排切回单排)。
        _revealActiveTab = true;
    }

    // ---- 「+」新建钮 ----

    /// <summary>「+」的边长:与溢出三连钮同一种 24px 的 <c>.tab-nav</c>。</summary>
    private const double NewTabButtonSize = 24;

    /// <summary>「+」与最后一个标签、与视口末端之间各留的空隙。</summary>
    private const double NewTabButtonGap = 2;

    /// <summary>纵排时「+」的左边距:让 14px 的「+」字形与标签内容(左内边距 14)对齐。</summary>
    private const double NewTabSideInset = 9;

    /// <summary>宿主接没接「+」变了(<see cref="DockWorkspaceControl.NewTabRequested" /> 的订阅增减),重摆一次。</summary>
    internal void RefreshNewTabButton() => UpdateNewTabButton();

    /// <summary>
    /// 摆「+」:跟在最后一个标签后面(横排在它右边、与它同一排垂直居中;纵排在它下面),
    /// 标签放不下而滚动时钉在视口末端。为它让出的那一截由 <c>TabScroll</c> 的外边距留在排列方向的末端,
    /// 横排时单排收窄与多排换行用的都是让过之后的视口宽度,所以「+」不会把标签条挤成溢出。
    /// </summary>
    private void UpdateNewTabButton()
    {
        bool available = Group is not null && WorkspaceControl?.CanRequestNewTab == true;
        bool vertical = IsVerticalStrip;
        double reserve = NewTabButtonSize + (2 * NewTabButtonGap);
        Thickness margin = !available ? default
                         : vertical ? new Thickness(0, 0, 0, reserve)
                         : new Thickness(0, 0, reserve, 0);
        if (TabScroll.Margin != margin)
        {
            TabScroll.Margin = margin;
        }
        NewTabButton.IsVisible = available;
        if (!available)
        {
            return;
        }

        Rect? last = LastTabBounds();
        double x;
        double y;
        if (vertical)
        {
            x = NewTabSideInset;
            y = Math.Min(last?.Bottom ?? 0, TabScroll.Viewport.Height) + NewTabButtonGap;
        }
        else
        {
            x = Math.Min(last?.Right ?? 0, TabScroll.Viewport.Width) + NewTabButtonGap;
            double rowTop = last?.Top ?? 0;
            double rowHeight = last?.Height ?? Math.Max(TabScroll.Bounds.Height, TabStripRowHeight);
            y = rowTop + ((rowHeight - NewTabButtonSize) / 2);
        }
        Canvas.SetLeft(NewTabButton, Math.Round(x));
        Canvas.SetTop(NewTabButton, Math.Round(y));
    }

    /// <summary>最后一个标签在「+」所在图层里的边界(已含滚动偏移);没有标签或还没排版时为 null。</summary>
    private Rect? LastTabBounds()
    {
        if (Group is not { Documents.Count: > 0 } group
            || TabsHost.ContainerFromIndex(group.Documents.Count - 1) is not { Bounds.Width: > 0 } container
            || container.TranslatePoint(default, NewTabLayer) is not { } origin)
        {
            return null;
        }
        return new Rect(origin, container.Bounds.Size);
    }

    /// <summary>「+」:请宿主在这一格里新开会话。停靠层不认识会话,只报是哪一格。</summary>
    private void NewTab_Click(object? sender, RoutedEventArgs e)
    {
        if (Group is { } group)
        {
            WorkspaceControl?.RequestNewTab(group);
        }
    }

    /// <summary>
    /// 溢出三连钮跟着标签排列的方向走:横排停在右端;纵排停在底端、仍排成一行(竖着摞三枚会再吃掉
    /// 一个标签的高度,横着放只要一排)。两枚滚动箭头沿排列轴指向(纵排转成上 / 下);
    /// 下拉箭头指向列表弹出的那一边 —— 纵排时若还朝下,就和「往下滚」长得一模一样。
    /// </summary>
    private void ApplyOverflowControlsLayout(DockTabsPosition position, Dock edge)
    {
        bool vertical = position != DockTabsPosition.Top;
        DockPanel.SetDock(OverflowControls, vertical ? Dock.Bottom : Dock.Right);
        OverflowControls.Margin = vertical ? new Thickness(0, 6) : new Thickness(6, 0);
        OverflowControls.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        OverflowControls.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        ScrollBackIcon.RenderTransform = vertical ? new RotateTransform(90) : null;
        ScrollForwardIcon.RenderTransform = vertical ? new RotateTransform(90) : null;
        TabListIcon.RenderTransform = edge switch
        {
            Dock.Left => new RotateTransform(-90),
            Dock.Right => new RotateTransform(90),
            Dock.Bottom => new RotateTransform(180),
            _ => null
        };
        UpdateScrollButtons();
    }

    /// <summary>「所有标签页」列表往哪边弹:离开窗口边缘、朝着内容区。</summary>
    private PlacementMode TabListPlacement => DockPanel.GetDock(TabStripArea) switch
    {
        Dock.Left => PlacementMode.RightEdgeAlignedBottom,
        Dock.Right => PlacementMode.LeftEdgeAlignedBottom,
        Dock.Bottom => PlacementMode.TopEdgeAlignedRight,
        _ => PlacementMode.BottomEdgeAlignedRight
    };

    private DockTabPanel CreateTabPanel() => new()
    {
        Orientation = _tabsOrientation,
        MultiRow = _multiRowTabs,
        MinRowHeight = TabStripRowHeight
    };

    // ---- 溢出控件(设计 nunbT pZGS4) ----

    /// <summary>沿排列轴的滚动量:(偏移, 视口, 内容) —— 横排取 X / 宽度,纵排取 Y / 高度。</summary>
    private (double Offset, double Viewport, double Extent) TabScrollAxis => IsVerticalStrip
        ? (TabScroll.Offset.Y, TabScroll.Viewport.Height, TabScroll.Extent.Height)
        : (TabScroll.Offset.X, TabScroll.Viewport.Width, TabScroll.Extent.Width);

    private void UpdateScrollButtons()
    {
        (double offset, double viewport, double extent) = TabScrollAxis;
        ScrollBackButton.IsEnabled = offset > 0.5;
        ScrollForwardButton.IsEnabled = offset + viewport < extent - 0.5;
    }

    /// <summary>沿标签排列的那条轴滚 <paramref name="delta" /> 像素(纵排滚 Y、横排滚 X),夹在可滚范围内。</summary>
    private void ScrollTabsBy(double delta)
    {
        (double offset, double viewport, double extent) = TabScrollAxis;
        double target = Math.Clamp(offset + delta, 0, Math.Max(0, extent - viewport));
        TabScroll.Offset = IsVerticalStrip ? TabScroll.Offset.WithY(target) : TabScroll.Offset.WithX(target);
    }

    private void ScrollBack_Click(object? sender, RoutedEventArgs e) => ScrollTabsBy(-ScrollButtonStep);

    private void ScrollForward_Click(object? sender, RoutedEventArgs e) => ScrollTabsBy(ScrollButtonStep);

    /// <summary>空窗格上的"关闭窗格":把这块空面板从布局里撤掉,兄弟随之填满整片区域。</summary>
    private void ClosePane_Click(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace && Group is { } group)
        {
            workspace.ClosePane(group);
        }
    }

    /// <summary>标签列表下拉(设计 nunbT tabListDrop):点击时按当前标签动态生成。</summary>
    private void TabListDrop_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Group is not { Documents.Count: > 0 } group || Workspace is not { } workspace)
        {
            return;
        }
        var flyout = new MenuFlyout { Placement = TabListPlacement };
        foreach (DockDocument document in group.Documents)
        {
            var item = new MenuItem { Header = document.Title };
            if (ReferenceEquals(group.ActiveDocument, document))
            {
                // 激活项以类名标识,着色交给样式表的 DynamicResource。
                item.Classes.Add("active-tab");
            }
            DockDocument captured = document;
            item.Click += (_, _) => workspace.ActivateDocument(captured);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(button);
    }
}
