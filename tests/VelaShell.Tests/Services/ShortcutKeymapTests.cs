using Avalonia.Input;
using VelaShell.Core.Models;
using VelaShell.Services;
using VelaShell.ViewModels;
// VelaShell.Services 里另有一个同名的 KeyModifiers(KeyboardShortcutService 的平台无关枚举)。
using KeyModifiers = Avalonia.Input.KeyModifiers;

namespace VelaShell.Tests.Services;

/// <summary>
/// 可自定义键位(#551):出厂表、改动的语义、改键时的判定,以及命令面板跟着键位表走。
/// </summary>
/// <remarks>
/// 断言一律只看键位与绑定 id,不看文案 —— 文案随跑测机器的界面语言变。
/// 平台相关的判定(固定键位、Meta 键名)显式传 <c>isMacOS</c>,不随跑测平台变。
/// </remarks>
[TestClass]
[TestCategory("Keyboard")]
public sealed class ShortcutKeymapTests
{
    private static ShortcutKeymap Keymap(params (string Id, string Gesture)[] overrides) =>
        new(overrides.ToDictionary(pair => pair.Id, pair => pair.Gesture), isMacOS: false);

    private static KeyGesture G(string gesture) => KeyGesture.Parse(gesture);

    // ———— 出厂表 ————

    /// <summary>id 是设置里存改动的键,重了就分不清改的是哪一条;出厂键位重了,其中一条永远按不到。</summary>
    [TestMethod]
    public void Bindings_HaveUniqueIdsAndUniqueDefaults()
    {
        List<string> repeatedIds = [.. ShortcutBindings.All.GroupBy(binding => binding.Id).Where(ids => ids.Count() > 1).Select(ids => ids.Key)];
        List<string> repeatedGestures = [.. ShortcutBindings.All
            .GroupBy(binding => ShortcutGestures.ToStorage(binding.Default))
            .Where(gestures => gestures.Count() > 1)
            .Select(gestures => gestures.Key)];

        Assert.IsEmpty(repeatedIds, string.Join(", ", repeatedIds));
        Assert.IsEmpty(repeatedGestures, string.Join(", ", repeatedGestures));
    }

    /// <summary>出厂键位自己必须过得了改键时的那道检查,否则用户把它改走之后再也改不回来。</summary>
    [TestMethod]
    public void Defaults_PassTheSameCheckAsRecordedGestures()
    {
        foreach (bool isMacOS in (bool[])[false, true])
        {
            List<string> rejected = [.. ShortcutBindings.All
                .Where(binding => ShortcutGestures.Check(binding.Default, isMacOS).Rejection != ShortcutRejection.None)
                .Select(binding => binding.Id)];

            Assert.IsEmpty(rejected, $"isMacOS={isMacOS}: " + string.Join(", ", rejected));
        }
    }

    /// <summary>
    /// 每条绑定执行的命令都真的存在:主窗口的 KeyBindings 只认命令 id,
    /// 写错一个字母,按键会被吃掉却什么也不做。
    /// </summary>
    [TestMethod]
    public void EveryBinding_ExecutesARealCommand()
    {
        var vm = new MainWindowViewModel();
        string[] outsideTheRegistry = ["tab.next", "tab.prev"];

        List<string> unknown = [.. ShortcutBindings.All
            .Select(binding => binding.CommandId)
            .Distinct()
            .Where(id => !outsideTheRegistry.Contains(id) && vm.Commands.Find(id) is null)];

        Assert.IsEmpty(unknown, "命令注册表里没有这些命令:" + string.Join(", ", unknown));
        Assert.IsTrue(vm.ExecuteShortcut("tab.next"), "标签循环不进命令面板,由 ExecuteShortcut 单独接。");
        Assert.IsTrue(vm.ExecuteShortcut("tab.prev"));
    }

    /// <summary>键帽写法与快捷键页、文档里一贯的写法一致(<c>Ctrl+,</c>、<c>Ctrl+=</c>、数字不带 D)。</summary>
    [TestMethod]
    public void Keycaps_UseTheDocumentedSpelling()
    {
        ShortcutKeymap keymap = ShortcutKeymap.Default;

        Assert.AreSequenceEqual(["Ctrl", ","], keymap.Keycaps("app.settings"));
        Assert.AreSequenceEqual(["Ctrl", "="], keymap.Keycaps("view.zoom.in"));
        Assert.AreSequenceEqual(["Ctrl", "-"], keymap.Keycaps("view.zoom.out"));
        Assert.AreSequenceEqual(["Ctrl", "0"], keymap.Keycaps("view.zoom.reset"));
        Assert.AreSequenceEqual(["Ctrl", "Alt", "3"], keymap.Keycaps("tab.goto.3"));
        Assert.AreSequenceEqual(["Ctrl", "Shift", "Tab"], keymap.Keycaps("tab.prev"));
        Assert.AreSequenceEqual(["Ctrl", "Shift", "Alt", "Cmd", "Esc"], ShortcutGestures.Keycaps(G("Ctrl+Shift+Alt+Meta+Escape"), isMacOS: true));
    }

