using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using VelaShell.Core.Models;
using VelaShell.Core.OutputRules;

namespace VelaShell.Core.Tests.OutputRules;

/// <summary>
/// 输出规则的校验与编译:正则安全(线性时间引擎)、自动应答不许碰口令提示、按连接 / 分组挑规则、
/// 设置规整与分类。
/// </summary>
[TestClass]
[TestCategory("OutputRules")]
public sealed class OutputRuleCompilerTests
{
    private static OutputRule Highlight(string pattern, int? foreground = 1) =>
        new() { Name = "h", Pattern = pattern, Action = OutputRuleActions.Highlight, Foreground = foreground };

    private static OutputRule Respond(string pattern, string response = "yes", bool enter = true) =>
        new() { Name = "r", Pattern = pattern, Action = OutputRuleActions.Respond, Response = response, SendEnter = enter };

    // ———— 正则安全 ————

    [TestMethod]
    public void CatastrophicPattern_RunsInLinearTime()
    {
        // (a+)+$ 撞上一长串 a 加一个不匹配的结尾:回溯引擎要试 2^n 种拆法,这一行就能卡死渲染线程。
        // 线性时间引擎下它和普通正则一样快。
        OutputRuleValidation result = OutputRuleCompiler.TryCompile("(a+)+$", caseSensitive: true, out Regex? regex);
        Assert.IsTrue(result.IsValid);
        Assert.IsTrue((regex!.Options & RegexOptions.NonBacktracking) != 0);
        string line = new string('a', 20_000) + "!";

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            _ = regex.IsMatch(line);
        }
        watch.Stop();

