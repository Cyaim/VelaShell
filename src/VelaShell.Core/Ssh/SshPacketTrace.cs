namespace VelaShell.Core.Ssh;

/// <summary>报文的方向。</summary>
public enum SshPacketDirection
{
    /// <summary>本机发出的。</summary>
    Sent,

    /// <summary>对端发来的。</summary>
    Received,
}

/// <summary>消息编号落在 RFC 4250 §4.1 的哪一段。</summary>
public enum SshMessageRange
{
    /// <summary>有固定名字的那些(<see cref="SshMessageNames.Name" /> 给得出名字)。</summary>
    Named,

    /// <summary>30–49:密钥交换方法专用,同一个编号在不同的交换方法里意思不同。</summary>
    KeyExchangeMethod,

    /// <summary>60–79:认证方法专用(公钥的 PK_OK、口令的改密请求、keyboard-interactive 的问答共用编号)。</summary>
    AuthenticationMethod,

    /// <summary>192–255:本地扩展。</summary>
    LocalExtension,

    /// <summary>没有分配的编号。</summary>
    Unassigned,
}

/// <summary>SSH 消息编号的名字(RFC 4250 §4.1;协议原名,不翻译)。</summary>
public static class SshMessageNames
{
    /// <summary>有固定名字的消息给出名字(去掉 <c>SSH_MSG_</c> 前缀);方法专用、扩展与没分配的编号为 <see langword="null" />,见 <see cref="RangeOf" />。</summary>
    /// <param name="number">消息编号(载荷的第一个字节)。</param>
    public static string? Name(byte number) => number switch
    {
        1 => "DISCONNECT",
        2 => "IGNORE",
        3 => "UNIMPLEMENTED",
        4 => "DEBUG",
        5 => "SERVICE_REQUEST",
        6 => "SERVICE_ACCEPT",
        7 => "EXT_INFO",
        8 => "NEWCOMPRESS",
        20 => "KEXINIT",
        21 => "NEWKEYS",
        50 => "USERAUTH_REQUEST",
        51 => "USERAUTH_FAILURE",
        52 => "USERAUTH_SUCCESS",
        53 => "USERAUTH_BANNER",
        80 => "GLOBAL_REQUEST",
        81 => "REQUEST_SUCCESS",
        82 => "REQUEST_FAILURE",
        90 => "CHANNEL_OPEN",
        91 => "CHANNEL_OPEN_CONFIRMATION",
        92 => "CHANNEL_OPEN_FAILURE",
        93 => "CHANNEL_WINDOW_ADJUST",
        94 => "CHANNEL_DATA",
        95 => "CHANNEL_EXTENDED_DATA",
        96 => "CHANNEL_EOF",
        97 => "CHANNEL_CLOSE",
        98 => "CHANNEL_REQUEST",
        99 => "CHANNEL_SUCCESS",
        100 => "CHANNEL_FAILURE",
        // OpenSSH 的传输层 PING 扩展(ping@openssh.com,按键时序混淆与往返测量用的那对)。
        192 => "PING",
        193 => "PONG",
        _ => null,
    };

    /// <summary>编号落在哪一段。</summary>
    /// <param name="number">消息编号。</param>
    public static SshMessageRange RangeOf(byte number) => Name(number) is not null
        ? SshMessageRange.Named
        : number switch
        {
            >= 30 and <= 49 => SshMessageRange.KeyExchangeMethod,
            >= 60 and <= 79 => SshMessageRange.AuthenticationMethod,
            >= 192 => SshMessageRange.LocalExtension,
            _ => SshMessageRange.Unassigned,
        };

    /// <summary>认证报文(50–79):旁路从不给它们的载荷,轨迹里我们发出的那几条连长度也不记(见 <see cref="SshPacketTrace" />)。</summary>
    /// <param name="number">消息编号。</param>
    public static bool IsAuthentication(byte number) => number is >= 50 and <= 79;
}

