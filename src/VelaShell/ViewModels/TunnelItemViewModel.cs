using System.Globalization;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>隧道列表项视图模型:包装共享的 <see cref="TunnelInfo" />,向界面暴露路由、状态、流量等只读展示属性。</summary>
public class TunnelItemViewModel : ReactiveObject
{
    /// <summary>
    /// 展开的连接列表最多列几条。动态转发挂着浏览器时一下就是几十条,全列出来面板就被撑成一长条;
    /// 多出来的写一句「还有 N 条未列出」。
    /// </summary>
    public const int MaxListedConnections = 20;

    private readonly TunnelInfo _tunnelInfo;

    /// <summary>包装一条隧道的共享运行时信息。</summary>
    /// <param name="tunnelInfo">隧道信息(与服务侧是同一个实例,读数由服务同步进来)。</param>
    public TunnelItemViewModel(TunnelInfo tunnelInfo)
    {
        _tunnelInfo = tunnelInfo ?? throw new ArgumentNullException(nameof(tunnelInfo));
        ToggleConnectionsCommand = ReactiveCommand.Create(() =>
        {
            IsConnectionListExpanded = !IsConnectionListExpanded;
        });
    }

    /// <summary>隧道唯一标识,用于在面板中定位与去重。</summary>
    public Guid Id => _tunnelInfo.Id;

    /// <summary>原始配置,编辑/重启时用来预填表单与重建转发。</summary>
    public TunnelConfig Config => _tunnelInfo.Config;

    /// <summary>该隧道所在的 SSH 会话(面板按服务器归组时用来定位)。</summary>
    public Guid SessionId => _tunnelInfo.SessionId;

    /// <summary>显示名称:非空白别名保留原值,空白时回退为本地化的简洁类型名称。</summary>
    public string Name => string.IsNullOrWhiteSpace(_tunnelInfo.Config.Name)
                              ? TunnelType switch
                              {
                                  TunnelType.RemoteForward => Strings.Get("Tunnel_FallbackRemote"),
                                  TunnelType.DynamicForward => Strings.Get("Tunnel_FallbackDynamic"),
                                  _ => Strings.Get("Tunnel_FallbackLocal")
                              }
                              : _tunnelInfo.Config.Name;

    /// <summary>隧道类型(本地/远程/动态转发)。</summary>
    public TunnelType TunnelType => _tunnelInfo.Config.Type;

    /// <summary>本地绑定主机。</summary>
    public string LocalHost => _tunnelInfo.Config.LocalHost;

    /// <summary>本地绑定端口。</summary>
    public uint LocalPort => _tunnelInfo.Config.LocalPort;

    /// <summary>远程目标主机。</summary>
    public string RemoteHost => _tunnelInfo.Config.RemoteHost;

    /// <summary>远程目标端口。</summary>
    public uint RemotePort => _tunnelInfo.Config.RemotePort;

    /// <summary>隧道创建时间(UTC),用于计算运行时长。</summary>
    public DateTime CreatedAt => _tunnelInfo.CreatedAt;

    /// <summary>语义路由描述:从角色视角描述转发方向与端点。</summary>
    public string DisplayRoute => TunnelType switch
    {
        TunnelType.RemoteForward => Strings.Format("Tunnel_RouteRemote", HostAddress.Bracket(LocalHost), LocalPort, RemotePort),
        TunnelType.DynamicForward => Strings.Format("Tunnel_RouteDynamic", HostAddress.Bracket(LocalHost), LocalPort),
        _ => Strings.Format("Tunnel_RouteLocal", HostAddress.Bracket(LocalHost), LocalPort, HostAddress.Bracket(RemoteHost), RemotePort)
    };

    /// <summary>类型标签(本地化徽标:Local/Remote/Dynamic)。</summary>
    public string TypeBadge => TunnelType switch
    {
        TunnelType.RemoteForward => Strings.Get("Tunnel_BadgeRemote"),
        TunnelType.DynamicForward => Strings.Get("Tunnel_BadgeDynamic"),
        _ => Strings.Get("Tunnel_BadgeLocal")
    };

    /// <summary>精确端点摘要(紧凑等宽):本地转发为本地→远端,远程转发颠倒顺序,动态仅显示SOCKS5端点。</summary>
    public string EndpointSummary => TunnelType switch
    {
        TunnelType.RemoteForward => $"{HostAddress.Bracket(RemoteHost)}:{RemotePort} → {HostAddress.Bracket(LocalHost)}:{LocalPort}",
        TunnelType.DynamicForward => $"{HostAddress.Bracket(LocalHost)}:{LocalPort} (SOCKS5)",
        _ => $"{HostAddress.Bracket(LocalHost)}:{LocalPort} → {HostAddress.Bracket(RemoteHost)}:{RemotePort}"
    };

