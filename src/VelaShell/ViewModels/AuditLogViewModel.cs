using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>审计日志窗口里的一行。</summary>
/// <param name="Timestamp">事件时间。</param>
/// <param name="Category">类别原值(<c>connection</c> / <c>security</c> …)。</param>
/// <param name="Action">动作原值(<c>connect</c> / <c>hostkey-rejected</c> …)。</param>
/// <param name="CategoryLabel">类别的界面文字。</param>
/// <param name="ActionLabel">动作的界面文字;认不出的动作原样显示。</param>
/// <param name="SessionName">会话名;记录里没有配置 Id、或配置已删时为 —。</param>
/// <param name="Detail">详情(<c>用户@主机:端口</c>、指纹裁决的说明、改了哪几块…)。</param>
/// <param name="IsProblem">是否值得留意:连接失败、拒绝或接受了变更的主机指纹。</param>
/// <param name="ProfileId">记录里带的配置 Id。</param>
/// <param name="ProfileExists">那条配置载入时还在库里 —— 「打开配置」只对它可用。</param>
public sealed record AuditLogRow(
    DateTimeOffset Timestamp,
    string Category,
    string Action,
    string CategoryLabel,
    string ActionLabel,
    string SessionName,
    string Detail,
    bool IsProblem,
    Guid? ProfileId = null,
    bool ProfileExists = false)
{
    /// <summary>本地时间,精确到秒。</summary>
    public string TimeText => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>带着配置 Id 的记录才有「打开配置」这个按钮(连接与配置类);配置删了就灰着。</summary>
    public bool HasProfile => ProfileId is not null;

    /// <summary>「打开配置」的悬停说明;配置已删时说明为什么灰着。</summary>
    public string OpenProfileTip => Strings.Get(ProfileExists ? "AuditLog_OpenProfile" : "AuditLog_ProfileGone");
}

/// <summary>
/// 审计日志窗口(设置 → 安全审计 → 查看审计日志):按时间倒序列出 <c>audit_log</c>,可按类别、时间段、只看异常与关键字筛,
/// 筛出来的可以导出成 CSV,带配置 Id 的行可以直接打开那条连接配置。
/// </summary>
/// <remarks>
/// <para>
/// 一次取最近 <see cref="MaxRows" /> 条。<b>时间段下推到查询里</b>(换了时间段就重新读库):只在内存里筛的话,
/// 「三个月前那一周」落在最近 2000 条之外,筛出来就是空的。类别、异常、关键字仍在本地筛 ——
/// 审计是逐条的短文本,几千条的筛选不值得来回查库。
/// </para>
/// <para>
/// 记录里只有配置 Id;会话名在载入时从会话库对一次。配置删掉之后那一行显示 —,「打开配置」灰掉,
/// 详情里的 <c>用户@主机:端口</c> 照样看得出是哪台。
/// </para>
/// </remarks>
public sealed class AuditLogViewModel : ReactiveObject
{
    /// <summary>一次最多载入的条数。</summary>
    public const int MaxRows = 2000;

    /// <summary>类别下拉的顺序(第 0 项「全部」不在表里)。</summary>
    private static readonly string[] Categories =
    [
        AuditCategories.Connection,
        AuditCategories.Security,
        AuditCategories.Profile,
        AuditCategories.Settings,
        AuditCategories.Credential,
    ];

    private readonly IAuditLogService _auditLog;
    private readonly ISessionRepository? _sessions;
    private readonly TimeProvider _time;
    private List<AuditLogRow> _all = [];
    private bool _truncated;
    private int _loadVersion;

