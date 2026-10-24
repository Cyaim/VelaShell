// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §4    GLOBAL_REQUEST / REQUEST_SUCCESS / REQUEST_FAILURE
//   RFC 4254 §5    通道的开、关、数据、请求
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §1、§2、§6、§8

using System.Buffers;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Session;

/// <summary>已认证的会话：通道的多路复用器。</summary>
/// <remarks>
/// <para>
/// 一条接收循环把入站报文分发到各通道；一个发送泵是唯一的写者 ——
/// 多条通道并发地往外写，由它按入队顺序逐帧封装、一轮合并刷出（architecture.md §5.4）。
/// </para>
/// <para>
/// <b>接收循环绝不因为某一条通道而停下。</b>通道的管道水位定得比窗口高，
/// 写进去不会阻塞；真正的背压由 SSH 窗口表达，而窗口挂在消费上。
/// 在接收循环里等某条通道，就是把其它所有通道一起饿死。
/// </para>
/// </remarks>
public sealed partial class SshConnection : ISshChannelHost, IAsyncDisposable
{
    private const int MaxFieldBytes = 64 * 1024;

    /// <summary>
    /// 一个 <c>CHANNEL_EXTENDED_DATA</c> 报文里，数据之外占 <c>packet_length</c> 的最大字节数：
    /// <c>padding_length</c>(1) + 编号(1) + 通道号(4) + 类型码(4) + 长度(4) + 最大填充(255)。
    /// </summary>
    private const int ChannelPacketOverhead = 1 + 1 + 4 + 4 + 4 + 255;

    private readonly SshPacketTransport _transport;
    private readonly SshConnectionLimits _limits;
    private readonly Lock _stateLock = new();

    private readonly Dictionary<uint, SshChannel> _channels = [];
    private readonly Dictionary<uint, TaskCompletionSource<SshChannel>> _pendingOpens = [];

    /// <summary>全局请求的应答账本。<b>同样没有 id，靠 FIFO 对齐。</b></summary>
    private readonly FifoRequestLedger<SshGlobalRequestReply> _globalRequests = new();

    /// <summary>已回收、但还不到复用时间的通道号，带回收那一刻（<see cref="Time"/> 的时间戳）。</summary>
    private readonly Queue<(uint Id, long RecycledAt)> _recycledIds = new();

    /// <summary>按通道类型登记的入站通道处理器（服务端发起的通道）。</summary>
    /// <remarks>
    /// 同一类型可以有<b>多个</b>处理器 —— 两个远程转发（<c>-R 8080</c> 与 <c>-R 9090</c>）
    /// 都要接 <c>forwarded-tcpip</c>。后登记的先问，第一个同意的接走。
    /// </remarks>
    private readonly Dictionary<string, List<IIncomingChannelHandler>> _incomingHandlers = [];

    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// <see cref="Disconnected"/> 的源。与 <see cref="_lifetime"/> 分开是因为两者
    /// 职责不同：<c>_lifetime</c> 用来把内部的收发泵停下来，而这一个是<b>对外</b>的信号，
    /// 会在 <see cref="Fault"/> 时就触发 —— 那时收发泵可能还在做收尾。
    /// </summary>
    private readonly CancellationTokenSource _disconnected = new();
    private Task? _receiveLoop;
    private Task? _keepAliveLoop;
    private Task? _rekeyMonitorLoop;

    private uint _nextChannelId;
    private long _windowBudgetUsed;
    private Exception? _fault;
    private bool _disposed;

    /// <summary>在一条已完成认证的传输上建立会话。</summary>
    /// <param name="transport">传输。</param>
    /// <param name="kex">首次密钥交换的结果：会话标识、协商出的算法、主机公钥。</param>
    /// <param name="limits">通道限额。</param>
    /// <remarks>
    /// <c>internal</c>：公开的入口只有 <see cref="ConnectAsync"/>。从外面拿一条裸传输拼出来的连接
    /// 没有重协商所需的上下文，<see cref="StartRekeyAsync"/> 一调就抛。
    /// </remarks>
    internal SshConnection(SshPacketTransport transport, SshKeyExchangeResult kex, SshConnectionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(kex);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _sessionId = kex.SessionId;
        SessionProof = kex.CreateSessionProof();
        Algorithms = kex.Algorithms;
        HostKey = kex.HostKey;
        // 只有 OpenSSH 才发 eow@openssh.com：有的实现收到不认识的通道请求会直接断开（RFC 4254 §5.4 不许，但确实有）。
        PeerAcceptsEndOfWrite = kex.ServerVersion.StartsWith("SSH-2.0-OpenSSH_", StringComparison.Ordinal);
        _limits = limits ?? SshConnectionLimits.Default;
        Disconnected = _disconnected.Token;

        // 发送泵从一开始就要在 —— 打开通道之前、重协商之前，任何出站都经过它。
        // 直接调用而不是 Task.Run：它同步跑到第一个 await（等队列）就让出。
        _sendPump = SendPumpAsync(_lifetime.Token);
    }

    private readonly byte[] _sessionId;

    /// <inheritdoc />
    bool ISshChannelHost.PeerAcceptsEndOfWrite => PeerAcceptsEndOfWrite;

    /// <summary>对端是 OpenSSH（按版本标识串判断），可以发 <c>eow@openssh.com</c>。</summary>
    internal bool PeerAcceptsEndOfWrite { get; }

    /// <summary>会话标识（首次密钥交换的交换哈希 H，RFC 4253 §7.2），整条连接不变。</summary>
    /// <remarks>
    /// 只交出只读视图：重协商拿的就是这份字节去算哈希，交出可写的数组等于让调用方能改坏下一次重协商。
    /// </remarks>
    public ReadOnlyMemory<byte> SessionId => _sessionId;

    /// <summary>首次密钥交换里服务端的身份证明 —— agent 转发给本机 agent 发会话声明时用（spec/07 §7.4）。</summary>
    internal Keys.SshSessionProof SessionProof { get; }

    /// <summary>这条会话实际协商出来的算法。</summary>
    /// <remarks>
    /// <para>
    /// <b>协商成功的结果同样要交出去，不只是失败时的名单。</b>
    /// 终端产品要在状态栏上写「aes256-gcm@openssh.com · zlib@openssh.com」，
    /// 排障时第一句话也是「你们这条连接到底谈成了什么」——
    /// 库知道而不说，使用者就只能自己再探一次（架构原则 4）。
    /// </para>
    /// <para>
    /// 也是唯一能确认<b>压缩到底有没有谈成</b>的地方：服务端不支持时会
    /// 静默落到 <c>none</c>，<b>不会报错</b>。
    /// </para>
    /// <para>
    /// 重协商会把它整个换掉（算法可以谈出不一样的结果），所以读写都走
    /// <see cref="_stateLock"/> —— 它不是一个装完就不动的值。
    /// </para>
    /// </remarks>
    public Crypto.SshNegotiatedAlgorithms Algorithms
    {
        get
        {
            lock (_stateLock)
            {
                return field;
            }
        }

        private set;
    }

    /// <summary>服务端出示的主机公钥。</summary>
    public HostKeys.SshPublicKey HostKey { get; }

    /// <summary>对端的标识串（<c>SSH-2.0-OpenSSH_10.3</c> 这样），已按对端文本清洗。</summary>
    /// <remarks>曾经只出现在异常与主机密钥裁决的材料里；连接信息、排障要回答「对面是什么服务端、什么版本」。</remarks>
    public string PeerVersion { get; internal init; } = "";

    /// <summary>认证最终用哪种方法成功的（<c>publickey</c> / <c>password</c> / <c>keyboard-interactive</c> / <c>none</c>）。</summary>
    public string AuthenticationMethod { get; internal init; } = "";

    /// <summary>服务端在 <c>server-sig-algs</c> 里宣告的签名算法（RFC 8308 §3.1）；没宣告时为空。</summary>
    /// <remarks>公钥认证挑签名算法看的就是它 —— 「为什么这把 RSA 钥用的是 SHA-1」要从这里回答。</remarks>
    public IReadOnlyList<string> ServerSignatureAlgorithms { get; internal init; } = [];

    /// <summary>这条连接建立时各阶段用了多久。</summary>
    public SshConnectTimings ConnectTimings { get; internal init; }

    /// <summary>
    /// 主机密钥策略裁决「信任并记住」、记的时候却失败了的原因；记下了（或者没让记）时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/03 §5.4〕信任已经给了，这次连接照常进行 —— 与 OpenSSH 一样只是提醒，
    /// 下次连接还会再问。<see cref="HostKeys.KnownHostsPolicy"/> 写不进 known_hosts 时它是
    /// <see cref="SshFailureReason.HostKeyStoreFailed"/> 的 <see cref="SshConnectException"/>；
    /// 调用方自己的策略抛的异常原样放在这里。经跳板时只记在那一跳自己的连接上。
    /// </remarks>
    public Exception? HostKeyPersistFailure { get; internal init; }

    /// <summary>保活策略。</summary>
    internal SshKeepAlivePolicy KeepAlive { get; init; } = SshKeepAlivePolicy.Disabled;

    /// <summary>测试用：对端开过来的通道，确认入队之后、起泵之前调用（在决定开通道的后台任务上）。</summary>
    /// <remarks>
    /// 用例在这里停住，让接收循环先处理对端紧跟着确认发来的 <c>CLOSE</c> —— 把「后台任务恰好在两步之间被抢占」这一刻摆出来
    /// （velashell-docs/zh/ssh/spec/05 §8.1）。
    /// </remarks>
    internal Action<SshChannel>? AfterIncomingOpenConfirmationQueued { get; set; }

    /// <summary>保活、重协商（时间阈值、期限）与通道号复用延迟用的时钟；建连时取 <see cref="SshConnectionOptions.TimeProvider"/>。</summary>
    /// <remarks>
    /// 只有测试会换成手动拨的时钟 —— 「复用延迟刚好到点」「闲了刚好一个保活间隔」这种时刻靠真实时钟摆不出来。
    /// 曾经这几处直接用 <c>Environment.TickCount64</c> / <c>Task.Delay</c>，只能拿真实时间去等、去碰运气。
    /// </remarks>
    internal TimeProvider Time
    {
        get;
        init
        {
            field = value ?? throw new ArgumentNullException(nameof(Time));

            // 两个基准时刻跟着换成这个时钟的口径（字段初值是按系统时钟取的）。
            long now = value.GetTimestamp();
            _lastInboundAt = now;
            _lastKexAt = now;
        }
    } = TimeProvider.System;

