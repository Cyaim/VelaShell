namespace VelaShell.Core.Ssh;

/// <summary>
/// 一条 SSH 连接此刻的样子:对端、谈成的算法、认证、各阶段耗时、流量与重协商、开着的通道、经过的跳板。
/// </summary>
/// <remarks>
/// <para>
/// 连接信息面板(会话标签右键 → 连接信息)的数据。全部取自 SSH 库已经交出来的只读属性,
/// <b>不另开探测</b> —— 往返时间也只取最近一次保活 / 延迟测量的结果,面板打开、刷新都不往线上多发一个报文。
/// </para>
/// <para>
/// 是快照不是活对象:刷新就是再取一份。与库类型隔开(Core 只认中立类型,见 <see cref="ISshClientWrapper" />)。
/// </para>
/// </remarks>
public sealed record SshConnectionDetails
{
    /// <summary>这条连接的目标(<c>用户@主机:端口</c>)。</summary>
    public required string Target { get; init; }

    /// <summary>取快照的时刻。</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>连接还活着(没掉线、没被关)。</summary>
    public required bool IsAlive { get; init; }

    /// <summary>对端的版本标识串(<c>SSH-2.0-OpenSSH_9.6p1</c>),已按对端文本清洗。</summary>
    public required string PeerVersion { get; init; }

    /// <summary>对端出示的主机密钥。</summary>
    public required SshHostKeyDetails HostKey { get; init; }

    /// <summary>会话标识(首次密钥交换的交换哈希 H),小写十六进制。</summary>
    public required string SessionId { get; init; }

    /// <summary>对端认不认 <c>ping@openssh.com</c>(往返时间与按键时序混淆的掩护要用它)。</summary>
    public bool PeerSupportsPing { get; init; }

    /// <summary>此刻生效的算法(重协商之后是最近一次谈成的)。</summary>
    public required SshAlgorithmDetails Algorithms { get; init; }

    /// <summary>认证最终用哪种方法成功(<c>publickey</c> / <c>password</c> / <c>keyboard-interactive</c> / <c>none</c>)。</summary>
    public required string AuthenticationMethod { get; init; }

    /// <summary>服务端 <c>server-sig-algs</c> 宣告的签名算法(RFC 8308);没宣告时为空。</summary>
    public IReadOnlyList<string> ServerSignatureAlgorithms { get; init; } = [];

    /// <summary>这条连接(最外一跳)建立时各阶段的耗时。</summary>
    public required SshTimingDetails Timings { get; init; }

    /// <summary>最近一次测到的往返时间(保活探测或延迟测量);还没测过时为 <see langword="null" />。</summary>
    public TimeSpan? LastRoundTrip { get; init; }

    /// <summary>重协商做过几次。</summary>
    public int RekeyCount { get; init; }

    /// <summary>最近一次重协商;还没做过时为 <see langword="null" />。</summary>
    public SshRekeyDetails? LastRekey { get; init; }

    /// <summary>线上一共发出的字节(含报文头、填充与 MAC,压缩之后的)。</summary>
    public long BytesSent { get; init; }

    /// <summary>线上一共收到的字节。</summary>
    public long BytesReceived { get; init; }

    /// <summary>一共发出的报文数。</summary>
    public long PacketsSent { get; init; }

    /// <summary>一共收到的报文数。</summary>
    public long PacketsReceived { get; init; }

    /// <summary>开着的通道,按通道号排(正在打开的那条也在里面)。</summary>
    public IReadOnlyList<SshChannelDetails> Channels { get; init; } = [];

    /// <summary>经过的出站代理(最内一跳之前);直连时为 <see langword="null" />。</summary>
    public string? Proxy { get; init; }

    /// <summary>经过的跳板,从近到远(本机先连的那一台在前);直连时为空。</summary>
    public IReadOnlyList<SshHopDetails> Hops { get; init; } = [];

    /// <summary>这条连接的配置开了按键时序混淆。</summary>
    public bool ObscureKeystrokeTiming { get; init; }
}

/// <summary>主机密钥。</summary>
/// <param name="Type">密钥类型(<c>ssh-ed25519</c>;证书时是证书类型)。</param>
/// <param name="Bits">位数。</param>
/// <param name="Fingerprint">SHA-256 指纹(<c>SHA256:…</c>,按密钥本身算,证书时是证书里那把钥)。</param>
/// <param name="IsCertificate">对端出示的是 OpenSSH 主机证书。</param>
public sealed record SshHostKeyDetails(string Type, int Bits, string Fingerprint, bool IsCertificate);

/// <summary>谈成的算法(两个方向分开记,界面上相同时合成一项)。</summary>
public sealed record SshAlgorithmDetails
{
    /// <summary>密钥交换。</summary>
    public required string KeyExchange { get; init; }

    /// <summary>主机密钥(签名)算法。</summary>
    public required string HostKey { get; init; }

