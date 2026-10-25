namespace VelaShell.Core.Models;

/// <summary>
/// 一条终端输出规则:输出里命中 <see cref="Pattern" /> 时,按 <see cref="Action" /> 高亮那一段,或者自动发送一段固定应答。
/// </summary>
/// <remarks>
/// <para>
/// 高亮与触发器(自动应答)共用这一张表:两者都是「输出命中正则 → 做一件事」,作用范围、启停、大小写的口径一样,
/// 分成两张表只会让用户在两个地方维护同一组正则。存在 <see cref="TerminalBehaviorOptions.OutputRules" />,
/// 于是随「终端」分类导出 / 导入、随应用设置云同步 —— 与快捷命令一样跟着人走。
/// </para>
/// <para>
/// <b>规则里不放口令。</b>应答只用于 yes / 回车 / 固定文本;编辑时凡是会命中口令提示的规则都不许设成自动应答
/// (<c>OutputRuleCompiler.Validate</c>),要输口令的场景用共享密码。这也是规则可以放心随设置同步的前提。
/// </para>
/// <para>
/// 字段一律用字符串或可空数值,不用枚举:文档库的 JSON 不带枚举转换器,新版本加的取值落到旧版本里
/// 只会被当成不认识的值跳过,而不是把整份设置读坏。
/// </para>
/// </remarks>
public sealed class OutputRule
{
    /// <summary>稳定标识(冷却与上限按它记账,改名不重置)。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>显示名称;自动应答时终端上方的提示用它。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>是否启用;停用的规则既不高亮也不应答,但留在表里。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 正则表达式(.NET 语法)。一律以 <see cref="System.Text.RegularExpressions.RegexOptions.NonBacktracking" />
    /// 编译 —— 匹配时间与行长成线性,用户写出 <c>(a+)+$</c> 也拖不垮渲染;代价是不支持反向引用与环视。
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>是否区分大小写(默认不区分)。</summary>
    public bool CaseSensitive { get; set; }

    /// <summary>作用范围:<see cref="OutputRuleScopes" /> 之一。</summary>
    public string Scope { get; set; } = OutputRuleScopes.Global;

    /// <summary>
    /// 作用范围的对象:<see cref="OutputRuleScopes.Group" /> 时是分组 Id,<see cref="OutputRuleScopes.Profile" /> 时是连接配置 Id;
    /// 全局时忽略。
    /// </summary>
    public Guid? ScopeId { get; set; }

    /// <summary>动作:<see cref="OutputRuleActions" /> 之一。</summary>
    public string Action { get; set; } = OutputRuleActions.Highlight;

    /// <summary>
    /// 高亮的前景色:终端调色板下标 0–15(当前配色方案里的 ANSI 16 色);null = 不改前景。
    /// 不收颜色字面量 —— 换主题、换配色方案时高亮跟着换,亮暗两套都读得清。
    /// </summary>
    public int? Foreground { get; set; }

    /// <summary>高亮的背景色:同 <see cref="Foreground" />;null = 不改背景。</summary>
    public int? Background { get; set; }

    /// <summary>高亮时是否加粗。</summary>
    public bool Bold { get; set; }

    /// <summary>
    /// 自动应答发送的固定文本(单行,不含回车;回车由 <see cref="SendEnter" /> 决定)。空串 + 回车 = 只按一下回车。
    /// </summary>
    public string Response { get; set; } = string.Empty;

    /// <summary>应答正文之后是否再按一次回车(默认是)。</summary>
    public bool SendEnter { get; set; } = true;

    /// <summary>
    /// 应答是否也转发给同一同步输入频道里的其它标签。默认否:频道里别的标签停在什么提示上,这里一概不知,
    /// 它们自己的触发器会各自应答 —— 与快捷命令关掉转发是同一个理由。
    /// </summary>
    public bool ForwardToSyncChannel { get; set; }

    /// <summary>同一条规则两次应答之间至少隔多少秒(同一行本来就只应答一次,这一项管的是接连出现的新行)。</summary>
    public int CooldownSeconds { get; set; } = OutputRuleLimits.DefaultCooldownSeconds;

    /// <summary>同一条规则一分钟内最多应答几次;到了上限暂停应答并在终端上方提示,防对端刷屏造成刷发。</summary>
    public int MaxPerMinute { get; set; } = OutputRuleLimits.DefaultMaxPerMinute;

    /// <summary>深拷贝(编辑框改的是副本,取消时原样留着)。</summary>
    public OutputRule Clone() => (OutputRule)MemberwiseClone();
}

/// <summary><see cref="OutputRule.Action" /> 的取值。</summary>
public static class OutputRuleActions
{
    /// <summary>高亮命中的那一段。</summary>
    public const string Highlight = "highlight";

    /// <summary>自动应答:命中时发送 <see cref="OutputRule.Response" />。</summary>
    public const string Respond = "respond";
}

/// <summary><see cref="OutputRule.Scope" /> 的取值。</summary>
public static class OutputRuleScopes
{
    /// <summary>所有终端(含本地终端)。</summary>
    public const string Global = "global";

    /// <summary>某个分组里的连接。</summary>
    public const string Group = "group";

    /// <summary>某一条连接。</summary>
    public const string Profile = "profile";
}

/// <summary>输出规则的取值区间(编辑框的上下限与载入时的钳制同一口径)。</summary>
public static class OutputRuleLimits
{
    /// <summary>规则条数上限:每条都要在每个新行上跑一遍,上限把渲染开销钉住。</summary>
    public const int MaxRules = 64;

    /// <summary>正则长度上限。</summary>
    public const int MaxPatternLength = 512;

    /// <summary>应答文本长度上限(应答是 yes / 菜单序号这类短文本,不是脚本)。</summary>
    public const int MaxResponseLength = 128;

    /// <summary>默认冷却秒数。</summary>
    public const int DefaultCooldownSeconds = 2;

    /// <summary>冷却秒数上限。</summary>
    public const int MaxCooldownSeconds = 3600;

    /// <summary>默认每分钟上限。</summary>
    public const int DefaultMaxPerMinute = 6;

    /// <summary>每分钟上限的上限。</summary>
    public const int MaxPerMinuteCeiling = 30;

    /// <summary>调色板下标上限(含):ANSI 16 色。</summary>
    public const int MaxPaletteIndex = 15;
}
