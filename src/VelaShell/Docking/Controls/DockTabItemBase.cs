using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VelaShell.Core.Resources;
using VelaShell.Docking.Model;

namespace VelaShell.Docking.Controls;

/// <summary>共用的激活、选中、拖拽、关闭、固定、拆分与标签位置行为。</summary>
public abstract class DockTabItemBase : UserControl
{
    /// <summary>
    /// 右键菜单里「多行显示标签页」那一项的名字。五种标签各自在 XAML 里声明这一项,
    /// 打开菜单时由基类按名字找到它、填上当前状态。
    /// </summary>
    /// <remarks>
    /// 勾选状态没法直接绑:菜单的数据上下文是文档,而这个开关属于整个工作区;
    /// 菜单又开在独立的弹出层里,<c>$parent</c> 够不到标签本身。
    /// </remarks>
    internal const string MultiRowTabsMenuItemName = "MultiRowTabsMenuItem";

    /// <summary>
    /// 「标签位置」子菜单里「回到横排」那一项的名字(有这个子菜单的三种标签各自声明)。
    /// 横排停在顶部还是底部由设置「标签栏位置」定,这一项的文字跟着它在「顶部」「底部」之间换。
    /// </summary>
    internal const string HorizontalTabsMenuItemName = "HorizontalTabsMenuItem";

    private DockGroupControl? _owner;
    private ContextMenu? _hookedMenu;

    /// <summary>经数据上下文绑定到本标签的文档。</summary>
    protected DockDocument? Document => DataContext as DockDocument;
    /// <summary>拥有本标签所属组的 workspace。</summary>
    protected DockWorkspace? Workspace => _owner?.Workspace;
    /// <summary>包含本标签的停靠组。</summary>
    protected DockGroup? Group => _owner?.Group;

