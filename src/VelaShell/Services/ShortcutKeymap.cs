using System.Windows.Input;
using Avalonia.Input;
using VelaShell.Core.Resources;
using VelaShell.Terminal.Emulation;
// 本命名空间另有一个同名的 KeyModifiers(KeyboardShortcutService 的平台无关枚举),
// 命名空间成员优先于 using,只能换个名字引用 Avalonia 的那个。
using InputModifiers = Avalonia.Input.KeyModifiers;

namespace VelaShell.Services;

/// <summary>可自定义键位的生效范围。</summary>
public enum ShortcutScope
{
    /// <summary>
    /// 窗口级:登记进主窗口的 <c>KeyBindings</c>。焦点在主窗口任何地方都生效,
    /// 而且<b>先于终端控件吃键</b>(Avalonia 在分发路由事件之前就匹配 KeyBindings)。
    /// </summary>
    Window,

    /// <summary>终端标签内:由 <c>TerminalTabView</c> 的隧道处理器匹配,焦点在终端标签里才生效。</summary>
    Terminal,
}

/// <summary>一条可自定义的键位。</summary>
/// <param name="Id">稳定 id,设置里按它存改动 —— <b>一经发布不许改名</b>,改了用户的自定义就丢了。</param>
/// <param name="CommandId">按下后执行的命令(命令注册表的 id;<c>tab.next</c> / <c>tab.prev</c> 不进命令面板,单独接)。</param>
/// <param name="DefaultGesture">出厂键位,Avalonia 手势写法。</param>
/// <param name="LabelKey">动作名的文案键(与快捷键页、命令面板同一套措辞)。</param>
/// <param name="LabelArgument">动作名的格式参数(「跳到第 N 个标签」);没有为 null。</param>
/// <param name="NoteKey">生效条件备注的文案键;无条件生效为 null。</param>
/// <param name="Scope">生效范围。</param>
public sealed record ShortcutBinding(
    string Id,
    string CommandId,
    string DefaultGesture,
    string LabelKey,
    int? LabelArgument = null,
    string? NoteKey = null,
    ShortcutScope Scope = ShortcutScope.Window)
{
    /// <summary>出厂键位(解析一次缓存下来)。</summary>
    public KeyGesture Default { get; } = KeyGesture.Parse(DefaultGesture);

    /// <summary>已本地化的动作名(按当前界面语言现取)。</summary>
    public string Label => LabelArgument is { } argument ? Strings.Format(LabelKey, argument) : Strings.Get(LabelKey);
}