/// <summary>协议轨迹里的一条:一个报文的元信息,<b>没有任何内容</b>。</summary>
/// <param name="Elapsed">从开始记录到看到这个报文过了多久。</param>
/// <param name="Hop">哪一跳的连接(<c>用户@主机:端口</c>);跳板链上每一跳各自记。</param>
/// <param name="Direction">方向。</param>
/// <param name="MessageNumber">消息编号。</param>
/// <param name="Length">载荷长度(含消息编号);我们发出的认证报文为 <see langword="null" />(不记)。</param>
/// <param name="SequenceNumber">这个方向上的报文序号。</param>
/// <param name="ChannelNumber">通道消息的接收方通道号;别的消息为 <see langword="null" />。</param>
public readonly record struct SshPacketTraceEntry(
    TimeSpan Elapsed,
    string Hop,
    SshPacketDirection Direction,
    byte MessageNumber,
    int? Length,
    uint SequenceNumber,
    uint? ChannelNumber);

/// <summary>
/// 连接诊断的协议轨迹:收发的每个报文记一条方向、类型、长度、序号与通道号 —— 类似 <c>ssh -vvv</c> 的报文层,但<b>不记任何内容</b>。
/// </summary>
/// <remarks>
/// <para>
/// 只在连接诊断时挂(<see cref="Models.ConnectionInfo.PacketTrace" />):平常的连接不挂报文旁路,收发循环上只多一次判空。
/// 记录在收发循环上调用,只在锁里往表里加一条,不做别的。
/// </para>
/// <para>
/// ⚠️ <b>我们发出的认证报文(50–79)连长度也不记</b>:口令认证的 <c>USERAUTH_REQUEST</c>、keyboard-interactive 的
/// <c>INFO_RESPONSE</c> 的长度减去固定的那几段就是口令 / 应答的长度,而轨迹是会被复制、导出、贴到工单里的。
/// 载荷本来就拿不到(SSH 库的旁路默认不给载荷,认证报文的载荷无论如何都不给)。
/// </para>
/// <para>只记前 <see cref="Capacity" /> 条,之后的只计数(<see cref="Dropped" />):诊断看的是握手与认证,它们都在最前面。</para>
/// </remarks>
public sealed class SshPacketTrace
{
    /// <summary>默认最多记多少条。一次测试连接(握手、认证、断开)通常几十条。</summary>
    public const int DefaultCapacity = 1000;

    private readonly Lock _gate = new();
    private readonly List<SshPacketTraceEntry> _entries = [];
    private readonly TimeProvider _time;
    private readonly long _started;
    private int _dropped;

    /// <param name="capacity">最多记多少条。</param>
    /// <param name="timeProvider">时钟;<see langword="null" /> 为系统时钟(测试可换)。</param>
    public SshPacketTrace(int capacity = DefaultCapacity, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _time = timeProvider ?? TimeProvider.System;
        _started = _time.GetTimestamp();
    }

    /// <summary>最多记多少条。</summary>
    public int Capacity { get; }

    /// <summary>超过上限没记的条数。</summary>
    public int Dropped
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    /// <summary>记一个报文(SSH 库报文旁路的回调里调,要快)。</summary>
    /// <param name="hop">哪一跳。</param>
    /// <param name="direction">方向。</param>
    /// <param name="messageNumber">消息编号。</param>
    /// <param name="length">载荷长度;我们发出的认证报文不记它。</param>
    /// <param name="sequenceNumber">报文序号。</param>
    /// <param name="channelNumber">通道号。</param>
    public void Record(string hop, SshPacketDirection direction, byte messageNumber, int length, uint sequenceNumber, uint? channelNumber)
    {
        int? shown = direction == SshPacketDirection.Sent && SshMessageNames.IsAuthentication(messageNumber) ? null : length;
        TimeSpan elapsed = _time.GetElapsedTime(_started);
        lock (_gate)
        {
            if (_entries.Count >= Capacity)
            {
                _dropped++;
                return;
            }
            _entries.Add(new SshPacketTraceEntry(elapsed, hop, direction, messageNumber, shown, sequenceNumber, channelNumber));
        }
    }

    /// <summary>此刻记下的全部(副本)。</summary>
    public IReadOnlyList<SshPacketTraceEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }
}