    /// <summary>创建审计日志视图模型。</summary>
    /// <param name="auditLog">审计日志。</param>
    /// <param name="sessions">会话库,用来把配置 Id 对成会话名、打开配置;没有时会话一栏显示 —。</param>
    /// <param name="timeProvider">时钟(「最近 7 天」从哪天算起);测试用。</param>
    public AuditLogViewModel(IAuditLogService auditLog, ISessionRepository? sessions = null, TimeProvider? timeProvider = null)
    {
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _sessions = sessions;
        _time = timeProvider ?? TimeProvider.System;
        CategoryOptions = [Strings.Get("AuditLog_CategoryAll"), .. Categories.Select(CategoryLabel)];
        RangeOptions =
        [
            Strings.Get("AuditLog_RangeAll"),
            Strings.Get("AuditLog_Range24Hours"),
            Strings.Get("AuditLog_Range7Days"),
            Strings.Get("AuditLog_Range30Days"),
            Strings.Get("AuditLog_RangeCustom"),
        ];
        RefreshCommand = ReactiveCommand.CreateFromTask(() => LoadAsync());
        this.WhenAnyValue(x => x.CategoryIndex, x => x.OnlyProblems, x => x.SearchText)
            .Skip(1)
            .Subscribe(_ => ApplyFilter());
        // 时间段一变就重读:它决定的是从库里取哪一段,不是在已载入的里面挑。
        this.WhenAnyValue(x => x.RangeIndex, x => x.CustomFrom, x => x.CustomTo)
            .Skip(1)
            .Subscribe(range =>
            {
                this.RaisePropertyChanged(nameof(IsCustomRange));
                _ = LoadAsync();
            });
    }

    /// <summary>筛选后的行。</summary>
    public ObservableCollection<AuditLogRow> Rows { get; } = [];

    /// <summary>类别下拉:全部 / 连接 / 安全 / 连接配置 / 设置 / 共享凭据。</summary>
    public IReadOnlyList<string> CategoryOptions { get; }

    /// <summary>类别下拉的选中项:0 全部,之后按 <see cref="Categories" /> 的顺序。</summary>
    public int CategoryIndex
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>时间段下拉:全部 / 最近 24 小时 / 最近 7 天 / 最近 30 天 / 自定义。</summary>
    public IReadOnlyList<string> RangeOptions { get; }

    /// <summary>时间段下拉的选中项。</summary>
    public int RangeIndex
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>选了「自定义」:显示起止两个日期。</summary>
    public bool IsCustomRange => RangeIndex == RangeOptions.Count - 1;

    /// <summary>自定义时间段的起始日(本地日期,含当天);空为不限。</summary>
    public DateTime? CustomFrom
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>自定义时间段的结束日(本地日期,含当天);空为不限。</summary>
    public DateTime? CustomTo
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>只看异常:连接失败、拒绝或接受了变更的主机指纹。</summary>
    public bool OnlyProblems
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>关键字:在事件、类别、会话名与详情里找(不区分大小写)。</summary>
    public string SearchText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>一行摘要:共几条、筛出几条、是否只载入了最近的一部分;载入失败时是失败原因。</summary>
    public string Status
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>筛选之后一条都没有。</summary>
    public bool IsEmpty
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>重新读一遍审计日志。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>
    /// 打开一条连接配置(由打开窗口的一方接上:编辑框是主窗口的,保存后要刷新会话树)。没接上时「打开配置」不可用。
    /// </summary>
    public Func<SessionProfile, Task>? OpenProfile { get; set; }

    /// <summary>当前选的时间段:起(含)与止(不含);不限的一端为 null。</summary>
    public (DateTimeOffset? From, DateTimeOffset? To) SelectedRange()
    {
        DateTimeOffset now = _time.GetUtcNow();
        switch (RangeIndex)
        {
            case 1:
                return (now.AddHours(-24), null);
            case 2:
                return (now.AddDays(-7), null);
            case 3:
                return (now.AddDays(-30), null);
        }
        if (!IsCustomRange)
        {
            return (null, null);
        }
        DateTime? from = CustomFrom?.Date;
        DateTime? to = CustomTo?.Date;
        if (from > to)
        {
            (from, to) = (to, from);
        }
        TimeZoneInfo zone = _time.LocalTimeZone;
        return (from is { } f ? LocalMidnight(f, zone) : null, to is { } t ? LocalMidnight(t.AddDays(1), zone) : null);
    }