    /// <summary>给人看的描述（<c>user@host:port</c>），进日志与异常。</summary>
    internal string Description { get; init; } = "";

    /// <summary>标着 <c>always_display</c> 的 <c>SSH_MSG_DEBUG</c> 的回调（<see cref="SshConnectionOptions.DebugMessageHandler"/>）。</summary>
    internal Func<string, CancellationToken, ValueTask>? DebugMessageHandler { get; init; }

    /// <summary>连上之后的调试消息：在线程池上交给回调，不挡接收循环；回调抛的丢掉，格式不对的照 RFC 忽略。</summary>
    private void OnDebugMessage(ReadOnlyMemory<byte> payload)
    {
        if (DebugMessageHandler is not { } handler)
        {
            return;
        }

        SshDebugMessage message;
        try
        {
            message = SshDebugMessage.Decode(payload);
        }
        catch (SshWireFormatException)
        {
            return;
        }

        if (message.AlwaysDisplay && PeerText.Sanitize(message.Message) is { Length: > 0 } text)
        {
            CancellationToken token = Disconnected;
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(text, token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 接收循环之外的通知：回调抛的不影响连接。
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>度量的 <c>host</c> 标签（逻辑目标）；<see langword="null"/>（测试里直接建的连接）时不记度量。</summary>
    internal string? MetricsHost { get; init; }

    /// <inheritdoc />
    string? ISshChannelHost.MetricsHost => MetricsHost;

    /// <summary>活着的连接数记没记上：0 还没记，1 记了 +1，2 收尾了（之后不再记）。</summary>
    private int _activeMetered;

    /// <summary>上一次收到任何入站报文的时刻（<see cref="Time"/> 的时间戳）。</summary>
    private long _lastInboundAt = TimeProvider.System.GetTimestamp();
    private int _rekeyCount;
    private int _sendGateOpensPosted;

    /// <summary>我们发出、还没被一次交换用掉的 <c>KEXINIT</c>（<see cref="_kexInProgress"/> 为假时才可能非空）。</summary>
    private byte[]? _ourPendingKexInit;

    /// <summary>一次重协商正在接收循环上跑（从对端的 <c>KEXINIT</c> 到开闸）。</summary>
    /// <remarks>与 <see cref="_ourPendingKexInit"/> 一起在 <c>_stateLock</c> 里读写。</remarks>
    private bool _kexInProgress;
    private long _bytesAtLastKex;
    private long _bytesReceivedAtLastKex;
    private long _packetsAtLastKex;
    private long _packetsReceivedAtLastKex;
    /// <summary>上一次密钥交换完成的时刻（<see cref="Time"/> 的时间戳）。</summary>
    private long _lastKexAt = TimeProvider.System.GetTimestamp();
    private SshRekeyCause? _lastRekey;

    /// <summary>当前占着通道号的通道数。</summary>
    /// <remarks>
    /// <b>正在打开、还没等到对端确认的那条也算在内</b> —— 通道号从发出
    /// <c>CHANNEL_OPEN</c> 起就已经占用，限额也按这个数算。
    /// </remarks>
    public int ChannelCount
    {
        get
        {
            lock (_stateLock)
            {
                return _channels.Count;
            }
        }
    }

    /// <summary>这条连接上此刻开着的通道（快照：类型、状态、开通时刻、字节数），按通道号排。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/05 §一〕连接信息里列「这条连接上开着哪些 shell、隧道、SFTP」用它。交出的是快照而不是通道本身 ——
    /// 通道归开它的那一方，别人拿到手就能关掉它。正在打开的那条也在里面（与 <see cref="ChannelCount"/> 一致）。
    /// 曾经只有一个数。
    /// </remarks>
    public IReadOnlyList<SshChannelSnapshot> Channels
    {
        get
        {
            SshChannel[] channels;
            lock (_stateLock)
            {
                channels = [.. _channels.Values];
            }

            // 通道自己的锁在连接的锁之外取（锁顺序：连接在前、通道在后，这里干脆不嵌套）。
            return [.. channels
                .OrderBy(channel => channel.LocalId)
                .Select(channel => new SshChannelSnapshot(
                    channel.LocalId, channel.ChannelType, channel.State, channel.OpenedAt, channel.BytesSent, channel.BytesReceived))];
        }
    }

    /// <summary>会话是否还活着。</summary>
    public bool IsAlive => Volatile.Read(ref _fault) is null && !_disposed;

    /// <summary>
    /// 在敏感通道打开之前取得连接级诊断载荷抑制；最后一个租约结束后仅恢复原诊断配置。
    /// </summary>
    /// <remarks>
    /// 依据 velashell-docs/zh/ssh/spec/08-failures.md §9.1。调用方须持有至通道在途收发及双向关闭完成；
    /// 取消、进程退出或通道 DisposeAsync 返回不等于此边界。租约重复释放安全。
    /// </remarks>
    public IAsyncDisposable SuppressSensitivePayload()
    {
        ObjectDisposedException.ThrowIf(!IsAlive, this);
        return _transport.AcquireSensitivePayloadLease();
    }

    /// <summary>
    /// 会话结束时被取消的令牌 —— 掉线、被对端断开、协议出错，或者自己释放。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是给「掉线要立刻有反应」的调用方准备的。</b>只有 <see cref="IsAlive"/>
    /// 的话，上层只能定时去问一句「还活着吗」——
    /// 而终端的读循环、自动重连、状态栏的连接指示灯都要的是「一断就知道」，
    /// 不是「最多一秒之后知道」。
    /// </para>
    /// <para>
    /// 它<b>只表示会话到此为止</b>，不表示为什么：原因在各个操作抛出的异常里
    /// （<c>SshConnectionClosedException</c> 带着 <c>DisconnectReason</c>）。
    /// 一个 <see cref="CancellationToken"/> 装不下原因，硬塞只会让调用方
    /// 从取消异常里去猜。
    /// </para>
    /// <para>
    /// 正常释放同样会取消它 —— 「连接没了」对读循环来说是同一件事，
    /// 区分「是我关的还是掉线」得看调用方自己的状态。
    /// </para>
    /// <para>
    /// 令牌在构造时就取好并留住，而不是每次读 <c>_disconnected.Token</c> ——
    /// 那个属性在源被释放之后会抛 <see cref="ObjectDisposedException"/>，
    /// 而「连接已经释放了」恰恰是最常去读这个令牌的时刻。
    /// </para>
    /// </remarks>
    public CancellationToken Disconnected { get; }

    /// <summary>连接结束时完成，结果是结束的原因。</summary>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/08 §2.1〕断线时是那次故障本身：保活超时（<c>KeepAliveTimeout</c>）、对端关闭（<c>ClosedByPeer</c>）、
    /// 收到 <c>DISCONNECT</c>（<see cref="SshConnectionClosedException"/> 带着原因码与对端原话）、协议错误……；
    /// 本端释放时是一个 <see cref="SshFailureReason.Aborted"/> 的 <see cref="SshConnectionClosedException"/>。
    /// </para>
    /// <para>
    /// <b>以结果完成，从不以异常完成</b>：没人等它时也不会变成未观察的任务异常。
    /// <see cref="Disconnected"/> 只说「到此为止」，原因在这里 —— 曾经没有这个入口，使用者挂 <see cref="Disconnected"/>
    /// 只能一律报「对端关闭」，或者从读管道的结束方式去反推。
    /// </para>
    /// </remarks>
    public Task<SshException> Completion => _completion.Task;

    /// <summary>连接是怎么结束的；还没结束时为 <see langword="null"/>。与 <see cref="Completion"/> 的结果相同。</summary>
    public SshException? CloseReason => _completion.Task.IsCompleted ? _completion.Task.Result : null;

    private readonly TaskCompletionSource<SshException> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>记下结束原因（第一个胜出）。故障不是 <see cref="SshException"/>（取消、释放）时换成一个说得清的。</summary>
    private void Complete(Exception reason)
    {
        _completion.TrySetResult(reason as SshException ?? new SshConnectionClosedException(
            SshFailureReason.Aborted, SshPhase.Open, $"连接中止了：{reason.Message}", reason));

        if (Interlocked.Exchange(ref _activeMetered, 2) == 1 && MetricsHost is { } host)
        {
            SshMetrics.ConnectionsActive.Add(-1, SshMetrics.HostTag(host));
        }
    }

    /// <summary>开始收包。</summary>
    /// <remarks>必须在打开任何通道之前调用一次。</remarks>
    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _receiveLoop ??= Task.Run(() => ReceiveLoopAsync(_lifetime.Token));

        if (MetricsHost is { } host && Interlocked.CompareExchange(ref _activeMetered, 1, 0) == 0)
        {
            SshMetrics.ConnectionsActive.Add(1, SshMetrics.HostTag(host));
        }

        if (KeepAlive.IsEnabled)
        {
            _keepAliveLoop ??= Task.Run(() => KeepAliveLoopAsync(_lifetime.Token));
        }

        // 只有工厂建的连接才谈得上主动发起重协商（要用 RekeyContext）。
        // 不看策略开没开：报文数的硬线（见 RekeyHardPacketLimit）关不掉，监视循环总要在。
        if (RekeyContext is not null)
        {
            _rekeyMonitorLoop ??= Task.Run(() => RekeyMonitorLoopAsync(_lifetime.Token));
        }
    }

    /// <summary>保活循环一次至少睡这么久：闲够之前不会每毫秒醒一次。</summary>
    private static readonly TimeSpan MinKeepAliveSleep = TimeSpan.FromMilliseconds(50);

    /// <summary>保活循环。</summary>
    /// <remarks>
    /// <para>
    /// <b>计时基准是「上次收到任何报文」，不是固定周期。</b>
    /// 连接正忙的时候根本不需要发保活 —— 数据本身就证明了链路活着，
    /// 按固定周期发只是在给一条已经证明活着的链路添噪音。
    /// </para>
    /// <para>
    /// <b>任何入站报文都重置计数</b>，不只是保活应答。
    /// </para>
    /// </remarks>
    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        int missed = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan idle = Time.GetElapsedTime(Volatile.Read(ref _lastInboundAt));
                TimeSpan interval = KeepAlive.Interval;

                if (idle < interval)
                {
                    // 还没闲够。睡到「刚好闲够」的那一刻再看。
                    TimeSpan rest = interval - idle;
                    await Task.Delay(rest > MinKeepAliveSleep ? rest : MinKeepAliveSleep, Time, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (Volatile.Read(ref _fault) is not null)
                {
                    return;   // 会话已经判死了，没什么可探的
                }

                // 「期间收到了别的报文」看报文计数，不看时间戳：时钟没走（粒度粗、或测试里手动拨的）时，
                // 新报文写下的时间戳与原来的一样，会被当成什么都没收到。
                long before = _transport.PacketsReceived;

                // ⚠️ **每一次探测都要有自己的期限。**
                //    保活要对付的正是半开连接：报文写得进本机的发送缓冲，
                //    应答却永远不会来。不设期限的话这一次 await 永远不返回，
                //    missed 永远不会加一 —— 判死的逻辑就成了摆设。
                //
                //    超时的那一项**仍然留在应答账本里**：应答只是迟到，
                //    它来的时候必须落在这一项上，后面的应答才对得上号。
                bool alive = await ProbeAsync(interval, cancellationToken)
                    .ConfigureAwait(false);

                // 有应答，或者期间收到了别的报文 —— 都算活着。
                if (alive || _transport.PacketsReceived != before)
                {
                    missed = 0;
                    continue;
                }

                if (++missed >= KeepAlive.MaxMissed)
                {
                    // 判死时用 KeepAliveTimeout 而不是笼统的 Timeout ——
                    // 上层的自动重连策略只应该对这一类生效。
                    Fault(new SshConnectionClosedException(
                        SshFailureReason.KeepAliveTimeout, SshPhase.Open,
                        $"连续 {missed} 次保活探测没有得到应答，判定连接已死" +
                        (Description.Length > 0 ? $"（{Description}）" : "") + "。"));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 我们自己要收工。
        }
        catch (Exception)
        {
            // 会话已经出问题了，接收循环会自己发现。
        }
    }

    /// <summary>发一次保活探测，在 <paramref name="timeout"/> 之内等应答。</summary>
    /// <returns>期限内有应答（成功或失败都算）为 <see langword="true"/>。</returns>
    /// <remarks>
    /// ⚠️ <b>期限从入队那一刻算起，罩住「发出去」与「等应答」两段。</b>
    /// 死链的典型样子是对端不再读、发送泵卡在一次写上、出站积压顶满背压：
    /// 这时探测若在背压上等、或等自己刷上线之后才开始计时，它就跟着一起卡住，
    /// <c>missed</c> 永远不加一 —— 恰恰在最需要判死的时候判不了。
    /// </remarks>
    private async ValueTask<bool> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> packet = EncodeGlobalRequest(
            SshProtocolNames.KeepAliveOpenSsh, default, wantReply: true);

        // 登记与入队是同一个动作：应答靠 FIFO 对齐（见 SendAsync 的重载说明）。
        long startedAt = Time.GetTimestamp();
        Task<SshGlobalRequestReply>? reply = null;
        if (!PostRegistered(packet, () => reply = _globalRequests.Register()))
        {
            return false;
        }

        try
        {
            await reply!.WaitAsync(timeout, Time, cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _fault) is not null)
            {
                return false;
            }
            RecordRoundTrip(Time.GetElapsedTime(startedAt));
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>最近一次测到的往返时间（ticks）；0 表示还没测过。</summary>
    private long _lastRoundTripTicks;

    /// <summary>
    /// 最近一次测到的往返时间：保活探测（链路闲下来时自动发）或 <see cref="MeasureRoundTripAsync"/> 从入队到收到应答；
    /// 还没测过时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/05 §6.3〕量的是整条路径（经代理、跳板也一样），含服务端处理一个全局请求的时间 ——
    /// 比 ICMP ping 更接近用户感到的「卡」，也不怕目标禁 ICMP。通道正在大量收发时，探测排在数据后面，量到的会偏大。
    /// </remarks>
    public TimeSpan? LastRoundTrip => Volatile.Read(ref _lastRoundTripTicks) is var ticks and > 0
        ? TimeSpan.FromTicks(ticks)
        : null;

    private void RecordRoundTrip(TimeSpan elapsed) =>
        Volatile.Write(ref _lastRoundTripTicks, Math.Max(1, elapsed.Ticks));

    /// <summary>量一次到服务端的往返时间：发一个要应答的保活全局请求，从入队到收到应答。</summary>
    /// <returns>往返时间（同时记进 <see cref="LastRoundTrip"/>）。</returns>
    /// <exception cref="SshException">连接已经断了。</exception>
    /// <exception cref="OperationCanceledException">取消。</exception>
    /// <remarks>服务端回成功还是失败都算（它多半不认这个请求类型），有应答就是到过一个来回。</remarks>
    public async ValueTask<TimeSpan> MeasureRoundTripAsync(CancellationToken cancellationToken = default)
    {
        // 对端认 PING 就用它：传输层的回声，不经服务端的全局请求处理，量得更准。
        if (PeerSupportsPing)
        {
            return await PingAsync(cancellationToken).ConfigureAwait(false);
        }

        long startedAt = Time.GetTimestamp();
        await SendGlobalRequestAsync(
            SshProtocolNames.KeepAliveOpenSsh, default, wantReply: true, cancellationToken).ConfigureAwait(false);
        TimeSpan elapsed = Time.GetElapsedTime(startedAt);
        RecordRoundTrip(elapsed);
        return elapsed;
    }

    // ------------------------------------------------------------ 通道

    /// <summary>打开一条 <c>session</c> 通道。</summary>
    public ValueTask<SshChannel> OpenSessionChannelAsync(
        SshChannelOptions? options = null, CancellationToken cancellationToken = default) =>
        OpenChannelAsync(SshProtocolNames.ChannelSession, default, options, cancellationToken);

    /// <summary>打开一条通道。</summary>
    /// <param name="channelType">通道类型。</param>
    /// <param name="typeSpecificPayload">类型相关的附加字段。</param>
    /// <param name="options">通道参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshChannelException">对端拒绝，或本端限额已满。</exception>
    internal async ValueTask<SshChannel> OpenChannelAsync(
        string channelType,
        ReadOnlyMemory<byte> typeSpecificPayload = default,
        SshChannelOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(channelType);

        SshChannelOptions effective = options ?? SshChannelOptions.Default;
        int window = effective.WindowPolicy.InitialBytes;

        // 我们宣告的单包上限必须装得进传输层允许的报文 —— 否则对端照着宣告发来的
        // 报文会被传输层当成超长报文而判为协议错误，错误还指不到这里。
        int maxPayload = _transport.MaxPacketLength - ChannelPacketOverhead;
        if (effective.ReceiveMaxPacketBytes > maxPayload)
        {
            throw new ArgumentException(
                $"通道的 ReceiveMaxPacketBytes（{effective.ReceiveMaxPacketBytes}）超过了传输层允许的上限 " +
                $"{maxPayload}（报文上限 {_transport.MaxPacketLength} 减去报文头与填充）。",
                nameof(options));
        }

        SshChannel channel;
        TaskCompletionSource<SshChannel> completion;

        lock (_stateLock)
        {
            ThrowIfFaulted();

            // 限额撞满**不断开会话** —— 通道是独立的失败域。原因码是 LimitExceeded（本端的上限），
            // 不是 ChannelOpenFailed：后者说的是对端拒绝，而且曾经连 OpenFailureReason 都没有。
            //
            // 只数 `_channels`：正在打开的那条**也在里面**（见下面的登记），
            // 两个字典加起来会把它数两遍，等于把上限砍了一半。
            if (_channels.Count >= _limits.MaxChannels)
            {
                throw new SshChannelException(
                    SshFailureReason.LimitExceeded,
                    $"本端的并发通道数已达上限 {_limits.MaxChannels}。");
            }

            if (_windowBudgetUsed + window > _limits.SessionWindowBudgetBytes)
            {
                throw new SshChannelException(
                    SshFailureReason.LimitExceeded,
                    $"会话的接收窗口总预算已用尽（{_limits.SessionWindowBudgetBytes / (1024 * 1024)} MiB）。" +
                    "把窗口策略调小，或者少开几条并发通道。");
            }

            uint localId = AllocateChannelId();
            _windowBudgetUsed += window;

            channel = new SshChannel(this, localId, channelType, effective);
            completion = new TaskCompletionSource<SshChannel>(TaskCreationOptions.RunContinuationsAsynchronously);

            // ⚠️ **通道要在发 CHANNEL_OPEN 之前就登记进 `_channels`。**
            //
            // 登记晚了会丢应答，而且是随机丢：收包循环和这里是两个线程，
            // 内存传输上服务端可以在 `SendAsync` 的 await 还没恢复时就把
            // OPEN_CONFIRMATION 送到。那一刻 `OnChannelOpenConfirmation`
            // 会把 `_pendingOpens` 里的项**取走**，再去 `_channels` 里找通道 ——
            // 找不到，于是 `return`，应答就被丢在地上，TCS 永远不完成。
            //
            // 表现是「OpenChannelAsync 挂到用例超时」，而且每次挂的用例都不一样，
            // 因为它只取决于那两个线程谁先跑。满跑约十轮复现一次。
            // 两条 dump 把它钉死：客户端收到的报文号最后一个正是 91
            // （OPEN_CONFIRMATION），而 `_pendingOpens` 已经空了。
            _pendingOpens[localId] = completion;
            _channels[localId] = channel;
        }

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelOpen);
        writer.WriteUtf8String(channelType);
        writer.WriteUInt32(channel.LocalId);
        writer.WriteUInt32((uint)window);
        writer.WriteUInt32((uint)effective.ReceiveMaxPacketBytes);
        writer.WriteRaw(typeSpecificPayload.Span);

        bool enqueued = false;
        try
        {
            // 取消只在入队之前生效（见 EnqueueAsync）：这一步正常返回，CHANNEL_OPEN 就一定会上线。
            await SendAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            enqueued = true;

            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (enqueued)
        {
            // CHANNEL_OPEN 已经发出去了，调用方不等了 —— 但对端迟早会应答。
            //
            // ⚠️ **不能把它从账本里摘掉。**摘了的话，迟到的 OPEN_CONFIRMATION 找不到主、被丢在地上：
            //    服务端那条通道永远不关，每取消一次就占掉它一个 MaxSessions 名额（OpenSSH 默认 10），
            //    之后再开通道全都失败。所以让应答照常走完：确认下来的通道立刻关掉；
            //    对端拒绝、连接断开或释放时，通道已经由那几条路径收尾了。
            //    「确认刚到、取消紧跟着到」的竞态也落在这里 —— 那时 completion 已经带着结果。
            //
            //    拒绝与断线这两种结局也要接住：没人等了，它们的异常留在任务上就是一个没人观察的任务异常，
            //    宿主据此写崩溃日志。曾经这里只接「确认下来」那一种（OnlyOnRanToCompletion）。
            _ = completion.Task.ContinueWith(
                static opened =>
                {
                    if (opened.IsCompletedSuccessfully)
                    {
                        _ = opened.Result.DisposeAsync().AsTask();
                    }
                    else
                    {
                        _ = opened.Exception;   // 看一眼，标成已观察
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
        catch (Exception)
        {
            // 没发出去（入队前取消、连接已经没了），或者对端拒绝了。
            lock (_stateLock)
            {
                _pendingOpens.Remove(channel.LocalId);

                // 对端拒绝时 OnOpenFailed 已经把通道摘掉、预算退回 —— 这里再退一次，
                // 预算就越拒越多，会话窗口总上限形同虚设。
                if (_channels.Remove(channel.LocalId))
                {
                    _windowBudgetUsed -= window;
                }
            }
            throw;
        }
    }

    /// <summary>这条连接上所有 X11 转发共用的入口（按假 cookie 分通道）。</summary>
    internal Forwarding.X11ChannelRouter X11Router
    {
        get
        {
            lock (_stateLock)
            {
                return field ??= new Forwarding.X11ChannelRouter(this);
            }
        }
    }

    /// <summary>给某一类型<b>再挂</b>一个处理器，不影响已有的。</summary>
    /// <remarks>
    /// 通道到达时<b>后登记的先问</b>：它的 <see cref="IIncomingChannelHandler.GetOptionsAsync"/>
    /// 抛异常（「这条不归我」）就问下一个，第一个同意的接走这条通道。
    /// 全都不要才拒绝，拒绝理由取最后一个问到的处理器给的。
    /// </remarks>
    internal void AddIncomingChannelHandler(string channelType, IIncomingChannelHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_stateLock)
        {
            if (!_incomingHandlers.TryGetValue(channelType, out List<IIncomingChannelHandler>? list))
            {
                list = [];
                _incomingHandlers[channelType] = list;
            }
            list.Add(handler);
        }
    }

    /// <summary>摘掉<b>这一个</b>处理器；别人登记的同类型处理器不受影响。</summary>
    internal void RemoveIncomingChannelHandler(string channelType, IIncomingChannelHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_stateLock)
        {
            if (_incomingHandlers.TryGetValue(channelType, out List<IIncomingChannelHandler>? list)
                && list.Remove(handler)
                && list.Count == 0)
            {
                _incomingHandlers.Remove(channelType);
            }
        }
    }

    // ------------------------------------------------------------ 全局请求

    /// <summary>发一个全局请求，把应答（含载荷）带回来。</summary>
    /// <returns><paramref name="wantReply"/> 为假时立刻返回一个成功的空应答。</returns>
    /// <remarks>
    /// 应答载荷要带回来：<c>tcpip-forward</c> 请求端口 0 时，服务端分配的实际端口就在
    /// <c>REQUEST_SUCCESS</c> 的载荷里（一个 <c>uint32</c>）—— 只回成不成的话那个端口号就永远拿不到了。
    /// </remarks>
    internal async ValueTask<SshGlobalRequestReply> SendGlobalRequestAsync(
        string requestType,
        ReadOnlyMemory<byte> payload = default,
        bool wantReply = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!wantReply)
        {
            await SendAsync(EncodeGlobalRequest(requestType, payload, wantReply: false), cancellationToken)
                .ConfigureAwait(false);
            return new SshGlobalRequestReply(true, ReadOnlyMemory<byte>.Empty);
        }

        return await SendGlobalRequestCoreAsync(requestType, payload, onReply: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>发一个要应答的全局请求；应答一到，先在接收循环上同步调用 <paramref name="onReply"/>。</summary>
    /// <remarks>
    /// 给「应答后面紧跟着的报文要用到应答内容」的请求用：远程转发请求端口 0 时，服务端回完应答
    /// 立刻就可能开回连通道，而给回连认路要用应答里的端口。等返回的任务完成再去记的话，
    /// 续体跑在线程池上，接收循环那时可能已经把那条回连当成「没人认领」拒掉了。
    /// <paramref name="onReply"/> 必须又短又不阻塞；连接断了时它拿到的是一个失败的应答。
    /// </remarks>
    internal ValueTask<SshGlobalRequestReply> SendGlobalRequestAsync(
        string requestType,
        ReadOnlyMemory<byte> payload,
        Action<SshGlobalRequestReply> onReply,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SendGlobalRequestCoreAsync(requestType, payload, onReply, cancellationToken);
    }

    private async ValueTask<SshGlobalRequestReply> SendGlobalRequestCoreAsync(
        string requestType,
        ReadOnlyMemory<byte> payload,
        Action<SshGlobalRequestReply>? onReply,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> packet = EncodeGlobalRequest(requestType, payload, wantReply: true);

        // 登记与入队是同一个动作：应答靠 FIFO 对齐（见 SendAsync 的重载说明）。
        Task<SshGlobalRequestReply>? reply = null;
        await SendAsync(packet, () => reply = _globalRequests.Register(onReply), cancellationToken)
            .ConfigureAwait(false);
        return await reply!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ReadOnlyMemory<byte> EncodeGlobalRequest(
        string requestType, ReadOnlyMemory<byte> payload, bool wantReply)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.GlobalRequest);
        writer.WriteUtf8String(requestType);
        writer.WriteBoolean(wantReply);
        writer.WriteRaw(payload.Span);
        return buffer.WrittenMemory;
    }

    /// <summary>发一次保活探测。</summary>
    /// <remarks>
    /// <b>应答内容不重要，有应答就说明链路活着</b> —— 服务端回
    /// <c>REQUEST_FAILURE</c> 也算数（它本来就不认识这个请求类型）。
    /// </remarks>
    internal async ValueTask<bool> SendKeepAliveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SendGlobalRequestAsync(
                SshProtocolNames.KeepAliveOpenSsh, default, wantReply: true, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (SshException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ ISshChannelHost

    /// <inheritdoc />
    ValueTask ISshChannelHost.SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
        SendAsync(packet, cancellationToken);

    /// <inheritdoc />
    ValueTask ISshChannelHost.SendAsync(
        ReadOnlyMemory<byte> packet, Action onEnqueued, CancellationToken cancellationToken) =>
        SendAsync(packet, onEnqueued, cancellationToken);

    /// <inheritdoc />
    ValueTask ISshChannelHost.SendIfAsync(
        ReadOnlyMemory<byte> packet, Func<bool> admit, bool isInteractive, CancellationToken cancellationToken) =>
        EnqueueAsync(packet, applyBackpressure: true, admit, cancellationToken, isInteractive: isInteractive);

    /// <inheritdoc />
    ValueTask ISshChannelHost.SendBorrowedIfAsync(
        ReadOnlyMemory<byte> packet, Func<bool> admit, bool isInteractive, CancellationToken cancellationToken) =>
        EnqueueAsync(packet, applyBackpressure: true, admit, cancellationToken, borrowed: true, isInteractive: isInteractive);

    /// <inheritdoc />
    bool ISshChannelHost.TryReserveWindowBudget(int bytes)
    {
        lock (_stateLock)
        {
            if (_windowBudgetUsed + bytes > _limits.SessionWindowBudgetBytes)
            {
                return false;
            }
            _windowBudgetUsed += bytes;
            return true;
        }
    }

    /// <inheritdoc />
    void ISshChannelHost.ReleaseWindowBudget(int bytes)
    {
        lock (_stateLock)
        {
            _windowBudgetUsed -= bytes;
        }
    }

    /// <inheritdoc />
    void ISshChannelHost.OnChannelClosed(uint localId, int windowBytes)
    {
        lock (_stateLock)
        {
            if (!_channels.Remove(localId))
            {
                return;
            }
            _windowBudgetUsed -= windowBytes;
            RecycleChannelId(localId);
        }
    }

    // ------------------------------------------------------------ 接收循环

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SshInboundPacket packet = await _transport.ReadPacketAsync(cancellationToken).ConfigureAwait(false);

                // 任何入站报文都证明链路活着 —— 不只是保活应答。
                Volatile.Write(ref _lastInboundAt, Time.GetTimestamp());

                if (packet.IsEndOfStream)
                {
                    Fault(new SshConnectionClosedException(
                        SshFailureReason.ClosedByPeer, SshPhase.Open, "对端关闭了连接。"));
                    return;
                }

                await DispatchAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 我们自己要收工（释放，或者 Fault 已经把会话判死）。
        }
        catch (Exception ex)
        {
            // 〔velashell-docs/zh/ssh/spec/08 §六〕对端发来的东西解不开、违反了协议、重协商时验不过主机密钥：
            // 先尽力告诉它为什么断（RFC 4253 §11.1），再判死。曾经一声不吭就断，服务端日志里只有「Connection closed」。
            // 套接字断了、对端在报文中途走了的，发了也没人收（DisconnectReasonFor 给 null）。
            if (Volatile.Read(ref _fault) is null && DisconnectReasonFor(ex) is { } reason)
            {
                await TrySendDisconnectAsync(reason, DisconnectDescription(reason, ex), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            Fault(ex);
        }
        finally
        {
            // _lifetime 只有 Fault 与释放会取消，而 Fault 在取消之前就记下了原因 ——
            // 没有故障就只能是本端释放的。
            CloseAllChannels(Volatile.Read(ref _fault) ?? DisposedReason());
        }
    }

    /// <remarks>
    /// ⚠️ <b>这里的每一条路径都不许等发送。</b>要回的报文一律 <see cref="Post"/>：
    /// 发送泵按投递顺序把它们送出去，而接收循环接着读。
    /// 在这里等发送，TCP 发送缓冲一满，接收循环就停了 —— 而对端正等着我们读，
    /// 才肯去读我们发的东西。
    /// </remarks>
    private async ValueTask DispatchAsync(SshInboundPacket packet, CancellationToken cancellationToken)
    {
        switch (packet.MessageNumber)
        {
            // 对端要重协商。必须应答 —— 不应答的表现是「会话忽然断掉」。
            case SshMessageNumber.KexInit:
                await OnPeerKexInitAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelData:
                await OnChannelDataAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelExtendedData:
                await OnChannelExtendedDataAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelWindowAdjust:
                await OnChannelWindowAdjustAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelOpenConfirmation:
                OnChannelOpenConfirmation(packet.Payload);
                return;

            case SshMessageNumber.ChannelOpenFailure:
                OnChannelOpenFailure(packet.Payload);
                return;

            case SshMessageNumber.ChannelEof:
                Lookup(ReadRecipient(packet.Payload))?.OnEof();
                return;

            case SshMessageNumber.ChannelClose:
                OnChannelClose(packet.Payload);
                return;

            case SshMessageNumber.ChannelRequest:
                OnChannelRequest(packet.Payload);
                return;

            case SshMessageNumber.ChannelSuccess:
            case SshMessageNumber.ChannelFailure:
                await OnChannelRequestReplyAsync(
                    packet.Payload, packet.MessageNumber == SshMessageNumber.ChannelSuccess, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case SshMessageNumber.GlobalRequest:
                OnGlobalRequest(packet.Payload);
                return;

            case SshMessageNumber.RequestSuccess:
            case SshMessageNumber.RequestFailure:
                await OnGlobalRequestReplyAsync(
                    packet.MessageNumber == SshMessageNumber.RequestSuccess,
                    packet.Payload.Length > 1 ? packet.Payload[1..].ToArray() : ReadOnlyMemory<byte>.Empty,
                    cancellationToken).ConfigureAwait(false);
                return;

            case SshMessageNumber.ChannelOpen:
                await OnPeerChannelOpenAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                return;

            // 这几种在任何阶段都可能出现，而且都不需要回应：
            //   · IGNORE / DEBUG —— RFC 4253 §11.2、§11.3；
            //   · UNIMPLEMENTED —— 对 UNIMPLEMENTED 再回 UNIMPLEMENTED 只会让两边互相回声；
            //   · EXT_INFO —— RFC 8308 §2.4 允许服务端在认证成功后再发一次。
            case SshMessageNumber.Ignore:
            case SshMessageNumber.Unimplemented:
                return;

            case SshMessageNumber.Debug:
                OnDebugMessage(packet.Payload);
                return;

            // RFC 8308 §2.4 允许服务端在认证成功后再发一次（OpenSSH 在这一次里宣告 ping@openssh.com 之类）。
            case SshMessageNumber.ExtInfo:
                OnExtensionInfo(packet.Payload);
                return;

            // 〔velashell-docs/zh/ssh/spec/05 §6.5〕对端的 PING 原样回 PONG；我们的 PING 的 PONG 交给等它的那一次测量。
            case SshMessageNumber.Ping:
                OnPing(packet.Payload);
                return;

            case SshMessageNumber.Pong:
                OnPong(packet.Payload);
                return;

            case SshMessageNumber.Disconnect:
                Fault(ReadDisconnect(packet.Payload));
                return;

            default:
                // 未知报文编号：回 UNIMPLEMENTED，不断开（RFC 4253 §11.4）。
                PostUnimplemented();
                return;
        }
    }

    /// <summary>把对端的 <c>DISCONNECT</c> 解成带原因码与原话的异常。</summary>
    /// <remarks>
    /// 对端给的描述常常是唯一有用的信息（<c>Too many authentication failures</c>），
    /// 拼进一句「服务端断开了」就等于把它扔了（原则 3）。
    /// </remarks>
    private static SshConnectionClosedException ReadDisconnect(ReadOnlyMemory<byte> payload)
    {
        SshDisconnectReason? reason = null;
        string? description = null;
        try
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            reader.ReadMessageNumber(SshMessageNumber.Disconnect);
            reason = (SshDisconnectReason)reader.ReadUInt32();
            description = reader.ReadUtf8String(MaxFieldBytes);
        }
        catch (Exception)
        {
            // 残缺的 DISCONNECT 仍然是 DISCONNECT —— 能读出多少算多少。
        }

        string message = "服务端发来了 DISCONNECT" +
            (reason is { } code ? $"（{code}，{(uint)code}）" : "") +
            (string.IsNullOrEmpty(description) ? "。" : $"：{PeerText.Sanitize(description)}");

        return new SshConnectionClosedException(SshFailureReason.Disconnected, SshPhase.Open, message)
        {
            DisconnectReason = reason,
            PeerDescription = description,
        };
    }

    private async ValueTask OnChannelDataAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelData);
        uint recipient = reader.ReadUInt32();
        ReadOnlySequence<byte> data = reader.ReadString(int.MaxValue);

        SshChannel? channel = Lookup(recipient);
        if (channel is null)
        {
            return;   // 刚回收的通道的在途数据 —— 忽略，不断开
        }

        if (await ExceedsMaxPacketAsync(channel, data.Length, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!channel.OnData(data))
        {
            await FaultProtocolAsync(
                $"通道 {recipient} 的对端发来了超出我们宣告窗口的数据 —— 这是明确的协议违规。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask OnChannelExtendedDataAsync(
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelExtendedData);
        uint recipient = reader.ReadUInt32();
        uint dataTypeCode = reader.ReadUInt32();
        ReadOnlySequence<byte> data = reader.ReadString(int.MaxValue);

        SshChannel? channel = Lookup(recipient);
        if (channel is null)
        {
            return;
        }

        if (await ExceedsMaxPacketAsync(channel, data.Length, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!channel.OnExtendedData(dataTypeCode, data))
        {
            await FaultProtocolAsync(
                $"通道 {recipient} 的对端发来了超出我们宣告窗口的扩展数据。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 〔velashell-docs/zh/ssh/spec/05 §八〕单个数据段超出我们宣告的 max packet 是协议违规：判死并返回 <see langword="true"/>。
    /// </summary>
    /// <remarks>曾经不查 —— 有窗口与传输层的上限兜着，没有内存风险，但那是一个照单全收的违规。</remarks>
    private async ValueTask<bool> ExceedsMaxPacketAsync(SshChannel channel, long length, CancellationToken cancellationToken)
    {
        if (length <= channel.ReceiveMaxPacketBytes)
        {
            return false;
        }

        await FaultProtocolAsync(
            $"通道 {channel.LocalId} 的对端发来了 {length} 字节的数据段，超出我们宣告的 max packet（{channel.ReceiveMaxPacketBytes} 字节）。",
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask OnChannelWindowAdjustAsync(
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelWindowAdjust);
        uint recipient = reader.ReadUInt32();
        uint bytes = reader.ReadUInt32();

        SshChannel? channel = Lookup(recipient);
        if (channel is not null && !channel.OnWindowAdjust(bytes))
        {
            await FaultProtocolAsync(
                $"通道 {recipient} 的窗口回补让计数溢出了 uint32 —— 对端在拿它撑爆我们的账。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnChannelOpenConfirmation(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelOpenConfirmation);
        uint recipient = reader.ReadUInt32();
        uint senderChannel = reader.ReadUInt32();
        uint initialWindow = reader.ReadUInt32();
        uint maxPacket = reader.ReadUInt32();

        TaskCompletionSource<SshChannel>? completion;
        SshChannel? channel;
        lock (_stateLock)
        {
            _pendingOpens.Remove(recipient, out completion);
            _channels.TryGetValue(recipient, out channel);
        }

        if (completion is null || channel is null)
        {
            return;
        }

        channel.OnOpenConfirmed(senderChannel, initialWindow, maxPacket);
        completion.TrySetResult(channel);
    }

    private void OnChannelOpenFailure(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelOpenFailure);
        uint recipient = reader.ReadUInt32();
        uint reasonCode = reader.ReadUInt32();
        string description = reader.ReadUtf8String(MaxFieldBytes);

        TaskCompletionSource<SshChannel>? completion;
        SshChannel? channel;
        string channelType = SshProtocolNames.ChannelSession;
        lock (_stateLock)
        {
            // 〔velashell-docs/zh/ssh/spec/05 §八〕只认还在等确认的那种：对一条已经开着的通道发 OPEN_FAILURE 是对端违规，忽略。
            // 曾经照样把通道收尾、**当场还号**（没走双向 CLOSE）—— 对端还以为那条通道开着，30 秒后号被复用就会串话。
            if (!_pendingOpens.Remove(recipient, out completion))
            {
                return;
            }
            if (_channels.TryGetValue(recipient, out channel))
            {
                channelType = channel.ChannelType;
            }
        }

        channel?.OnOpenFailed();
        completion.TrySetException(
            SshChannelException.FromOpenFailure(channelType, reasonCode, description));
    }

    private void OnChannelClose(ReadOnlyMemory<byte> payload)
    {
        uint recipient = ReadRecipient(payload);
        SshChannel? channel = Lookup(recipient);
        if (channel is null)
        {
            return;
        }

        // 还在等确认的通道收到 CLOSE（对端违规）：我们还不知道对端的通道号，回不了 CLOSE；按「没开成」收尾，
        // 让等着开通道的人拿到结局。曾经照常回 CLOSE（带着还是 0 的对端号 —— 关掉的是对端的 0 号通道）、
        // 而等确认的那个 completion 没人结算：不带令牌开通道的调用方就永远挂着。
        TaskCompletionSource<SshChannel>? pendingOpen;
        lock (_stateLock)
        {
            _pendingOpens.Remove(recipient, out pendingOpen);
        }
        if (pendingOpen is not null)
        {
            channel.OnOpenFailed();
            pendingOpen.TrySetException(new SshChannelException(
                SshFailureReason.ChannelOpenFailed, $"对端还没确认通道 {recipient} 就把它关掉了。"));
            return;
        }

        // CLOSE **必须双向**：收到对端的就得回一个（除非我们已经发过）。
        // 「要不要回」与入队在同一把锁里判定 —— 泵正要发的数据要么排在这个 CLOSE 前面，
        // 要么看到「已发」而不再发（见 ISshChannelHost.SendIfAsync）。
        byte[] reply = new byte[5];
        reply[0] = (byte)SshMessageNumber.ChannelClose;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(1), channel.RemoteId);
        PostIf(reply, channel.OnClose, channel.IsInteractive);

        channel.OnCloseCompleted();
    }

    private void OnChannelRequest(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelRequest);
        uint recipient = reader.ReadUInt32();
        string requestType = reader.ReadUtf8String(MaxFieldBytes);
        bool wantReply = reader.ReadBoolean();

        // 只把**类型相关的那一段**交给通道 —— 报文头(编号、通道号、类型、want_reply)
        // 的长度是变的(类型是个变长 string)，让下游自己去跳过它，
        // 迟早会有人按固定长度跳，然后把 exit-status 读成别的数。
        ReadOnlyMemory<byte> typeSpecific = payload[(int)reader.Consumed..];

        SshChannel? channel = Lookup(recipient);
        if (channel is null)
        {
            // 不存在的通道：不回。通道号要等双向 CLOSE 走完才回收，所以这里只剩对端违规一种可能；
            // 而回应答需要**对端的**通道号，我们手上只有自己的 —— 曾经就拿它去填，
            // FAILURE 落在对端一条不相干的通道上（velashell-docs/zh/ssh/spec/05 §8：忽略）。
            return;
        }

        bool handled = channel.OnPeerRequest(requestType, typeSpecific);

        if (!wantReply)
        {
            return;
        }

        // 认得就回 SUCCESS，不认得回 FAILURE —— 但**必须回**。
        // 沉默会让对端的 FIFO 队列永远错位。
        // 例外是我们已经发过 CLOSE：之后这条通道上不许再有任何报文（RFC 4254 §5.3），
        // 对端收到 CLOSE 也就不再等这个应答了。
        byte[] reply = new byte[5];
        reply[0] = (byte)(handled ? SshMessageNumber.ChannelSuccess : SshMessageNumber.ChannelFailure);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(1), channel.RemoteId);
        PostIf(reply, () => !channel.CloseSent, channel.IsInteractive);
    }

    private async ValueTask OnChannelRequestReplyAsync(
        ReadOnlyMemory<byte> payload, bool success, CancellationToken cancellationToken)
    {
        uint recipient = ReadRecipient(payload);
        SshChannel? channel = Lookup(recipient);

        if (channel is not null && !channel.OnRequestReply(success))
        {
            // 队列空时收到应答 = FIFO 失步。再往下走，后面每一个应答
            // 都会被安到错误的请求上，而那是**静默的**错误答案。
            await FaultProtocolAsync(
                $"通道 {recipient} 收到了没有对应请求的应答 —— 请求/应答的 FIFO 已经失步。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnGlobalRequest(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.GlobalRequest);
        string requestType = reader.ReadUtf8String(MaxFieldBytes);
        bool wantReply = reader.ReadBoolean();

        if (requestType == SshProtocolNames.RequestHostKeys)
        {
            OnHostKeysAnnounced(ref reader);
        }

        if (!wantReply)
        {
            return;
        }

        // 〔重要 velashell-docs/zh/ssh/spec/05 §6.4〕对未知全局请求回 REQUEST_FAILURE 是**必须**的，
        // 不能沉默。沉默会让对端的 FIFO 队列永远错位 ——
        // 它下一个请求的应答会被认成这一个的。
        byte[] failure = [(byte)SshMessageNumber.RequestFailure];
        Post(failure);
    }

    private async ValueTask OnGlobalRequestReplyAsync(
        bool success, ReadOnlyMemory<byte> replyPayload, CancellationToken cancellationToken)
    {
        if (!_globalRequests.TryComplete(new SshGlobalRequestReply(success, replyPayload)))
        {
            await FaultProtocolAsync(
                "收到了没有对应全局请求的应答 —— FIFO 已经失步。", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>同时在「问处理器要不要接」的对端开通道请求上限，超出的直接以资源不足拒绝。</summary>
    internal const int MaxPeerOpensDeciding = 64;

    /// <summary>正在后台问处理器的对端开通道请求数。</summary>
    private int _peerOpensDeciding;

    /// <remarks>
    /// <para>
    /// ⚠️ <b>问处理器这一步不在接收循环上等。</b>处理器是使用者写的，它的 <c>GetOptionsAsync</c>
    /// 可能要弹窗问人、查配置、连别的东西 —— 曾经就地 await，它一慢，整条连接上所有通道的收包都停住。
    /// 现在解析与查处理器在这里做完，问与接受交给后台：接收循环立刻回去收包。
    /// </para>
    /// <para>
    /// 对端在我们确认之前不能往这条通道上发任何东西，所以后台完成的确认与接收循环之间没有顺序问题；
    /// 两条开通道请求的确认谁先谁后也不要紧 —— 各自带着对端的通道号。
    /// 同时在问的请求有上限（<see cref="MaxPeerOpensDeciding"/>）：没有的话，对端刷一堆开通道请求、
    /// 处理器又慢，就是一堆挂着的任务。
    /// </para>
    /// </remarks>
    private ValueTask OnPeerChannelOpenAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ChannelOpen);
        string channelType = reader.ReadUtf8String(MaxFieldBytes);
        uint senderChannel = reader.ReadUInt32();
        uint initialWindow = reader.ReadUInt32();
        uint maxPacket = reader.ReadUInt32();

        // ⚠️ **必须复制。**载荷背后是传输的接收缓冲，下一次读包就会被覆盖；
        //    而处理器是在另一个任务上、在接收循环读了别的报文之后才用它。
        ReadOnlyMemory<byte> typeSpecific = payload[(int)reader.Consumed..].ToArray();

        IIncomingChannelHandler[] candidates;
        lock (_stateLock)
        {
            candidates = _incomingHandlers.TryGetValue(channelType, out List<IIncomingChannelHandler>? list)
                ? [.. Enumerable.Reverse(list)]
                : [];
        }

        if (candidates.Length == 0)
        {
            // 没人处理这种类型。**必须明确拒绝**，不能沉默 —— 沉默会让对端一直等着。
            PostOpenFailure(
                senderChannel,
                SshChannelOpenFailureReason.UnknownChannelType,
                $"本端没有登记 {channelType} 的处理器。");
            return default;
        }

        if (Interlocked.Increment(ref _peerOpensDeciding) > MaxPeerOpensDeciding)
        {
            Interlocked.Decrement(ref _peerOpensDeciding);
            PostOpenFailure(
                senderChannel,
                SshChannelOpenFailureReason.ResourceShortage,
                $"同时在决定的开通道请求已达上限 {MaxPeerOpensDeciding}。");
            return default;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await AcceptPeerChannelAsync(
                            channelType, senderChannel, initialWindow, maxPacket, typeSpecific, candidates,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 会话在收工（取消），或者会话已经坏了（Post 不再发）。单条开通道请求没有别的补救。
                }
                finally
                {
                    Interlocked.Decrement(ref _peerOpensDeciding);
                }
            },
            CancellationToken.None);
        return default;
    }

    /// <summary>处理器拒绝时回给对端的原因码（RFC 4254 §5.1）。</summary>
    /// <remarks>
    /// 〔FW-D2〕连不上要连的东西（远程转发的本机目标、agent 转发的本机 agent）回 2（connect failed），
    /// 并发名额满了回 4（resource shortage），其余回 1（administratively prohibited）。曾经一律回 1：
    /// 远端分不出是「不让连」还是「连不上」，服务端日志里也没有 connect failed。
    /// </remarks>
    private static SshChannelOpenFailureReason OpenFailureReasonFor(Exception refusal) => (refusal as SshException)?.Reason switch
    {
        SshFailureReason.TcpRefused or SshFailureReason.TcpTimeout or SshFailureReason.TcpUnreachable
            or SshFailureReason.DnsFailure or SshFailureReason.AgentUnavailable or SshFailureReason.AgentNotRunning
            => SshChannelOpenFailureReason.ConnectFailed,
        SshFailureReason.LimitExceeded => SshChannelOpenFailureReason.ResourceShortage,
        _ => SshChannelOpenFailureReason.AdministrativelyProhibited,
    };

    /// <summary>问处理器要不要接，接的话建通道、回确认、交给处理器（后台执行，见 <see cref="OnPeerChannelOpenAsync"/>）。</summary>
    private async Task AcceptPeerChannelAsync(
        string channelType,
        uint senderChannel,
        uint initialWindow,
        uint maxPacket,
        ReadOnlyMemory<byte> typeSpecific,
        IIncomingChannelHandler[] candidates,
        CancellationToken cancellationToken)
    {
        // 后登记的先问；第一个同意的接走。「不归我」用异常表达（见接口说明）。
        IIncomingChannelHandler? handler = null;
        SshChannelOptions options = SshChannelOptions.Default;
        string refusal = "";
        SshChannelOpenFailureReason refusalCode = SshChannelOpenFailureReason.AdministrativelyProhibited;
        foreach (IIncomingChannelHandler candidate in candidates)
        {
            try
            {
                options = await candidate.GetOptionsAsync(channelType, typeSpecific, cancellationToken)
                    .ConfigureAwait(false);
                handler = candidate;
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 处理器拒绝（例如远程转发的路由表里找不到这个绑定地址）。
                refusal = ex.Message;
                refusalCode = OpenFailureReasonFor(ex);
            }
        }

        if (handler is null)
        {
            PostOpenFailure(senderChannel, refusalCode, refusal);
            return;
        }

        int window = options.WindowPolicy.InitialBytes;
        SshChannel? channel = null;

        lock (_stateLock)
        {
            if (_fault is null
                && _channels.Count < _limits.MaxChannels
                && _windowBudgetUsed + window <= _limits.SessionWindowBudgetBytes)
            {
                uint localId = AllocateChannelId();
                _windowBudgetUsed += window;
                channel = new SshChannel(this, localId, channelType, options);
                _channels[localId] = channel;
            }
        }

        if (channel is null)
        {
            // ⚠️ 处理器在 GetOptionsAsync 里可能已经占了资源（并发槽位）。
            //    这条通道不会走到 HandleAsync 了，得告诉它还回去 —— 否则每拒一次漏一个，
            //    漏满之后这一类通道就再也开不出来了。
            NotifyOpenAborted(handler, channelType, typeSpecific);
            PostOpenFailure(
                senderChannel,
                SshChannelOpenFailureReason.ResourceShortage,
                "本端的通道数或窗口预算已用尽。");
            return;
        }

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelOpenConfirmation);
        writer.WriteUInt32(senderChannel);
        writer.WriteUInt32(channel.LocalId);
        writer.WriteUInt32((uint)window);
        writer.WriteUInt32((uint)options.ReceiveMaxPacketBytes);

        // ① 对端的号、窗口与状态在确认发出之前就设好：对端一收到确认就可能 EOF + CLOSE，
        //    接收循环处理那个 CLOSE 时，回给对端的 CLOSE 要带着它的真实通道号（见 OnOpenAccepted）。
        // ② 确认先入队，泵后启动 —— 泵发出的数据在队列里排在确认之后，
        //    对端一定先认得这条通道，再收到它的数据。
        channel.OnOpenAccepted(senderChannel, initialWindow, maxPacket);
        Post(buffer.WrittenMemory);
        AfterIncomingOpenConfirmationQueued?.Invoke(channel);
        channel.StartPumps();

        // 交给处理器时**不等它** —— 它多半要去连一个本地目标，
        // 而接收循环不能停在任何一条通道上。
        SshChannel accepted = channel;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await handler.HandleAsync(accepted, typeSpecific, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 单条通道的失败不影响会话 —— 通道是独立的失败域。
                }
                finally
                {
                    await accepted.DisposeAsync().ConfigureAwait(false);
                }
            },
            CancellationToken.None);
    }

    private static void NotifyOpenAborted(
        IIncomingChannelHandler handler, string channelType, ReadOnlyMemory<byte> typeSpecific)
    {
        try
        {
            handler.OnOpenAborted(channelType, typeSpecific);
        }
        catch (Exception)
        {
            // 处理器自己的善后出错，不该变成会话的问题。
        }
    }

    private void PostOpenFailure(uint senderChannel, SshChannelOpenFailureReason reason, string description)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelOpenFailure);
        writer.WriteUInt32(senderChannel);
        writer.WriteUInt32((uint)reason);

        // 描述里常常带着对端给的通道类型名（最长 64 KiB）—— 原样回显的话，
        // 每一个畸形的 CHANNEL_OPEN 都换来一个同样大的应答，对端拿它来撑爆我们的发送队列。
        writer.WriteUtf8String(description.Length <= MaxOpenFailureDescription
            ? description
            : description[..MaxOpenFailureDescription] + "…");
        writer.WriteUtf8String("");
        Post(buffer.WrittenMemory);
    }

    /// <summary>回 <c>CHANNEL_OPEN_FAILURE</c> 时描述的最大字符数。</summary>
    private const int MaxOpenFailureDescription = 256;

    /// <summary>回一个 <c>UNIMPLEMENTED</c>。</summary>
    /// <remarks>
    /// 里面要带<b>被拒那个报文的序号</b>（RFC 4253 §11.4），不是 0 ——
    /// 对端靠它知道是哪一个报文没被认出来。刚读完的那个报文用的是
    /// 「当前接收序号 − 1」（序号在取出一帧之后才推进）。
    /// </remarks>
    private void PostUnimplemented()
    {
        byte[] packet = new byte[5];
        packet[0] = (byte)SshMessageNumber.Unimplemented;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(1), unchecked(_transport.ReceiveSequenceNumber - 1));
        Post(packet);
    }

    // ------------------------------------------------------------ 内部

    private static uint ReadRecipient(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadByte();
        return reader.ReadUInt32();
    }

    private SshChannel? Lookup(uint localId)
    {
        lock (_stateLock)
        {
            return _channels.GetValueOrDefault(localId);
        }
    }

    /// <summary>分配一个通道号，优先用「已经放够时间」的回收号。</summary>
    /// <remarks>调用时必须持有 <see cref="_stateLock"/>。</remarks>
    private uint AllocateChannelId()
    {
        // 回收号要放够 ChannelIdReuseDelay 才能再用 —— 对端可能还在路上
        // 发这个号的数据，复用得太早会让那些数据投递到新通道上（串话）。
        if (_recycledIds.TryPeek(out (uint Id, long RecycledAt) head)
            && Time.GetElapsedTime(head.RecycledAt) >= _limits.ChannelIdReuseDelay)
        {
            _recycledIds.Dequeue();
            return head.Id;
        }

        return _nextChannelId++;
    }

    /// <remarks>调用时必须持有 <see cref="_stateLock"/>。</remarks>
    private void RecycleChannelId(uint localId) =>
        _recycledIds.Enqueue((localId, Time.GetTimestamp()));

    /// <summary>对端违反了协议：先把 <c>DISCONNECT</c> 送出去，再把会话判死。</summary>
    /// <remarks>
    /// ⚠️ <b>顺序不能反。</b>先判死的话，发送路径第一步就会因为「已经故障」而拒绝，
    /// <c>DISCONNECT</c> 永远到不了对端 —— 对端只看到连接莫名其妙地断了，
    /// 它的日志里也就没有那句指得到原因的话。
    /// </remarks>
    private async ValueTask FaultProtocolAsync(string message, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _fault) is not null)
        {
            return;
        }

        await TrySendDisconnectAsync(SshDisconnectReason.ProtocolError, message, cancellationToken)
            .ConfigureAwait(false);
        Fault(new SshProtocolException(SshPhase.Open, message));
    }

    /// <summary>尽力发一个 <c>DISCONNECT</c>，最多等 <see cref="DisconnectFlushTimeout"/>。</summary>
    /// <remarks>
    /// 有期限是因为这时链路多半已经不太好了：发不出去不是新问题，
    /// 为它把接收循环挂住才是。
    /// </remarks>
    private async ValueTask TrySendDisconnectAsync(
        SshDisconnectReason reason, string description, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.Disconnect);
        writer.WriteUInt32((uint)reason);
        writer.WriteUtf8String(description);
        writer.WriteUtf8String("");

        Task sending = SendControlAsync(buffer.WrittenMemory, cancellationToken).AsTask();
        try
        {
            await sending.WaitAsync(DisconnectFlushTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 已经在断开的路上了。
            // 等不及（超时、被取消）时这一帧还在发送队列里：链路堵着（对端不读）正是会超时的那种情形，
            // 随后释放停下发送泵，它就以「连接已经关闭」失败 —— 那时已经没人等它了。
            // 把异常看掉：曾经它成了未观察的任务异常，每关一个堵住的连接，宿主的崩溃日志里就多一条。
            _ = sending.ContinueWith(
                static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>发 <c>DISCONNECT</c> 时最多等多久。</summary>
    private static readonly TimeSpan DisconnectFlushTimeout = TimeSpan.FromSeconds(2);

    /// <summary>释放时等收发循环收工最多等多久（到点先释放传输再等一次，见 <see cref="DisposeAsync"/>）。</summary>
    private static readonly TimeSpan LoopShutdownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>等 <paramref name="task"/> 结束，最多 <paramref name="timeout"/>；它自己失败了也算结束。不抛。</summary>
    private static async ValueTask<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception)
        {
            return true;   // 循环自己的失败早已经由 Fault 交代过了
        }
    }

    /// <summary>因这个失败断开时，告诉对端的原因码；不必（或者发了也没人收）时为 <see langword="null"/>。</summary>
    /// <remarks>
    /// <para>
    /// 帧层的失败一律报 <see cref="SshDisconnectReason.ProtocolError"/>，不区分「完整性校验失败」与「格式不对」——
    /// 把校验细节回送给对端是侧信道（见 <c>SshFrameFormatException.IntegrityCheckFailed</c>）。
    /// </para>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/03 §3.7.4〕密钥交换里对端的公开值不合格（<see cref="Crypto.Kex.SshKeyExchangeException"/>：长度、编码、
    /// 不在曲线上、X25519 全零、DH 越界…）报 <see cref="SshDisconnectReason.KeyExchangeFailed"/>，所有方法一律这样 ——
    /// RFC 10042 §2.1 对混合方法是「必须」，RFC 8731 对 curve25519 是「应当」。本端的原因照旧是 <c>ProtocolError</c>。曾经发 2。
    /// </para>
    /// </remarks>
    internal static SshDisconnectReason? DisconnectReasonFor(Exception failure) => failure switch
    {
        Crypto.SshFrameFormatException { PeerClosedMidPacket: true } => null,
        Crypto.SshFrameFormatException or SshWireFormatException or SshProtocolException => SshDisconnectReason.ProtocolError,
        Crypto.Kex.SshKeyExchangeException or SshNegotiationException => SshDisconnectReason.KeyExchangeFailed,
        SshException { Reason: SshFailureReason.HostKeyRejected or SshFailureReason.HostKeyChanged } =>
            SshDisconnectReason.HostKeyNotVerifiable,
        Auth.SshAuthenticationException => SshDisconnectReason.NoMoreAuthMethodsAvailable,
        _ => null,
    };

    /// <summary><c>DISCONNECT</c> 里给对端日志看的那句话（英文：对端的日志是给运维看的，不按本机界面语言）。</summary>
    /// <param name="reason">原因码。</param>
    /// <param name="failure">断开的原因：同是 <see cref="SshDisconnectReason.KeyExchangeFailed"/>，协商不上与公开值不合格说的话不一样。</param>
    internal static string DisconnectDescription(SshDisconnectReason reason, Exception? failure = null) => reason switch
    {
        SshDisconnectReason.ProtocolError => "protocol error",
        SshDisconnectReason.KeyExchangeFailed when failure is Crypto.Kex.SshKeyExchangeException => "key exchange failed: invalid public value",
        SshDisconnectReason.KeyExchangeFailed => "no matching algorithms",
        SshDisconnectReason.HostKeyNotVerifiable => "host key not verifiable",
        SshDisconnectReason.NoMoreAuthMethodsAvailable => "no more authentication methods available",
        SshDisconnectReason.AuthCancelledByUser => "authentication cancelled by user",
        SshDisconnectReason.ByApplication => "disconnected by application",
        _ => reason.ToString(),
    };

    /// <summary>把会话的故障原因归成公开的异常类型。</summary>
    /// <remarks>
    /// <para>
    /// 接收循环与发送泵接到的异常五花八门：套接字的 <see cref="IOException"/>、帧格式与完整性校验的
    /// <see cref="Crypto.SshFrameFormatException"/>、解析报文时的内部格式异常。它们会被原样抛给使用者
    /// （连接上的每个调用、每条通道的读者），而使用者按 <see cref="SshException"/> 与它的
    /// <see cref="SshException.Reason"/> 分流（重连只该对「断了」生效）。原样抛出的话，
    /// 一个内部类型使用者连 catch 都 catch 不到，宿主的异常翻译也认不出它。
    /// </para>
    /// <para>
    /// 归法：I/O 失败与「报文中途断开」→ <see cref="SshFailureReason.ClosedByPeer"/>；
    /// 帧格式、完整性校验、解析失败 → 协议错误；本来就是 <see cref="SshException"/> 的原样；
    /// 取消与释放原样（那是本端在收工，不是故障）；其它意外包一层，原异常留在 InnerException 里。
    /// </para>
    /// </remarks>
    private static Exception NormalizeFault(Exception exception) => exception switch
    {
        SshException or OperationCanceledException or ObjectDisposedException => exception,
        Crypto.SshFrameFormatException { PeerClosedMidPacket: true } =>
            new SshConnectionClosedException(
                SshFailureReason.ClosedByPeer, SshPhase.Open, $"连接断了：{exception.Message}", exception),
        IOException or System.Net.Sockets.SocketException =>
            new SshConnectionClosedException(
                SshFailureReason.ClosedByPeer, SshPhase.Open, $"连接断了：{exception.Message}", exception),
        Crypto.SshFrameFormatException or SshWireFormatException =>
            new SshProtocolException(SshPhase.Open, $"对端违反了协议：{exception.Message}", exception),
        _ => new SshConnectionClosedException(
            SshFailureReason.Unknown, SshPhase.Open, $"连接因意外错误中断：{exception.Message}", exception),
    };

    /// <summary>把会话判死：记下原因、放出信号、叫醒所有在等的人、停下收发。</summary>
    /// <remarks>
    /// <para>
    /// <b>判死就要真的停下来。</b>只记一个「已故障」而让接收循环接着读、
    /// 通道接着开着，会让会话挂在一个「<see cref="IsAlive"/> 是假、却还在收数据」的
    /// 半死状态里 —— 通道的读者等不到结尾，保活判死之后也一样。
    /// </para>
    /// <para>幂等：第一个原因胜出，后面的都是它的连带结果。</para>
    /// <para>
    /// 原因先归一成公开的 <see cref="SshException"/>（见 <see cref="NormalizeFault"/>）再记下 ——
    /// 它会被原样抛给这条连接上的每一个调用者、每一条通道的读者。
    /// </para>
    /// </remarks>
    private void Fault(Exception exception)
    {
        exception = NormalizeFault(exception);

        lock (_stateLock)
        {
            if (_fault is not null)
            {
                return;
            }
            Volatile.Write(ref _fault, exception);
        }

        // 先放出「断了」这个信号，再去收拾等在里面的人 ——
        // 顺序反过来的话，被唤醒的调用方回头去看 Disconnected，会看到它还没取消。
        // 原因先记下：被唤醒的人可能马上就去看 CloseReason。
        _transport.StopPacketTap();
        Complete(exception);
        SignalDisconnected();

        // 还在等开通道的人不会等到应答了。让它们拿到原因，而不是永远挂着。
        foreach (TaskCompletionSource<SshChannel> pending in TakePendingOpens())
        {
            pending.TrySetException(exception);
        }

        _globalRequests.Close(new SshGlobalRequestReply(false, ReadOnlyMemory<byte>.Empty));

        // 停下接收循环、发送泵、保活与重协商监视 —— 它们都挂在这一个令牌上。
        // 回调放到线程池上执行：Fault 常常跑在接收循环或发送泵上，同步执行回调会把
        // 使用者的续体拉到这两个线程上来（见 SshChannel.FinishClose 的说明）。
        Lifecycle.CancelInBackground(_lifetime);

        CloseAllChannels(exception);
    }

    private List<TaskCompletionSource<SshChannel>> TakePendingOpens()
    {
        lock (_stateLock)
        {
            List<TaskCompletionSource<SshChannel>> pending = [.. _pendingOpens.Values];
            _pendingOpens.Clear();
            return pending;
        }
    }

    private static ObjectDisposedException DisposedReason() =>
        new(nameof(SshConnection), "连接已释放，通道随之关闭。");

    /// <summary>会话没了：每条通道跟着收尾，还没读完的读端拿到 <paramref name="reason"/>。</summary>
    private void CloseAllChannels(Exception reason)
    {
        SshChannel[] channels;
        lock (_stateLock)
        {
            channels = [.. _channels.Values];
        }

        foreach (SshChannel channel in channels)
        {
            channel.OnSessionClosed(reason);
        }
    }

    /// <summary>
    /// 放出 <see cref="Disconnected"/>。幂等，而且<b>绝不让回调里的异常冒出来</b> ——
    /// 这条路径上正在处理的是「连接已经没了」，再被一个订阅者的异常打断，
    /// 剩下的清理就做不完了。
    /// </summary>
    /// <remarks>
    /// <see cref="Disconnected"/> 上登记的是<b>使用者的</b>回调（重连、改状态栏）——
    /// 它们在线程池上执行，而不是在调用这里的接收循环 / 发送泵 / 保活线程上。
    /// <see cref="CancellationToken.IsCancellationRequested"/> 仍然是当场变真的。
    /// </remarks>
    private void SignalDisconnected()
    {
        Task? signal = Lifecycle.CancelInBackground(_disconnected);
        if (signal is not null)
        {
            Interlocked.CompareExchange(ref _disconnectedSignal, signal, null);
        }
    }

    /// <summary><see cref="Disconnected"/> 的回调还在线程池上跑着的那个任务；释放前要等它。</summary>
    private Task? _disconnectedSignal;

    /// <summary>连接已经释放或判死时抛 —— 与开通道、发全局请求时同一种异常（释放了是 <see cref="ObjectDisposedException"/>，判死了是那次故障）。</summary>
    internal void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
    }

    private void ThrowIfFaulted()
    {
        Exception? fault = Volatile.Read(ref _fault);
        if (fault is not null)
        {
            throw fault;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _transport.StopPacketTap();
        Complete(new SshConnectionClosedException(SshFailureReason.Aborted, SshPhase.Open, "本端释放了连接。"));
        SignalDisconnected();

        // 〔velashell-docs/zh/ssh/spec/08 §六〕正常收工：先限时冲刷已入队的帧、发 DISCONNECT(BY_APPLICATION)，再停收发。
        // DISCONNECT 排在发送队列的末尾，它出去了就说明前面的帧都出去了 —— 关标签页之前的最后一次输入不会丢；
        // 服务端日志里也有了原因。曾经直接取消：已入队的帧作废，对端只看到连接没了。
        if (Volatile.Read(ref _fault) is null)
        {
            await TrySendDisconnectAsync(
                SshDisconnectReason.ByApplication, DisconnectDescription(SshDisconnectReason.ByApplication), CancellationToken.None)
                .ConfigureAwait(false);
        }

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛。
        }

        _globalRequests.Close(new SshGlobalRequestReply(false, ReadOnlyMemory<byte>.Empty));

        // 还在等开通道的人等不到应答了。Fault 那条路径会结算它们，而释放不走 Fault ——
        // 曾经就漏在这里：关标签页时正在开的 SFTP / 隧道，不带令牌的话就永远挂着。
        foreach (TaskCompletionSource<SshChannel> pending in TakePendingOpens())
        {
            pending.TrySetException(new SshConnectionClosedException(
                SshFailureReason.Aborted, SshPhase.Open, "连接已释放，通道没有打开。"));
        }

        // 本端释放的：读端拿到 ObjectDisposedException —— 使用者据此分得清「自己拆的」与「断线」。
        CloseAllChannels(DisposedReason());

        // 等收发循环收工要有时限：底层流的读写不一定响应取消（Windows 上 ProxyCommand 的匿名管道
        // 在线程池上阻塞完成，architecture.md §11.2.19），曾经那时释放就一直挂着。
        // 到点先释放传输（关掉底层流，卡着的读写随之结束），再等一次。
        var loops = Task.WhenAll(
            new[] { _receiveLoop, _keepAliveLoop, _rekeyMonitorLoop, _sendPump }.OfType<Task>());
        if (!await CompletesWithinAsync(loops, LoopShutdownTimeout).ConfigureAwait(false))
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _ = await CompletesWithinAsync(loops, LoopShutdownTimeout).ConfigureAwait(false);
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();

        // 放在最后：上面那些收尾里还可能有人去读 Disconnected。
        // 它的回调在线程池上跑（SignalDisconnected），先等它们跑完再释放。
        if (Volatile.Read(ref _disconnectedSignal) is { } signal)
        {
            try
            {
                await signal.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 使用者的回调出了错或者太慢 —— 不是释放路径该管的事。
            }
        }

        _disconnected.Dispose();
    }
}