        Assert.IsLessThan(2000L, watch.ElapsedMilliseconds, $"20 次 × 2 万字符用了 {watch.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public void BacktrackingOnlyConstructs_AreRejected_NotSilentlyRunOnTheBacktracker()
    {
        Assert.AreEqual(OutputRuleError.PatternUnsupported, OutputRuleCompiler.Validate(Highlight(@"(a)\1")).Error);
        Assert.AreEqual(OutputRuleError.PatternUnsupported, OutputRuleCompiler.Validate(Highlight("foo(?=bar)")).Error);
        Assert.AreEqual(OutputRuleError.PatternUnsupported, OutputRuleCompiler.Validate(Highlight("(?<!x)y")).Error);
        Assert.AreEqual(OutputRuleError.PatternUnsupported, OutputRuleCompiler.Validate(Highlight("(?>a+)b")).Error);
    }

    [TestMethod]
    public void BrokenPattern_ReportsInvalid_WithAPosition()
    {
        OutputRuleValidation result = OutputRuleCompiler.Validate(Highlight("error[abc"));

        Assert.AreEqual(OutputRuleError.PatternInvalid, result.Error);
        Assert.IsGreaterThan(0, result.Offset);
        Assert.IsFalse(string.IsNullOrEmpty(result.Describe()));
    }

    [TestMethod]
    public void EmptyAndOverlongPatterns_AreRejected()
    {
        Assert.AreEqual(OutputRuleError.PatternEmpty, OutputRuleCompiler.Validate(Highlight("")).Error);
        Assert.AreEqual(
            OutputRuleError.PatternTooLong,
            OutputRuleCompiler.Validate(Highlight(new string('a', OutputRuleLimits.MaxPatternLength + 1))).Error);
    }

    [TestMethod]
    public void SamePattern_IsCompiledOnce()
    {
        _ = OutputRuleCompiler.TryCompile("compile-once-[0-9]+", false, out Regex? first);
        _ = OutputRuleCompiler.TryCompile("compile-once-[0-9]+", false, out Regex? second);
        _ = OutputRuleCompiler.TryCompile("compile-once-[0-9]+", true, out Regex? caseSensitive);

        Assert.AreSame(first, second);
        Assert.AreNotSame(first, caseSensitive);
    }

    [TestMethod]
    public void EmptyMatches_DoNotCount()
    {
        // y* 能匹配空串:当成「命中」的话,触发器会对每一行输出应答。
        OutputRuleSet set = OutputRuleCompiler.Build([Highlight("y*")], null, null);

        Assert.AreEqual(0, set.Highlights[0].FirstMatch("no letters here").Length);
        Assert.AreEqual(3, set.Highlights[0].FirstMatch("abc yyy").Length);
        List<OutputHighlightSpan> spans = [];
        set.CollectHighlights("no letters here", spans);
        Assert.IsEmpty(spans);
    }

    [TestMethod]
    public void CaseSensitivity_FollowsTheRule()
    {
        OutputRuleSet insensitive = OutputRuleCompiler.Build([Highlight("error")], null, null);
        OutputRule sensitiveRule = Highlight("error");
        sensitiveRule.CaseSensitive = true;
        OutputRuleSet sensitive = OutputRuleCompiler.Build([sensitiveRule], null, null);

        List<OutputHighlightSpan> spans = [];
        insensitive.CollectHighlights("ERROR", spans);
        Assert.HasCount(1, spans);
        spans.Clear();
        sensitive.CollectHighlights("ERROR", spans);
        Assert.IsEmpty(spans);
    }

    // ———— 高亮规则 ————

    [TestMethod]
    public void HighlightWithoutAnyStyle_IsRejected()
    {
        Assert.AreEqual(OutputRuleError.NoStyle, OutputRuleCompiler.Validate(Highlight("x", foreground: null)).Error);
        OutputRule bold = Highlight("x", foreground: null);
        bold.Bold = true;
        Assert.IsTrue(OutputRuleCompiler.Validate(bold).IsValid);
    }

    [TestMethod]
    public void PaletteIndexOutOfRange_MeansUnchanged()
    {
        OutputRule rule = Highlight("x", foreground: 99);
        rule.Background = 4;
        OutputRuleSet set = OutputRuleCompiler.Build([rule], null, null);

        Assert.IsFalse(set.Highlights[0].Style.HasForeground);
        Assert.AreEqual(4, set.Highlights[0].Style.Background);
    }

    [TestMethod]
    public void CollectHighlights_ReportsEveryMatch_InRuleOrder()
    {
        OutputRuleSet set = OutputRuleCompiler.Build([Highlight("disk"), Highlight("full")], null, null);
        List<OutputHighlightSpan> spans = [];

        set.CollectHighlights("disk full, disk", spans);

        CollectionAssert.AreEqual(
            new[] { new OutputHighlightSpan(0, 4, 0), new OutputHighlightSpan(11, 4, 0), new OutputHighlightSpan(5, 4, 1) },
            spans);
    }

    // ———— 自动应答:不许碰口令 ————

    [TestMethod]
    [DataRow("[Pp]assword:")]
    [DataRow("passphrase for key")]
    [DataRow("密码")]
    [DataRow("Verification code")]
    [DataRow("token")]
    public void RespondRule_MentioningASecret_IsRejected(string pattern) =>
        Assert.AreEqual(OutputRuleError.SecretPrompt, OutputRuleCompiler.Validate(Respond(pattern)).Error);

    [TestMethod]
    [DataRow(@":\s*$")]
    [DataRow(".*")]
    [DataRow("for alice")]
    [DataRow("'s")]
    public void RespondRule_ThatWouldMatchAPasswordPrompt_IsRejected_EvenWithoutTheWord(string pattern) =>
        Assert.AreEqual(OutputRuleError.SecretPrompt, OutputRuleCompiler.Validate(Respond(pattern)).Error);

    [TestMethod]
    [DataRow(@"\(yes/no(/\[fingerprint\])?\)\?")]
    [DataRow(@"Do you want to continue\? \[Y/n\]")]
    [DataRow("--More--")]
    public void RespondRule_ForOrdinaryPrompts_IsAccepted(string pattern) =>
        Assert.IsTrue(OutputRuleCompiler.Validate(Respond(pattern)).IsValid, OutputRuleCompiler.Validate(Respond(pattern)).Error.ToString());

    [TestMethod]
    public void HighlightRule_MayMentionPasswords()
    {
        // 高亮不发送任何东西:把日志里的 "password" 标红是正当用法。
        Assert.IsTrue(OutputRuleCompiler.Validate(Highlight("password")).IsValid);
    }

    [TestMethod]
    public void RespondRule_ResponseMustBeShortSingleLineText()
    {
        Assert.AreEqual(OutputRuleError.ResponseNotSingleLine, OutputRuleCompiler.Validate(Respond("continue", "y\ry")).Error);
        Assert.AreEqual(OutputRuleError.ResponseNotSingleLine, OutputRuleCompiler.Validate(Respond("continue", "y\u001b")).Error);
        Assert.AreEqual(
            OutputRuleError.ResponseTooLong,
            OutputRuleCompiler.Validate(Respond("continue", new string('y', OutputRuleLimits.MaxResponseLength + 1))).Error);
        Assert.AreEqual(OutputRuleError.ResponseEmpty, OutputRuleCompiler.Validate(Respond("continue", "", enter: false)).Error);
        Assert.IsTrue(OutputRuleCompiler.Validate(Respond("continue", "", enter: true)).IsValid, "空正文 + 回车 = 只按回车,合法。");
        Assert.IsTrue(OutputRuleCompiler.Validate(Respond("--More--", " ", enter: false)).IsValid, "一个空格(翻页)也是固定文本。");
    }

    [TestMethod]
    public void UnknownValues_FromANewerVersion_AreRejected_NotGuessed()
    {
        OutputRule action = Highlight("x");
        action.Action = "run-script";
        OutputRule scope = Highlight("x");
        scope.Scope = "tag";

        Assert.AreEqual(OutputRuleError.UnknownKind, OutputRuleCompiler.Validate(action).Error);
        Assert.AreEqual(OutputRuleError.UnknownKind, OutputRuleCompiler.Validate(scope).Error);
    }

    // ———— 作用范围 ————

    [TestMethod]
    public void Build_PicksGlobal_PlusThisProfile_PlusThisGroup_InTableOrder()
    {
        Guid profile = Guid.NewGuid();
        Guid group = Guid.NewGuid();
        OutputRule global = Highlight("global");
        OutputRule forProfile = Highlight("profile");
        forProfile.Scope = OutputRuleScopes.Profile;
        forProfile.ScopeId = profile;
        OutputRule otherProfile = Highlight("other-profile");
        otherProfile.Scope = OutputRuleScopes.Profile;
        otherProfile.ScopeId = Guid.NewGuid();
        OutputRule forGroup = Highlight("group");
        forGroup.Scope = OutputRuleScopes.Group;
        forGroup.ScopeId = group;
        OutputRule disabled = Highlight("disabled");
        disabled.Enabled = false;
        OutputRule broken = Highlight("broken[");

        List<OutputRule> rules = [forGroup, otherProfile, global, disabled, broken, forProfile];
        OutputRuleSet set = OutputRuleCompiler.Build(rules, profile, group);
        OutputRuleSet local = OutputRuleCompiler.Build(rules, null, null);

        CollectionAssert.AreEqual(new[] { "group", "global", "profile" }, set.Highlights.Select(r => r.Regex.ToString()).ToArray());
        CollectionAssert.AreEqual(new[] { "global" }, local.Highlights.Select(r => r.Regex.ToString()).ToArray(), "本地终端只吃全局规则。");
    }

    [TestMethod]
    public void ScopedRule_WithoutTarget_IsRejected()
    {
        OutputRule rule = Highlight("x");
        rule.Scope = OutputRuleScopes.Group;

        Assert.AreEqual(OutputRuleError.ScopeTargetMissing, OutputRuleCompiler.Validate(rule).Error);
    }

    [TestMethod]
    public void Build_SplitsHighlightsAndTriggers_AndFingerprintsTheContent()
    {
        List<OutputRule> rules = [Highlight("a"), Respond("continue")];
        OutputRuleSet first = OutputRuleCompiler.Build(rules, null, null);
        OutputRuleSet again = OutputRuleCompiler.Build(rules, null, null);
        rules[1].Response = "y";
        OutputRuleSet changed = OutputRuleCompiler.Build(rules, null, null);

        Assert.HasCount(1, first.Highlights);
        Assert.HasCount(1, first.Triggers);
        Assert.AreEqual(first.Fingerprint, again.Fingerprint);
        Assert.AreNotEqual(first.Fingerprint, changed.Fingerprint);
        Assert.AreSame(OutputRuleSet.Empty, OutputRuleCompiler.Build([], null, null));
    }

    // ———— 设置:规整、分类、序列化 ————

    [TestMethod]
    public void Normalize_ClampsLimits_DropsNulls_AndCapsTheTable()
    {
        var settings = new AppSettings();
        List<OutputRule> rules = [null!];
        rules.AddRange(Enumerable.Range(0, OutputRuleLimits.MaxRules + 5).Select(i => Highlight($"r{i}")));
        rules[1].Foreground = -3;
        rules[1].Background = 16;
        rules[1].CooldownSeconds = 0;
        rules[1].MaxPerMinute = 1000;
        settings.TerminalBehavior.OutputRules = rules;

        settings.Normalize();

        List<OutputRule> normalized = settings.TerminalBehavior.OutputRules;
        Assert.HasCount(OutputRuleLimits.MaxRules, normalized);
        Assert.IsNull(normalized[0].Foreground, "越界的调色板下标当「不改」,不夹成一个用户没选过的颜色。");
        Assert.IsNull(normalized[0].Background);
        Assert.AreEqual(1, normalized[0].CooldownSeconds);
        Assert.AreEqual(OutputRuleLimits.MaxPerMinuteCeiling, normalized[0].MaxPerMinute);
    }

    [TestMethod]
    public void Rules_RideInTheTerminalCategory()
    {
        // 导出 / 导入、审计按「终端」分类走;云同步随应用设置走 —— 不必另登记一类。
        var before = new AppSettings();
        var after = new AppSettings();
        after.TerminalBehavior.OutputRules = [Respond("continue")];

        IReadOnlyList<SettingsCategory> changed = SettingsCategories.Changed(before, after);

        CollectionAssert.AreEqual(new[] { SettingsCategories.Terminal }, changed.Select(c => c.Id).ToArray());
    }

    [TestMethod]
    public void Rules_RoundTripThroughTheSettingsJson()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
        var settings = new AppSettings();
        OutputRule rule = Respond(@"\[Y/n\]", "Y");
        rule.Scope = OutputRuleScopes.Group;
        rule.ScopeId = Guid.NewGuid();
        rule.CooldownSeconds = 5;
        rule.ForwardToSyncChannel = true;
        settings.TerminalBehavior.OutputRules = [rule, Highlight("ERROR")];

        AppSettings copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, options), options)!;
        copy.Normalize();

        Assert.HasCount(2, copy.TerminalBehavior.OutputRules);
        OutputRule back = copy.TerminalBehavior.OutputRules[0];
        Assert.AreEqual(rule.Id, back.Id);
        Assert.AreEqual(@"\[Y/n\]", back.Pattern);
        Assert.AreEqual(OutputRuleActions.Respond, back.Action);
        Assert.AreEqual(rule.ScopeId, back.ScopeId);
        Assert.AreEqual(5, back.CooldownSeconds);
        Assert.IsTrue(back.ForwardToSyncChannel);
        Assert.AreEqual(1, copy.TerminalBehavior.OutputRules[1].Foreground);
    }

    [TestMethod]
    public void OldSettings_WithoutRules_LoadAsAnEmptyTable()
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>("""{"terminalBehavior":{"lineHeight":1.2}}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        settings.Normalize();

        Assert.IsEmpty(settings.TerminalBehavior.OutputRules, "默认没有任何规则 —— 也就没有任何触发器。");
    }
}
