using System.Globalization;
using System.Text;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.ViewModels;

/// <summary>连接信息面板上的一块(对端、协商结果、认证、计时、流量与重协商)。</summary>
/// <param name="Title">块标题。</param>
/// <param name="Fields">一行一项。</param>
public sealed record ConnectionInfoSection(string Title, IReadOnlyList<ConnectionInfoField> Fields);

/// <summary>一项:名字 + 值。</summary>
/// <param name="Label">名字(界面语言)。</param>
/// <param name="Value">值(协议名、数字原样,不翻译)。</param>
public sealed record ConnectionInfoField(string Label, string Value);

/// <summary>通道表的一行。</summary>
public sealed record ConnectionInfoChannelRow(string Id, string Type, string Phase, string Age, string Sent, string Received);

/// <summary>经过的一跳(出站代理或跳板)。</summary>
/// <param name="Index">第几跳(从近到远,1 起)。</param>
/// <param name="Target">这一跳(<c>用户@主机:端口</c>;代理是 <c>socks5 主机:端口</c>)。</param>
/// <param name="Summary">算法摘要:版本 · 密钥交换 · 主机密钥 · 加密 · 认证;代理一行写「出站代理」。</param>
/// <param name="IsDown">这一跳的连接已经断了。</param>
public sealed record ConnectionInfoHopRow(int Index, string Target, string Summary, bool IsDown);

/// <summary>
/// SSH 连接信息面板(会话标签右键 → 连接信息):把库已经交出来的那些只读属性按块摆出来,能刷新、能整份复制成文本。
/// </summary>
/// <remarks>
/// <para>
/// 数据源是一个回调而不是某个连接对象:标签重连之后会话 id 换了,回调每次按标签此刻的会话去取,
/// 刷新自然跟上新连接;标签没连着时回调给 <see langword="null" />,面板如实说「没有连着」。
/// </para>
/// <para>
/// 不发任何探测(见 <see cref="SshConnectionDetails" />):排障时原先只能开「连接诊断」重新连一遍去探,
/// 而这条连接到底谈成了什么、慢在哪一步,库早就知道。
/// </para>
/// </remarks>
public sealed class SshConnectionInfoViewModel : ReactiveObject
{
    private static readonly TimeSpan CopyFeedbackDuration = TimeSpan.FromSeconds(1.5);

    private readonly Func<SshConnectionDetails?> _source;
    private IDisposable? _copyFeedbackReset;

    /// <param name="sessionName">会话的名字(标题栏上那枚标识)。</param>
    /// <param name="source">取此刻的连接信息;没连着时给 <see langword="null" />。</param>
    public SshConnectionInfoViewModel(string sessionName, Func<SshConnectionDetails?> source)
    {
        SessionName = sessionName;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        RefreshCommand = ReactiveCommand.Create(Refresh);
        CopyCommand = ReactiveCommand.CreateFromTask(CopyAsync, this.WhenAnyValue(x => x.HasDetails));
        Refresh();
    }

    /// <summary>设计期与窗口无参构造用的空面板。</summary>
    public SshConnectionInfoViewModel()
        : this(string.Empty, static () => null)
    {
    }

    /// <summary>会话的名字。</summary>
    public string SessionName { get; }

