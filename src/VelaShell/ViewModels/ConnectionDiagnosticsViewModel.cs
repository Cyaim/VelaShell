using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;

namespace VelaShell.ViewModels;

/// <summary>诊断步骤行(设计 RGXg1 stepPanel):序号 + 名称 + 状态图形/耗时。</summary>
/// <param name="index">步骤在诊断流程中的序号(从 0 开始)。</param>
/// <param name="name">步骤显示名称,如"DNS 解析"。</param>
public sealed class DiagnosticStepItemViewModel(int index, string name) : ReactiveObject
{
    private int Index { get; } = index;

    private string Name { get; set; } = name;

    /// <summary>列表中显示的带序号步骤名,如"1. DNS 解析"。</summary>
    public string DisplayName => $"{Index + 1}. {Name}";

    /// <summary>当前步骤的检测状态(待检/检测中/成功/警告/失败/跳过)。</summary>
    public DiagnosticStepStatus Status
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = DiagnosticStepStatus.Pending;

    /// <summary>该步骤的补充说明或失败原因,可为空。</summary>
    public string? Detail
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>该步骤耗时(毫秒),未完成时为空。</summary>
    public long? ElapsedMs
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>状态列文本:✅ 4ms / ⚠ 原因 / ✗ 原因 / ⏸ 等待修复后重试(设计 RGXg1)。</summary>
    public string StatusText => Status switch
    {
        DiagnosticStepStatus.Running => Strings.Get("Diag_StatusChecking"),
        DiagnosticStepStatus.Success => ElapsedMs is { } ms ? $"✓  {ms}ms" : "✓",
        DiagnosticStepStatus.Warning => "⚠",
        DiagnosticStepStatus.Failed => "✗",
        DiagnosticStepStatus.Skipped => "⏸",
        _ => "—"
    };

    /// <summary>该步骤是否检测成功,供视图着色。</summary>
    public bool IsSuccess => Status == DiagnosticStepStatus.Success;

    /// <summary>该步骤是否处于警告状态,供视图着色。</summary>
    public bool IsWarning => Status == DiagnosticStepStatus.Warning;

    /// <summary>该步骤是否检测失败,供视图着色。</summary>
    public bool IsFailed => Status == DiagnosticStepStatus.Failed;

    /// <summary>该步骤是否为待检或跳过状态,供视图弱化显示。</summary>
    public bool IsMuted => Status is DiagnosticStepStatus.Pending or DiagnosticStepStatus.Skipped;

    /// <summary>该步骤是否正在检测中,供视图显示进行态。</summary>
    public bool IsRunning => Status == DiagnosticStepStatus.Running;

    /// <summary>该步骤是否存在补充说明文本。</summary>
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>应用一次步骤更新,刷新状态、说明、耗时并通知相关派生属性。</summary>
    /// <param name="update">来自诊断服务的步骤更新数据。</param>
    public void Apply(DiagnosticStepUpdate update)
    {
        Name = update.Name;
        Status = update.Status;
        Detail = update.Detail;
        ElapsedMs = update.ElapsedMs;
        this.RaisePropertyChanged(nameof(DisplayName));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(IsSuccess));
        this.RaisePropertyChanged(nameof(IsWarning));
        this.RaisePropertyChanged(nameof(IsFailed));
        this.RaisePropertyChanged(nameof(IsMuted));
        this.RaisePropertyChanged(nameof(IsRunning));
        this.RaisePropertyChanged(nameof(HasDetail));
    }

    /// <summary>将步骤重置为待检状态,用于重新开始诊断。</summary>
    public void Reset() => Apply(new(Index, Name, DiagnosticStepStatus.Pending));
}

/// <summary>协议轨迹的一行:一个报文的时刻、方向、类型、长度、序号与通道号(没有内容)。</summary>
public sealed class PacketTraceRowViewModel
{
    /// <summary>由一条轨迹记录生成一行。</summary>
    /// <param name="entry">轨迹记录。</param>
    /// <param name="hopHeader">跳板链上换到另一跳时,这一行上面写出那一跳;直连或同一跳为 <see langword="null" />。</param>
    public PacketTraceRowViewModel(SshPacketTraceEntry entry, string? hopHeader)
    {
        Entry = entry;
        HopHeader = hopHeader;
    }

    /// <summary>原始记录。</summary>
    public SshPacketTraceEntry Entry { get; }

