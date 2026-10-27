using System.Collections.ObjectModel;
using Avalonia.Media;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.OutputRules;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>颜色下拉的一项:不改,或终端调色板里的一个下标(色块取当前配色方案,不写死颜色)。</summary>
/// <param name="Index">调色板下标 0–15;null = 不改。</param>
/// <param name="Name">显示名称。</param>
/// <param name="Swatch">色块画刷;「不改」为 null。</param>
public sealed record OutputRulePaletteChoice(int? Index, string Name, IBrush? Swatch)
{
    /// <summary>有色块(「不改」那一项没有)。</summary>
    public bool HasSwatch => Swatch is not null;
}

/// <summary>作用范围下拉里的一个对象(分组或连接)。</summary>
/// <param name="Id">分组 / 连接 Id。</param>
/// <param name="Name">显示名称。</param>
public sealed record OutputRuleScopeTarget(Guid Id, string Name);

/// <summary>测试结果里的一段文字(带上规则的样式)。</summary>
public sealed class OutputRuleTestSegmentViewModel(string text, IBrush? foreground, IBrush? background, bool bold, bool isMatch)
{
    /// <summary>文字。</summary>
    public string Text { get; } = text;

    /// <summary>规则设的前景;null = 用界面正文色。</summary>
    public IBrush? Foreground { get; } = foreground;

    /// <summary>规则设了前景。</summary>
    public bool HasForeground => Foreground is not null;

    /// <summary>规则设的背景;null = 不填。</summary>
    public IBrush? Background { get; } = background;

    /// <summary>字重。</summary>
    public FontWeight Weight { get; } = bold ? FontWeight.Bold : FontWeight.Normal;

    /// <summary>命中段一律画下划线:只设了粗体、或背景与底色相近时,也看得出这一段的边界。</summary>
    public TextDecorationCollection? Decorations { get; } = isMatch ? TextDecorations.Underline : null;
}

/// <summary>测试结果里命中过的一行。</summary>
public sealed class OutputRuleTestLineViewModel(int lineNumber, IReadOnlyList<OutputRuleTestSegmentViewModel> segments, string? respondNote)
{
    /// <summary>行号。</summary>
    public string LineNumber { get; } = lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>各段。</summary>
    public IReadOnlyList<OutputRuleTestSegmentViewModel> Segments { get; } = segments;

    /// <summary>「自动应答 X → 发送 …」;没有应答规则命中时为 null。</summary>
    public string? RespondNote { get; } = respondNote;

    /// <summary>有应答说明。</summary>
    public bool HasRespondNote => RespondNote is not null;
}

/// <summary>规则列表里的一行。</summary>
public sealed class OutputRuleRowViewModel : ReactiveObject
{
    private readonly OutputRulesViewModel _owner;

    internal OutputRuleRowViewModel(OutputRule rule, OutputRulesViewModel owner)
    {
        Rule = rule;
        _owner = owner;
    }

    /// <summary>这一行背后的规则(编辑框改的是它的副本,点「完成」才写回)。</summary>
    public OutputRule Rule { get; private set; }

    /// <summary>名称(没填时显示正则)。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Rule.Name) ? Rule.Pattern : Rule.Name;

    /// <summary>正则。</summary>
    public string Pattern => Rule.Pattern;

    /// <summary>动作的显示文字。</summary>
    public string ActionText => Strings.Get(Rule.Action == OutputRuleActions.Respond ? "OutRule_ActionRespond" : "OutRule_ActionHighlight");

    /// <summary>作用范围的显示文字。</summary>
    public string ScopeText => _owner.DescribeScope(Rule);

    /// <summary>启用开关(直接写回规则)。</summary>
    public bool Enabled
    {
        get => Rule.Enabled;
        set
        {
            if (Rule.Enabled == value)
            {
                return;
            }
            Rule.Enabled = value;
            this.RaisePropertyChanged();
            _owner.OnRulesChanged();
        }
    }