    /// <summary>加密,客户端 → 服务端。</summary>
    public required string EncryptionClientToServer { get; init; }

    /// <summary>加密,服务端 → 客户端。</summary>
    public required string EncryptionServerToClient { get; init; }

    /// <summary>MAC,客户端 → 服务端;AEAD 加密(自带完整性)时为 <see langword="null" />。</summary>
    public string? MacClientToServer { get; init; }

    /// <summary>MAC,服务端 → 客户端;AEAD 时为 <see langword="null" />。</summary>
    public string? MacServerToClient { get; init; }

    /// <summary>压缩,客户端 → 服务端。</summary>
    public required string CompressionClientToServer { get; init; }

    /// <summary>压缩,服务端 → 客户端。</summary>
    public required string CompressionServerToClient { get; init; }

    /// <summary>严格 KEX(Terrapin 缓解)双方都开了。</summary>
    public bool StrictKeyExchange { get; init; }

    /// <summary>对端支持 RFC 8308 扩展协商。</summary>
    public bool ExtensionNegotiation { get; init; }
}

/// <summary>建连各阶段的耗时。</summary>
/// <param name="Dialing">拨号(直连 TCP,或经代理 / 跳板到达,含跳板自己的建连)。</param>
/// <param name="VersionExchange">版本交换。</param>
/// <param name="KeyExchange">首次密钥交换,含等用户裁决主机密钥的时间。</param>
/// <param name="Authentication">用户认证,含等用户输口令、动态码的时间。</param>
public sealed record SshTimingDetails(TimeSpan Dialing, TimeSpan VersionExchange, TimeSpan KeyExchange, TimeSpan Authentication)
{
    /// <summary>四段之和。</summary>
    public TimeSpan Total => Dialing + VersionExchange + KeyExchange + Authentication;
}

/// <summary>一次重协商是怎么来的。</summary>
public enum SshRekeyReason
{
    /// <summary>说不清。</summary>
    Unknown = 0,

    /// <summary>对端发起。</summary>
    Peer,

    /// <summary>本端显式请求。</summary>
    Requested,

    /// <summary>单向字节数到了阈值。</summary>
    Bytes,

    /// <summary>单向报文数到了阈值。</summary>
    Packets,

    /// <summary>距上次交换的时长到了阈值。</summary>
    Interval,

    /// <summary>同一套密钥下的报文数到了硬线(序号回绕之前必须换钥)。</summary>
    PacketHardLimit,
}

/// <summary>最近一次重协商。</summary>
/// <param name="Reason">怎么来的。</param>
/// <param name="Observed">到阈值时的观测值(字节、报文数或毫秒,随 <paramref name="Reason" />);对端发起与显式请求时为 0。</param>
/// <param name="Threshold">对应的阈值;对端发起与显式请求时为 0。</param>
/// <param name="Duration">从收到对端 <c>KEXINIT</c> 到新密钥装好用了多久(期间通道数据暂存);不知道时为 <see langword="null" />。</param>
public sealed record SshRekeyDetails(SshRekeyReason Reason, long Observed, long Threshold, TimeSpan? Duration);

/// <summary>通道此刻处在哪一步。</summary>
public enum SshChannelPhase
{
    /// <summary>正在打开,还没等到确认。</summary>
    Opening,

    /// <summary>开着。</summary>
    Open,

    /// <summary>一边或两边已经发了 EOF,还没关。</summary>
    HalfClosed,

    /// <summary>正在关。</summary>
    Closing,
}

/// <summary>一条通道。</summary>
/// <param name="Id">本端给它的通道号。</param>
/// <param name="Type">通道类型(<c>session</c> / <c>direct-tcpip</c> / <c>forwarded-tcpip</c> / …,协议原名)。</param>
/// <param name="Phase">处在哪一步。</param>
/// <param name="OpenedAt">开通的时刻;还在打开时为 <see langword="null" />。</param>
/// <param name="BytesSent">发出的数据字节。</param>
/// <param name="BytesReceived">收到的数据字节。</param>
public sealed record SshChannelDetails(uint Id, string Type, SshChannelPhase Phase, DateTimeOffset? OpenedAt, long BytesSent, long BytesReceived);

/// <summary>经过的一台跳板(<c>ProxyJump</c>)。</summary>
/// <param name="Target">这一跳(<c>用户@主机:端口</c>)。</param>
/// <param name="PeerVersion">它的版本标识串。</param>
/// <param name="KeyExchange">与它谈成的密钥交换。</param>
/// <param name="HostKey">它的主机密钥算法。</param>
/// <param name="Encryption">加密(客户端 → 服务端)。</param>
/// <param name="AuthenticationMethod">在它上面用哪种认证方法成功。</param>
/// <param name="IsAlive">这一跳的连接还活着。</param>
public sealed record SshHopDetails(
    string Target, string PeerVersion, string KeyExchange, string HostKey, string Encryption, string AuthenticationMethod, bool IsAlive);