/// <summary>
/// 全部可自定义键位的<b>出厂表</b>:主窗口的 KeyBindings、命令面板右侧的键位提示、
/// 快捷键页与终端内搜索都从这里取数。
/// </summary>
/// <remarks>
/// <para>
/// 这里只收「做一件宿主动作」的键位。终端控件内的剪贴板 / 翻页 / 编码、各对话框与面板自己的
/// Esc / Enter / 方向键、鼠标手势都是固定的 —— 它们要么是终端惯例,要么只在一个小范围里生效,
/// 不会与远端程序抢键。
/// </para>
/// <para>
/// 新增可自定义键位就在这里加一行,再在 <c>ShortcutCatalog</c> 对应分组里用 <c>Bound</c> 引用它;
/// <c>ShortcutKeymapTests</c> 会拦住漏进目录的条目。
/// </para>
/// </remarks>
public static class ShortcutBindings
{
    /// <summary>出厂表,顺序即快捷键页里的顺序,也是冲突时的先后(见 <see cref="ShortcutKeymap" />)。</summary>
    public static IReadOnlyList<ShortcutBinding> All { get; } =
    [
        new("session.new", "session.new", "Ctrl+N", "Cmd_NewSshConnection"),
        new("session.new.tab", "session.new", "Ctrl+T", "Sc_NewTabAlias"),
        new("session.clone", "session.clone", "Ctrl+Shift+N", "Sc_CloneSession"),
        new("app.settings", "app.settings", "Ctrl+OemComma", "Cmd_OpenSettings"),
        new("app.palette", "app.palette", "Ctrl+P", "Cmd_CommandPalette"),
        new("session.close", "session.close", "Ctrl+W", "CloseTab"),
        new("session.close.all", "session.close.all", "Ctrl+Shift+W", "Cmd_CloseAllTabs"),
        new("tab.next", "tab.next", "Ctrl+Tab", "Sc_NextTab"),
        new("tab.prev", "tab.prev", "Ctrl+Shift+Tab", "Sc_PrevTab"),
        // 跳标签用 Ctrl+Alt+数字:Ctrl+数字会吃掉 ^@ ^[ ^\ ^] ^^ ^_ 六个控制字符(Windows Terminal 同样如此)。
        // 欧洲键盘布局上 AltGr 就是 Ctrl+Alt,AltGr+数字是打 { [ ] } 的键 —— 那些用户可以把这几条解绑。
        .. Enumerable.Range(1, 8).Select(slot =>
            new ShortcutBinding($"tab.goto.{slot}", $"tab.goto.{slot}", $"Ctrl+Alt+D{slot}", "Cmd_GotoTab", slot)),
        new("tab.goto.last", "tab.goto.last", "Ctrl+Alt+D9", "Cmd_GotoLastTab"),
        new("split.horizontal", "split.horizontal", "Ctrl+Shift+D", "Dock_SplitHorizontal"),
        new("split.vertical", "split.vertical", "Ctrl+Shift+S", "Dock_SplitVertical"),
        // 未分屏时它只是没事可做,按键并不会送去远端 —— 窗口级键位是无条件吃键的。
        new("pane.maximize", "pane.maximize", "Ctrl+Shift+X", "Dock_ToggleMaximizePane", NoteKey: "Sc_NoteNeedsSplit"),
        new("view.sidebar", "view.sidebar", "Ctrl+B", "Cmd_ToggleSidebar"),
        new("tools.files", "tools.files", "Ctrl+Shift+F", "Sc_ToggleFileBrowser"),
        new("tools.tunnel", "tools.tunnel", "Ctrl+Shift+T", "Cmd_TunnelManager"),
        new("terminal.linegutter", "terminal.linegutter", "Ctrl+Shift+L", "Cmd_ToggleLineGutter"),
        new("search.terminal", "search.terminal", "Ctrl+F", "Sc_SearchTerminal", Scope: ShortcutScope.Terminal),
        new("edit.clear", "edit.clear", "Ctrl+Shift+K", "Cmd_ClearScreen"),
        new("view.zoom.in", "view.zoom.in", "Ctrl+OemPlus", "Cmd_ZoomIn"),
        new("view.zoom.out", "view.zoom.out", "Ctrl+OemMinus", "Cmd_ZoomOut"),
        new("view.zoom.reset", "view.zoom.reset", "Ctrl+D0", "Cmd_ZoomReset"),
    ];

    private static readonly Dictionary<string, ShortcutBinding> ById =
        All.ToDictionary(binding => binding.Id, StringComparer.Ordinal);

    /// <summary>按 id 取绑定;不存在时返回 null。</summary>
    public static ShortcutBinding? Find(string id) => ById.GetValueOrDefault(id);
}

/// <summary>一次改键被拒的原因。</summary>
public enum ShortcutRejection
{
    /// <summary>可以用。</summary>
    None,

    /// <summary>既不带 Ctrl / Alt / Meta,也不是 F1–F24 —— 绑上就打不了这个字了。</summary>
    NeedsModifier,

    /// <summary>与一个固定键位(复制、粘贴、发送 ^C……)相同,绑上会把那一条压掉。</summary>
    Reserved,
}

/// <summary>
/// 一份<b>生效中</b>的键位表:出厂表 + 用户的改动。不可变,改一次键就换一份新的。
/// </summary>
/// <remarks>
/// <para>
/// 改动的语义:绑定 id → 手势;<b>空串 = 解绑</b>;认不出的手势、或者违反
/// <see cref="ShortcutGestures.Check" /> 的手势(只可能是手改配置文件改出来的)按出厂键位处理。
/// 与出厂键位相同的改动等于没改。
/// </para>
/// <para>
/// 同一个手势只归一条绑定:改过的压过出厂的(把 <c>Ctrl+W</c> 改给命令面板,关标签那一条随之解绑);
/// 两条改动撞在一起时按出厂表顺序,先到先得。设置页的改键流程会显式把被抢的那条写成解绑,
/// 所以这条规则只为手改配置兜底。
/// </para>
/// </remarks>
public sealed class ShortcutKeymap
{
    private readonly Dictionary<string, KeyGesture?> _gestures = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _customized = [with(StringComparer.Ordinal)];

    /// <summary>全部出厂键位。</summary>
    public static ShortcutKeymap Default { get; } = new(null);

