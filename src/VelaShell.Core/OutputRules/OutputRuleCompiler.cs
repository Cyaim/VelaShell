using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Core.OutputRules;

/// <summary>一条规则为什么不能生效。</summary>
public enum OutputRuleError
{
    /// <summary>没有问题。</summary>
    None,

    /// <summary>正则为空。</summary>
    PatternEmpty,

    /// <summary>正则超过 <see cref="OutputRuleLimits.MaxPatternLength" />。</summary>
    PatternTooLong,

    /// <summary>正则写法有误(解析不过)。</summary>
    PatternInvalid,

    /// <summary>用了线性时间引擎不支持的写法(反向引用、环视、原子组、条件分支)。</summary>
    PatternUnsupported,

    /// <summary>不认识的动作或作用范围(更新的版本写的)。</summary>
    UnknownKind,

    /// <summary>按分组 / 按连接,但没选是哪一个。</summary>
    ScopeTargetMissing,

    /// <summary>高亮规则一项样式都没选。</summary>
    NoStyle,

    /// <summary>应答为空,又不按回车 —— 什么都不会发出去。</summary>
    ResponseEmpty,

    /// <summary>应答文本过长。</summary>
    ResponseTooLong,

    /// <summary>应答文本里有换行或控制字符。</summary>
    ResponseNotSingleLine,

    /// <summary>这条规则会命中口令提示:不许用于自动应答。</summary>
    SecretPrompt,
}

/// <summary>校验结果。</summary>
/// <param name="Error">问题种类。</param>
/// <param name="Offset">正则写法有误时 .NET 报的出错位置(大致是第几个字符);报不出位置或别的错误为 0。</param>
public readonly record struct OutputRuleValidation(OutputRuleError Error, int Offset = 0)
{
    /// <summary>没有问题。</summary>
    public bool IsValid => Error == OutputRuleError.None;

    /// <summary>给设置页的一句话(按当前界面语言)。没有问题时为空串。</summary>
    /// <remarks>正则解析失败不转述 .NET 的原话(那是英文的、按实现措辞的),只说位置。</remarks>
    public string Describe() => Error switch
    {
        OutputRuleError.None => string.Empty,
        OutputRuleError.PatternEmpty => Strings.Get("OutRule_ErrPatternEmpty"),
        OutputRuleError.PatternTooLong => Strings.Format("OutRule_ErrPatternTooLong", OutputRuleLimits.MaxPatternLength),
        OutputRuleError.PatternInvalid => Offset > 0
            ? Strings.Format("OutRule_ErrPatternInvalid", Offset)
            : Strings.Get("OutRule_ErrPatternInvalidNoPos"),
        OutputRuleError.PatternUnsupported => Strings.Get("OutRule_ErrPatternUnsupported"),
        OutputRuleError.ScopeTargetMissing => Strings.Get("OutRule_ErrScopeTarget"),
        OutputRuleError.NoStyle => Strings.Get("OutRule_ErrNoStyle"),
        OutputRuleError.ResponseEmpty => Strings.Get("OutRule_ErrResponseEmpty"),
        OutputRuleError.ResponseTooLong => Strings.Format("OutRule_ErrResponseTooLong", OutputRuleLimits.MaxResponseLength),
        OutputRuleError.ResponseNotSingleLine => Strings.Get("OutRule_ErrResponseNotSingleLine"),
        OutputRuleError.SecretPrompt => Strings.Get("OutRule_ErrSecretPrompt"),
        _ => Strings.Get("OutRule_ErrUnknownKind"),
    };
}

/// <summary>用户高亮规则的样式:前景 / 背景只能是调色板下标(-1 = 不改),外加粗体。</summary>
/// <param name="Foreground">前景调色板下标 0–15,-1 = 不改。</param>
/// <param name="Background">背景调色板下标 0–15,-1 = 不改。</param>
/// <param name="Bold">是否加粗。</param>
public readonly record struct OutputHighlightStyle(sbyte Foreground, sbyte Background, bool Bold)
{
    /// <summary>设了前景。</summary>
    public bool HasForeground => Foreground >= 0;

    /// <summary>设了背景。</summary>
    public bool HasBackground => Background >= 0;
}

/// <summary>编译好的一条规则(只读;同一份可以被所有标签共用)。</summary>
public sealed class CompiledOutputRule
{
    internal CompiledOutputRule(OutputRule rule, Regex regex)
    {
        Id = rule.Id;
        Name = string.IsNullOrWhiteSpace(rule.Name) ? rule.Pattern : rule.Name.Trim();
        Regex = regex;
        Style = new(PaletteSlot(rule.Foreground), PaletteSlot(rule.Background), rule.Bold);
        Response = rule.Response;
        SendEnter = rule.SendEnter;
        ForwardToSyncChannel = rule.ForwardToSyncChannel;
        Cooldown = TimeSpan.FromSeconds(Math.Clamp(rule.CooldownSeconds, 1, OutputRuleLimits.MaxCooldownSeconds));
        MaxPerMinute = Math.Clamp(rule.MaxPerMinute, 1, OutputRuleLimits.MaxPerMinuteCeiling);
    }