    /// <summary>读最近 <see cref="MaxRows" /> 条(限在所选时间段内),对上会话名,再按当前条件筛一遍。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        int version = ++_loadVersion;
        (DateTimeOffset? from, DateTimeOffset? to) = SelectedRange();
        List<AuditEntry> entries;
        try
        {
            entries = await _auditLog.QueryAsync(MaxRows, from: from, to: to, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version != _loadVersion)
            {
                return;
            }
            _all = [];
            _truncated = false;
            Rows.Clear();
            IsEmpty = true;
            Status = Strings.Format("AuditLog_LoadFailed", ex.Message);
            return;
        }

        Dictionary<Guid, string> names = await SessionNamesAsync(entries);
        // 换时间段时可能有两次读库在路上:只认最后发起的那一次,先发后到的不能盖掉它。
        if (version != _loadVersion)
        {
            return;
        }
        _all = [.. entries.Select(e => ToRow(e, names))];
        _truncated = entries.Count >= MaxRows;
        ApplyFilter();
    }

    /// <summary>把一条记录翻成界面上的一行。</summary>
    internal static AuditLogRow ToRow(AuditEntry entry, IReadOnlyDictionary<Guid, string> sessionNames)
    {
        bool exists = entry.ProfileId is { } id && sessionNames.ContainsKey(id);
        return new(entry.Timestamp,
            entry.Category,
            entry.Action,
            CategoryLabel(entry.Category),
            ActionLabel(entry.Action),
            exists ? sessionNames[entry.ProfileId!.Value] : "—",
            entry.Detail,
            IsProblem(entry.Action),
            entry.ProfileId,
            exists);
    }

    /// <summary>类别的界面文字;认不出的原样返回。</summary>
    internal static string CategoryLabel(string category) => category switch
    {
        AuditCategories.Connection => Strings.Get("AuditLog_CategoryConnection"),
        AuditCategories.Security => Strings.Get("AuditLog_CategorySecurity"),
        AuditCategories.Profile => Strings.Get("AuditLog_CategoryProfile"),
        AuditCategories.Settings => Strings.Get("AuditLog_CategorySettings"),
        AuditCategories.Credential => Strings.Get("AuditLog_CategoryCredential"),
        _ => category,
    };

    /// <summary>动作的界面文字;认不出的原样返回(以后加的新动作至少看得见原值)。</summary>
    internal static string ActionLabel(string action) => action switch
    {
        "connect" => Strings.Get("AuditLog_ActionConnect"),
        "connect-failed" => Strings.Get("AuditLog_ActionConnectFailed"),
        "hostkey-rejected" => Strings.Get("AuditLog_ActionHostKeyRejected"),
        "hostkey-trusted-once" => Strings.Get("AuditLog_ActionHostKeyTrustedOnce"),
        "hostkey-changed-accepted" => Strings.Get("AuditLog_ActionHostKeyChangedAccepted"),
        "hostkey-persist-failed" => Strings.Get("AuditLog_ActionHostKeyPersistFailed"),
        "external-launch" => Strings.Get("AuditLog_ActionExternalLaunch"),
        AuditActions.ProfileCreated => Strings.Get("AuditLog_ActionProfileCreated"),
        AuditActions.ProfileUpdated => Strings.Get("AuditLog_ActionProfileUpdated"),
        AuditActions.ProfileDeleted => Strings.Get("AuditLog_ActionProfileDeleted"),
        AuditActions.SettingsChanged => Strings.Get("AuditLog_ActionSettingsChanged"),
        AuditActions.CredentialCreated => Strings.Get("AuditLog_ActionCredentialCreated"),
        AuditActions.CredentialUpdated => Strings.Get("AuditLog_ActionCredentialUpdated"),
        AuditActions.CredentialDeleted => Strings.Get("AuditLog_ActionCredentialDeleted"),
        _ => action,
    };

    /// <summary>值得留意的动作:没连上、拒掉了指纹、接受了一把变过的指纹、信任了却没能保存。</summary>
    internal static bool IsProblem(string action) =>
        action is "connect-failed" or "hostkey-rejected" or "hostkey-changed-accepted" or "hostkey-persist-failed";

    /// <summary>
    /// 把筛出来的行写成 CSV(不含 BOM,编码见 <see cref="SessionFileText.EncodeCsv" />)。
    /// </summary>
    /// <remarks>
    /// 导出的是<b>眼前筛出来的那些</b>:要全部就先清掉筛选。表头随界面语言,时间写成带时区的本地时间
    /// (<c>2026-10-24T09:30:00+08:00</c>),动作另给一列原值方便按代码过滤。详情里有远端主机的原话、
    /// 会话名来自别人给的连接文件 —— 单元格一律过 <see cref="CsvText.Cell" />,以 <c>= + - @</c> 开头的补单引号。
    /// </remarks>
    /// <returns>CSV 文本。</returns>
    public string BuildCsv()
    {
        var builder = new StringBuilder();
        CsvText.AppendRow(builder,
        [
            Strings.Get("AuditLog_ColTime"),
            Strings.Get("AuditLog_ColCategory"),
            Strings.Get("AuditLog_ColAction"),
            Strings.Get("AuditLog_ColActionCode"),
            Strings.Get("AuditLog_ColSession"),
            Strings.Get("AuditLog_ColProfileId"),
            Strings.Get("AuditLog_ColDetail"),
        ]);
        foreach (AuditLogRow row in Rows)
        {
            CsvText.AppendRow(builder,
            [
                row.Timestamp.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                row.CategoryLabel,
                row.ActionLabel,
                row.Action,
                row.ProfileExists ? row.SessionName : string.Empty,
                row.ProfileId?.ToString("D") ?? string.Empty,
                row.Detail,
            ]);
        }
        return builder.ToString();
    }

    /// <summary>把导出 CSV 的结果(写到了哪、或为什么没写成)放进摘要那一行;下次筛选时换回条数。</summary>
    /// <param name="message">要显示的话。</param>
    public void ReportExport(string message) => Status = message;

    /// <summary>
    /// 打开这一行对应的连接配置(由窗口的「打开配置」调用)。打开之前再从库里取一次:
    /// 窗口开着的这段时间里它可能已经被删了,那就在摘要里说一声。
    /// </summary>
    /// <param name="row">那一行。</param>
    public async Task OpenProfileAsync(AuditLogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.ProfileId is not { } id || _sessions is null || OpenProfile is null)
        {
            return;
        }
        SessionProfile? profile;
        try
        {
            profile = await _sessions.GetSessionAsync(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = Strings.Format("AuditLog_LoadFailed", ex.Message);
            return;
        }
        if (profile is null)
        {
            Status = Strings.Get("AuditLog_ProfileGone");
            return;
        }
        await OpenProfile(profile);
    }

    private static DateTimeOffset LocalMidnight(DateTime date, TimeZoneInfo zone)
    {
        DateTime local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private async Task<Dictionary<Guid, string>> SessionNamesAsync(List<AuditEntry> entries)
    {
        if (_sessions is null || !entries.Any(e => e.ProfileId is not null))
        {
            return [];
        }
        try
        {
            List<SessionProfile> profiles = await _sessions.GetAllSessionsAsync();
            return profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 对不上会话名只是少一栏信息,记录本身照常显示。
            return [];
        }
    }

    private void ApplyFilter()
    {
        string? category = CategoryIndex > 0 && CategoryIndex <= Categories.Length ? Categories[CategoryIndex - 1] : null;
        string keyword = SearchText.Trim();
        AuditLogRow[] matches =
        [
            .. _all.Where(row =>
                (category is null || row.Category == category)
                && (!OnlyProblems || row.IsProblem)
                && (keyword.Length == 0
                    || row.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || row.SessionName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || row.ActionLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || row.CategoryLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        ];
        Rows.Clear();
        foreach (AuditLogRow row in matches)
        {
            Rows.Add(row);
        }
        IsEmpty = matches.Length == 0;
        string count = matches.Length == _all.Count
            ? Strings.Format("AuditLog_Count", _all.Count)
            : Strings.Format("AuditLog_CountFiltered", matches.Length, _all.Count);
        Status = _truncated ? $"{count} · {Strings.Format("AuditLog_Truncated", MaxRows)}" : count;
    }
}
