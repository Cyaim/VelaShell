using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace VelaShell.Views;

/// <summary>把键盘焦点放进文件列表、留在文件列表里。</summary>
internal static class ListBoxFocus
{
    /// <summary>
    /// 焦点给选中的那一行(先滚到它),没有选中项时给第一行;列表是空的就什么也不做。
    /// </summary>
    /// <remarks>
    /// 焦点只能给行容器:ListBox 本身不可聚焦,<c>list.Focus()</c> 什么也不做。
    /// 代码里 Focus 一行不会选中它,所以给了第一行,选中集照旧是空的。
    /// </remarks>
    /// <param name="list">文件列表。</param>
    public static void FocusSelectedOrFirstRow(this ListBox list)
    {
        int index = Math.Max(list.SelectedIndex, 0);
        if (index < list.ItemCount)
        {
            list.ScrollIntoView(index);
            list.ContainerFromIndex(index)?.Focus();
        }
    }

    /// <summary>
    /// 行整批换掉时(进了别的目录、刷新、删掉了焦点所在的那一行),焦点所在的行跟着被拆掉,键盘焦点就没了着落 ——
    /// 接着敲字定位、按方向键都落不到这个列表上,得再点一下行才行。这里在焦点<b>没了着落</b>时把它还给列表;
    /// 用户自己把焦点移到了别处(点了终端、另一栏、输入框)就不动。
    /// </summary>
    /// <param name="list">文件列表。</param>
    public static void KeepFocusAcrossReloads(this ListBox list) =>
        list.PropertyChanged += (_, e) =>
        {
            if (e.Property != InputElement.IsKeyboardFocusWithinProperty || e.NewValue is not false)
            {
                return;
            }
            // 等新的行实现出来再给;那时再看一眼焦点是不是真的没了着落。
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (TopLevel.GetTopLevel(list) is not { } top
                        || top is Window { IsActive: false }
                        || !list.IsEffectivelyVisible
                        || list.IsKeyboardFocusWithin)
                    {
                        return;
                    }
                    IInputElement? focused = top.FocusManager?.GetFocusedElement();
                    if (focused is null || ReferenceEquals(focused, top))
                    {
                        FocusVisibleRow(list);
                    }
                },
                DispatcherPriority.Loaded);
        };

    /// <summary>
    /// 焦点给眼前的一行,不滚动:选中的行整行都在视口里就给它,否则给视口里最上面那一整行。
    /// 目录在后台自动刷新时也会走到这里,滚回选中项或顶部会把正在往下翻的人拽回去;
    /// 只露出半行的也不能给 —— 获焦会把它整行滚进来(实现出来的行通常比视口多出上下各一行)。
    /// </summary>
    private static void FocusVisibleRow(ListBox list)
    {
        bool FullyVisible(Control row) =>
            row.TranslatePoint(default, list) is { } top
            && top.Y >= -0.5 && top.Y + row.Bounds.Height <= list.Bounds.Height + 0.5;

        Control? row = list.SelectedIndex >= 0 ? list.ContainerFromIndex(list.SelectedIndex) : null;
        if (row is null || !FullyVisible(row))
        {
            row = list.GetRealizedContainers().Where(FullyVisible).MinBy(list.IndexFromContainer);
        }
        row?.Focus();
    }
}
