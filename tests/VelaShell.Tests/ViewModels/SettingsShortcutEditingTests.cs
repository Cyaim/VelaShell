using Avalonia.Input;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.ViewModels;
// VelaShell.Services 里另有一个同名的 KeyModifiers(KeyboardShortcutService 的平台无关枚举)。
using KeyModifiers = Avalonia.Input.KeyModifiers;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 设置 → 快捷键页的改键流程(#551):录键、被拒、撞车先问、解绑、恢复默认,以及保存时写进设置。
/// </summary>
/// <remarks>断言只看键位与绑定 id,不看文案(文案随跑测机器的界面语言变)。</remarks>
[TestClass]
[TestCategory("Settings")]
public sealed class SettingsShortcutEditingTests
{
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly IThemeService _themeService = Substitute.For<IThemeService>();

    private async Task<SettingsViewModel> LoadedVm(Dictionary<string, string>? overrides = null)
    {
        _settingsService.GetSettingsAsync().Returns(new AppSettings
        {
            Shortcuts = new() { Overrides = overrides ?? [] },
        });
        var vm = new SettingsViewModel(_settingsService, _themeService);
        await vm.LoadCommand.Execute().FirstAsync();
        return vm;
    }

    private static ShortcutItem Row(SettingsViewModel vm, string bindingId) =>
        ShortcutCatalog.Flatten(vm.ShortcutGroups).Single(item => item.BindingId == bindingId);

    private static string Combo(ShortcutItem item) => string.Join('+', item.Keys);

    [TestMethod]
    public async Task Load_ShowsSavedShortcutsAsCustomized()
    {
        SettingsViewModel vm = await LoadedVm(new() { ["app.palette"] = "Ctrl+Shift+P", ["session.close"] = "" });

        Assert.AreEqual("Ctrl+Shift+P", Combo(Row(vm, "app.palette")));
        Assert.IsTrue(Row(vm, "app.palette").IsCustomized);
        Assert.IsTrue(Row(vm, "session.close").IsUnbound);
        Assert.IsFalse(Row(vm, "session.clone").IsCustomized);
        Assert.IsTrue(vm.HasCustomShortcuts);
    }

    /// <summary>固定键位只读:没有绑定 id,改键入口不出现。</summary>
    [TestMethod]
    public async Task FixedRows_AreNotEditable()
    {
        SettingsViewModel vm = await LoadedVm();

        ShortcutItem copy = ShortcutCatalog.Flatten(vm.ShortcutGroups).First(item => Combo(item) == "Ctrl+Shift+C");
        Assert.IsFalse(copy.IsEditable);
        vm.BeginShortcutRecording(copy);
        Assert.IsFalse(copy.IsRecording);
    }

    [TestMethod]
    public async Task Recording_AFreeGesture_RebindsAtOnce()
    {
        SettingsViewModel vm = await LoadedVm();
        ShortcutItem palette = Row(vm, "app.palette");

        vm.BeginShortcutRecording(palette);
        vm.ApplyRecordedShortcut(palette, new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift));

        Assert.IsFalse(palette.IsRecording);
        Assert.AreEqual("Ctrl+Shift+P", Combo(palette));
        Assert.IsTrue(palette.IsCustomized);
        Assert.AreEqual("Ctrl+Shift+P", vm.Shortcuts.Overrides["app.palette"]);
        Assert.IsTrue(vm.HasCustomShortcuts);
    }

    /// <summary>不带修饰键 / 撞固定键位:当场说明原因,留在录键状态等下一次按键,什么都不改。</summary>
    [TestMethod]
    public async Task Recording_AnUnusableGesture_StaysOpenWithAReason()
    {
        SettingsViewModel vm = await LoadedVm();
        ShortcutItem palette = Row(vm, "app.palette");
        vm.BeginShortcutRecording(palette);

        vm.ApplyRecordedShortcut(palette, new KeyGesture(Key.K, KeyModifiers.Shift));
        Assert.IsTrue(palette.IsRecording);
        Assert.IsTrue(palette.HasMessage);

        vm.ApplyRecordedShortcut(palette, new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.IsTrue(palette.IsRecording);
        Assert.IsTrue(palette.HasMessage);

        Assert.AreEqual("Ctrl+P", Combo(palette));
        Assert.IsEmpty(vm.Shortcuts.Overrides);
    }

    /// <summary>录到的手势已经绑给别的动作:先问,不动任何东西;确认替换后那一条变为解绑。</summary>
    [TestMethod]
    public async Task Recording_ATakenGesture_AsksThenUnbindsTheOther()
    {
        SettingsViewModel vm = await LoadedVm();
        ShortcutItem palette = Row(vm, "app.palette");
        ShortcutItem close = Row(vm, "session.close");
        vm.BeginShortcutRecording(palette);

        vm.ApplyRecordedShortcut(palette, new KeyGesture(Key.W, KeyModifiers.Control));

        Assert.IsTrue(palette.HasPendingReplace);
        Assert.IsTrue(palette.HasMessage);
        Assert.AreEqual("Ctrl+P", Combo(palette), "确认之前什么都不改。");
        Assert.AreEqual("Ctrl+W", Combo(close));

        vm.ConfirmShortcutReplace(palette);

        Assert.IsFalse(palette.HasPendingReplace);
        Assert.AreEqual("Ctrl+W", Combo(palette));
        Assert.IsTrue(close.IsUnbound);
        Assert.IsTrue(close.IsCustomized, "被抢走的那一条显式记成解绑,恢复默认按钮随之出现。");
    }

    [TestMethod]
    public async Task CancellingAPendingReplace_ChangesNothing()
    {
        SettingsViewModel vm = await LoadedVm();
        ShortcutItem palette = Row(vm, "app.palette");
        vm.BeginShortcutRecording(palette);
        vm.ApplyRecordedShortcut(palette, new KeyGesture(Key.W, KeyModifiers.Control));

        vm.CancelShortcutRecording(palette);

        Assert.IsFalse(palette.HasPendingReplace);
        Assert.IsFalse(palette.HasMessage);
        Assert.IsEmpty(vm.Shortcuts.Overrides);
    }

    /// <summary>同一时间只有一行在录键:开始录另一行时,前一行的录键框与待确认的替换一并收起。</summary>
    [TestMethod]
    public async Task OnlyOneRowRecordsAtATime()
    {
        SettingsViewModel vm = await LoadedVm();
        ShortcutItem palette = Row(vm, "app.palette");
        ShortcutItem sidebar = Row(vm, "view.sidebar");
        vm.BeginShortcutRecording(palette);

        vm.BeginShortcutRecording(sidebar);

        Assert.IsFalse(palette.IsRecording);
        Assert.IsTrue(sidebar.IsRecording);
    }

    [TestMethod]
    public async Task Unbind_ThenReset_RestoresTheDefault()
    {
        SettingsViewModel vm = await LoadedVm();
        ShortcutItem sidebar = Row(vm, "view.sidebar");

        vm.UnbindShortcut(sidebar);
        Assert.IsTrue(sidebar.IsUnbound);
        Assert.IsFalse(sidebar.HasWarning, "解绑了就不再抢 ^B。");
        Assert.AreEqual("", vm.Shortcuts.Overrides["view.sidebar"]);

        vm.ResetShortcut(sidebar);
        Assert.AreEqual("Ctrl+B", Combo(sidebar));
        Assert.IsFalse(sidebar.IsCustomized);
        Assert.IsTrue(sidebar.HasWarning, "出厂的 Ctrl+B 会抢走 tmux 的前缀键 ^B,要提示。");
        Assert.IsEmpty(vm.Shortcuts.Overrides);
    }

    /// <summary>录回出厂键位等于没改:不留记录,改动标记消失。</summary>
    [TestMethod]
    public async Task RecordingTheDefaultAgain_ClearsTheOverride()
    {
        SettingsViewModel vm = await LoadedVm(new() { ["app.palette"] = "Ctrl+Shift+P" });
        ShortcutItem palette = Row(vm, "app.palette");

        vm.BeginShortcutRecording(palette);
        vm.ApplyRecordedShortcut(palette, new KeyGesture(Key.P, KeyModifiers.Control));

        Assert.IsFalse(palette.IsCustomized);
        Assert.IsEmpty(vm.Shortcuts.Overrides);
    }

    /// <summary>出厂键位眼下被别的改动占着时,恢复默认也要先问,而不是悄悄恢复成「未绑定」。</summary>
    [TestMethod]
    public async Task Reset_WhenTheDefaultIsTaken_AsksFirst()
    {
        SettingsViewModel vm = await LoadedVm(new() { ["app.palette"] = "Ctrl+W", ["session.close"] = "" });
        ShortcutItem close = Row(vm, "session.close");

        vm.ResetShortcut(close);

        Assert.IsTrue(close.HasPendingReplace);
        Assert.IsTrue(close.IsUnbound);

        vm.ConfirmShortcutReplace(close);

        Assert.AreEqual("Ctrl+W", Combo(close));
        Assert.IsFalse(close.IsCustomized);
        Assert.IsTrue(Row(vm, "app.palette").IsUnbound);
    }

    [TestMethod]
    public async Task ResetAll_ClearsEveryOverride()
    {
        SettingsViewModel vm = await LoadedVm(new() { ["app.palette"] = "Ctrl+Shift+P", ["session.close"] = "" });

        vm.ResetAllShortcuts();

        Assert.IsEmpty(vm.Shortcuts.Overrides);
        Assert.IsFalse(vm.HasCustomShortcuts);
        Assert.AreEqual("Ctrl+P", Combo(Row(vm, "app.palette")));
        Assert.AreEqual("Ctrl+W", Combo(Row(vm, "session.close")));
    }

    /// <summary>改键只是暂存,保存设置才落盘 —— 落盘的就是这份改动表。</summary>
    [TestMethod]
    public async Task Save_PersistsTheOverrides()
    {
        SettingsViewModel vm = await LoadedVm();
        vm.UnbindShortcut(Row(vm, "session.close"));
        AppSettings? saved = null;
        _ = _settingsService.SaveSettingsAsync(Arg.Do<AppSettings>(settings => saved = settings));

        await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.AreEqual("", saved.Shortcuts.Overrides["session.close"]);
    }

    /// <summary>改键之后搜索词照样命中新键位(搜索文本跟着键帽刷新)。</summary>
    [TestMethod]
    public async Task Search_FindsTheNewGesture()
    {
        SettingsViewModel vm = await LoadedVm();
        ShortcutItem palette = Row(vm, "app.palette");
        vm.BeginShortcutRecording(palette);
        vm.ApplyRecordedShortcut(palette, new KeyGesture(Key.J, KeyModifiers.Control | KeyModifiers.Shift));

        vm.ShortcutFilter = "Ctrl+Shift+J";

        Assert.Contains(palette, vm.FilteredShortcutGroups.SelectMany(group => group.FilteredItems));
    }
}