    /// <summary>按出厂表与改动建一份生效中的键位表。</summary>
    /// <param name="overrides">改动(绑定 id → 手势,空串 = 解绑);null 等于没改动。</param>
    /// <param name="isMacOS">按哪个平台判定固定键位;null = 当前平台。</param>
    public ShortcutKeymap(IReadOnlyDictionary<string, string>? overrides, bool? isMacOS = null)
    {
        IsMacOS = isMacOS ?? OperatingSystem.IsMacOS();
        List<(ShortcutBinding Binding, KeyGesture Gesture)> claimed = [];
        foreach (ShortcutBinding binding in ShortcutBindings.All)
        {
            if (overrides?.GetValueOrDefault(binding.Id) is not { } value)
            {
                continue;
            }
            if (value.Length == 0)
            {
                _gestures[binding.Id] = null;
                _customized.Add(binding.Id);
                continue;
            }
            if (!ShortcutGestures.TryParse(value, out KeyGesture? gesture)
                || ShortcutGestures.Check(gesture, IsMacOS).Rejection != ShortcutRejection.None
                || ShortcutGestures.AreSame(gesture, binding.Default))
            {
                continue;
            }
            _customized.Add(binding.Id);
            bool taken = claimed.Any(other => ShortcutGestures.AreSame(other.Gesture, gesture));
            _gestures[binding.Id] = taken ? null : gesture;
            if (!taken)
            {
                claimed.Add((binding, gesture));
            }
        }
        foreach (ShortcutBinding binding in ShortcutBindings.All.Where(binding => !_customized.Contains(binding.Id)))
        {
            bool taken = claimed.Any(other => ShortcutGestures.AreSame(other.Gesture, binding.Default));
            _gestures[binding.Id] = taken ? null : binding.Default;
        }
    }

    /// <summary>固定键位按 macOS 判定(Cmd+C / Cmd+V 是终端里的复制粘贴)。</summary>
    public bool IsMacOS { get; }

    /// <summary>是否有任何一条改过。</summary>
    public bool HasCustomizations => _customized.Count > 0;

    /// <summary>该绑定当前的手势;解绑(或被别的改动抢走)时为 null。</summary>
    public KeyGesture? GestureFor(string bindingId) => _gestures.GetValueOrDefault(bindingId);

    /// <summary>该绑定是否被用户改过(含解绑)。</summary>
    public bool IsCustomized(string bindingId) => _customized.Contains(bindingId);

    /// <summary>该范围内当前绑着键的全部绑定,按出厂表顺序。</summary>
    public IEnumerable<(ShortcutBinding Binding, KeyGesture Gesture)> Bound(ShortcutScope scope) =>
        ShortcutBindings.All
                        .Where(binding => binding.Scope == scope)
                        .Select(binding => (binding, GestureFor(binding.Id)))
                        .Where(pair => pair.Item2 is not null)
                        .Select(pair => (pair.binding, pair.Item2!));

    /// <summary>这次按键是否就是该绑定当前的手势。</summary>
    public bool Matches(string bindingId, KeyEventArgs e) => GestureFor(bindingId)?.Matches(e) == true;

    /// <summary>当前占着这个手势的绑定;没人占用为 null。</summary>
    public ShortcutBinding? Owner(KeyGesture gesture) =>
        ShortcutBindings.All.FirstOrDefault(binding => GestureFor(binding.Id) is { } bound && ShortcutGestures.AreSame(bound, gesture));

    /// <summary>键帽序列(快捷键页):解绑时为空数组。</summary>
    public string[] Keycaps(string bindingId) =>
        GestureFor(bindingId) is { } gesture ? ShortcutGestures.Keycaps(gesture, IsMacOS) : [];

    /// <summary>
    /// 命令面板右侧的键位提示:命令归键位表管时取当前生效的那一个(同一命令有两个键位取先绑着的那个,
    /// 全部解绑则不显示);不归键位表管的命令用它注册时自带的 <paramref name="fallback" />。
    /// </summary>
    public string? HintFor(string commandId, string? fallback)
    {
        bool managed = false;
        foreach (ShortcutBinding binding in ShortcutBindings.All.Where(binding => binding.CommandId == commandId))
        {
            managed = true;
            if (GestureFor(binding.Id) is { } gesture)
            {
                return ShortcutGestures.Display(gesture, IsMacOS);
            }
        }
        return managed ? null : fallback;
    }