    /// <summary>定位所属组控件,并订阅激活文档变更。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = this.FindAncestorOfType<DockGroupControl>();
        if (_owner?.Group is { } group)
        {
            group.PropertyChanged += OnGroupPropertyChanged;
        }
        _hookedMenu = ContextMenu;
        _hookedMenu?.Opening += OnContextMenuOpening;
        UpdateSelected();
    }

    /// <summary>取消组属性变更订阅并释放所属引用。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_owner?.Group is { } group)
        {
            group.PropertyChanged -= OnGroupPropertyChanged;
        }
        _hookedMenu?.Opening -= OnContextMenuOpening;
        _hookedMenu = null;
        _owner = null;
    }

    /// <summary>
    /// 菜单打开前填上「多行显示标签页」的勾选状态。标签条停在左 / 右侧时它不起作用,置灰而不是藏起来 ——
    /// 藏起来的话,用户会以为这个功能不见了。
    /// 「标签位置」里回到横排的那一项按「标签栏位置」写成「顶部」或「底部」:设置成底部之后它还叫「顶部」,
    /// 点下去标签条却落到了底部,菜单就是在说谎。
    /// </summary>
    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }
        if (FindMenuItem(menu.Items, MultiRowTabsMenuItemName) is { } multiRow)
        {
            multiRow.IsChecked = Workspace?.MultiRowTabs == true;
            multiRow.IsEnabled = Workspace is not null && Group?.TabsPosition is null or DockTabsPosition.Top;
        }
        if (FindMenuItem(menu.Items, HorizontalTabsMenuItemName) is { } horizontal)
        {
            horizontal.Header = Strings.Get(Workspace?.TabsAtBottom == true ? "Dock_TabsBottom" : "Dock_TabsTop");
        }
    }

    private static MenuItem? FindMenuItem(IEnumerable<object?> items, string name)
    {
        foreach (object? entry in items)
        {
            if (entry is not MenuItem item)
            {
                continue;
            }
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

    /// <summary>左键单击激活文档,按下时发起拖拽。</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Document is not { } document || Workspace is not { } workspace)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed)
        {
            // 中键关标签:浏览器与各家编辑器通用的手势,省掉"先瞄准那枚 11px 的 ×"。
            // 同样走 RequestClose —— 已连接会话的确认闸对哪个入口都得生效。
            // 固定的标签不响应(VS Code 默认同样如此):中键是最容易误触的关闭方式,
            // 而固定它正是为了不被随手关掉;真要关就右键「关闭」或 Ctrl+W。
            e.Handled = true;
            if (!document.IsPinned)
            {
                workspace.RequestClose(document);
            }
            return;
        }
        if (point.Properties.IsLeftButtonPressed)
        {
            workspace.ActivateDocument(document);
            _owner?.WorkspaceControl?.DragController.OnTabPressed(this, e);
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            workspace.ActivateDocument(document);
        }
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DockGroup.ActiveDocument))
        {
            UpdateSelected();
        }
    }

    private void UpdateSelected() =>
        PseudoClasses.Set(":selected", Document is not null && ReferenceEquals(Group?.ActiveDocument, Document));

    /// <summary>关闭当前标签的文档。</summary>
    // 走 RequestClose 而不是 CloseDocument:关闭已连接会话前的确认闸挂在前者上。
    protected void CloseTab_Click(object? sender, RoutedEventArgs e) => Workspace?.RequestClose(Document!);
    /// <summary>关闭组内除当前标签外的所有文档。</summary>
    protected void CloseOthers_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseOtherDocuments(Document!);
    /// <summary>关闭组内的全部文档。</summary>
    protected void CloseAll_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseAllDocuments(Document!);
    /// <summary>关闭当前标签左侧的全部文档。</summary>
    protected void CloseLeft_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseLeftDocuments(Document!);
    /// <summary>关闭当前标签右侧的全部文档。</summary>
    protected void CloseRight_Click(object? sender, RoutedEventArgs e) => Workspace?.CloseRightDocuments(Document!);
    /// <summary>固定当前标签:挪到固定区末尾,批量关闭与中键都绕开它。</summary>
    protected void PinTab_Click(object? sender, RoutedEventArgs e) => Workspace?.SetPinned(Document!, pinned: true);
    /// <summary>取消固定当前标签(右键菜单,或固定标签上替代 × 的那枚图钉)。</summary>
    protected void UnpinTab_Click(object? sender, RoutedEventArgs e) => Workspace?.SetPinned(Document!, pinned: false);

    /// <summary>切换「多行显示标签页」:整个工作区一起变,宿主负责写回设置。</summary>
    protected void ToggleMultiRowTabs_Click(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace)
        {
            workspace.MultiRowTabs = !workspace.MultiRowTabs;
        }
    }
    /// <summary>把本窗格最大化到整片工作区,或从最大化状态还原。</summary>
    protected void ToggleMaximizePane_Click(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } workspace && Group is { } group)
        {
            workspace.ToggleMaximizeGroup(group);
        }
    }

    /// <summary>将文档水平拆分为新组。</summary>
    protected void SplitHorizontal_Click(object? sender, RoutedEventArgs e) => Workspace?.SplitDocument(Document!, DockOrientation.Horizontal);
    /// <summary>将文档垂直拆分为新组。</summary>
    protected void SplitVertical_Click(object? sender, RoutedEventArgs e) => Workspace?.SplitDocument(Document!, DockOrientation.Vertical);
    /// <summary>把标签条放回横排:停在顶部还是底部,看设置「标签栏位置」(<see cref="DockWorkspace.TabsAtBottom" />)。</summary>
    protected void TabsTop_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Top);
    /// <summary>将标签移到组的左侧。</summary>
    protected void TabsLeft_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Left);
    /// <summary>将标签移到组的右侧。</summary>
    protected void TabsRight_Click(object? sender, RoutedEventArgs e) => SetTabsPosition(DockTabsPosition.Right);

    private void SetTabsPosition(DockTabsPosition position)
    {
        if (Group is { } group)
        {
            group.TabsPosition = position;
        }
    }
}