    /// <summary>
    /// 存进设置的写法要能原样读回 —— 包括 <see cref="Key" /> 里那批同值别名
    /// (Return / Enter、Prior / PageUp、Oem2 / OemQuestion),<c>ToString()</c> 取到哪个名字没有保证。
    /// </summary>
    [TestMethod]
    public void StorageForm_RoundTrips()
    {
        IEnumerable<KeyGesture> gestures = ShortcutBindings.All.Select(binding => binding.Default).Concat(
        [
            new(Key.Enter, KeyModifiers.Alt | KeyModifiers.Control),
            new(Key.PageUp, KeyModifiers.Control),
            new(Key.OemQuestion, KeyModifiers.Control | KeyModifiers.Shift),
            new(Key.OemBackslash, KeyModifiers.Alt),
            new(Key.F12, KeyModifiers.None),
            new(Key.K, KeyModifiers.Meta),
        ]);

        foreach (KeyGesture gesture in gestures)
        {
            string stored = ShortcutGestures.ToStorage(gesture);
            Assert.IsTrue(ShortcutGestures.TryParse(stored, out KeyGesture? back), stored);
            Assert.IsTrue(ShortcutGestures.AreSame(gesture, back), $"{stored} 读回来变成了 {ShortcutGestures.ToStorage(back)}");
        }
    }

    [TestMethod]
    public void TryParse_RejectsJunk()
    {
        foreach (string? junk in (string?[])[null, "", "   ", "Ctrl+NoSuchKey", "Ctrl+", "Ctrl", "Ctrl+Shift"])
        {
            Assert.IsFalse(ShortcutGestures.TryParse(junk, out _), junk ?? "null");
        }
    }

    // ———— 改动的语义 ————

    [TestMethod]
    public void Default_BindsEveryBindingToItsDefault()
    {
        ShortcutKeymap keymap = ShortcutKeymap.Default;

        foreach (ShortcutBinding binding in ShortcutBindings.All)
        {
            Assert.IsTrue(ShortcutGestures.AreSame(binding.Default, keymap.GestureFor(binding.Id)!), binding.Id);
            Assert.IsFalse(keymap.IsCustomized(binding.Id), binding.Id);
        }
        Assert.IsFalse(keymap.HasCustomizations);
        Assert.HasCount(ShortcutBindings.All.Count(binding => binding.Scope == ShortcutScope.Window),
                        keymap.Bound(ShortcutScope.Window));
    }

    [TestMethod]
    public void Rebinding_MovesTheGesture()
    {
        ShortcutKeymap keymap = Keymap(("app.palette", "Ctrl+Shift+P"));

        Assert.IsTrue(ShortcutGestures.AreSame(G("Ctrl+Shift+P"), keymap.GestureFor("app.palette")!));
        Assert.IsTrue(keymap.IsCustomized("app.palette"));
        Assert.IsNull(keymap.Owner(G("Ctrl+P")), "改走之后 Ctrl+P 没人占用,原样交给终端。");
        Assert.AreEqual("app.palette", keymap.Owner(G("Ctrl+Shift+P"))?.Id);
    }

    /// <summary>空串 = 解绑:这一条不登记,按键原样交给终端(#551 要的就是这个)。</summary>
    [TestMethod]
    public void EmptyGesture_Unbinds()
    {
        ShortcutKeymap keymap = Keymap(("session.close", ""));

        Assert.IsNull(keymap.GestureFor("session.close"));
        Assert.IsTrue(keymap.IsCustomized("session.close"));
        Assert.DoesNotContain(pair => pair.Binding.Id == "session.close", keymap.Bound(ShortcutScope.Window));
        Assert.IsNull(keymap.Owner(G("Ctrl+W")));
        Assert.IsEmpty(keymap.Keycaps("session.close"));
    }

    /// <summary>手改配置改出来的坏值(认不出、不带修饰键、撞固定键位)按出厂键位处理,不算改过。</summary>
    [TestMethod]
    public void UnusableOverrides_FallBackToTheDefault()
    {
        foreach (string bad in (string[])["Ctrl+NoSuchKey", "K", "Shift+K", "Ctrl+Shift+C", "Ctrl+C"])
        {
            ShortcutKeymap keymap = Keymap(("app.palette", bad));

            Assert.IsTrue(ShortcutGestures.AreSame(G("Ctrl+P"), keymap.GestureFor("app.palette")!), bad);
            Assert.IsFalse(keymap.IsCustomized("app.palette"), bad);
        }
    }

