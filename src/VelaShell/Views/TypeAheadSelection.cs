using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace VelaShell.Views;

/// <summary>
/// 给 <see cref="ListBox" /> 加上资源管理器式的键入定位:直接敲名字开头的几个字母,
/// 就选中并滚到以它开头的那一项;连按同一个字母,在同字母开头的项之间轮转。
/// <para>
/// 匹配规则在 <see cref="TypeAheadSearch" />(可脱离 UI 单测),这里只管接线。
/// </para>
/// <para>
/// 不用 Avalonia 自带的 <c>IsTextSearchEnabled</c>:它总是从第一项找起(连按同一个字母不会轮转)、
/// 不把匹配项滚进视口,焦点也不跟过去 —— 一百个文件里定位到第八十个,选中了却看不见,
/// 接着按方向键还是从原来的位置走。
/// </para>
/// </summary>
internal sealed class TypeAheadSelection
{
    private readonly ListBox _list;
    private readonly Func<object, string?> _nameOf;
    private readonly TypeAheadSearch _search;

    private TypeAheadSelection(ListBox list, Func<object, string?> nameOf, TypeAheadSearch search)
    {
        _list = list;
        _nameOf = nameOf;
        _search = search;
    }

    /// <summary>
    /// 把键入定位接到 <paramref name="list" /> 上。
    /// </summary>
    /// <param name="list">要定位的列表。</param>
    /// <param name="scope">
    /// 在哪里收键入。传整个窗口:焦点停在按钮上、或窗口刚打开谁都没焦点时,敲字也该能定位。
    /// </param>
    /// <param name="nameOf">数据项的名字;返回 null 的项不参与匹配(用来排除合成的 ".." 行)。</param>
    public static void Attach(ListBox list, InputElement scope, Func<object, string?> nameOf)
    {
        var typeAhead = new TypeAheadSelection(list, nameOf, new());

        // 只收没被处理过的键入:路径输入框、下拉框自己的键入搜索都会把事件标成已处理。
        scope.AddHandler(InputElement.TextInputEvent, typeAhead.OnTextInput);
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled
            || !_list.IsEffectivelyVisible
            || !_list.IsEffectivelyEnabled
            || (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
        {
            return;
        }

        if (_search.Append(e.Text) is not { } term)
        {
            return;
        }
        e.Handled = true;

        List<string?> names = [.. _list.Items.Select(item => item is null ? null : _nameOf(item))];
        int index = TypeAheadSearch.FindMatch(names, CurrentIndex(), term);
        if (index >= 0)
        {
            Reveal(index);
        }
    }

    /// <summary>从哪一项起找:焦点在列表里就是焦点所在的项(资源管理器同款),否则退回选中项。</summary>
    private int CurrentIndex()
    {
        if (TopLevel.GetTopLevel(_list)?.FocusManager?.GetFocusedElement() is Visual focused
            && focused.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } container
            && _list.IndexFromContainer(container) is var index and >= 0)
        {
            return index;
        }
        return _list.SelectedIndex;
    }

    /// <summary>
    /// 选中这一项(之前的多选不保留,与资源管理器一致 —— 否则连敲几次就攒出一堆没人要的选中),
    /// 滚进视口,焦点也跟过去:接着按方向键、Shift+方向扩选,都从这一项开始。
    /// </summary>
    private void Reveal(int index)
    {
        _list.SelectedIndex = index;

        // 按下标滚动,不需要容器已经存在:虚拟化面板会先把它实现出来再滚过去。
        _list.ScrollIntoView(index);
        _list.ContainerFromIndex(index)?.Focus();
    }
}
