using ReactiveUI.Primitives;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 设置 → 输出规则:列表、编辑框(改副本、校验过了才能写回)、作用范围、测试,以及随设置窗口保存。
/// </summary>
[TestClass]
[TestCategory("OutputRules")]
public sealed class OutputRulesViewModelTests
{
    private static readonly string[] Palette =
    [
        "#000000", "#AA0000", "#00AA00", "#AAAA00", "#0000AA", "#AA00AA", "#00AAAA", "#AAAAAA",
        "#555555", "#FF5555", "#55FF55", "#FFFF55", "#5555FF", "#FF55FF", "#55FFFF", "#FFFFFF",
    ];

    private static OutputRulesViewModel Create(ISessionRepository? sessions = null) => new(sessions, () => Palette);

    private static async Task<bool> CanCommit(OutputRulesViewModel vm) => await vm.CommitEditCommand.CanExecute.FirstAsync();

    [TestMethod]
    public async Task Load_WorksOnCopies_TheSourceTableIsUntouchedUntilSaved()
    {
        OutputRule original = new() { Name = "e", Pattern = "ERROR", Foreground = 1 };
        OutputRulesViewModel vm = Create();
        vm.Load([original]);

        vm.BeginEdit(vm.Rules[0]);
        vm.DraftPattern = "FATAL";
        await vm.CommitEditCommand.Execute().FirstAsync();
        vm.Rules[0].Enabled = false;

        Assert.AreEqual("ERROR", original.Pattern, "取消设置窗口时原表不能已经被改掉。");
        Assert.IsTrue(original.Enabled);
        OutputRule saved = vm.ToModels().Single();
        Assert.AreEqual("FATAL", saved.Pattern);
        Assert.IsFalse(saved.Enabled);
        Assert.AreEqual(original.Id, saved.Id, "编辑不换 Id(冷却与上限按它记账)。");
    }

    [TestMethod]
    public async Task NewRule_CannotBeCommitted_UntilItIsValid()
    {
        OutputRulesViewModel vm = Create();
        await vm.AddCommand.Execute().FirstAsync();

        Assert.IsTrue(vm.IsEditing);
        Assert.IsTrue(vm.HasDraftError, "空正则。");
        Assert.IsFalse(await CanCommit(vm));

        vm.DraftPattern = "disk full";
        Assert.IsTrue(vm.HasDraftError, "高亮一项样式都没选。");
        vm.DraftForeground = vm.PaletteChoices.Single(c => c.Index == 9);
        Assert.IsFalse(vm.HasDraftError);
        Assert.IsTrue(await CanCommit(vm));

        await vm.CommitEditCommand.Execute().FirstAsync();
        Assert.IsFalse(vm.IsEditing);
        Assert.HasCount(1, vm.Rules);
        Assert.AreEqual(9, vm.Rules[0].Rule.Foreground);
    }

    [TestMethod]
    public async Task AutoAnswerOnAPasswordPrompt_CannotBeWrittenIntoTheTable()
    {
        // 规则随设置云同步:命中口令提示的应答规则一条都不许进表,哪怕借窗口的「保存」也不行。
        OutputRulesViewModel vm = Create();
        await vm.AddCommand.Execute().FirstAsync();
        vm.DraftActionIndex = 1;
        vm.DraftPattern = "[Pp]assword:";
        vm.DraftResponse = "hunter2";

        Assert.IsTrue(vm.HasDraftError);
        Assert.IsFalse(await CanCommit(vm));
        Assert.IsEmpty(vm.ToModels(), "校验不过的草稿在保存时丢掉,不写进设置。");
    }

    [TestMethod]
    public async Task ValidDraft_IsSavedEvenIfTheUserClicksSaveDirectly()
    {
        OutputRulesViewModel vm = Create();
        await vm.AddCommand.Execute().FirstAsync();
        vm.DraftActionIndex = 1;
        vm.DraftPattern = @"\[Y/n\]";
        vm.DraftResponse = "Y";

        List<OutputRule> saved = vm.ToModels();

        Assert.HasCount(1, saved);
        Assert.AreEqual(OutputRuleActions.Respond, saved[0].Action);
        Assert.AreEqual("Y", saved[0].Response);
    }

    [TestMethod]
    public void MoveAndDelete_ChangeThePriorityOrder()
    {
        OutputRulesViewModel vm = Create();
        vm.Load([
            new() { Name = "a", Pattern = "a", Bold = true },
            new() { Name = "b", Pattern = "b", Bold = true },
            new() { Name = "c", Pattern = "c", Bold = true },
        ]);

        vm.Move(vm.Rules[2], -1);
        vm.Move(vm.Rules[0], -1); // 已在最前,不动
        vm.Delete(vm.Rules[0]);

        CollectionAssert.AreEqual(new[] { "c", "b" }, vm.ToModels().Select(r => r.Name).ToArray());
    }