    /// <summary>调色板下标 → 样式里的槽位;越界(手改的配置)按「不改这一项」算,不夹成一个用户没选过的颜色。</summary>
    internal static sbyte PaletteSlot(int? index) =>
        index is >= 0 and <= OutputRuleLimits.MaxPaletteIndex ? (sbyte)index.Value : (sbyte)-1;

    /// <summary>规则 Id(冷却与上限按它记账)。</summary>
    public Guid Id { get; }

    /// <summary>显示名称(没填名称时是正则本身)。</summary>
    public string Name { get; }

    /// <summary>编译好的正则(线性时间引擎)。</summary>
    public Regex Regex { get; }

    /// <summary>高亮样式(只对高亮规则有意义)。</summary>
    public OutputHighlightStyle Style { get; }

    /// <summary>应答正文。</summary>
    public string Response { get; }

    /// <summary>正文之后是否按回车。</summary>
    public bool SendEnter { get; }

    /// <summary>是否转发给同步输入频道。</summary>
    public bool ForwardToSyncChannel { get; }

    /// <summary>两次应答之间的最小间隔。</summary>
    public TimeSpan Cooldown { get; }

    /// <summary>一分钟内最多应答几次。</summary>
    public int MaxPerMinute { get; }

    /// <summary>
    /// 在 <paramref name="text" /> 里找第一处<b>非空</b>命中。
    /// </summary>
    /// <remarks>
    /// 不用 <see cref="Regex.IsMatch(ReadOnlySpan{char})" />:<c>y*</c> 这类能匹配空串的写法对任何一行都「命中」,
    /// 触发器会对每一行输出应答。超时(线性引擎几乎不可能,但这是渲染与输出路径,不能让异常冒出去)按没命中算。
    /// </remarks>
    /// <param name="text">一行文本。</param>
    /// <returns>命中的那一段;没命中时 Length 为 0。</returns>
    public ValueMatch FirstMatch(ReadOnlySpan<char> text)
    {
        try
        {
            foreach (ValueMatch match in Regex.EnumerateMatches(text))
            {
                if (match.Length > 0)
                {
                    return match;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // 见 remarks。
        }
        return default;
    }
}

/// <summary>一行里一段用户高亮。</summary>
/// <param name="Start">起始字符偏移。</param>
/// <param name="Length">长度(字符数)。</param>
/// <param name="RuleIndex">在 <see cref="OutputRuleSet.Highlights" /> 里的下标(越小优先级越高)。</param>
public readonly record struct OutputHighlightSpan(int Start, int Length, int RuleIndex);

/// <summary>
/// 某一个终端此刻生效的规则:全局规则加上作用于这条连接 / 它所在分组的规则,按表里的顺序。
/// </summary>
/// <remarks>
/// 不可变;设置保存时宿主对每个标签重算一份,内容没变(<see cref="Fingerprint" /> 相同)时终端不清缓存。
/// </remarks>
public sealed class OutputRuleSet
{
    /// <summary>空集(没有任何用户规则)。</summary>
    public static OutputRuleSet Empty { get; } = new([], [], string.Empty);

    internal OutputRuleSet(IReadOnlyList<CompiledOutputRule> highlights, IReadOnlyList<CompiledOutputRule> triggers, string fingerprint)
    {
        Highlights = highlights;
        Triggers = triggers;
        Fingerprint = fingerprint;
    }

    /// <summary>高亮规则,按优先级(表里的顺序)。</summary>
    public IReadOnlyList<CompiledOutputRule> Highlights { get; }

    /// <summary>自动应答规则,按表里的顺序。</summary>
    public IReadOnlyList<CompiledOutputRule> Triggers { get; }

    /// <summary>内容指纹:两份指纹相同 = 行为相同。</summary>
    public string Fingerprint { get; }

    /// <summary>有高亮规则。</summary>
    public bool HasHighlights => Highlights.Count > 0;

    /// <summary>有自动应答规则。</summary>
    public bool HasTriggers => Triggers.Count > 0;

    /// <summary>
    /// 收集一行里全部高亮规则的命中(不去重叠 —— 逐列「先到先得」由调用方做,表里排前面的赢)。
    /// </summary>
    /// <param name="line">一行文本。</param>
    /// <param name="into">追加到这里。</param>
    public void CollectHighlights(ReadOnlySpan<char> line, List<OutputHighlightSpan> into)
    {
        for (int i = 0; i < Highlights.Count; i++)
        {
            try
            {
                foreach (ValueMatch match in Highlights[i].Regex.EnumerateMatches(line))
                {
                    if (match.Length > 0)
                    {
                        into.Add(new(match.Index, match.Length, i));
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // 渲染路径上不许抛:这一条这一行按没命中算。
            }
        }
    }
}

/// <summary>
/// 输出规则的校验与编译。
/// </summary>
/// <remarks>
/// <para>
/// <b>一律线性时间引擎。</b>正则是用户写的、跑在逐行渲染与每批输出上:回溯引擎遇到 <c>(a+)+$</c> 一类写法,
/// 一行几十个字符就能卡上几秒。<see cref="RegexOptions.NonBacktracking" /> 保证匹配时间与行长成线性,
/// 代价是不支持反向引用、环视、原子组 —— 用了这些的规则编辑时直接报出来,而不是退回回溯引擎赌超时:
/// 超时在渲染路径上同样是卡顿,只是有上限。另挂一个匹配超时兜底(线性引擎几乎碰不到)。
/// </para>
/// <para>
/// <b>编译一次。</b>同一个(正则, 大小写)只编一份,所有标签共用 —— 每次保存设置都会对每个标签重算规则集,
/// 不缓存就是「标签数 × 规则数」次编译。
/// </para>
/// </remarks>
public static class OutputRuleCompiler
{
    /// <summary>匹配超时兜底。</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private const int CacheCapacity = 256;

    private static readonly Lock CacheGate = new();
    private static readonly Dictionary<(string Pattern, bool CaseSensitive), Regex> Cache = [];

    /// <summary>
    /// 校验一条规则(不看 <see cref="OutputRule.Enabled" />:停用的规则也要能看出写错了没有)。
    /// </summary>
    /// <param name="rule">规则。</param>
    /// <returns>校验结果。</returns>
    public static OutputRuleValidation Validate(OutputRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Action is not (OutputRuleActions.Highlight or OutputRuleActions.Respond)
            || rule.Scope is not (OutputRuleScopes.Global or OutputRuleScopes.Group or OutputRuleScopes.Profile))
        {
            return new(OutputRuleError.UnknownKind);
        }
        if (rule.Scope != OutputRuleScopes.Global && rule.ScopeId is null)
        {
            return new(OutputRuleError.ScopeTargetMissing);
        }
        OutputRuleValidation compiled = TryCompile(rule.Pattern, rule.CaseSensitive, out Regex? regex);
        if (!compiled.IsValid)
        {
            return compiled;
        }
        if (rule.Action == OutputRuleActions.Highlight)
        {
            return CompiledOutputRule.PaletteSlot(rule.Foreground) < 0
                && CompiledOutputRule.PaletteSlot(rule.Background) < 0
                && !rule.Bold
                ? new(OutputRuleError.NoStyle)
                : default;
        }

        string response = rule.Response ?? string.Empty;
        if (response.Length > OutputRuleLimits.MaxResponseLength)
        {
            return new(OutputRuleError.ResponseTooLong);
        }
        if (response.Any(char.IsControl))
        {
            return new(OutputRuleError.ResponseNotSingleLine);
        }
        if (response.Length == 0 && !rule.SendEnter)
        {
            return new(OutputRuleError.ResponseEmpty);
        }
        return OutputRuleSecretGuard.LooksLikeSecretPrompt(rule.Pattern, regex!) ? new(OutputRuleError.SecretPrompt) : default;
    }

    /// <summary>编译一个正则(线性时间引擎,带缓存)。</summary>
    /// <param name="pattern">正则。</param>
    /// <param name="caseSensitive">是否区分大小写。</param>
    /// <param name="regex">编译结果;失败时为 null。</param>
    /// <returns>校验结果。</returns>
    public static OutputRuleValidation TryCompile(string? pattern, bool caseSensitive, out Regex? regex)
    {
        regex = null;
        if (string.IsNullOrEmpty(pattern))
        {
            return new(OutputRuleError.PatternEmpty);
        }
        if (pattern.Length > OutputRuleLimits.MaxPatternLength)
        {
            return new(OutputRuleError.PatternTooLong);
        }
        lock (CacheGate)
        {
            if (Cache.TryGetValue((pattern, caseSensitive), out regex))
            {
                return default;
            }
        }
        RegexOptions options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        if (!caseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }
        try
        {
            regex = new(pattern, options, MatchTimeout);
        }
        catch (RegexParseException ex)
        {
            return new(OutputRuleError.PatternInvalid, ex.Offset);
        }
        catch (NotSupportedException)
        {
            return new(OutputRuleError.PatternUnsupported);
        }
        catch (ArgumentException)
        {
            return new(OutputRuleError.PatternInvalid);
        }
        lock (CacheGate)
        {
            if (Cache.Count >= CacheCapacity)
            {
                Cache.Clear();
            }
            Cache[(pattern, caseSensitive)] = regex;
        }
        return default;
    }

    /// <summary>
    /// 按一个终端的身份挑出生效的规则并编译:全局规则,加上作用于这条连接或它所在分组的。
    /// 停用的、校验不过的跳过。
    /// </summary>
    /// <param name="rules">设置里的整张表。</param>
    /// <param name="profileId">这个终端的连接配置 Id;本地终端为 null(只吃全局规则)。</param>
    /// <param name="groupId">连接所在分组;未分组为 null。</param>
    /// <returns>规则集;一条都没有时是 <see cref="OutputRuleSet.Empty" />。</returns>
    public static OutputRuleSet Build(IReadOnlyList<OutputRule>? rules, Guid? profileId, Guid? groupId)
    {
        if (rules is not { Count: > 0 })
        {
            return OutputRuleSet.Empty;
        }
        List<CompiledOutputRule> highlights = [];
        List<CompiledOutputRule> triggers = [];
        var fingerprint = new StringBuilder();
        foreach (OutputRule rule in rules.Take(OutputRuleLimits.MaxRules))
        {
            if (rule is not { Enabled: true } || !AppliesTo(rule, profileId, groupId) || !Validate(rule).IsValid)
            {
                continue;
            }
            _ = TryCompile(rule.Pattern, rule.CaseSensitive, out Regex? regex);
            var compiled = new CompiledOutputRule(rule, regex!);
            (rule.Action == OutputRuleActions.Respond ? triggers : highlights).Add(compiled);
            AppendFingerprint(fingerprint, rule);
        }
        return highlights.Count == 0 && triggers.Count == 0
            ? OutputRuleSet.Empty
            : new(highlights, triggers, fingerprint.ToString());
    }

    /// <summary>规则是否作用于这个终端。</summary>
    /// <param name="rule">规则。</param>
    /// <param name="profileId">连接配置 Id;本地终端为 null。</param>
    /// <param name="groupId">所在分组。</param>
    /// <returns>作用于它时为 true。</returns>
    public static bool AppliesTo(OutputRule rule, Guid? profileId, Guid? groupId) => rule.Scope switch
    {
        OutputRuleScopes.Global => true,
        OutputRuleScopes.Profile => profileId is not null && rule.ScopeId == profileId,
        OutputRuleScopes.Group => groupId is not null && rule.ScopeId == groupId,
        _ => false,
    };

    private static void AppendFingerprint(StringBuilder into, OutputRule rule)
    {
        // 名称也算:提示里显示的是它,改了名要让终端拿到新名字。
        into.Append(rule.Id.ToString("N"))
            .Append('\u001f').Append(rule.Name)
            .Append('\u001f').Append(rule.Pattern)
            .Append('\u001f').Append(rule.CaseSensitive ? '1' : '0')
            .Append('\u001f').Append(rule.Action)
            .Append('\u001f').Append(rule.Foreground?.ToString(CultureInfo.InvariantCulture))
            .Append('\u001f').Append(rule.Background?.ToString(CultureInfo.InvariantCulture))
            .Append('\u001f').Append(rule.Bold ? '1' : '0')
            .Append('\u001f').Append(rule.Response)
            .Append('\u001f').Append(rule.SendEnter ? '1' : '0')
            .Append('\u001f').Append(rule.ForwardToSyncChannel ? '1' : '0')
            .Append('\u001f').Append(rule.CooldownSeconds.ToString(CultureInfo.InvariantCulture))
            .Append('\u001f').Append(rule.MaxPerMinute.ToString(CultureInfo.InvariantCulture))
            .Append('\u001e');
    }
}

/// <summary>终端报给宿主的一次自动应答。</summary>
/// <param name="RuleId">规则 Id。</param>
/// <param name="RuleName">规则名称(提示里显示)。</param>
/// <param name="Response">要发送的正文。</param>
/// <param name="SendEnter">正文之后按回车。</param>
/// <param name="ForwardToSyncChannel">转发给同步输入频道。</param>
/// <param name="RateLimited">
/// true = 这一次命中被每分钟上限挡下了,不要发送,只提示一句「暂停应答」(每个窗口期只报一次)。
/// </param>
public sealed record OutputTriggerHit(
    Guid RuleId,
    string RuleName,
    string Response,
    bool SendEnter,
    bool ForwardToSyncChannel,
    bool RateLimited = false);