    [TestMethod]
    public void OverrideEqualToTheDefault_IsNotACustomization()
    {
        ShortcutKeymap keymap = Keymap(("app.palette", "Ctrl+P"));

        Assert.IsFalse(keymap.IsCustomized("app.palette"));
        Assert.IsFalse(keymap.HasCustomizations);
    }

    /// <summary>改过的压过出厂的:把 Ctrl+W 改给命令面板,关标签那一条随之解绑。</summary>
    [TestMethod]
    public void Override_TakesTheGestureFromADefault()
    {
        ShortcutKeymap keymap = Keymap(("app.palette", "Ctrl+W"));

        Assert.AreEqual("app.palette", keymap.Owner(G("Ctrl+W"))?.Id);
        Assert.IsNull(keymap.GestureFor("session.close"));
        Assert.ContainsSingle(pair => ShortcutGestures.AreSame(pair.Gesture, G("Ctrl+W")), keymap.Bound(ShortcutScope.Window));
    }

    /// <summary>两条改动撞在一起(只可能是手改的):按出厂表顺序先到先得,后一条解绑。</summary>
    [TestMethod]
    public void TwoOverridesOnOneGesture_FirstInTableOrderWins()
    {
        ShortcutKeymap keymap = Keymap(("view.zoom.reset", "Ctrl+Shift+J"), ("session.new", "Ctrl+Shift+J"));

        Assert.AreEqual("session.new", keymap.Owner(G("Ctrl+Shift+J"))?.Id);
        Assert.IsNull(keymap.GestureFor("view.zoom.reset"));
    }

    [TestMethod]
    public void Matches_FollowsTheCurrentGesture()
    {
        ShortcutKeymap keymap = Keymap(("search.terminal", "Ctrl+Shift+G"));

        Assert.IsTrue(keymap.Matches("search.terminal", new KeyEventArgs { Key = Key.G, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift }));
        Assert.IsFalse(keymap.Matches("search.terminal", new KeyEventArgs { Key = Key.F, KeyModifiers = KeyModifiers.Control }),
                       "改走之后 Ctrl+F 原样交给远端(vim / less 的下翻页)。");
    }

    // ———— 改键时的判定 ————

    [TestMethod]
    public void Check_RequiresAModifierUnlessFunctionKey()
    {
        Assert.AreEqual(ShortcutRejection.NeedsModifier, ShortcutGestures.Check(G("K"), isMacOS: false).Rejection);
        Assert.AreEqual(ShortcutRejection.NeedsModifier, ShortcutGestures.Check(G("Shift+K"), isMacOS: false).Rejection);
        Assert.AreEqual(ShortcutRejection.NeedsModifier, ShortcutGestures.Check(G("Enter"), isMacOS: false).Rejection);
        Assert.AreEqual(ShortcutRejection.None, ShortcutGestures.Check(G("F5"), isMacOS: false).Rejection);
        Assert.AreEqual(ShortcutRejection.None, ShortcutGestures.Check(G("Shift+F5"), isMacOS: false).Rejection);
        Assert.AreEqual(ShortcutRejection.None, ShortcutGestures.Check(G("Alt+K"), isMacOS: false).Rejection);
        Assert.AreEqual(ShortcutRejection.None, ShortcutGestures.Check(G("Meta+K"), isMacOS: false).Rejection);
    }

    /// <summary>固定键位(复制、粘贴、^C、Alt+方向……)不许被压掉;Cmd+C / Cmd+V 只在 macOS 上是固定的。</summary>
    [TestMethod]
    public void Check_RejectsFixedShortcuts()
    {
        ShortcutCheck copy = ShortcutGestures.Check(G("Ctrl+Shift+C"), isMacOS: false);
        Assert.AreEqual(ShortcutRejection.Reserved, copy.Rejection);
        Assert.AreEqual("Copy", copy.ReservedLabelKey);
        Assert.AreEqual(ShortcutRejection.Reserved, ShortcutGestures.Check(G("Alt+Left"), isMacOS: false).Rejection);
        Assert.AreEqual(ShortcutRejection.Reserved, ShortcutGestures.Check(G("Ctrl+L"), isMacOS: false).Rejection);

        Assert.AreEqual(ShortcutRejection.None, ShortcutGestures.Check(G("Meta+C"), isMacOS: false).Rejection);
        Assert.AreEqual(ShortcutRejection.Reserved, ShortcutGestures.Check(G("Meta+C"), isMacOS: true).Rejection);
    }

