using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Generators;
using Avalonia.Input;
using Avalonia.Layout;

namespace VelaShell.Views;

/// <summary>
/// 等尺寸格子的虚拟化面板:格子从左到右排、排满一行换下一行,只为视口附近的项实现容器。
/// 资源管理器「中等图标」那种排法;<see cref="ItemWidth" /> 为 NaN 时退化成一格占满整行的列表(「详细信息」)。
/// <para>
/// 为什么不用 <c>WrapPanel</c>:它不虚拟化,一个几千项的目录(System32、node_modules)就是几千套控件一次性建出来。
/// 为什么两种视图共用这一个面板、而不是切换时换 <c>ItemsPanel</c>:换面板时 Avalonia 只把旧面板的容器从可视树上拿掉,
/// 它们仍挂在列表的逻辑子项里,来回切几次就攒下几百个没人用的容器;共用一个面板,切视图只是改两个尺寸、重建一遍容器。
/// </para>
/// <para>
/// 格子尺寸统一,所以位置、可见范围、方向键的去向都直接由下标算出(<see cref="TileGridMath" />),
/// 不需要像 <see cref="VirtualizingStackPanel" /> 那样边量边估。容器的创建、回收与「焦点所在的容器滚出视口时留着不回收」
/// 照 <see cref="VirtualizingStackPanel" /> 的规矩来,ListBox 的选择、键盘导航与 ScrollIntoView 因此照常可用。
/// </para>
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel
{
    /// <summary>格子宽度;NaN = 一格占满整行(列表)。</summary>
    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<VirtualizingTilePanel, double>(nameof(ItemWidth), double.NaN);

    /// <summary>格子高度(列表时即行高)。</summary>
    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<VirtualizingTilePanel, double>(nameof(ItemHeight), 28);

    /// <summary>容器的回收键;数据项自己就是控件时取 <see cref="ItemIsOwnContainer" />。</summary>
    private static readonly AttachedProperty<object?> RecycleKeyProperty =
        AvaloniaProperty.RegisterAttached<VirtualizingTilePanel, Control, object?>("RecycleKey");

    private static readonly object ItemIsOwnContainer = new();

    /// <summary>视口还没量出来(第一次布局)时先实现这么高的一段,够铺满一屏。</summary>
    private const double InitialRealizeHeight = 1000;

    private readonly Dictionary<int, Control> _realized = [];
    private readonly Dictionary<object, Stack<Control>> _recyclePool = [];

    private Rect _viewport;
    private int _columns = 1;
    private double _tileWidth;
    private (int First, int Last) _realizedRange = (-1, -1);
    private bool _isInLayout;

    /// <summary>键盘焦点所在、但已滚出视口的容器:回收了焦点就没了着落,所以单独留着。</summary>
    private Control? _focusedElement;
    private int _focusedIndex = -1;

    /// <summary>ScrollIntoView 为视口外的项临时实现的容器,滚到位后由测量接管。</summary>
    private Control? _scrollToElement;
    private int _scrollToIndex = -1;

    static VirtualizingTilePanel() =>
        AffectsMeasure<VirtualizingTilePanel>(ItemWidthProperty, ItemHeightProperty);

    /// <summary>创建面板。</summary>
    public VirtualizingTilePanel() => EffectiveViewportChanged += OnEffectiveViewportChanged;

    /// <inheritdoc cref="ItemWidthProperty" />
    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    /// <inheritdoc cref="ItemHeightProperty" />
    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    /// <summary>上一次测量得出的每行格数(列表时恒为 1)。</summary>
    public int Columns => _columns;

    private bool IsList => !double.IsFinite(ItemWidth) || ItemWidth <= 0;

    private double TileHeight => ItemHeight > 0 ? ItemHeight : 28;

    /// <summary>
    /// 面板自身坐标(即内容坐标)里的矩形碰到的项,按下标升序。框选用:
    /// 只看几何、不看容器,视口外没有容器的项也算得出来。
    /// </summary>
    internal IReadOnlyList<int> IndicesInRect(Point corner1, Point corner2) =>
        TileGridMath.IndicesInRect(
            corner1.X, corner1.Y, corner2.X, corner2.Y,
            _tileWidth, TileHeight, _columns, Items.Count);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        IReadOnlyList<object?> items = Items;
        double width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : _viewport.Width > 0 ? _viewport.Width : 0;
        _tileWidth = IsList ? width : ItemWidth;
        _columns = IsList ? 1 : TileGridMath.Columns(width, ItemWidth);
        var tileSize = new Size(_tileWidth, TileHeight);

        _isInLayout = true;
        try
        {
            (int first, int last) = VisibleRange(items.Count);
            foreach (int index in _realized.Keys.Where(index => index < first || index > last).ToList())
            {
                Control element = _realized[index];
                _ = _realized.Remove(index);
                RecycleElement(element, index);
            }
            for (int index = first; index >= 0 && index <= last; index++)
            {
                if (!_realized.TryGetValue(index, out Control? element))
                {
                    element = GetOrCreateElement(items, index);
                    _realized[index] = element;
                }
                element.Measure(tileSize);
            }
            _realizedRange = (first, last);
            _focusedElement?.Measure(tileSize);
            _scrollToElement?.Measure(tileSize);
        }
        finally
        {
            _isInLayout = false;
        }

        int rows = TileGridMath.Rows(items.Count, _columns);
        return new(IsList ? width : Math.Max(width, _columns * _tileWidth), rows * TileHeight);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (IsList)
        {
            _tileWidth = finalSize.Width;
        }
        _isInLayout = true;
        try
        {
            foreach ((int index, Control element) in _realized)
            {
                element.Arrange(TileRect(index));
            }
            if (_focusedElement is not null && _focusedIndex >= 0)
            {
                _focusedElement.Arrange(TileRect(_focusedIndex));
            }
            if (_scrollToElement is not null && _scrollToIndex >= 0)
            {
                _scrollToElement.Arrange(TileRect(_scrollToIndex));
            }
        }
        finally
        {
            _isInLayout = false;
        }
        return finalSize;
    }

    /// <inheritdoc />
    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        InvalidateMeasure();
        int start, count;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                (start, count) = (e.NewStartingIndex, e.NewItems.Count);
                Remap(index => index >= start ? index + count : index);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                (start, count) = (e.OldStartingIndex, e.OldItems.Count);
                Remap(index => index < start ? index : index < start + count ? -1 : index - count);
                break;
            case NotifyCollectionChangedAction.Replace when e.OldItems is not null:
                (start, count) = (e.OldStartingIndex, e.OldItems.Count);
                Remap(index => index >= start && index < start + count ? -1 : index);
                break;
            default:
                // Reset 与 Move:整批重来。文件列表换目录、刷新都是 Reset,不值得为 Move 单写一套。
                Remap(_ => -1);
                break;
        }
    }

    /// <inheritdoc />
    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        base.OnItemsControlChanged(oldValue);
        oldValue?.PropertyChanged -= OnItemsControlPropertyChanged;
        ItemsControl?.PropertyChanged += OnItemsControlPropertyChanged;

        _realized.Clear();
        _realizedRange = (-1, -1);
        _focusedElement = null;
        _focusedIndex = -1;
        _scrollToElement = null;
        _scrollToIndex = -1;
    }

    /// <inheritdoc />
    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        int count = Items.Count;
        if (count == 0)
        {
            return null;
        }
        int fromIndex = from is Control control ? IndexFromContainer(control) : -1;
        if (fromIndex < 0 && direction is not NavigationDirection.First and not NavigationDirection.Last)
        {
            return null;
        }
        // 列表里左右键不换行(资源管理器「详细信息」视图同样不动)。
        if (IsList && direction is NavigationDirection.Left or NavigationDirection.Right)
        {
            return from;
        }
        int pageRows = _viewport.Height > 0 ? (int)Math.Floor(_viewport.Height / TileHeight) : 1;
        int target = TileGridMath.Navigate(fromIndex, direction, _columns, count, pageRows, wrap);
        if (target < 0)
        {
            return null;
        }
        return target == fromIndex ? from : ScrollIntoView(target);
    }

    /// <inheritdoc />
    protected override IEnumerable<Control>? GetRealizedContainers() =>
        _realized.OrderBy(pair => pair.Key).Select(pair => pair.Value);

    /// <inheritdoc />
    protected override Control? ContainerFromIndex(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return null;
        }
        if (index == _scrollToIndex)
        {
            return _scrollToElement;
        }
        if (index == _focusedIndex)
        {
            return _focusedElement;
        }
        if (_realized.TryGetValue(index, out Control? element))
        {
            return element;
        }
        return Items[index] is Control own && own.GetValue(RecycleKeyProperty) == ItemIsOwnContainer ? own : null;
    }

    /// <inheritdoc />
    protected override int IndexFromContainer(Control container)
    {
        if (container == _scrollToElement)
        {
            return _scrollToIndex;
        }
        if (container == _focusedElement)
        {
            return _focusedIndex;
        }
        foreach ((int index, Control element) in _realized)
        {
            if (element == container)
            {
                return index;
            }
        }
        return -1;
    }

    /// <inheritdoc />
    protected override Control? ScrollIntoView(int index)
    {
        IReadOnlyList<object?> items = Items;
        if (_isInLayout || index < 0 || index >= items.Count || !IsEffectivelyVisible)
        {
            return null;
        }
        if (_realized.TryGetValue(index, out Control? realized))
        {
            realized.BringIntoView();
            return realized;
        }

        // 项刚加进来时每行格数与面板高度还是旧的,算出的位置不准、外层滚动区也还没长到那么高:先补一次布局。
        if (!new Rect(Bounds.Size).Contains(TileRect(index)))
        {
            UpdateLayout();
        }
        Control element = GetOrCreateElement(items, index);
        element.Measure(new(_tileWidth, TileHeight));
        element.Arrange(TileRect(index));
        _scrollToElement = element;
        _scrollToIndex = index;

        // 滚过去,再跑一次布局:视口一变,测量就把这一项连同它周围的一屏实现出来,并接管这个临时容器。
        element.BringIntoView();
        UpdateLayout();

        if (_scrollToElement == element)
        {
            // 没被接管(面板不在可见的树上、滚动区不肯动):别留一个孤零零的容器在那儿。
            _scrollToElement = null;
            _scrollToIndex = -1;
            RecycleElement(element, index);
            return null;
        }
        return element;
    }

    private Rect TileRect(int index)
    {
        int columns = Math.Max(1, _columns);
        return new((index % columns) * _tileWidth, (index / columns) * TileHeight, _tileWidth, TileHeight);
    }

    /// <summary>该实现哪些项:视口上下各多留一行,滚动时边上的那行不至于先白一下。</summary>
    private (int First, int Last) VisibleRange(int count)
    {
        double top, bottom;
        if (_viewport.Height > 0)
        {
            top = _viewport.Top - TileHeight;
            bottom = _viewport.Bottom + TileHeight;
        }
        else
        {
            top = 0;
            bottom = InitialRealizeHeight;
        }
        return TileGridMath.IndexRange(top, bottom, TileHeight, _columns, count);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport.Intersect(new(Bounds.Size));
        if (VisibleRange(Items.Count) != _realizedRange)
        {
            InvalidateMeasure();
        }
    }

    /// <summary>焦点离开了那个滚出视口的容器:它再没有留着的理由。</summary>
    private void OnItemsControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_focusedElement is not null
            && e.Property == KeyboardNavigation.TabOnceActiveElementProperty
            && e.GetOldValue<IInputElement?>() == _focusedElement)
        {
            Control element = _focusedElement;
            int index = _focusedIndex;
            _focusedElement = null;
            _focusedIndex = -1;
            RecycleElement(element, index);
        }
    }

    /// <summary>项集合变了:已实现的容器按新下标重新登记,<paramref name="map" /> 返回 -1 的(被删、被换掉的)回收。</summary>
    private void Remap(Func<int, int> map)
    {
        List<KeyValuePair<int, Control>> previous = [.. _realized];
        _realized.Clear();
        _realizedRange = (-1, -1);
        foreach ((int index, Control element) in previous)
        {
            int moved = map(index);
            if (moved < 0)
            {
                Discard(element);
                continue;
            }
            _realized[moved] = element;
            if (moved != index)
            {
                ItemContainerGenerator?.ItemContainerIndexChanged(element, index, moved);
            }
        }
        RemapSpecial(ref _focusedElement, ref _focusedIndex, map);
        RemapSpecial(ref _scrollToElement, ref _scrollToIndex, map);
    }

    private void RemapSpecial(ref Control? element, ref int index, Func<int, int> map)
    {
        if (element is null)
        {
            return;
        }
        int moved = map(index);
        if (moved < 0)
        {
            Discard(element);
            element = null;
            index = -1;
            return;
        }
        if (moved != index)
        {
            ItemContainerGenerator?.ItemContainerIndexChanged(element, index, moved);
            index = moved;
        }
    }

    private Control GetOrCreateElement(IReadOnlyList<object?> items, int index)
    {
        if (TakeSpecial(index, ref _focusedIndex, ref _focusedElement) is { } focused)
        {
            return focused;
        }
        if (TakeSpecial(index, ref _scrollToIndex, ref _scrollToElement) is { } scrollTo)
        {
            return scrollTo;
        }

        ItemContainerGenerator generator = ItemContainerGenerator
            ?? throw new InvalidOperationException("The panel is not attached to an ItemsControl.");
        object? item = items[index];
        if (!generator.NeedsContainer(item, index, out object? recycleKey))
        {
            var own = (Control)item!;
            if (!own.IsSet(RecycleKeyProperty))
            {
                generator.PrepareItemContainer(own, own, index);
                AddInternalChild(own);
                own.SetValue(RecycleKeyProperty, ItemIsOwnContainer);
                generator.ItemContainerPrepared(own, item, index);
            }
            own.SetCurrentValue(IsVisibleProperty, true);
            return own;
        }

        if (recycleKey is not null
            && _recyclePool.TryGetValue(recycleKey, out Stack<Control>? pool)
            && pool.TryPop(out Control? recycled))
        {
            recycled.SetCurrentValue(IsVisibleProperty, true);
            generator.PrepareItemContainer(recycled, item, index);
            AddInternalChild(recycled);
            generator.ItemContainerPrepared(recycled, item, index);
            return recycled;
        }

        Control container = generator.CreateContainer(item, index, recycleKey);
        container.SetValue(RecycleKeyProperty, recycleKey);
        generator.PrepareItemContainer(container, item, index);
        AddInternalChild(container);
        generator.ItemContainerPrepared(container, item, index);
        return container;
    }

    private static Control? TakeSpecial(int index, ref int specialIndex, ref Control? specialElement)
    {
        if (specialIndex != index || specialElement is null)
        {
            return null;
        }
        Control element = specialElement;
        specialElement = null;
        specialIndex = -1;
        return element;
    }

    /// <summary>滚出视口的容器:键盘焦点在它上面时留着(否则焦点没了着落),其余回收待用。</summary>
    private void RecycleElement(Control element, int index)
    {
        if (element.GetValue(RecycleKeyProperty) == ItemIsOwnContainer)
        {
            element.SetCurrentValue(IsVisibleProperty, false);
            return;
        }
        if (ItemsControl is { } owner && KeyboardNavigation.GetTabOnceActiveElement(owner) == element)
        {
            if (_focusedElement is { } previous && previous != element)
            {
                Discard(previous);
            }
            _focusedElement = element;
            _focusedIndex = index;
            return;
        }
        Discard(element);
    }

    /// <summary>容器不再对应任何项(滚出视口、项被删):清掉内容放回池子。</summary>
    private void Discard(Control element)
    {
        if (ItemsControl is null)
        {
            return;
        }
        object? recycleKey = element.GetValue(RecycleKeyProperty);
        if (recycleKey == ItemIsOwnContainer)
        {
            RemoveInternalChild(element);
            return;
        }
        ItemContainerGenerator?.ClearItemContainer(element);
        if (recycleKey is not null)
        {
            if (!_recyclePool.TryGetValue(recycleKey, out Stack<Control>? pool))
            {
                pool = new();
                _recyclePool.Add(recycleKey, pool);
            }
            pool.Push(element);
            element.SetCurrentValue(IsVisibleProperty, false);
        }
        RemoveInternalChild(element);
    }
}