    /// <summary>换到另一跳时的那一跳标题;不换时为 <see langword="null" />。</summary>
    public string? HopHeader { get; }

    /// <summary>有没有跳标题。</summary>
    public bool HasHopHeader => HopHeader is not null;

    /// <summary>从开始记录起的时刻,如 <c>+0.012s</c>。</summary>
    public string Time => "+" + Entry.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s";

    /// <summary>方向:箭头 + 「发」/「收」。</summary>
    public string Direction => Entry.Direction == SshPacketDirection.Sent
        ? "→ " + Strings.Get("Diag_TraceSent")
        : "← " + Strings.Get("Diag_TraceReceived");

    /// <summary>本机发出的(界面上换个颜色)。</summary>
    public bool IsSent => Entry.Direction == SshPacketDirection.Sent;

    /// <summary>消息类型:协议原名加编号;方法专用、扩展与没分配的编号给类别名。</summary>
    public string Message => Describe(Entry.MessageNumber);

    /// <summary>长度(字节);我们发出的认证报文不记,显示「—」。</summary>
    public string Length => Entry.Length is { } length ? length.ToString("N0", CultureInfo.InvariantCulture) + " B" : "—";

    /// <summary>这个方向上的报文序号。</summary>
    public string Sequence => "#" + Entry.SequenceNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>通道号;不是通道消息时为空串。</summary>
    public string Channel => Entry.ChannelNumber is { } channel ? Strings.Format("Diag_TraceChannel", channel) : string.Empty;

    /// <summary>消息编号的说法(协议原名不翻译;按编号段归类的那几种给本地化的类别名)。</summary>
    /// <param name="number">消息编号。</param>
    public static string Describe(byte number)
    {
        string name = SshMessageNames.Name(number) ?? SshMessageNames.RangeOf(number) switch
        {
            SshMessageRange.KeyExchangeMethod => Strings.Get("Diag_TraceKexMethod"),
            SshMessageRange.AuthenticationMethod => Strings.Get("Diag_TraceAuthMethod"),
            SshMessageRange.LocalExtension => Strings.Get("Diag_TraceLocalExtension"),
            _ => Strings.Get("Diag_TraceUnassigned"),
        };
        return $"{name} ({number.ToString(CultureInfo.InvariantCulture)})";
    }
}

/// <summary>连接诊断中心(设计 RGXg1):对一条连接配置逐步检测并给出问题与修复建议。</summary>
public class ConnectionDiagnosticsViewModel : ReactiveObject
{
    private readonly IConnectionDiagnosticsService _diagnosticsService;
    private readonly SessionProfile _profile;
    private DiagnosticReport? _lastReport;