    /// <summary>
    /// 该绑定当前的手势会不会从终端手里抢走一个按键 —— 会的话给一句提示
    /// (同一个控制字符还有别的按法可发时,顺带指出来);不会则为 null。
    /// </summary>
    public string? TerminalWarning(string bindingId)
    {
        if (GestureFor(bindingId) is not { } gesture || ShortcutGestures.TerminalBytesLost(gesture) is not { } bytes)
        {
            return null;
        }
        if (bytes is not [< 0x20 and var control])
        {
            return Strings.Get("Sc_WarnTerminalKey");
        }
        string caret = $"^{(char)(control + 0x40)}";
        // 编码器对 Ctrl+Shift+字母 发同一个控制字符:那一按没人占的话,远端照样收得到。
        if ((gesture.KeyModifiers & InputModifiers.Shift) == 0)
        {
            var shifted = new KeyGesture(gesture.Key, gesture.KeyModifiers | InputModifiers.Shift);
            if (Owner(shifted) is null
                && ShortcutGestures.Check(shifted, IsMacOS).Rejection == ShortcutRejection.None
                && ShortcutGestures.Encode(shifted) is { } same
                && same.SequenceEqual(bytes))
            {
                return Strings.Format("Sc_WarnControlCharAlt", caret, ShortcutGestures.Display(shifted, IsMacOS));
            }
        }
        return Strings.Format("Sc_WarnControlChar", caret);
    }
}

/// <summary>改键检查的结果。</summary>
/// <param name="Rejection">被拒的原因;<see cref="ShortcutRejection.None" /> 表示可以用。</param>
/// <param name="ReservedLabelKey">被哪个固定键位占着(文案键);只在 <see cref="ShortcutRejection.Reserved" /> 时有值。</param>
public readonly record struct ShortcutCheck(ShortcutRejection Rejection, string? ReservedLabelKey = null);

/// <summary>手势的解析、规整、展示与可用性判定。</summary>
public static class ShortcutGestures
{
    /// <summary>
    /// 固定键位里与可自定义键位同一片地盘(主窗口)的那些:绑上会把它们压掉。
    /// 文案键与快捷键页里那一行一致,改键被拒时告诉用户是被谁占着。
    /// </summary>
    /// <remarks>
    /// Shift+Insert、Shift+PageUp 这类只带 Shift 的不必列 —— <see cref="Check" /> 本来就要求带 Ctrl / Alt / Meta。
    /// </remarks>
    private static readonly (string Gesture, string LabelKey)[] Reserved =
    [
        ("Ctrl+Shift+C", "Copy"),
        ("Ctrl+Shift+V", "Cmd_Paste"),
        ("Ctrl+C", "Sc_SendInterrupt"),
        ("Ctrl+Shift+Up", "Sc_JumpPrevPrompt"),
        ("Ctrl+Shift+Down", "Sc_JumpNextPrompt"),
        ("Ctrl+Back", "Sc_DeleteWord"),
        ("Ctrl+R", "Sc_ReconnectAlt"),
        ("Alt+Enter", "Sc_CompletionPopup"),
        ("Alt+Left", "Sc_FocusPane"),
        ("Alt+Right", "Sc_FocusPane"),
        ("Alt+Up", "Sc_FocusPane"),
        ("Alt+Down", "Sc_FocusPane"),
        ("Ctrl+L", "Sc_EditPath"),
        // 文件栏的视图内按键(Views/FilePaneKeys):窗口级键位先于路由事件吃键,占了它们文件栏就刷新 / 重命名不了。
        ("F5", "Sc_RefreshDirectory"),
        ("F2", "Sc_RenameSelected"),
    ];

    /// <summary>macOS 上终端内的复制粘贴(<c>KeyboardShortcutService</c>)。</summary>
    private static readonly (string Gesture, string LabelKey)[] ReservedOnMac =
    [
        ("Meta+C", "Copy"),
        ("Meta+V", "Cmd_Paste"),
    ];

    /// <summary>固定键位清单(测试核对它们都真在快捷键页里)。</summary>
    public static IEnumerable<(KeyGesture Gesture, string LabelKey)> ReservedGestures(bool isMacOS) =>
        (isMacOS ? Reserved.Concat(ReservedOnMac) : Reserved)
            .Select(entry => (KeyGesture.Parse(entry.Gesture), entry.LabelKey));

