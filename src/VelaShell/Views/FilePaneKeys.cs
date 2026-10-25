using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
}

/// <summary>
/// 本地栏与远端栏共用的视图内按键,口径照资源管理器:F5 刷新、F2 重命名、Delete 删除(先确认),
/// 与任务管理器的 F5 / Delete 一致。两栏共用这一份判定,免得两边各写一套、慢慢长歪。
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
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || IsInTextBox(e.Source))
        {
            return FilePaneKeyAction.None;
        }
        return e.Key switch
        {
            Key.F5 => FilePaneKeyAction.Refresh,
            Key.F2 when list.IsKeyboardFocusWithin => FilePaneKeyAction.Rename,
            Key.Delete when list.IsKeyboardFocusWithin => FilePaneKeyAction.Delete,
            _ => FilePaneKeyAction.None,
        };
    }

    /// <summary>事件来源是不是在一个输入框里。</summary>
    internal static bool IsInTextBox(object? source) =>
        (source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is not null;
}