    /// <summary>
    /// 「抢走终端的按键」直接问编码器:Ctrl+K 的 ^K 单按 K 发不出来,算丢;Ctrl+Shift+K 发的 ^K 去掉 Shift 也发,
    /// Ctrl+Tab 的 ^I 单按 Tab 也发,都不算丢;F 键本身就发给远端程序(htop、mc),算丢。
    /// </summary>
    [TestMethod]
    public void TerminalBytesLost_AsksTheEncoder()
    {
        Assert.AreSequenceEqual(new byte[] { 0x0B }, ShortcutGestures.TerminalBytesLost(G("Ctrl+K")));
        Assert.AreSequenceEqual(new byte[] { 0x1F }, ShortcutGestures.TerminalBytesLost(G("Ctrl+OemMinus")));
        Assert.IsNotNull(ShortcutGestures.TerminalBytesLost(G("F5")));
        Assert.IsNotNull(ShortcutGestures.TerminalBytesLost(G("Ctrl+Up")), "Ctrl+方向是 CSI 1;5A,不带 Ctrl 的那一按发的不一样。");

        Assert.IsNull(ShortcutGestures.TerminalBytesLost(G("Ctrl+Shift+K")));
        Assert.IsNull(ShortcutGestures.TerminalBytesLost(G("Ctrl+Tab")));
        Assert.IsNull(ShortcutGestures.TerminalBytesLost(G("Ctrl+Shift+Tab")));
        Assert.IsNull(ShortcutGestures.TerminalBytesLost(G("Ctrl+Alt+D1")));
        Assert.IsNull(ShortcutGestures.TerminalBytesLost(G("Ctrl+OemComma")));
    }

    /// <summary>
    /// 终端冲突提示:同一个控制字符还有没人占用的按法(Ctrl+Shift+同一个键)时指出来;
    /// 那一按也被占着,就只说「远端收不到」。
    /// </summary>
    [TestMethod]
    public void TerminalWarning_PointsAtAFreeAlternative()
    {
        ShortcutKeymap keymap = ShortcutKeymap.Default;

        string palette = keymap.TerminalWarning("app.palette")!;
        Assert.Contains("^P", palette);
        Assert.Contains("Ctrl+Shift+P", palette);

        // Ctrl+Shift+W 被「关闭全部标签」占着:没有别的按法可发 ^W。
        string close = keymap.TerminalWarning("session.close")!;
        Assert.Contains("^W", close);
        Assert.IsFalse(close.Contains("Ctrl+Shift+W", StringComparison.Ordinal), close);
        // 把它解绑之后,Ctrl+Shift+W 就能发 ^W 了。
        Assert.Contains("Ctrl+Shift+W", Keymap(("session.close.all", "")).TerminalWarning("session.close")!);

        Assert.IsNull(keymap.TerminalWarning("split.horizontal"));
        Assert.IsNull(Keymap(("session.close", "")).TerminalWarning("session.close"), "解绑了就不抢键。");
    }

    // ———— 命令面板跟着键位表走 ————

    [TestMethod]
    public void HintFor_FollowsTheKeymap()
    {
        Assert.AreEqual("Ctrl+P", ShortcutKeymap.Default.HintFor("app.palette", "旧提示"));
        Assert.AreEqual("Ctrl+Shift+P", Keymap(("app.palette", "Ctrl+Shift+P")).HintFor("app.palette", null));
        Assert.IsNull(Keymap(("app.palette", "")).HintFor("app.palette", "Ctrl+P"), "解绑了就不显示键位。");
        // 同一命令两个键位:Ctrl+N 解绑后显示 Ctrl+T。
        Assert.AreEqual("Ctrl+T", Keymap(("session.new", "")).HintFor("session.new", null));
        // 不归键位表管的命令用它注册时自带的提示。
        Assert.AreEqual("Ctrl+Shift+C", ShortcutKeymap.Default.HintFor("edit.copy", "Ctrl+Shift+C"));
    }

    [TestMethod]
    public void Palette_ShowsTheEffectiveShortcut()
    {
        var keymap = new ShortcutKeymapService();
        keymap.Update(new ShortcutOptions { Overrides = new() { ["app.palette"] = "Ctrl+Shift+P", ["tools.files"] = "" } });
        var vm = new MainWindowViewModel(shortcutKeymap: keymap);

        vm.CommandPalette.Open();
        List<CommandPaletteItem> items = [.. vm.CommandPalette.Groups.SelectMany(group => group.Items)];

        Assert.AreEqual("Ctrl+Shift+P", items.Single(item => item.Id == "app.palette").Hint);
        Assert.IsNull(items.Single(item => item.Id == "tools.files").Hint);
        Assert.AreEqual("Ctrl+Shift+N", items.Single(item => item.Id == "session.clone").Hint);
    }

    [TestMethod]
    public void Service_PublishesEveryUpdate()
    {
        var service = new ShortcutKeymapService();
        List<ShortcutKeymap> published = [];
        service.Changed += published.Add;

        service.Update(new ShortcutOptions { Overrides = new() { ["app.palette"] = "" } });

        Assert.HasCount(1, published);
        Assert.AreSame(service.Current, published[0]);
        Assert.IsNull(service.Current.GestureFor("app.palette"));
    }
}