    /// <summary>解析手势;空串、认不出的写法、或者只有修饰键时返回 false。</summary>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out KeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        try
        {
            gesture = KeyGesture.Parse(text);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            return false;
        }
        if (gesture.Key == Key.None || IsModifierKey(gesture.Key))
        {
            gesture = null;
            return false;
        }
        return true;
    }

    /// <summary>
    /// 存进设置的写法:修饰键按 Ctrl、Shift、Alt、Meta 的固定顺序,键名用 <see cref="Key" /> 的枚举名
    /// (<see cref="KeyGesture.Parse" /> 能原样读回)。
    /// </summary>
    public static string ToStorage(KeyGesture gesture) =>
        string.Join('+', ModifierNames(gesture.KeyModifiers, "Ctrl", "Shift", "Alt", "Meta").Append(StorageKeyName(gesture.Key)));

    /// <summary>键帽序列,如 <c>["Ctrl", "Shift", "P"]</c>。</summary>
    public static string[] Keycaps(KeyGesture gesture, bool isMacOS) =>
        [.. ModifierNames(gesture.KeyModifiers, "Ctrl", "Shift", "Alt", MetaName(isMacOS)), DisplayKeyName(gesture.Key)];

    /// <summary>展示用的一串,如 <c>Ctrl+Shift+P</c>(命令面板右侧、冲突提示)。</summary>
    public static string Display(KeyGesture gesture, bool isMacOS) => string.Join('+', Keycaps(gesture, isMacOS));

    /// <summary>两个手势是否同一按(键与修饰键都相同;Enter / Return 这类别名视为同一个键)。</summary>
    public static bool AreSame(KeyGesture a, KeyGesture b) => a.Key == b.Key && a.KeyModifiers == b.KeyModifiers;

    /// <summary>纯修饰键(录键时等用户按下真正的键)。</summary>
    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    /// <summary>
    /// 这个手势能不能拿来做可自定义键位:必须带 Ctrl / Alt / Meta(单独的按键只能用 F1–F24),
    /// 且不能与固定键位相同。会不会抢走终端的按键是另一回事,见 <see cref="TerminalBytesLost" />,只提示不拦。
    /// </summary>
    public static ShortcutCheck Check(KeyGesture gesture, bool isMacOS)
    {
        bool hasModifier = (gesture.KeyModifiers & (InputModifiers.Control | InputModifiers.Alt | InputModifiers.Meta)) != 0;
        if (!hasModifier && gesture.Key is not (>= Key.F1 and <= Key.F24))
        {
            return new(ShortcutRejection.NeedsModifier);
        }
        foreach ((KeyGesture reserved, string labelKey) in ReservedGestures(isMacOS))
        {
            if (AreSame(reserved, gesture))
            {
                return new(ShortcutRejection.Reserved, labelKey);
            }
        }
        return new(ShortcutRejection.None);
    }

    /// <summary>这一按在终端里会编码成什么字节(默认终端模式);不产生字节为 null。</summary>
    public static byte[]? Encode(KeyGesture gesture) =>
        InputEncoder.Encode(gesture.Key, gesture.KeyModifiers, new(), TerminalType.Xterm);

    /// <summary>
    /// 绑上这个手势会让远端<b>再也收不到</b>的字节;不会丢东西时为 null。
    /// </summary>
    /// <remarks>
    /// 「丢」的判定:这一按在终端里本来会发出字节,而且去掉 Shift 或 Ctrl 后的那一按发的不是同样的字节。
    /// <c>Ctrl+Tab</c> 发的 <c>^I</c> 单按 Tab 也发,<c>Ctrl+Shift+K</c> 发的 <c>^K</c> 去掉 Shift 也发,
    /// 都不算丢;<c>Ctrl+K</c> 的 <c>^K</c> 单按 K 发不出来,算丢。判定直接问终端的编码器,
    /// 与真正发往远端的字节同源。
    /// </remarks>
    public static byte[]? TerminalBytesLost(KeyGesture gesture)
    {
        if (Encode(gesture) is not { Length: > 0 } bytes)
        {
            return null;
        }
        foreach (InputModifiers drop in (ReadOnlySpan<InputModifiers>)[InputModifiers.Shift, InputModifiers.Control])
        {
            if ((gesture.KeyModifiers & drop) != 0
                && Encode(new(gesture.Key, gesture.KeyModifiers & ~drop)) is { } simpler
                && simpler.SequenceEqual(bytes))
            {
                return null;
            }
        }
        return bytes;
    }

    private static IEnumerable<string> ModifierNames(InputModifiers modifiers, string ctrl, string shift, string alt, string meta)
    {
        if ((modifiers & InputModifiers.Control) != 0)
        {
            yield return ctrl;
        }
        if ((modifiers & InputModifiers.Shift) != 0)
        {
            yield return shift;
        }
        if ((modifiers & InputModifiers.Alt) != 0)
        {
            yield return alt;
        }
        if ((modifiers & InputModifiers.Meta) != 0)
        {
            yield return meta;
        }
    }

    private static string MetaName(bool isMacOS) =>
        isMacOS ? "Cmd" : OperatingSystem.IsWindows() ? "Win" : "Super";

    /// <summary>
    /// 存储用的键名。<see cref="Key" /> 里有一批同值的别名(Return / Enter、Prior / PageUp、Oem1 / OemSemicolon……),
    /// <c>ToString()</c> 取到哪个名字没有保证,这里把它们钉死在一个上。
    /// </summary>
    private static string StorageKeyName(Key key) => key switch
    {
        Key.Enter => "Enter",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.CapsLock => "CapsLock",
        Key.OemSemicolon => "OemSemicolon",
        Key.OemQuestion => "OemQuestion",
        Key.OemTilde => "OemTilde",
        Key.OemOpenBrackets => "OemOpenBrackets",
        Key.OemPipe => "OemPipe",
        Key.OemCloseBrackets => "OemCloseBrackets",
        Key.OemQuotes => "OemQuotes",
        Key.OemBackslash => "OemBackslash",
        _ => key.ToString(),
    };

    /// <summary>键帽上的写法:与快捷键页、文档里一贯的写法一致(<c>Ctrl+,</c>、<c>Ctrl+=</c>、<c>Esc</c>)。</summary>
    private static string DisplayKeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => $"Num{key - Key.NumPad0}",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe or Key.OemBackslash => "\\",
        Key.OemTilde => "`",
        Key.Back => "Backspace",
        Key.Escape => "Esc",
        _ => StorageKeyName(key),
    };
}