    /// <summary>校验不过时的说明(这条规则不会生效);没问题时为空串。</summary>
    public string Error => OutputRuleCompiler.Validate(Rule).Describe();

    /// <summary>校验不过。</summary>
    public bool HasError => Error.Length > 0;

    internal void Replace(OutputRule rule)
    {
        Rule = rule;
        Refresh();
    }

    internal void Refresh()
    {
        this.RaisePropertyChanged(nameof(DisplayName));
        this.RaisePropertyChanged(nameof(Pattern));
        this.RaisePropertyChanged(nameof(ActionText));
        this.RaisePropertyChanged(nameof(ScopeText));
        this.RaisePropertyChanged(nameof(Enabled));
        this.RaisePropertyChanged(nameof(Error));
        this.RaisePropertyChanged(nameof(HasError));
    }
}

/// <summary>
/// 设置 → 输出规则:规则列表、编辑框与「测试」。
/// </summary>
/// <remarks>
/// <para>
/// 与设置窗口别的页一样,改动在点设置窗口的「保存」时才落盘(<see cref="ToModels" />),「取消」原样丢掉。
/// 编辑框改的是副本;只有校验通过才能点「完成」写回列表 —— 尤其是「会命中口令提示的自动应答」一律写不进来,
/// 规则随设置云同步,不能让口令有机会进到这张表里。
/// </para>
/// <para>颜色只给调色板下标(当前配色方案的 ANSI 16 色),色块是按方案现取的,不写死颜色。</para>
/// </remarks>
public sealed class OutputRulesViewModel : ReactiveObject
{
    private readonly ISessionRepository? _sessions;
    private readonly Func<IReadOnlyList<string>> _paletteHex;
    private OutputRuleRowViewModel? _editingRow;
    private bool _suppressEditorSync;

    /// <summary>创建视图模型。</summary>
    /// <param name="sessions">连接仓储(作用范围下拉要列出分组与连接);null = 只能选全局。</param>
    /// <param name="paletteHex">当前配色方案的 16 色(#RRGGBB),色块与测试预览用。</param>
    public OutputRulesViewModel(ISessionRepository? sessions, Func<IReadOnlyList<string>> paletteHex)
    {
        _sessions = sessions;
        _paletteHex = paletteHex;
        AddCommand = ReactiveCommand.Create(BeginAdd, this.WhenAnyValue(x => x.CanAdd));
        CommitEditCommand = ReactiveCommand.Create(CommitEdit, this.WhenAnyValue(x => x.DraftError, error => error.Length == 0));
        CancelEditCommand = ReactiveCommand.Create(CancelEdit);
        RebuildPalette();
    }

    /// <summary>规则列表(顺序 = 优先级)。</summary>
    public ObservableCollection<OutputRuleRowViewModel> Rules { get; } = [];

    /// <summary>一条规则都没有。</summary>
    public bool IsEmpty => Rules.Count == 0;

    /// <summary>还能再加(没到 <see cref="OutputRuleLimits.MaxRules" />)。</summary>
    public bool CanAdd => Rules.Count < OutputRuleLimits.MaxRules;

    /// <summary>到上限时的说明。</summary>
    public string LimitText { get; } = Strings.Format("OutRule_LimitReached", OutputRuleLimits.MaxRules);

    /// <summary>新建。</summary>
    public ReactiveCommand<RxVoid, RxVoid> AddCommand { get; }

    /// <summary>编辑框「完成」:写回列表(校验不过时不可点)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CommitEditCommand { get; }

    /// <summary>编辑框「取消」。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelEditCommand { get; }

    // ———— 载入 / 写回 ————

    /// <summary>从设置载入(深拷贝:取消设置窗口时原表不受影响)。</summary>
    /// <param name="rules">设置里的规则表。</param>
    public void Load(IEnumerable<OutputRule>? rules)
    {
        Draft = null;
        _editingRow = null;
        Rules.Clear();
        foreach (OutputRule rule in rules ?? [])
        {
            Rules.Add(new(rule.Clone(), this));
        }
        OnRulesChanged();
    }

