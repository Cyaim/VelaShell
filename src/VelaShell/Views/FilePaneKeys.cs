using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VelaShell.Views;

/// <summary>文件栏视图内按键对应的动作。</summary>
internal enum FilePaneKeyAction
{
    /// <summary>不归文件栏管,原样放行。</summary>
    None,

    /// <summary>刷新当前目录(F5)。</summary>
    Refresh,

    /// <summary>重命名选中的那一项(F2)。</summary>
    Rename,

    /// <summary>删除选中的条目,先确认(Delete)。</summary>
    Delete,

    /// <summary>后退到上一个待过的目录(Alt+←)。</summary>
    Back,

    /// <summary>前进(Alt+→)。</summary>
    Forward,
}

/// <summary>
/// 本地栏与远端栏共用的视图内按键,口径照资源管理器:F5 刷新、F2 重命名、Delete 删除(先确认)、
/// Alt+← / Alt+→ 后退 / 前进,与任务管理器的 F5 / Delete 一致。两栏共用这一份判定,免得两边各写一套、慢慢长歪。
/// </summary>
/// <remarks>
/// <para>
/// 这些键只在焦点落在这一栏里时生效,不进全局改键表:全局键位由 <c>Window.KeyBindings</c> 无条件先吃,
/// 登记成全局的 F5 会让终端里的 mc、htop 再也收不到 F5。F2 / F5 因此列进了
/// <c>ShortcutGestures</c> 的固定键位,不许再改给全局动作(同 <c>Ctrl+L</c> 的先例)。
/// </para>
/// <para>
/// 焦点在输入框里(路径框、过滤框)时一律不接:Delete 在那里是删字,F2 / F5 也轮不到文件栏。
/// F2 / Delete 还要焦点在文件列表里 —— 焦点在工具条按钮上按 Delete 删掉一批文件,谁也想不到。
/// </para>
/// <para>
/// Alt+← / Alt+→ 在主窗口上另有一层意思:分屏时在窗格间移焦(<c>MainWindow.OnPaneNavigationKeyDown</c>,走隧道、先于这里)。
/// 焦点在文件栏里时那一层让路(<see cref="OwnsPaneNavigationKey" />),左右两个方向归文件栏的后退 / 前进;
/// Alt+↑ / Alt+↓ 照旧移焦。
/// </para>
/// </remarks>
internal static class FilePaneKeys
{
    /// <summary>这一按在文件栏里是什么动作。</summary>
    /// <param name="e">按键事件(挂在文件栏视图上收到的)。</param>
    /// <param name="list">这一栏的文件列表。</param>
    public static FilePaneKeyAction Classify(KeyEventArgs e, ListBox list)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(list);
        if (e.Handled || IsInTextBox(e.Source))
        {
            return FilePaneKeyAction.None;
        }
        return (e.Key, e.KeyModifiers) switch
        {
            (Key.F5, KeyModifiers.None) => FilePaneKeyAction.Refresh,
            (Key.F2, KeyModifiers.None) when list.IsKeyboardFocusWithin => FilePaneKeyAction.Rename,
            (Key.Delete, KeyModifiers.None) when list.IsKeyboardFocusWithin => FilePaneKeyAction.Delete,
            (Key.Left, KeyModifiers.Alt) => FilePaneKeyAction.Back,
            (Key.Right, KeyModifiers.Alt) => FilePaneKeyAction.Forward,
            _ => FilePaneKeyAction.None,
        };
    }

    /// <summary>
    /// 主窗口的 Alt+方向移焦要不要给文件栏让路:焦点在本地栏或远端栏里(输入框除外)时,Alt+← / Alt+→ 归文件栏。
    /// </summary>
    /// <param name="e">主窗口隧道阶段收到的按键。</param>
    public static bool OwnsPaneNavigationKey(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.KeyModifiers == KeyModifiers.Alt
            && e.Key is Key.Left or Key.Right
            && e.Source is Visual source
            && !IsInTextBox(source)
            && (source.FindAncestorOfType<FileBrowserView>(includeSelf: true) is not null
                || source.FindAncestorOfType<LocalFilePaneView>(includeSelf: true) is not null);
    }

    /// <summary>与按钮一样:先问命令能不能执行(没有历史、上一步还在走时不能),能才执行。</summary>
    public static void Run(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    /// <summary>事件来源是不是在一个输入框里。</summary>
    internal static bool IsInTextBox(object? source) =>
        (source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is not null;
}

/// <summary>两栏过滤框共用的焦点与按键处理。</summary>
internal static class FilePaneFilter
{
    /// <summary>过滤框刚显现(还没参与布局,立刻 Focus 会落空):等到 Loaded 优先级再聚焦并全选。</summary>
    public static void FocusWhenShown(TextBox box) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (box.IsEffectivelyVisible)
                {
                    box.Focus();
                    box.SelectAll();
                }
            },
            DispatcherPriority.Loaded);

    /// <summary>
    /// 过滤框里的 Esc(清空并收起)与回车(过滤留着,焦点交回列表);处理了返回 true。
    /// 焦点交回列表要等行重新实现出来(清空过滤会整批换行),所以推迟到 Loaded。
    /// </summary>
    /// <param name="e">过滤框收到的按键。</param>
    /// <param name="list">这一栏的文件列表。</param>
    /// <param name="close">收起过滤框(连带清空)。</param>
    public static bool HandleKey(KeyEventArgs e, ListBox list, Action close)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Escape or Key.Enter))
        {
            return false;
        }
        if (e.Key == Key.Escape)
        {
            close();
        }
        Dispatcher.UIThread.Post(list.FocusSelectedOrFirstRow, DispatcherPriority.Loaded);
        return true;
    }
}