    /// <summary>创建连接诊断视图模型,绑定目标连接配置与诊断服务并初始化四个检测步骤。</summary>
    /// <param name="profile">要诊断的连接配置。</param>
    /// <param name="diagnosticsService">执行逐步诊断的服务。</param>
    public ConnectionDiagnosticsViewModel(SessionProfile profile, IConnectionDiagnosticsService diagnosticsService)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _diagnosticsService = diagnosticsService ?? throw new ArgumentNullException(nameof(diagnosticsService));
        Steps =
        [
            new(0, Strings.Get("DiagSvc_StepDns")),
            new(1, Strings.Get("DiagSvc_StepTcp")),
            new(2, Strings.Get("DiagSvc_StepSsh")),
            new(3, Strings.Get("DiagSvc_StepAuth"))
        ];
        Suggestions = [];
        IObservable<bool> canRun = this.WhenAnyValue(x => x.IsBusy, busy => !busy);
        RunCommand = ReactiveCommand.CreateFromTask(RunAsync, canRun);
        CopyPacketTraceCommand = ReactiveCommand.CreateFromTask(
            CopyPacketTraceAsync, this.WhenAnyValue(x => x.HasPacketTrace));
    }

    /// <summary>标题栏副标题里的目标描述。</summary>
    public string TargetSummary => Strings.Format(
        "Diag_TargetSummary",
        _profile.Name is { Length: > 0 } n ? n : _profile.Host,
        HostAddress.FormatTarget(_profile.Username, _profile.Host, _profile.Port));

    /// <summary>诊断步骤集合(DNS、TCP、SSH、认证),供步骤面板绑定。</summary>
    public ObservableCollection<DiagnosticStepItemViewModel> Steps { get; }

    /// <summary>诊断得出的修复建议文本集合。</summary>
    public ObservableCollection<string> Suggestions { get; }

    /// <summary>启动一次连接诊断的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RunCommand { get; }

    /// <summary>是否正在执行诊断,用于禁用重复触发并显示进度。</summary>
    public bool IsBusy
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowsNoPacketTrace));
        }
    }

    /// <summary>诊断发现的问题标题,无问题时为空。</summary>
    public string? IssueTitle
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasIssue));
        }
    }

    /// <summary>诊断发现问题的详细描述,无问题时为空。</summary>
    public string? IssueDescription
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否存在已发现的问题,用于问题面板显隐。</summary>
    public bool HasIssue => !string.IsNullOrEmpty(IssueTitle);

    /// <summary>四步全部通过时问题面板显示绿色的"未发现问题"。</summary>
    public bool AllPassed
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否已有可导出的诊断报告,用于导出按钮可用性。</summary>
    public bool CanExport => _lastReport is not null;

    // ---- 协议轨迹(认证那一步的报文旁路;只有元信息) ----

    /// <summary>认证那一步收发的报文,一行一个。</summary>
    public ObservableCollection<PacketTraceRowViewModel> PacketTrace { get; } = [];

    /// <summary>这一轮有没有协议轨迹。</summary>
    public bool HasPacketTrace
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>右侧面板显示协议轨迹(<see langword="true" />)还是诊断结果。</summary>
    public bool ShowsPacketTrace
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowsResults));
        }
    }

    /// <summary>右侧面板显示诊断结果(问题与建议)。</summary>
    public bool ShowsResults
    {
        get => !ShowsPacketTrace;
        set => ShowsPacketTrace = !value;
    }

    /// <summary>「协议轨迹」页签上的字:带上报文数。</summary>
    public string PacketTraceTabTitle => PacketTrace.Count == 0
        ? Strings.Get("Diag_TabTrace")
        : Strings.Format("Diag_TabTraceCount", PacketTrace.Count);

    /// <summary>超过记录上限没记的报文数的说明;没有时为 <see langword="null" />。</summary>
    public string? PacketTraceDroppedText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>这一轮没有走到 SSH 连接:轨迹页给一句说明,而不是一片空白。</summary>
    public bool ShowsNoPacketTrace => !IsBusy && !HasPacketTrace;

    /// <summary>复制协议轨迹(纯文本,与导出报告里的那一段相同)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyPacketTraceCommand { get; }

    /// <summary>写剪贴板;视图打开时接上(单测里换成记录)。</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    private async Task CopyPacketTraceAsync()
    {
        if (CopyToClipboard is { } copy)
        {
            await copy(BuildPacketTraceText());
        }
    }

    /// <summary>
    /// 把一轮的轨迹摆成行:跳板链上不止一跳时,换到另一跳的那一行上面写出那一跳 —— 直连只有一跳,不写。
    /// </summary>
    private void ShowPacketTrace(DiagnosticReport report)
    {
        bool multiHop = report.PacketTrace.Select(static e => e.Hop).Distinct(StringComparer.Ordinal).Skip(1).Any();
        string? previousHop = null;
        foreach (SshPacketTraceEntry entry in report.PacketTrace)
        {
            string? header = multiHop && !string.Equals(entry.Hop, previousHop, StringComparison.Ordinal) ? entry.Hop : null;
            previousHop = entry.Hop;
            PacketTrace.Add(new PacketTraceRowViewModel(entry, header));
        }
        PacketTraceDroppedText = report.PacketTraceDropped > 0
            ? Strings.Format("Diag_TraceDropped", report.PacketTraceDropped)
            : null;
        HasPacketTrace = PacketTrace.Count > 0;
        this.RaisePropertyChanged(nameof(PacketTraceTabTitle));
        this.RaisePropertyChanged(nameof(ShowsNoPacketTrace));
    }

    private void ClearPacketTrace()
    {
        PacketTrace.Clear();
        PacketTraceDroppedText = null;
        HasPacketTrace = false;
        this.RaisePropertyChanged(nameof(PacketTraceTabTitle));
    }

    /// <summary>协议轨迹的纯文本:一行一个报文,只有元信息(复制与导出报告共用)。</summary>
    public string BuildPacketTraceText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Strings.Get("Diag_TraceTextHeader"));
        foreach (PacketTraceRowViewModel row in PacketTrace)
        {
            if (row.HopHeader is { } hop)
            {
                builder.AppendLine($"[{hop}]");
            }
            builder.Append($"{row.Time,-9} {row.Direction,-5} {row.Message,-32} {row.Length,10} {row.Sequence,-7}");
            if (row.Channel.Length > 0)
            {
                builder.Append(' ').Append(row.Channel);
            }
            builder.AppendLine();
        }
        if (PacketTraceDroppedText is { } dropped)
        {
            builder.AppendLine(dropped);
        }
        return builder.ToString();
    }

    /// <summary>导出文件名建议。</summary>
    public string SuggestedReportFileName => Strings.Format(
        "Diag_ReportFileName",
        _profile.Name is { Length: > 0 } n ? n : _profile.Host,
        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        IssueTitle = null;
        IssueDescription = null;
        AllPassed = false;
        Suggestions.Clear();
        ClearPacketTrace();
        foreach (DiagnosticStepItemViewModel step in Steps)
        {
            step.Reset();
        }
        try
        {
            var progress = new Progress<DiagnosticStepUpdate>(update =>
            {
                // Progress<T> 回调已被封送回创建线程(UI);保险起见仍走 Dispatcher。
                if (Dispatcher.UIThread.CheckAccess())
                {
                    Steps[update.Index].Apply(update);
                }
                else
                {
                    Dispatcher.UIThread.Post(() => Steps[update.Index].Apply(update));
                }
            });
            DiagnosticReport report = await _diagnosticsService.DiagnoseAsync(_profile, progress, cancellationToken);
            _lastReport = report;
            IssueTitle = report.IssueTitle;
            IssueDescription = report.IssueDescription;
            foreach (string suggestion in report.Suggestions)
            {
                Suggestions.Add(suggestion);
            }
            AllPassed = report.Success && Suggestions.Count == 0;
            ShowPacketTrace(report);
            this.RaisePropertyChanged(nameof(CanExport));
        }
        catch (Exception ex)
        {
            IssueTitle = Strings.Get("Diag_RunFailed");
            IssueDescription = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>生成可保存的纯文本诊断报告(导出诊断报告按钮)。</summary>
    public string BuildReportText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Strings.Get("Diag_ReportTitle"));
        builder.AppendLine(Strings.Format("Diag_ReportGeneratedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        builder.AppendLine(Strings.Format("Diag_ReportTarget", $"{_profile.Name} ({HostAddress.FormatTarget(_profile.Username, _profile.Host, _profile.Port)})"));
        if (_profile.JumpHostProfileId is not null)
        {
            builder.AppendLine(Strings.Get("Diag_ReportViaJump"));
        }
        builder.AppendLine(new('-', 48));
        foreach (DiagnosticStepItemViewModel step in Steps)
        {
            string status = step.Status switch
            {
                DiagnosticStepStatus.Success => Strings.Get("Diag_ReportStatusPassed"),
                DiagnosticStepStatus.Warning => Strings.Get("Diag_ReportStatusWarning"),
                DiagnosticStepStatus.Failed => Strings.Get("Diag_ReportStatusFailed"),
                DiagnosticStepStatus.Skipped => Strings.Get("Diag_ReportStatusSkipped"),
                _ => Strings.Get("Diag_ReportStatusNotRun")
            };
            builder.Append($"{step.DisplayName,-24} [{status}]");
            if (step.ElapsedMs is { } ms)
            {
                builder.Append($" {ms}ms");
            }
            builder.AppendLine();
            if (!string.IsNullOrEmpty(step.Detail))
            {
                builder.AppendLine($"    {step.Detail}");
            }
        }
        builder.AppendLine(new('-', 48));
        if (HasIssue)
        {
            builder.AppendLine(Strings.Format("Diag_ReportIssue", IssueTitle));
            if (!string.IsNullOrEmpty(IssueDescription))
            {
                builder.AppendLine(IssueDescription);
            }
        }
        else
        {
            builder.AppendLine(Strings.Get("Diag_ReportNoIssue"));
        }
        if (Suggestions.Count > 0)
        {
            builder.AppendLine();
            for (int i = 0; i < Suggestions.Count; i++)
            {
                builder.AppendLine(Strings.Format("Diag_ReportSuggestion", i + 1, Suggestions[i]));
            }
        }
        if (HasPacketTrace)
        {
            // 报告是拿去给别人看的:轨迹只有元信息(没有内容,我们发出的认证报文连长度也没有),整段附上。
            builder.AppendLine(new('-', 48));
            builder.Append(BuildPacketTraceText());
        }
        return builder.ToString();
    }
}