    [TestMethod]
    public async Task TableIsCapped()
    {
        OutputRulesViewModel vm = Create();
        vm.Load(Enumerable.Range(0, OutputRuleLimits.MaxRules).Select(i => new OutputRule { Pattern = $"r{i}", Bold = true }));

        Assert.IsFalse(vm.CanAdd);
        Assert.IsFalse(await vm.AddCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    public async Task Scope_ListsGroupsAndConnections_AndSwitchingClearsTheTarget()
    {
        ISessionRepository sessions = Substitute.For<ISessionRepository>();
        var group = new ServerGroup { Name = "prod" };
        var profile = new SessionProfile { Name = "db-1", Host = "10.0.0.5" };
        sessions.GetAllGroupsAsync().Returns([group]);
        sessions.GetAllSessionsAsync().Returns([profile]);
        OutputRulesViewModel vm = Create(sessions);
        await vm.RefreshTargetsAsync();
        await vm.AddCommand.Execute().FirstAsync();
        vm.DraftPattern = "x";
        vm.DraftBold = true;

        vm.DraftScopeIndex = 2;
        Assert.IsTrue(vm.DraftNeedsScopeTarget);
        Assert.IsTrue(vm.HasDraftError, "按连接,但还没选是哪一条。");
        vm.DraftScopeTarget = vm.ScopeTargets.Single();
        Assert.AreEqual(profile.Id, vm.Draft!.ScopeId);
        Assert.IsFalse(vm.HasDraftError);

        vm.DraftScopeIndex = 1;
        Assert.IsNull(vm.Draft.ScopeId, "分组的 Id 拿去当连接 Id 没有意义。");
        Assert.AreEqual("prod", vm.ScopeTargets.Single().Name);

        vm.DraftScopeTarget = vm.ScopeTargets.Single();
        await vm.CommitEditCommand.Execute().FirstAsync();
        StringAssert.Contains(vm.Rules[0].ScopeText, "prod");
    }

    [TestMethod]
    public async Task Test_ShowsWhereTheDraftMatches_AndWhatWouldBeSent()
    {
        OutputRulesViewModel vm = Create();
        vm.TestText = "starting\nDo you want to continue? [Y/n] \nERROR: disk full";
        vm.Load([new() { Name = "err", Pattern = "ERROR", Foreground = 9 }]);
        Assert.HasCount(1, vm.TestResults, "编辑框关着:测表里全部启用的规则。");
        Assert.AreEqual("3", vm.TestResults[0].LineNumber);
        Assert.IsTrue(vm.TestResults[0].Segments[0].HasForeground);
        Assert.AreEqual("ERROR", vm.TestResults[0].Segments[0].Text);

        await vm.AddCommand.Execute().FirstAsync();
        vm.DraftActionIndex = 1;
        vm.DraftPattern = @"\[Y/n\]";
        vm.DraftResponse = "Y";

        Assert.HasCount(1, vm.TestResults, "编辑框开着:只测草稿。");
        Assert.AreEqual("2", vm.TestResults[0].LineNumber);
        Assert.IsTrue(vm.TestResults[0].HasRespondNote);
        StringAssert.Contains(vm.TestResults[0].RespondNote, "Y");
    }

    [TestMethod]
    public void PaletteChoices_FollowTheColorScheme_NotLiterals()
    {
        string[] palette = (string[])Palette.Clone();
        var vm = new OutputRulesViewModel(null, () => palette);
        Assert.HasCount(17, vm.PaletteChoices, "「不改」+ ANSI 16 色。");
        Assert.IsNull(vm.PaletteChoices[0].Index);
        Assert.IsFalse(vm.PaletteChoices[0].HasSwatch);

        palette[1] = "#123456";
        vm.RebuildPalette();
        Assert.AreEqual("#ff123456", vm.PaletteChoices[2].Swatch!.ToString()!.ToLowerInvariant());
    }

    [TestMethod]
    public async Task SettingsWindow_SavesTheRulesIntoTerminalBehavior()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        IThemeService theme = Substitute.For<IThemeService>();
        var stored = new AppSettings();
        stored.TerminalBehavior.OutputRules = [new() { Name = "keep", Pattern = "WARN", Foreground = 3 }];
        settings.GetSettingsAsync().Returns(stored);
        AppSettings? saved = null;
        settings.SaveSettingsAsync(Arg.Do<AppSettings>(s => saved = s)).Returns(Task.CompletedTask);
        var vm = new SettingsViewModel(settings, theme);
        await vm.LoadCommand.Execute().FirstAsync();

        Assert.HasCount(1, vm.OutputRules.Rules);
        await vm.OutputRules.AddCommand.Execute().FirstAsync();
        vm.OutputRules.DraftPattern = "ERROR";
        vm.OutputRules.DraftBold = true;
        await vm.OutputRules.CommitEditCommand.Execute().FirstAsync();
        await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        CollectionAssert.AreEqual(new[] { "WARN", "ERROR" }, saved.TerminalBehavior.OutputRules.Select(r => r.Pattern).ToArray());
    }
}