    /// <summary>
    /// 交出要保存的表。编辑框还开着、草稿又校验得过时一并写回(用户多半是直接点了窗口的「保存」);
    /// 校验不过的草稿丢掉 —— 尤其是命中口令提示的应答,不能借「保存」写进设置。
    /// </summary>
    /// <returns>规则表(副本)。</returns>
    public List<OutputRule> ToModels()
    {
        if (Draft is not null && DraftError.Length == 0)
        {
            CommitEdit();
        }
        return [.. Rules.Select(row => row.Rule.Clone())];
    }

    /// <summary>重新读取分组与连接(作用范围下拉的条目)。</summary>
    public async Task RefreshTargetsAsync()
    {
        if (_sessions is null)
        {
            return;
        }
        try
        {
            List<ServerGroup> groups = await _sessions.GetAllGroupsAsync();
            List<SessionProfile> profiles = await _sessions.GetAllSessionsAsync();
            Groups = [.. groups.OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new OutputRuleScopeTarget(g.Id, g.Name))];
            Profiles = [.. profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new OutputRuleScopeTarget(p.Id, string.IsNullOrWhiteSpace(p.Name) ? p.Host : p.Name))];
        }
        catch
        {
            // 读不出来就只能选全局;列表照常可用。
        }
        foreach (OutputRuleRowViewModel row in Rules)
        {
            row.Refresh();
        }
        this.RaisePropertyChanged(nameof(ScopeTargets));
        SyncScopeTargetFromDraft();
    }

    /// <summary>可选的分组。</summary>
    public IReadOnlyList<OutputRuleScopeTarget> Groups { get; private set; } = [];

    /// <summary>可选的连接。</summary>
    public IReadOnlyList<OutputRuleScopeTarget> Profiles { get; private set; } = [];

    /// <summary>作用范围的显示文字(列表里用)。</summary>
    internal string DescribeScope(OutputRule rule)
    {
        if (rule.Scope == OutputRuleScopes.Group)
        {
            string? name = Groups.FirstOrDefault(g => g.Id == rule.ScopeId)?.Name;
            return Strings.Format("OutRule_ScopeGroupNamed", name ?? Strings.Get("OutRule_ScopeMissing"));
        }
        if (rule.Scope == OutputRuleScopes.Profile)
        {
            string? name = Profiles.FirstOrDefault(p => p.Id == rule.ScopeId)?.Name;
            return Strings.Format("OutRule_ScopeProfileNamed", name ?? Strings.Get("OutRule_ScopeMissing"));
        }
        return Strings.Get("OutRule_ScopeGlobal");
    }

    internal void OnRulesChanged()
    {
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(CanAdd));
        RunTest();
    }

    // ———— 列表操作 ————

    private void BeginAdd()
    {
        _editingRow = null;
        Draft = new OutputRule { Name = string.Empty };
        IsNewDraft = true;
    }

    /// <summary>打开编辑框编辑一行(改的是副本)。</summary>
    /// <param name="row">要编辑的行。</param>
    public void BeginEdit(OutputRuleRowViewModel row)
    {
        _editingRow = row;
        Draft = row.Rule.Clone();
        IsNewDraft = false;
    }

    /// <summary>删除一行(正在编辑它时编辑框一并关掉)。</summary>
    /// <param name="row">要删的行。</param>
    public void Delete(OutputRuleRowViewModel row)
    {
        if (ReferenceEquals(row, _editingRow))
        {
            CancelEdit();
        }
        Rules.Remove(row);
        OnRulesChanged();
    }

    /// <summary>上移(<paramref name="delta" /> 为 -1,优先级提高)或下移(+1)。</summary>
    /// <param name="row">要挪的行。</param>
    /// <param name="delta">-1 或 +1。</param>
    public void Move(OutputRuleRowViewModel row, int delta)
    {
        int index = Rules.IndexOf(row);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Rules.Count)
        {
            return;
        }
        Rules.Move(index, target);
        OnRulesChanged();
    }

    private void CommitEdit()
    {
        if (Draft is not { } draft || DraftError.Length > 0)
        {
            return;
        }
        if (_editingRow is { } row && Rules.Contains(row))
        {
            row.Replace(draft.Clone());
        }
        else if (CanAdd)
        {
            Rules.Add(new(draft.Clone(), this));
        }
        _editingRow = null;
        Draft = null;
        OnRulesChanged();
    }

    private void CancelEdit()
    {
        _editingRow = null;
        Draft = null;
        RunTest();
    }

    // ———— 编辑框 ————

    /// <summary>正在编辑的副本;null = 编辑框关着。</summary>
    public OutputRule? Draft
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsEditing));
            SyncEditorFromDraft();
        }
    }

    /// <summary>编辑框开着。</summary>
    public bool IsEditing => Draft is not null;

    /// <summary>编辑框是「新建」而不是「编辑」。</summary>
    public bool IsNewDraft
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>编辑框标题。</summary>
    public string EditorTitle => Strings.Get(IsNewDraft ? "OutRule_Add" : "OutRule_EditTitle");

    /// <summary>名称。</summary>
    public string DraftName
    {
        get => Draft?.Name ?? string.Empty;
        set => UpdateDraft(rule => rule.Name = value ?? string.Empty);
    }

    /// <summary>正则。</summary>
    public string DraftPattern
    {
        get => Draft?.Pattern ?? string.Empty;
        set => UpdateDraft(rule => rule.Pattern = value ?? string.Empty);
    }

    /// <summary>区分大小写。</summary>
    public bool DraftCaseSensitive
    {
        get => Draft?.CaseSensitive ?? false;
        set => UpdateDraft(rule => rule.CaseSensitive = value);
    }

    /// <summary>动作下拉(0 = 高亮,1 = 自动应答)。</summary>
    public int DraftActionIndex
    {
        get => Draft?.Action == OutputRuleActions.Respond ? 1 : 0;
        set => UpdateDraft(rule => rule.Action = value == 1 ? OutputRuleActions.Respond : OutputRuleActions.Highlight);
    }

    /// <summary>当前是高亮规则。</summary>
    public bool DraftIsHighlight => DraftActionIndex == 0;

    /// <summary>当前是自动应答规则。</summary>
    public bool DraftIsRespond => DraftActionIndex == 1;

    /// <summary>作用范围下拉(0 = 全局,1 = 分组,2 = 连接)。</summary>
    public int DraftScopeIndex
    {
        get => Draft?.Scope switch
        {
            OutputRuleScopes.Group => 1,
            OutputRuleScopes.Profile => 2,
            _ => 0,
        };
        set => UpdateDraft(rule =>
        {
            string scope = value switch
            {
                1 => OutputRuleScopes.Group,
                2 => OutputRuleScopes.Profile,
                _ => OutputRuleScopes.Global,
            };
            if (rule.Scope != scope)
            {
                rule.Scope = scope;
                rule.ScopeId = null; // 分组的 Id 拿去当连接 Id 用没有意义。
            }
        });
    }

    /// <summary>作用范围要选一个对象(分组 / 连接)。</summary>
    public bool DraftNeedsScopeTarget => DraftScopeIndex != 0;

    /// <summary>作用范围下拉的条目(随范围切换)。</summary>
    public IReadOnlyList<OutputRuleScopeTarget> ScopeTargets => DraftScopeIndex switch
    {
        1 => Groups,
        2 => Profiles,
        _ => [],
    };

    /// <summary>选中的分组 / 连接。</summary>
    public OutputRuleScopeTarget? DraftScopeTarget
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            if (!_suppressEditorSync && value is not null)
            {
                UpdateDraft(rule => rule.ScopeId = value.Id);
            }
        }
    }

    /// <summary>颜色下拉的条目(首项「不改」)。</summary>
    public IReadOnlyList<OutputRulePaletteChoice> PaletteChoices { get; private set; } = [];

    /// <summary>前景。</summary>
    public OutputRulePaletteChoice? DraftForeground
    {
        get => ChoiceFor(Draft?.Foreground);
        set => UpdateDraft(rule => rule.Foreground = value?.Index);
    }

    /// <summary>背景。</summary>
    public OutputRulePaletteChoice? DraftBackground
    {
        get => ChoiceFor(Draft?.Background);
        set => UpdateDraft(rule => rule.Background = value?.Index);
    }

    /// <summary>加粗。</summary>
    public bool DraftBold
    {
        get => Draft?.Bold ?? false;
        set => UpdateDraft(rule => rule.Bold = value);
    }

    /// <summary>应答文本。</summary>
    public string DraftResponse
    {
        get => Draft?.Response ?? string.Empty;
        set => UpdateDraft(rule => rule.Response = value ?? string.Empty);
    }

    /// <summary>应答后按回车。</summary>
    public bool DraftSendEnter
    {
        get => Draft?.SendEnter ?? true;
        set => UpdateDraft(rule => rule.SendEnter = value);
    }

    /// <summary>冷却秒数。</summary>
    public decimal? DraftCooldownSeconds
    {
        get => Draft?.CooldownSeconds ?? OutputRuleLimits.DefaultCooldownSeconds;
        set => UpdateDraft(rule => rule.CooldownSeconds =
            Math.Clamp((int)(value ?? OutputRuleLimits.DefaultCooldownSeconds), 1, OutputRuleLimits.MaxCooldownSeconds));
    }

    /// <summary>每分钟上限。</summary>
    public decimal? DraftMaxPerMinute
    {
        get => Draft?.MaxPerMinute ?? OutputRuleLimits.DefaultMaxPerMinute;
        set => UpdateDraft(rule => rule.MaxPerMinute =
            Math.Clamp((int)(value ?? OutputRuleLimits.DefaultMaxPerMinute), 1, OutputRuleLimits.MaxPerMinuteCeiling));
    }

    /// <summary>也转发给同步输入频道。</summary>
    public bool DraftForwardToSyncChannel
    {
        get => Draft?.ForwardToSyncChannel ?? false;
        set => UpdateDraft(rule => rule.ForwardToSyncChannel = value);
    }

    /// <summary>草稿校验不过时的说明;没问题为空串(编辑框关着时也是空串)。</summary>
    public string DraftError => Draft is { } draft ? OutputRuleCompiler.Validate(draft).Describe() : string.Empty;

    /// <summary>草稿校验不过。</summary>
    public bool HasDraftError => DraftError.Length > 0;

    private void UpdateDraft(Action<OutputRule> change)
    {
        if (_suppressEditorSync || Draft is not { } draft)
        {
            return;
        }
        change(draft);
        SyncEditorFromDraft();
    }

    /// <summary>草稿变了:编辑框的各项、校验与测试一起刷新。</summary>
    private void SyncEditorFromDraft()
    {
        this.RaisePropertyChanged(nameof(EditorTitle));
        foreach (string name in (string[])
            [
                nameof(DraftName), nameof(DraftPattern), nameof(DraftCaseSensitive), nameof(DraftActionIndex),
                nameof(DraftIsHighlight), nameof(DraftIsRespond), nameof(DraftScopeIndex), nameof(DraftNeedsScopeTarget),
                nameof(ScopeTargets), nameof(DraftForeground), nameof(DraftBackground), nameof(DraftBold),
                nameof(DraftResponse), nameof(DraftSendEnter), nameof(DraftCooldownSeconds), nameof(DraftMaxPerMinute),
                nameof(DraftForwardToSyncChannel), nameof(DraftError), nameof(HasDraftError),
            ])
        {
            this.RaisePropertyChanged(name);
        }
        SyncScopeTargetFromDraft();
        RunTest();
    }

    private void SyncScopeTargetFromDraft()
    {
        _suppressEditorSync = true;
        try
        {
            DraftScopeTarget = Draft?.ScopeId is { } id ? ScopeTargets.FirstOrDefault(t => t.Id == id) : null;
        }
        finally
        {
            _suppressEditorSync = false;
        }
    }

    private OutputRulePaletteChoice? ChoiceFor(int? index) =>
        PaletteChoices.FirstOrDefault(choice => choice.Index == index) ?? (PaletteChoices.Count > 0 ? PaletteChoices[0] : null);

    /// <summary>按当前配色方案重建颜色下拉(换了方案之后色块要跟着换)。</summary>
    public void RebuildPalette()
    {
        IReadOnlyList<string> hex = _paletteHex();
        string[] baseNames =
        [
            "OutRule_ColorBlack", "OutRule_ColorRed", "OutRule_ColorGreen", "OutRule_ColorYellow",
            "OutRule_ColorBlue", "OutRule_ColorMagenta", "OutRule_ColorCyan", "OutRule_ColorWhite",
        ];
        List<OutputRulePaletteChoice> choices = [new(null, Strings.Get("OutRule_ColorNone"), null)];
        for (int i = 0; i <= OutputRuleLimits.MaxPaletteIndex; i++)
        {
            string name = Strings.Get(baseNames[i % 8]);
            if (i >= 8)
            {
                name = Strings.Format("OutRule_ColorBright", name);
            }
            choices.Add(new(i, name, BrushFor(hex, i)));
        }
        PaletteChoices = choices;
        this.RaisePropertyChanged(nameof(PaletteChoices));
        this.RaisePropertyChanged(nameof(DraftForeground));
        this.RaisePropertyChanged(nameof(DraftBackground));
        RunTest();
    }

    private static SolidColorBrush? BrushFor(IReadOnlyList<string> hex, int? index) =>
        index is { } i && i < hex.Count && Color.TryParse(hex[i], out Color color) ? new SolidColorBrush(color) : null;

    // ———— 测试 ————

    /// <summary>粘贴进来的测试文本。</summary>
    public string TestText
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RunTest();
        }
    } = string.Empty;

    /// <summary>命中过的行。</summary>
    public ObservableCollection<OutputRuleTestLineViewModel> TestResults { get; } = [];

    /// <summary>「N 行里有 M 行命中」;没贴文本时为空串。</summary>
    public string TestSummary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    /// 跑一遍测试:编辑框开着时只测草稿(还没写回列表的那条),否则测表里全部启用的规则。
    /// </summary>
    private void RunTest()
    {
        TestResults.Clear();
        if (string.IsNullOrEmpty(TestText))
        {
            TestSummary = string.Empty;
            return;
        }
        List<OutputRule> rules = Draft is { } draft ? [draft] : [.. Rules.Select(row => row.Rule)];
        IReadOnlyList<OutputRuleTestLine> lines = OutputRuleTester.Run(rules, TestText, out int tested);
        IReadOnlyList<string> hex = _paletteHex();
        foreach (OutputRuleTestLine line in lines)
        {
            List<OutputRuleTestSegmentViewModel> segments = [];
            foreach (OutputRuleTestSegment segment in line.Segments)
            {
                OutputRule? rule = segment.Rule;
                segments.Add(new(
                    segment.Text,
                    BrushFor(hex, rule?.Foreground),
                    BrushFor(hex, rule?.Background),
                    rule?.Bold ?? false,
                    rule is not null));
            }
            string? note = line.Responders.Count > 0 ? DescribeResponse(line.Responders[0]) : null;
            TestResults.Add(new(line.LineNumber, segments, note));
        }
        TestSummary = lines.Count == 0
            ? Strings.Get("OutRule_TestNoMatch")
            : Strings.Format("OutRule_TestSummary", tested, lines.Count);
    }

    private static string DescribeResponse(OutputRule rule)
    {
        string name = string.IsNullOrWhiteSpace(rule.Name) ? rule.Pattern : rule.Name;
        string sent = rule.Response + (rule.SendEnter ? Strings.Get("OutRule_TestEnterKey") : string.Empty);
        return Strings.Format("OutRule_TestRespond", name, sent);
    }
}
