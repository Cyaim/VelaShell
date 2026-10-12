// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/design/architecture.md §6.2;velashell-docs/zh/ssh/spec/05-connection.md §6.3

using System.Globalization;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Session;

/// <summary>建立一条 SSH 连接需要的一切。</summary>
public sealed record SshConnectionOptions
{
    /// <summary>用 <c>user@host:port</c> 形式构造。</summary>
    /// <param name="target">
    /// 形如 <c>root@10.0.0.1:22</c>、<c>joe@example.com</c>、<c>example.com</c>。
    /// IPv6 带端口要写成 <c>joe@[::1]:22</c>；不带方括号的 IPv6（<c>joe@2001:db8::1</c>）整段是地址，端口取 22。
    /// </param>
    public SshConnectionOptions(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        (UserName, Host, Port) = ParseTarget(target);
    }

    /// <summary>分开给用户名与主机。</summary>
    public SshConnectionOptions(string userName, string host, int port = 22)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        UserName = userName;
        Host = host;
        Port = port;
    }

    /// <summary>用户名。</summary>
    public string UserName { get; }

    /// <summary>主机。</summary>
    public string Host { get; }

    /// <summary>端口。</summary>
    public int Port { get; }

    /// <summary>
    /// 凭据，<b>按尝试顺序</b>。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/04 §2.2〕<b>这就是全部，库不做任何隐式回退</b> ——
    /// 不自动读 <c>~/.ssh/id_*</c>，不自动连 ssh-agent。
    /// 要用它们就显式加进来（<c>SshPrivateKeyFile.LoadAsync</c> /
    /// <c>SshAgentClient.GetCredentialsAsync</c>）。
    /// </remarks>
    public IReadOnlyList<SshCredential> Credentials { get; init; } = [];

    /// <summary>主机密钥策略。</summary>
    /// <remarks>
    /// 默认是<b>按 <c>known_hosts</c> 且没见过就拒绝</b> ——
    /// 交互式客户端要换成带询问回调的那一种。
    /// 默认不能是「接受任何密钥」：那等于关掉中间人防护。
    /// </remarks>
    public IHostKeyPolicy HostKeyPolicy { get; init; } =
        new KnownHostsPolicy { UnknownHost = UnknownHostBehavior.Reject };

    /// <summary>拨号器。</summary>
    public ISshTransportDialer Dialer { get; init; } = TcpTransportDialer.Shared;

    /// <summary>外层连接的计时器（这条连接是另一条连接的跳板那一跳时）。</summary>
    internal SshConnectDeadline? OuterDeadline { get; init; }

    /// <summary>度量的 <c>host</c> 标签；<see langword="null"/> 时用 <see cref="Host"/>。经跳板时是最终目标（见 <see cref="SshMetrics"/>）。</summary>
    internal string? MetricsHost { get; init; }

    /// <summary>连接计时器用的时钟；连上之后保活、重协商与通道号复用延迟也用它。</summary>
    /// <remarks>只有测试会换成手动拨的时钟 —— 「停表时预算刚好用完」这种时刻靠真实时钟摆不出来。</remarks>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>算法清单。</summary>
    public SshAlgorithmSet Algorithms { get; init; } = SshAlgorithmSet.Default;

    /// <summary>通道限额。</summary>
    public SshConnectionLimits Limits { get; init; } = SshConnectionLimits.Default;

    /// <summary>
    /// 连接超时 —— <b>不含主机密钥裁决的时间</b>。
    /// </summary>
    /// <remarks>
    /// 裁决要弹窗问用户，而弹窗摆着的时间算进连接超时的话，
    /// 用户点完「信任」这一轮已经被判死，只能原地补连一次。
    /// 那个「补连一次」在两个计时分开之后就不必存在了。
    /// </remarks>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>主机密钥裁决的超时。默认无限 —— 等用户看指纹。</summary>
    public TimeSpan HostKeyDecisionTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// 认证超时。
    /// </summary>
    /// <remarks>默认两分钟 —— 用户可能要去掏手机看动态码。</remarks>
    public TimeSpan AuthenticationTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>保活策略。</summary>
    public SshKeepAlivePolicy KeepAlive { get; init; } = SshKeepAlivePolicy.Disabled;

    /// <summary>我们主动发起重协商的阈值。默认 1 GiB / 1 小时 / 2³¹ 个报文。</summary>
    /// <remarks>
    /// 与保活不同，<b>这一条默认是开着的</b> —— 它防的是 nonce 回绕那一类
    /// 灾难性后果，不是一个可选的优化。<see cref="SshRekeyPolicy.Disabled"/>
    /// 能关掉按字节与时长的主动发起，但关不掉报文数的硬线（<see cref="SshRekeyPolicy.MaximumPackets"/>），
    /// 接住对端发起的那一半也永远开着。
    /// </remarks>
    public SshRekeyPolicy Rekey { get; init; } = SshRekeyPolicy.Default;

    /// <summary>报文数的硬线（见 <c>SshConnection.RekeyHardPacketLimit</c>）。internal：只有用例需要把它调小。</summary>
    internal long RekeyHardPacketLimit { get; init; } = SshRekeyPolicy.MaximumPackets;

    /// <summary>阈值多久看一眼。</summary>
    /// <remarks>
    /// internal：阈值下限是 1 分钟 / 64 MiB / 1024 个报文，5 秒的粒度对它们足够，
    /// 又不至于让一条闲着的连接每秒都醒一次。<b>只有用例需要把它调小</b>，
    /// 否则一条验阈值的用例要干等 5 秒。
    /// </remarks>
    internal TimeSpan RekeyCheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>一次重协商最多等多久（见 <c>SshConnection.RekeyTimeout</c>）。</summary>
    /// <remarks>internal：同上，只有用例需要把它调小。</remarks>
    internal TimeSpan RekeyTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>报文旁路：每个收发的报文调一次（诊断面板、协议级排错）；<see langword="null"/>（默认）不启用。</summary>
    /// <remarks>见 <see cref="IPacketTap"/>：回调跑在收发循环上，要快；抛的异常被吞掉。跳板各跳要看的话，各自的连接参数里各设一个。</remarks>
    public IPacketTap? PacketTap { get; init; }

    /// <summary>
    /// 旁路拿不拿得到载荷。默认 <see langword="false"/>：只给方向、消息编号、长度、序号、通道号。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>打开之后，通道数据里有什么就带出什么</b>：终端里敲的口令（<c>sudo</c>）、私钥文件的内容（传输时）、转发的流量。
    /// 认证报文（50–79）的载荷无论如何都不给（velashell-docs/zh/ssh/spec/08 §9）。
    /// </remarks>
    public bool AllowPacketTapPayload { get; init; }

    /// <summary>服务端横幅的回调。<b>文本来自未认证的对端，是注入面。</b></summary>
    public Func<string, CancellationToken, ValueTask>? BannerHandler { get; init; }

    /// <summary>
    /// 服务端标着「一定要给用户看」（<c>always_display</c>）的调试消息（<c>SSH_MSG_DEBUG</c>，RFC 4253 §11.3）的回调；
    /// 文本已按对端文本清洗。<see langword="null"/>（默认）表示不交出。
    /// </summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/05 §八〕认证期间收到的在认证流程里依次交出，回调自己抛的照实交还（与 <see cref="BannerHandler"/> 一样）；
    /// 连上之后收到的在线程池上交出、不挡接收循环，回调抛的丢掉。不带 <c>always_display</c> 的不交出 —— 那是给排错用的。
    /// 曾经一律丢掉，RFC 说的是「应当展示」。
    /// </remarks>
    public Func<string, CancellationToken, ValueTask>? DebugMessageHandler { get; init; }

    /// <summary>
    /// 服务端在标识串<b>之前</b>发的前导行（法律声明、公告，RFC 4253 §4.2）的回调；有前导行时在版本交换之后调一次。
    /// <see langword="null"/>（默认）表示不交出。
    /// </summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/02 §三〕这些文本在企业环境里常有法律意义，库吞掉不合适；
    /// 但它们来自<b>还没验明身份</b>的对端（主机密钥都还没交换），默认往界面上打就是一个注入面 —— 所以默认不交出，
    /// 交出的是原文（行数与字节已经限过），展示之前由调用方清洗。回调自己抛的异常原样交还（spec/08 §2.1）。
    /// 曾经收集了却从没有交出去的路：有的设备只在这里打印使用声明，用户看不到。
    /// </remarks>
    public Func<IReadOnlyList<string>, CancellationToken, ValueTask>? PreAuthBannerHandler { get; init; }

    /// <summary>是否允许 RSA 降级到 SHA-1 签名。默认<b>否</b>。</summary>
    public bool AllowSha1RsaSignatures { get; init; }

    /// <summary>要连的主机与端口（不含用户名；展示时是 <c>host:port</c>，IPv6 带方括号）。</summary>
    public SshEndPoint EndPoint => new(Host, Port);

    private static (string User, string Host, int Port) ParseTarget(string target)
    {
        string rest = target;
        string user = Environment.UserName;

        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            user = rest[..at];
            rest = rest[(at + 1)..];
        }

        if (user.Length == 0)
        {
            throw new ArgumentException($"目标 {target} 里的用户名是空的。", nameof(target));
        }

        // IPv6 要写成 [::1]:22 —— 不加方括号的话冒号分不清是地址还是端口。
        if (rest.StartsWith('['))
        {
            int close = rest.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                throw new ArgumentException($"目标 {target} 里的方括号没有闭合。", nameof(target));
            }

            string address = rest[1..close];
            string tail = rest[(close + 1)..];

            return (user, address, tail.StartsWith(':') ? ParsePort(tail[1..], target) : 22);
        }

        // 不带方括号又有不止一个冒号的，整段是 IPv6 地址、端口取默认（与 ParseJumpSpec 同一个口径）。
        // 曾经按最后一个冒号拆：joe@2001:db8::1 报「端口 “1” 不合法」，joe@2001:db8::1:22 被读成另一个地址的 22 端口。
        int colon = rest.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 || colon != rest.LastIndexOf(':')
            ? (user, rest, 22)
            : (user, rest[..colon], ParsePort(rest[(colon + 1)..], target));
    }

    private static int ParsePort(string text, string target)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
        {
            throw new ArgumentException($"目标 {target} 里的端口 “{text}” 不合法。", nameof(target));
        }
        return port;
    }
}