    /// <summary>最近一次取到的快照;没连着时为 <see langword="null" />。</summary>
    public SshConnectionDetails? Details
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasDetails));
            this.RaisePropertyChanged(nameof(IsUnavailable));
        }
    }

    /// <summary>取到了连接信息。</summary>
    public bool HasDetails => Details is not null;

    /// <summary>这条会话现在没有连着(或不是 SSH 会话)。</summary>
    public bool IsUnavailable => Details is null;

    /// <summary>标题栏下面的副标题:目标 + 取快照的时刻。</summary>
    public string Subtitle
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>键值块。</summary>
    public IReadOnlyList<ConnectionInfoSection> Sections
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

    /// <summary>通道块的标题(带条数)。</summary>
    public string ChannelsTitle
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>开着的通道。</summary>
    public IReadOnlyList<ConnectionInfoChannelRow> Channels
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasNoChannels));
        }
    } = [];

    /// <summary>一条通道都没有(连上了但还没开 shell,或者都关了)。</summary>
    public bool HasNoChannels => HasDetails && Channels.Count == 0;

    /// <summary>经过的代理与跳板,从近到远。</summary>
    public IReadOnlyList<ConnectionInfoHopRow> Route
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasRoute));
        }
    } = [];

    /// <summary>经过了代理或跳板(否则整块不显示)。</summary>
    public bool HasRoute => Route.Count > 0;

    /// <summary>重新取一份快照。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>把整份信息复制成文本(与面板上摆的是同一份)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyCommand { get; }

    /// <summary>写系统剪贴板的回调;由窗口注入(视图模型层拿不到 TopLevel)。</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>刚复制过:按钮上短暂换成「已复制」的勾。</summary>
    public bool Copied
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>重取一份并重排各块。</summary>
    public void Refresh()
    {
        SshConnectionDetails? details;
        try
        {
            details = _source();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // 取的那一刻会话正好在拆:与没连着同一个结论。
            details = null;
        }
        Details = details;
        if (details is null)
        {
            Subtitle = SessionName;
            Sections = [];
            ChannelsTitle = string.Empty;
            Channels = [];
            Route = [];
            return;
        }
        Subtitle = Strings.Format("SshInfo_Subtitle", details.Target, details.CapturedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        Sections = BuildSections(details);
        Channels = [.. details.Channels.Select(channel => ChannelRow(channel, details.CapturedAt))];
        ChannelsTitle = Strings.Format("SshInfo_SecChannels", Channels.Count);
        Route = BuildRoute(details);
    }

    /// <summary>整份信息的纯文本:块标题加缩进的「名字: 值」,通道与跳板各一行。贴给同事、发给设备厂商用。</summary>
    public string BuildText()
    {
        if (Details is null)
        {
            return string.Empty;
        }
        StringBuilder text = new();
        text.Append(Strings.Get("SshInfo_Title")).Append(" — ").AppendLine(Subtitle);
        foreach (ConnectionInfoSection section in Sections)
        {
            text.AppendLine().Append('[').Append(section.Title).AppendLine("]");
            foreach (ConnectionInfoField field in section.Fields)
            {
                text.Append("  ").Append(field.Label).Append(": ").AppendLine(field.Value);
            }
        }
        text.AppendLine().Append('[').Append(ChannelsTitle).AppendLine("]");
        if (Channels.Count == 0)
        {
            text.Append("  ").AppendLine(Strings.Get("SshInfo_NoChannels"));
        }
        foreach (ConnectionInfoChannelRow row in Channels)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"  #{row.Id}  {row.Type}  {row.Phase}  {row.Age}  {Strings.Get("SshInfo_Sent")} {row.Sent}  {Strings.Get("SshInfo_Received")} {row.Received}");
            text.AppendLine();
        }
        if (Route.Count > 0)
        {
            text.AppendLine().Append('[').Append(Strings.Get("SshInfo_SecRoute")).AppendLine("]");
            foreach (ConnectionInfoHopRow hop in Route)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {hop.Index}. {hop.Target}  {hop.Summary}");
                if (hop.IsDown)
                {
                    text.Append("  (").Append(Strings.Get("SshInfo_HopDown")).Append(')');
                }
                text.AppendLine();
            }
        }
        return text.ToString();
    }

    private async Task CopyAsync()
    {
        if (CopyToClipboard is not { } copy || !HasDetails)
        {
            return;
        }
        await copy(BuildText()).ConfigureAwait(true);
        Copied = true;
        _copyFeedbackReset?.Dispose();
        _copyFeedbackReset = DispatcherTimer.RunOnce(() => Copied = false, CopyFeedbackDuration);
    }

    private static List<ConnectionInfoSection> BuildSections(SshConnectionDetails d)
    {
        SshAlgorithmDetails a = d.Algorithms;
        List<ConnectionInfoField> peer =
        [
            new(Strings.Get("SshInfo_State"), Strings.Get(d.IsAlive ? "SshInfo_StateAlive" : "SshInfo_StateClosed")),
            new(Strings.Get("SshInfo_PeerVersion"), Text(d.PeerVersion)),
            new(Strings.Get("SshInfo_HostKey"), HostKeyText(d.HostKey)),
            new(Strings.Get("SshInfo_Fingerprint"), d.HostKey.Fingerprint),
            new(Strings.Get("SshInfo_SessionId"), Text(d.SessionId)),
            new(Strings.Get("SshInfo_Ping"), YesNo(d.PeerSupportsPing)),
        ];
        List<ConnectionInfoField> negotiated =
        [
            new(Strings.Get("Ssh_AlgoKindKex"), a.KeyExchange),
            new(Strings.Get("Ssh_AlgoKindHostKey"), a.HostKey),
            new(Strings.Get("Ssh_AlgoKindEncryption"), TwoWay(a.EncryptionClientToServer, a.EncryptionServerToClient)),
            new(Strings.Get("Ssh_AlgoKindMac"), a is { MacClientToServer: null, MacServerToClient: null }
                ? Strings.Get("SshInfo_MacAead")
                : TwoWay(a.MacClientToServer ?? Strings.Get("SshInfo_MacAead"), a.MacServerToClient ?? Strings.Get("SshInfo_MacAead"))),
            new(Strings.Get("SshInfo_Compression"), TwoWay(a.CompressionClientToServer, a.CompressionServerToClient)),
            new(Strings.Get("SshInfo_StrictKex"), YesNo(a.StrictKeyExchange)),
            new(Strings.Get("SshInfo_ExtInfo"), YesNo(a.ExtensionNegotiation)),
        ];
        List<ConnectionInfoField> auth =
        [
            new(Strings.Get("SshInfo_AuthMethod"), Text(d.AuthenticationMethod)),
            new(Strings.Get("SshInfo_ServerSigAlgs"), d.ServerSignatureAlgorithms.Count == 0
                ? Strings.Get("SshInfo_NotAnnounced")
                : string.Join(", ", d.ServerSignatureAlgorithms)),
        ];
        List<ConnectionInfoField> timing =
        [
            new(Strings.Get("SshInfo_TimeDialing"), Duration(d.Timings.Dialing)),
            new(Strings.Get("SshInfo_TimeVersion"), Duration(d.Timings.VersionExchange)),
            new(Strings.Get("SshInfo_TimeKex"), Duration(d.Timings.KeyExchange)),
            new(Strings.Get("SshInfo_TimeAuth"), Duration(d.Timings.Authentication)),
            new(Strings.Get("SshInfo_TimeTotal"), Duration(d.Timings.Total)),
            new(Strings.Get("SshInfo_RoundTrip"), d.LastRoundTrip is { } rtt ? Duration(rtt) : Strings.Get("SshInfo_NotMeasured")),
        ];
        List<ConnectionInfoField> traffic =
        [
            new(Strings.Get("SshInfo_Sent"), Strings.Format("SshInfo_BytesPackets", TransferItemViewModel.FormatBytes(d.BytesSent), d.PacketsSent)),
            new(Strings.Get("SshInfo_Received"), Strings.Format("SshInfo_BytesPackets", TransferItemViewModel.FormatBytes(d.BytesReceived), d.PacketsReceived)),
            new(Strings.Get("SshInfo_RekeyCount"), d.RekeyCount.ToString(CultureInfo.InvariantCulture)),
        ];
        if (d.LastRekey is { } rekey)
        {
            traffic.Add(new(Strings.Get("SshInfo_LastRekey"), RekeyText(rekey)));
            traffic.Add(new(Strings.Get("SshInfo_LastRekeyDuration"), rekey.Duration is { } took ? Duration(took) : "—"));
        }
        traffic.Add(new(Strings.Get("SshInfo_KeystrokeTiming"), !d.ObscureKeystrokeTiming
            ? Strings.Get("SshInfo_KeystrokeOff")
            : Strings.Get(d.PeerSupportsPing ? "SshInfo_KeystrokeOn" : "SshInfo_KeystrokeBatchOnly")));
        return
        [
            new(Strings.Get("SshInfo_SecPeer"), peer),
            new(Strings.Get("SshInfo_SecAlgorithms"), negotiated),
            new(Strings.Get("SshInfo_SecAuth"), auth),
            new(Strings.Get("SshInfo_SecTiming"), timing),
            new(Strings.Get("SshInfo_SecTraffic"), traffic),
        ];
    }

    private static List<ConnectionInfoHopRow> BuildRoute(SshConnectionDetails d)
    {
        List<ConnectionInfoHopRow> rows = [];
        if (d.Proxy is { } proxy)
        {
            rows.Add(new(1, proxy, Strings.Get("SshInfo_Proxy"), IsDown: false));
        }
        foreach (SshHopDetails hop in d.Hops)
        {
            string summary = string.Join(" · ",
                new[] { hop.PeerVersion, hop.KeyExchange, hop.HostKey, hop.Encryption, hop.AuthenticationMethod }
                    .Where(static part => !string.IsNullOrEmpty(part)));
            rows.Add(new(rows.Count + 1, hop.Target, summary, !hop.IsAlive));
        }
        return rows;
    }

    private static ConnectionInfoChannelRow ChannelRow(SshChannelDetails channel, DateTimeOffset now) => new(
        channel.Id.ToString(CultureInfo.InvariantCulture),
        channel.Type,
        channel.Phase switch
        {
            SshChannelPhase.Opening => Strings.Get("SshInfo_PhaseOpening"),
            SshChannelPhase.Open => Strings.Get("SshInfo_PhaseOpen"),
            SshChannelPhase.HalfClosed => Strings.Get("SshInfo_PhaseHalfClosed"),
            _ => Strings.Get("SshInfo_PhaseClosing"),
        },
        channel.OpenedAt is { } opened ? Age(now - opened) : "—",
        TransferItemViewModel.FormatBytes(channel.BytesSent),
        TransferItemViewModel.FormatBytes(channel.BytesReceived));

    private static string RekeyText(SshRekeyDetails rekey)
    {
        string observed = rekey.Reason == SshRekeyReason.Interval
            ? Duration(TimeSpan.FromMilliseconds(rekey.Observed))
            : rekey.Observed.ToString("N0", CultureInfo.InvariantCulture);
        string threshold = rekey.Reason == SshRekeyReason.Interval
            ? Duration(TimeSpan.FromMilliseconds(rekey.Threshold))
            : rekey.Threshold.ToString("N0", CultureInfo.InvariantCulture);
        return rekey.Reason switch
        {
            SshRekeyReason.Peer => Strings.Get("SshInfo_RekeyPeer"),
            SshRekeyReason.Requested => Strings.Get("SshInfo_RekeyRequested"),
            SshRekeyReason.Bytes => Strings.Format("SshInfo_RekeyBytes", observed, threshold),
            SshRekeyReason.Packets => Strings.Format("SshInfo_RekeyPackets", observed, threshold),
            SshRekeyReason.Interval => Strings.Format("SshInfo_RekeyInterval", observed, threshold),
            SshRekeyReason.PacketHardLimit => Strings.Format("SshInfo_RekeyHardLimit", observed, threshold),
            _ => Strings.Get("SshInfo_RekeyUnknown"),
        };
    }

    private static string HostKeyText(SshHostKeyDetails key)
    {
        string text = Strings.Format("SshInfo_HostKeyValue", key.Type, key.Bits);
        return key.IsCertificate ? $"{text} · {Strings.Get("SshInfo_HostKeyCertificate")}" : text;
    }

    /// <summary>两个方向一样就只写一个;不一样写成「发 a · 收 b」。</summary>
    private static string TwoWay(string clientToServer, string serverToClient) =>
        string.Equals(clientToServer, serverToClient, StringComparison.Ordinal)
            ? clientToServer
            : Strings.Format("SshInfo_TwoWay", clientToServer, serverToClient);

    private static string YesNo(bool value) => Strings.Get(value ? "SshInfo_Yes" : "SshInfo_No");

    private static string Text(string value) => string.IsNullOrEmpty(value) ? "—" : value;

    /// <summary>毫秒级的耗时:不到一秒写毫秒,不到一分钟写秒(两位小数),再长写时分秒。</summary>
    internal static string Duration(TimeSpan value) =>
        value.TotalSeconds < 1 ? $"{Math.Max(0, (int)Math.Round(value.TotalMilliseconds))} ms"
        : value.TotalMinutes < 1 ? value.TotalSeconds.ToString("0.00 's'", CultureInfo.InvariantCulture)
        : Age(value);

    /// <summary>开了多久:<c>m:ss</c>,一小时以上 <c>h:mm:ss</c>。</summary>
    private static string Age(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }
}