    /// <summary>
    /// 状态直接读写共享的 <see cref="TunnelInfo" />:服务侧(会话断开、停止全部)
    /// 改的状态,界面经 <see cref="RefreshLive" /> 就能看到,不再各存一份而彼此失联。
    /// </summary>
    public TunnelStatus Status
    {
        get => _tunnelInfo.Status;
        set
        {
            if (_tunnelInfo.Status == value)
            {
                return;
            }
            _tunnelInfo.Status = value;
            RaiseLiveChanged();
        }
    }

    /// <summary>累计转发字节数(由服务侧写入共享 TunnelInfo)。</summary>
    public long BytesTransferred => _tunnelInfo.BytesTransferred;

    /// <summary>累计流量的人类可读格式(如 1.2 MB)。</summary>
    public string FormattedBytes => FormatBytes(BytesTransferred);

    /// <summary>累计接受的连接数(由服务侧写入共享 TunnelInfo)。</summary>
    public int TotalConnections => _tunnelInfo.TotalConnections;

    /// <summary>当前仍在传输的连接数。</summary>
    public int ActiveConnections => _tunnelInfo.ActiveConnections;

    /// <summary>
    /// 流量统计行(设计 fuXS7 预留的说明行):没有连接过就直说,有在传的连接则把
    /// 并发数一并点出 —— "3 连接"和"3 连接(2 在传)"对排查问题是两回事。
    /// </summary>
    public string StatsText =>
        TotalConnections == 0
            ? Strings.Get("Tunnel_StatsNone")
            : ActiveConnections > 0
                ? Strings.Format("Tunnel_StatsLive", TotalConnections, ActiveConnections, FormattedBytes)
                : Strings.Format("Tunnel_Stats", TotalConnections, FormattedBytes);

    /// <summary>限速(每个方向,字节 / 秒);<see langword="null" /> 不限。</summary>
    public long? MaxBytesPerSecond => _tunnelInfo.Config.MaxBytesPerSecond;

    /// <summary>
    /// 速率与限速行:运行中显示两个方向此刻的速率(最近三个整秒的平均),设了限速时一并写出;
    /// 停着时只在设了限速时写一句限多少。都没有时为空、整行隐藏。
    /// </summary>
    public string? TrafficText
    {
        get
        {
            string? limit = MaxBytesPerSecond is { } bytesPerSecond
                ? Strings.Format("Tunnel_RateLimit", FormatBytes(bytesPerSecond))
                : null;
            if (!IsActive)
            {
                return limit;
            }
            string rate = $"↑ {FormatRate(_tunnelInfo.SentBytesPerSecond)}  ↓ {FormatRate(_tunnelInfo.ReceivedBytesPerSecond)}";
            return limit is null ? rate : $"{rate} · {limit}";
        }
    }

    /// <summary>速率与限速行要不要显示。</summary>
    public bool HasTrafficText => TrafficText is not null;

    /// <summary>速率行的悬停说明:设了限速才有(说清楚是按方向、全部连接共用)。</summary>
    public string? TrafficToolTip => MaxBytesPerSecond is null ? null : Strings.Get("Tunnel_RateLimitTip");

    /// <summary>此刻正在搬的连接数(取自连接快照)。</summary>
    public int LiveConnectionCount => _tunnelInfo.Connections.Count;

    /// <summary>「当前连接」那一行要不要显示:运行中、且有连接在搬。</summary>
    public bool HasConnections => IsActive && LiveConnectionCount > 0;