/// <summary>
/// 生效中的键位表,全应用一份:设置保存后换新,主窗口据此重建 KeyBindings,
/// 终端标签与命令面板每次用时现取。
/// </summary>
public sealed class ShortcutKeymapService
{
    /// <summary>订阅设置保存;没有设置服务(测试、设计器)时一直是出厂键位。</summary>
    public ShortcutKeymapService(Core.Data.ISettingsService? settings = null)
    {
        settings?.SettingsSaved += saved => Update(saved.Shortcuts);
        if (settings?.CurrentSnapshot is { } snapshot)
        {
            Update(snapshot.Shortcuts);
        }
    }

    /// <summary>当前生效的键位表。</summary>
    public ShortcutKeymap Current { get; private set; } = ShortcutKeymap.Default;

    /// <summary>键位表换新之后触发(可能在线程池线程上;界面层自己编组回 UI 线程)。</summary>
    public event Action<ShortcutKeymap>? Changed;

    /// <summary>按设置里的改动换一份键位表(启动载入设置后、保存设置后)。</summary>
    public void Update(Core.Models.ShortcutOptions? options)
    {
        Current = new(options?.Overrides);
        Changed?.Invoke(Current);
    }

    private static ShortcutKeymapService? _resolved;

    /// <summary>
    /// 从 DI 取全局那一份;取不到(测试、设计器)时为 null,调用方按出厂键位处理。
    /// 终端标签每按一个键都要问一次,取到之后就缓存下来(它是单例,进程内不会换)。
    /// </summary>
    public static ShortcutKeymapService? Resolve() =>
        _resolved ??= Avalonia.Application.Current is App { Services: { } services }
            ? services.GetService(typeof(ShortcutKeymapService)) as ShortcutKeymapService
            : null;
}

/// <summary>把键位表落到窗口的 KeyBindings 上。</summary>
public static class ShortcutKeyBindings
{
    /// <summary>
    /// 用键位表里窗口级的绑定整表替换 <paramref name="target" />:每条执行
    /// <paramref name="command" />,参数是该绑定的命令 id。解绑的那几条不登记,按键原样交给终端。
    /// </summary>
    public static void Apply(IList<KeyBinding> target, ShortcutKeymap keymap, ICommand command)
    {
        target.Clear();
        foreach ((ShortcutBinding binding, KeyGesture gesture) in keymap.Bound(ShortcutScope.Window))
        {
            target.Add(new() { Gesture = gesture, Command = command, CommandParameter = binding.CommandId });
        }
    }
}