    /// <summary>连接列表是否展开。默认收起 —— 多数时候看一眼速率就够了。</summary>
    public bool IsConnectionListExpanded
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseConnectionsChanged();
        }
    }

    /// <summary>展开 / 收起连接列表。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleConnectionsCommand { get; }

    /// <summary>展开开关上的字:「当前连接 3 条」。</summary>
    public string ConnectionsToggleText => Strings.Format("Tunnel_ConnectionsToggle", LiveConnectionCount);

    /// <summary>列表真的摆出来:展开了、且有连接。</summary>
    public bool IsConnectionListVisible => HasConnections && IsConnectionListExpanded;

    /// <summary>
    /// 展开时列出的连接(至多 <see cref="MaxListedConnections" /> 条):来源 → 目标、开了多久、两个方向各搬了多少。
    /// 收起时为空 —— 没人看的那几十行不必每秒重算一遍。
    /// </summary>
    public IReadOnlyList<TunnelConnectionRow> ConnectionRows
    {
        get
        {
            if (!IsConnectionListVisible)
            {
                return [];
            }
            DateTime now = DateTime.UtcNow;
            return
            [
                .. _tunnelInfo.Connections.Take(MaxListedConnections).Select(c => new TunnelConnectionRow(
                    $"{c.Source ?? Strings.Get("Tunnel_ConnectionSourceUnknown")} → {c.Target}",
                    Strings.Format("Tunnel_ConnectionDetail", FormatDuration(now - c.StartedAtUtc), FormatBytes(c.BytesSent), FormatBytes(c.BytesReceived))))
            ];
        }
    }

    /// <summary>超出 <see cref="MaxListedConnections" /> 没列出来的条数。</summary>
    public int HiddenConnectionCount => IsConnectionListVisible ? Math.Max(0, LiveConnectionCount - MaxListedConnections) : 0;

    /// <summary>有没列出来的连接。</summary>
    public bool HasHiddenConnections => HiddenConnectionCount > 0;

    /// <summary>「还有 N 条未列出」。</summary>
    public string HiddenConnectionsText => Strings.Format("Tunnel_ConnectionsMore", HiddenConnectionCount);

    /// <summary>承载会话掉线后是否自动重建这条隧道。</summary>
    public bool AutoReconnect => _tunnelInfo.Config.AutoReconnect;

    /// <summary>程序启动时是否自动建立这条隧道。</summary>
    public bool AutoStart => _tunnelInfo.Config.AutoStart;

    /// <summary>
    /// 这条隧道是被用户按停的,而不是被掉线带停的。自动恢复据此放过它 ——
    /// 「掉线后自动重连」说的是替用户扛住网络抖动,不是把他刚按下的停止键撤销掉。
    /// 重建出来的条目是新对象,自然回到 false,所以手动启动后自动恢复照常生效。
    /// </summary>
    public bool StoppedByUser { get; set; }

    /// <summary>隧道是否处于活动状态。</summary>
    public bool IsActive => Status == TunnelStatus.Active;

    /// <summary>
    /// 正在启动(含先在后台连上服务器)。启动与停止都要走网络、要等一会儿:这段时间里
    /// 启停键换成一圈转着的环、行内按键全部点不动,免得用户以为没点上又连点几下。
    /// </summary>
    public bool IsStarting
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseBusyChanged();
        }
    }

    /// <summary>正在停止;与 <see cref="IsStarting" /> 同理。</summary>
    public bool IsStopping
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseBusyChanged();
        }
    }

    /// <summary>启动或停止正在进行中。</summary>
    public bool IsBusy => IsStarting || IsStopping;

    /// <summary>显示「停止」键:运行中且没有正在进行的启停。</summary>
    public bool ShowStopButton => IsActive && !IsBusy;

    /// <summary>显示「启动」键:已停止且没有正在进行的启停。</summary>
    public bool ShowStartButton => !IsActive && !IsBusy;

    /// <summary>编辑键可用:只有停着、且不在启停途中的隧道能改配置。</summary>
    public bool CanEdit => !IsActive && !IsBusy;

    /// <summary>启停进行中那圈环的提示文字。</summary>
    public string BusyText => IsStopping ? Strings.Get("Tunnel_Stopping") : Strings.Get("Tunnel_Starting");

    /// <summary>编辑按钮提示:活动隧道不可编辑。</summary>
    public string EditToolTip => Strings.Get(IsActive ? "Tunnel_EditDisabledTip" : "Tunnel_EditTip");

    /// <summary>换语言后让绑定重取 C# 侧拼的文案(端点摘要、统计、编辑提示、状态)。</summary>
    public void RefreshLocalizedText()
    {
        this.RaisePropertyChanged(nameof(EndpointSummary));
        this.RaisePropertyChanged(nameof(StatsText));
        this.RaisePropertyChanged(nameof(EditToolTip));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(BusyText));
        this.RaisePropertyChanged(nameof(TrafficText));
        this.RaisePropertyChanged(nameof(TrafficToolTip));
        RaiseConnectionsChanged();
    }

    /// <summary>最近一次转发通道错误(目标拒绝连接等),由服务写入共享 TunnelInfo。</summary>
    public string? LastError => _tunnelInfo.LastError;

    /// <summary>是否存在最近一次错误。</summary>
    public bool HasError => !string.IsNullOrEmpty(_tunnelInfo.LastError);

    /// <summary>状态行:启停途中显示「正在启动/停止…」,活动中显示运行时长,否则显示状态文字(设计 B3Rth tunI1Stats)。</summary>
    public string StatusText => IsBusy
        ? BusyText
        : Status switch
        {
            TunnelStatus.Active => Strings.Format("Msg_TunnelRunning", FormatUptime(DateTime.UtcNow - CreatedAt)),
            TunnelStatus.Error => Strings.Get("Msg_ErrorOccurred"),
            _ => Strings.Get("Msg_Stopped")
        };

    /// <summary>由面板的时钟周期性调用:刷新运行时长、透传服务侧的状态/错误变化。</summary>
    public void RefreshLive() => RaiseLiveChanged();

    /// <summary>
    /// 面板开着时每秒调一次:只刷流量相关的几行(累计、速率、连接列表、运行时长),
    /// 不碰状态与按键 —— 那些仍随 5 秒一拍的 <see cref="RefreshLive" /> 走。
    /// </summary>
    public void RefreshTraffic()
    {
        this.RaisePropertyChanged(nameof(BytesTransferred));
        this.RaisePropertyChanged(nameof(FormattedBytes));
        this.RaisePropertyChanged(nameof(TotalConnections));
        this.RaisePropertyChanged(nameof(ActiveConnections));
        this.RaisePropertyChanged(nameof(StatsText));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(TrafficText));
        this.RaisePropertyChanged(nameof(HasTrafficText));
        RaiseConnectionsChanged();
    }

    private void RaiseConnectionsChanged()
    {
        this.RaisePropertyChanged(nameof(LiveConnectionCount));
        this.RaisePropertyChanged(nameof(HasConnections));
        this.RaisePropertyChanged(nameof(ConnectionsToggleText));
        this.RaisePropertyChanged(nameof(IsConnectionListVisible));
        this.RaisePropertyChanged(nameof(ConnectionRows));
        this.RaisePropertyChanged(nameof(HiddenConnectionCount));
        this.RaisePropertyChanged(nameof(HasHiddenConnections));
        this.RaisePropertyChanged(nameof(HiddenConnectionsText));
    }

    private void RaiseBusyChanged()
    {
        this.RaisePropertyChanged(nameof(IsBusy));
        this.RaisePropertyChanged(nameof(ShowStopButton));
        this.RaisePropertyChanged(nameof(ShowStartButton));
        this.RaisePropertyChanged(nameof(CanEdit));
        this.RaisePropertyChanged(nameof(BusyText));
        this.RaisePropertyChanged(nameof(StatusText));
    }

    private void RaiseLiveChanged()
    {
        this.RaisePropertyChanged(nameof(Status));
        this.RaisePropertyChanged(nameof(IsActive));
        this.RaisePropertyChanged(nameof(ShowStopButton));
        this.RaisePropertyChanged(nameof(ShowStartButton));
        this.RaisePropertyChanged(nameof(CanEdit));
        this.RaisePropertyChanged(nameof(EditToolTip));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(LastError));
        this.RaisePropertyChanged(nameof(HasError));
        this.RaisePropertyChanged(nameof(BytesTransferred));
        this.RaisePropertyChanged(nameof(FormattedBytes));
        this.RaisePropertyChanged(nameof(TotalConnections));
        this.RaisePropertyChanged(nameof(ActiveConnections));
        this.RaisePropertyChanged(nameof(StatsText));
        this.RaisePropertyChanged(nameof(TrafficText));
        this.RaisePropertyChanged(nameof(HasTrafficText));
        this.RaisePropertyChanged(nameof(TrafficToolTip));
        RaiseConnectionsChanged();
    }

    /// <summary>速率的写法:<c>1.2 KB/s</c>(单位与累计字节数同一套)。</summary>
    public static string FormatRate(double bytesPerSecond) =>
        $"{FormatBytes((long)Math.Round(Math.Max(0, bytesPerSecond)))}/s";

    /// <summary>
    /// 一条连接开了多久:<c>42s</c> / <c>3m 05s</c> / <c>1h 02m</c>。比隧道的运行时长细一档 ——
    /// 连接大多是短的,「不到 1 分钟」对它们等于什么也没说。
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }
        return duration.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}h {duration.Minutes:00}m")
            : duration.TotalMinutes >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s")
                : string.Create(CultureInfo.InvariantCulture, $"{duration.Seconds}s");
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero)
        {
            uptime = TimeSpan.Zero;
        }
        if (uptime.TotalHours >= 1)
        {
            return Strings.Format("Msg_UptimeHours", (int)uptime.TotalHours, uptime.Minutes);
        }
        if (uptime.TotalMinutes >= 1)
        {
            return Strings.Format("Msg_UptimeMinutes", (int)uptime.TotalMinutes);
        }
        return Strings.Get("Msg_UptimeUnderMinute");
    }

    /// <summary>将字节数格式化为带单位(B/KB/MB/GB/TB)的可读字符串。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes == 0)
        {
            return "0 B";
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = (int)Math.Floor(Math.Log(bytes, 1024));
        i = Math.Min(i, units.Length - 1);
        return $"{bytes / Math.Pow(1024, i):F1} {units[i]}";
    }
}

/// <summary>隧道「当前连接」列表里的一行。</summary>
/// <param name="Route">来源 → 目标。</param>
/// <param name="Detail">开了多久、两个方向各搬了多少。</param>
public sealed record TunnelConnectionRow(string Route, string Detail);