// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.1  CHANNEL_OPEN / CONFIRMATION / FAILURE
//   RFC 4254 §5.2  CHANNEL_WINDOW_ADJUST / DATA / EXTENDED_DATA
//   RFC 4254 §5.3  CHANNEL_EOF / CHANNEL_CLOSE
//   RFC 4254 §5.4  CHANNEL_REQUEST / SUCCESS / FAILURE
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §1、§3、§4、§5

using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading.Channels;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Channels;

/// <summary>一条 SSH 通道。</summary>
/// <remarks>
/// <para>
/// 数据面是三条管子：<see cref="StandardOutput"/>、<see cref="StandardError"/>、
/// <see cref="StandardInput"/>；其余一切（退出状态、半关闭、关闭）走
/// <see cref="ReadEventAsync"/> 这条有序事件流。
/// </para>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/05 §4.3〕<b>stdout 与 stderr 是两条独立的 <c>PipeReader</c></b>，
/// 不是一个带标志位的读取接口。理由的硬的那一条是：
/// 两条流要能被并发地各读各的。只有一个读接口的话，
/// 调用方轮流读两边，一边读空时另一边可能正在被对端写满 —— 那就是经典的双管道死锁。
/// </para>
/// <para>
/// ⚠️ <b>但两条流共用一个窗口</b>（RFC 4254 §5.2：扩展数据同样计入窗口）。一边不读，
/// 那边的数据堆满窗口之后<b>另一边也会停住</b>。所以要么两边都读，要么把不关心的 stderr 设成
/// <see cref="SshStderrMode.Discard"/>。
/// </para>
/// </remarks>
public sealed class SshChannel : IAsyncDisposable
{
    private const int MaxFieldBytes = 64 * 1024;

    /// <summary>扩展数据里代表 stderr 的类型码（RFC 4254 §5.2）。</summary>
    private const uint ExtendedDataStderr = 1;

    private readonly ISshChannelHost _host;
    private readonly Lock _stateLock = new();

    private readonly Pipe _stdoutPipe;
    private readonly Pipe? _stderrPipe;
    private readonly Pipe _stdinPipe;
    private readonly WindowedPipeReader _stdoutReader;
    private readonly WindowedPipeReader? _stderrReader;

    private readonly SshWindow _receiveWindow;
    private readonly SshWindow _sendWindow;
    private readonly AsyncGate _sendWindowGate = new();

    /// <summary>事件流的终结事件；关了之后 <see cref="ReadEventAsync"/> 一直交回它。</summary>
    private SshChannelEvent.Closed? _closedEvent;

    /// <summary>通道关了时完成。<see cref="WaitForExitAsync"/> 等它，<b>不去读事件流</b>。</summary>
    private readonly TaskCompletionSource _closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Channel<SshChannelEvent> _events =
        Channel.CreateUnbounded<SshChannelEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// 等待应答的通道请求。
    /// </summary>
    /// <remarks>
    /// <b>通道请求的应答没有 id，靠 FIFO 顺序对齐</b>（velashell-docs/zh/ssh/spec/05 §5.1）——
    /// 所以这里是队列而不是字典。用字典就需要一个 id，而协议根本没给。
    /// </remarks>
    private readonly Session.FifoRequestLedger<bool> _pendingRequests = new();

    /// <summary>通道的生命周期；也就是公开出去的 <see cref="Closed"/>。</summary>
    /// <remarks>
    /// <b>不释放它。</b>它的令牌交给了使用者，释放之后再读 <c>Token</c> 会抛；
    /// 而一个没有定时器、没有链接的令牌源本来就没有要还的资源。
    /// </remarks>
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _stdinPump;
    private Task? _windowAdjustPump;

    /// <summary>消费者已消费、但还没回补给对端的字节数。跨轮累加，攒够回补阈值时由回补泵整笔取走。</summary>
    private long _consumedPendingAdjust;
    private readonly AsyncGate _consumedGate = new();
    private int _windowAdjustPumpWakeups;

    /// <summary>回补泵被叫醒过几次（测试用：离阈值还远时不该叫醒它）。</summary>
    internal int WindowAdjustPumpWakeups => Volatile.Read(ref _windowAdjustPumpWakeups);

    /// <summary>stdin 泵已经交给会话发送的字节数（<see cref="WaitStandardInputSentAsync"/> 用）。</summary>
    private long _stdinSentBytes;

    /// <summary>stdin 泵每交出一段、或者泵退出时响一次。</summary>
    private readonly AsyncGate _stdinProgressGate = new();

    /// <summary>stdin 泵已经退出（之后不会再有进展）。</summary>
    private volatile bool _stdinPumpExited;

    /// <summary>接收窗口的伸缩策略。</summary>
    private readonly SshWindowPolicy _windowPolicy;

    /// <summary>连续几轮没有把窗口用尽。</summary>
    private int _idleRounds;

    /// <summary>上一轮回补以来，接收窗口有没有被对端吃到见底。</summary>
    /// <remarks>
    /// <b>这才是「窗口是瓶颈」的真信号。</b>
    /// 用「两次回补的间隔很短」当信号是不对的：局域网上间隔本来就短，
    /// 那会让窗口一路涨到上限，白占几十 MiB 内存 —— 而那条链路上
    /// 窗口根本不是瓶颈。
    /// </remarks>
    private volatile bool _windowWasExhausted;

    /// <summary>交给了读的一方、还没被读走的字节数（伸缩判据用，见 <see cref="NoteWindowPressure"/>）。</summary>
    private long _unreadBytes;

    /// <summary>读的一方上一次读空了在等数据，是几次回补之前的事（见 <see cref="NoteWindowPressure"/>）。</summary>
    /// <remarks>接收循环写 0，回补泵每次回补加一。起始值很大：还没收到数据时不算「等过」。</remarks>
    private int _adjustsSinceStarved = int.MaxValue / 2;

    /// <summary>收到过数据了（第一包到来之前读的一方本来就空着，那不算「等」）。</summary>
    private bool _receivedAnyData;

    /// <summary>上一包数据到达的时刻（<see cref="Stopwatch.GetTimestamp"/>；只有接收循环碰）。</summary>
    private long _lastArrivalAt;

    /// <summary>
    /// 这一轮里读的一方读空之后、等下一包等得最长的一段（<see cref="Stopwatch"/> 刻度）。
    /// 接收循环往大里写，回补泵每轮取走清零（见 <see cref="ResizeWindowIfNeeded"/> 的「空等」）。
    /// </summary>
    private long _roundLongestWait;

    /// <summary>上一轮的最长空等；扩过一次窗口就作废（只有回补泵碰）。</summary>
    private long _previousRoundLongestWait;

    /// <summary>最近两次回补的时刻（只有回补泵碰；0 = 还没有）。</summary>
    private long _lastAdjustAt, _adjustBeforeLastAt;

    private SshChannelState _state = SshChannelState.Opening;

    /// <summary><c>CHANNEL_CLOSE</c> 已经<b>入队</b>（不是「打算发」）。之后这条通道上不许再有任何报文。</summary>
    private bool _closeSent;
    private bool _closeReceived;

    /// <summary>对端的 <c>CHANNEL_CLOSE</c> 到的时候我们还没发 —— 是对端先关的。</summary>
    private bool _peerClosedFirst;

    /// <summary>通道号已经还给会话。</summary>
    private bool _idReleased;
    private bool _disposed;

    /// <summary><see cref="MayStillSend"/> 的缓存委托 —— 每一帧都要用，别每次分配。</summary>
    private readonly Func<bool> _mayStillSend;

    /// <summary>发 stdin 数据帧用的放行判定：同 <see cref="_mayStillSend"/>，另把结论记进 <see cref="_dataAdmitted"/>。</summary>
    /// <remarks>数据帧只有 stdin 泵一个发送方、一帧一帧地发，所以一个字段就够记，不必每帧分配一个闭包。</remarks>
    private readonly Func<bool> _admitData;

    /// <summary>上一个 stdin 数据帧有没有放行（<see cref="_admitData"/> 写，泵在那一帧发完之后读）。</summary>
    private bool _dataAdmitted;

    /// <summary>这条通道此刻计在会话窗口总预算上的字节数。</summary>
    /// <remarks>
    /// 开通道时会话按初始窗口计了一笔；自适应扩窗、缩窗各自追加或退回。关闭时按这个数退 ——
    /// 曾经按「当前窗口大小」退：扩过的窗口从没计过，却按扩后的大小退，预算越退越多，
    /// 会话窗口总上限形同虚设。在 <see cref="_stateLock"/> 里读写。
    /// </remarks>
    private int _budgetCharged;

    internal SshChannel(
        ISshChannelHost host,
        uint localId,
        string channelType,
        SshChannelOptions options)
    {
        _host = host;
        LocalId = localId;
        ChannelType = channelType;
        _mayStillSend = MayStillSend;
        _admitData = () => _dataAdmitted = MayStillSend();

        _windowPolicy = options.WindowPolicy;
        ReceiveMaxPacketBytes = options.ReceiveMaxPacketBytes;
        IsInteractive = options.IsInteractive;
        int window = options.WindowPolicy.InitialBytes;
        _receiveWindow = new SshWindow(window);
        _budgetCharged = window;   // 会话开通道时按它计的
        _sendWindow = new SshWindow(0);   // 真正的值要等 OPEN_CONFIRMATION

        // 管道的暂停水位定得比窗口高：窗口本身就是背压机制，
        // 对端**不可能**发来超过窗口的未消费数据，所以写这一侧永远不该阻塞。
        //
        // 让它阻塞是有害的：接收循环是所有通道共用的，卡在一条通道上
        // 会把其它通道一起饿死（队头阻塞）。而且 `WriteToPipe` 正是靠
        // 「flush 必定同步完成」才敢不等它 —— 一旦真的顶到水位，
        // 那条路会在**上一次 flush 还没完成时继续写同一个 PipeWriter**，
        // 那是对 Pipe 的误用，症状是随机的
        // 「Reading is not allowed after reader was completed」。
        //
        // ⚠️ **水位要按 `MaximumBytes` 算，不是 `InitialBytes`。**
        // 自适应策略下窗口会一路长到上限；按起步值算水位的话，
        // 窗口一旦长过 2×起步，上面那个「不可能」就不成立了 ——
        // 这不是理论风险，它在自适应用例里被复现了出来（约八轮一次）。
        // 固定策略下 Maximum == Initial，这个改动对它没有任何影响。
        PipeOptions pipeOptions = new(
            pauseWriterThreshold: _windowPolicy.MaximumBytes * 2L,
            resumeWriterThreshold: _windowPolicy.MaximumBytes,
            useSynchronizationContext: false);

        _stdoutPipe = new Pipe(pipeOptions);
        _stderrPipe = options.StderrMode == SshStderrMode.Buffer ? new Pipe(pipeOptions) : null;
        _stderrTailLimit = _stderrPipe is null ? options.DiscardedStderrTailBytes : 0;
        _stdinPipe = new Pipe(new PipeOptions(useSynchronizationContext: false));

        // 收尾的读端在 _stateLock 里标记，TryDeliver 在同一把锁里看（见 WindowedPipeReader.ReleaseUnread）。
        StandardOutput = _stdoutReader = new WindowedPipeReader(_stdoutPipe.Reader, NoteReaderConsumed, _stateLock);
        _stderrReader = _stderrPipe is null ? null : new WindowedPipeReader(_stderrPipe.Reader, NoteReaderConsumed, _stateLock);
        StandardError = _stderrReader ?? (PipeReader)new EmptyPipeReader();
        StandardInput = new StandardInputWriter(_stdinPipe.Writer, AcceptsInput);
    }

    /// <summary>我们给这条通道的编号。</summary>
    public uint LocalId { get; }

    /// <summary>对端给这条通道的编号。<c>CHANNEL_OPEN_CONFIRMATION</c> 之后才有效。</summary>
    public uint RemoteId { get; private set; }

    /// <summary>通道类型（<c>"session"</c> / <c>"direct-tcpip"</c> / …）。</summary>
    public string ChannelType { get; }

    /// <summary>对端确认（或者我们确认对端开过来）的时刻；还在打开、或者没开成时为 <see langword="null"/>。</summary>
    /// <remarks>连接信息里列「这条隧道开了多久」用它；曾经没有，每通道只有字节数。</remarks>
    public DateTimeOffset? OpenedAt => Volatile.Read(ref _openedAtTicks) is var ticks and > 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero)
        : null;

    private long _openedAtTicks;

    /// <summary>通道<b>整个</b>结束（状态进入 <see cref="SshChannelState.Closed"/>）时被取消。</summary>
    /// <remarks>
    /// 回调在线程池上执行，不在接收循环上。
    /// 与 <see cref="SshChannelState.RemoteEof"/> 不同：EOF 只是对端不再发，往它写仍然有意义；
    /// 到了这里，两个方向都没有了。
    /// </remarks>
    public CancellationToken Closed => _lifetime.Token;

    /// <summary>通道为什么关闭；还没关时是 <see cref="SshChannelCloseReason.Unknown"/>。</summary>
    /// <remarks>
    /// <para>
    /// 在 stdout / stderr 读到头之前就已经记下：读端看到结尾时来看它，就分得出「对端关的（远端进程退了）」「会话没了」「本端关的」。
    /// 只收到 EOF、还没收到 CLOSE 时读端也会读到头，那时它还是 <see cref="SshChannelCloseReason.Unknown"/>。
    /// </para>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/05 §一〕事件流里也有（<see cref="SshChannelEvent.Closed"/>），但事件流是单读者的；这里谁来问、问几次都一样。
    /// 曾经只在事件流里，宿主只好从读管道的结束方式反推「远端退出 / 断线 / 本端拆除」。
    /// </para>
    /// </remarks>
    public SshChannelCloseReason CloseReason => Volatile.Read(ref _closedEvent)?.Reason ?? SshChannelCloseReason.Unknown;

    /// <summary>当前状态。</summary>
    public SshChannelState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>对端宣告的单个数据段上限。<b>发送时必须遵守</b>。</summary>
    public int RemoteMaxPacketBytes { get; private set; }

    /// <summary>一个 <c>CHANNEL_DATA</c> 的数据段最多多大：本端报文长度上限减去报文头、通道头与最大填充。</summary>
    private const int MaxSendDataBytes = Crypto.SshPacketFormat.DefaultMaxPacketLength - 512;

    /// <summary>我们宣告的单个数据段上限（<c>maximum packet size</c>）：对端发来的每个 DATA / EXTENDED_DATA 都不许超过它。</summary>
    internal int ReceiveMaxPacketBytes { get; }

    /// <summary>这条通道发的报文走交互道（<see cref="SshChannelOptions.IsInteractive"/>）；开通道时定下，之后不变 —— 中途换道会让它自己的报文乱序。</summary>
    internal bool IsInteractive { get; }

    /// <summary>远端的标准输出。</summary>
    /// <remarks>
    /// <para>
    /// <b>读完（<c>IsCompleted</c> 且没抛）只说明对端自己收了尾</b>：发了 <c>CHANNEL_EOF</c>
    /// 或 <c>CHANNEL_CLOSE</c>。连接中途断了，读会抛出连接的故障（<see cref="Diagnostics.SshException"/>）；
    /// 本端释放了连接，读会抛 <see cref="ObjectDisposedException"/>。
    /// </para>
    /// <para>
    /// 这样区分是因为「断线」与「对端说完了」在读的一方眼里必须不同：当成读完的话，
    /// 下载到一半的文件、跑到一半的命令输出都会被当成完整结果交出去，
    /// 终端也分不清是用户敲了 <c>exit</c> 还是链路断了（后者才该自动重连）。
    /// 断线之前已经收到、还没读走的那部分随之作废 —— 反正结果已经不完整了。
    /// </para>
    /// </remarks>
    public PipeReader StandardOutput { get; }

    /// <summary>
    /// 远端的标准错误。
    /// </summary>
    /// <remarks>
    /// <see cref="SshStderrMode.Discard"/> 时这是一条**立刻结束的空流** ——
    /// 不是一条永远不返回的流，那会让调用方挂死。
    /// 连接中途断了的时候读会抛，与 <see cref="StandardOutput"/> 一样。
    /// </remarks>
    public PipeReader StandardError { get; }

    /// <summary>写进去的内容变成 <c>CHANNEL_DATA</c>。</summary>
    /// <remarks>
    /// <para>
    /// 完成这个 writer（<c>Complete</c> / <c>CompleteAsync</c>）等同于 <see cref="SendEofAsync"/>：
    /// 已写入的内容冲干净之后发 <c>CHANNEL_EOF</c>。要等 EOF 真正入队再往下走，用 <see cref="SendEofAsync"/>。
    /// </para>
    /// <para>
    /// 通道关了之后（本端开始关闭、任一方向的 CLOSE 已经入队、或者连接断了），<c>FlushAsync</c> / <c>WriteAsync</c>
    /// <b>当场</b>返回 <see cref="FlushResult.IsCompleted"/> 为真（<see cref="PipeWriter"/> 表达「读的一方不要了」的惯用法），
    /// 写进来的不再发出，不抛异常。这个 writer 归调用方：库不替它完成。
    /// </para>
    /// </remarks>
    public PipeWriter StandardInput { get; }

    /// <summary>经这条通道发出的应用数据字节数（<c>CHANNEL_DATA</c> 的数据段，不含协议开销）。</summary>
    /// <remarks>传输面板、隧道统计里每条通道的数字。线上的总流量看 <c>SshConnection.BytesSent</c>。</remarks>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>经这条通道收到的应用数据字节数（<c>CHANNEL_DATA</c> 与 <c>CHANNEL_EXTENDED_DATA</c> 的数据段，读没读走都算）。</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    private long _bytesSent;
    private long _bytesReceived;

    /// <summary>开着的通道数记没记上 +1（收尾时据此 -1）；在 <see cref="_stateLock"/> 里读写。</summary>
    private bool _activeMetered;

    /// <summary>度量的 <c>host</c> 标签（来自会话）；<see langword="null"/> 时不记。</summary>
    internal string? MetricsHost => _host.MetricsHost;

    /// <summary>当前的接收窗口剩余（诊断用）。</summary>
    public uint ReceiveWindowRemaining => _receiveWindow.Remaining;

    /// <summary>
    /// 接收窗口当前的**额定大小**。自适应策略下它会随链路情况变。
    /// </summary>
    public int ReceiveWindowSize => _receiveWindow.Size;

    /// <summary>当前的发送窗口剩余（诊断用）。</summary>
    public uint SendWindowRemaining => _sendWindow.Remaining;

    // ------------------------------------------------------------ 事件

    /// <summary>读下一件事。</summary>
    /// <remarks>
    /// 事件流以一条 <see cref="SshChannelEvent.Closed"/> 结尾。通道关了之后再读，
    /// <b>永远得到那同一条 <see cref="SshChannelEvent.Closed"/></b> —— 不抛异常，
    /// 等退出状态的几个调用方（<c>WaitAsync</c> 被调了两次之类）不必各自去接一个 BCL 的异常类型。
    /// </remarks>
    public async ValueTask<SshChannelEvent> ReadEventAsync(CancellationToken cancellationToken = default)
    {
        while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_events.Reader.TryRead(out SshChannelEvent? channelEvent))
            {
                NoteEventRead(channelEvent);
                return channelEvent;
            }
        }

        return Volatile.Read(ref _closedEvent)
            ?? throw new InvalidOperationException("事件流结束了却没有 Closed 事件 —— 这是库的 bug。");
    }

    /// <summary>读走一条对端请求带来的事件，积压计数减一（见 <see cref="OnPeerRequest"/>）。</summary>
    private void NoteEventRead(SshChannelEvent? channelEvent)
    {
        if (channelEvent is SshChannelEvent.PeerRequest or SshChannelEvent.FlowControl)
        {
            Interlocked.Decrement(ref _queuedPeerRequests);
        }
    }

    /// <summary>把这条通道当成一条双向字节流（读 stdout、写 stdin）。</summary>
    /// <param name="ownsChannel">释放流时是否一并关掉通道。</param>
    /// <remarks>
    /// <c>direct-tcpip</c> / <c>direct-streamlocal</c> 这类隧道通道最常这么用 ——
    /// 交给只认 <see cref="Stream"/> 的 API（<c>SslStream</c>、HTTP 客户端的连接回调、拨号器）。
    /// </remarks>
    public SshChannelStream AsStream(bool ownsChannel = true) => new(this, ownsChannel);

    /// <summary>等通道关闭，交回退出状态（<see cref="SshCommand.WaitAsync"/> 与 <see cref="SshShell.WaitAsync"/> 共用）。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/05 §5.4〕退出状态缓存在通道上（<see cref="_exitEvent"/>），不只活在事件流里 ——
    /// 事件流是单读者的，读过一次就没了：曾经 <c>ReadToEndAsync</c> 之后再 <c>WaitAsync</c>、或者调用方自己读过事件，
    /// 再问就是 null。现在调多少次、谁先读过事件，答案都一样。
    /// <para>
    /// 等的是关闭信号，<b>不读事件流</b>：事件流归调用方（<see cref="ReadEventAsync"/>）。曾经这里循环读事件直到 <c>Closed</c>，
    /// 与调用方自己的读者抢同一条单读者流 —— 并发时谁拿到退出状态说不准，先等完再读的话事件已经被它读光了。
    /// </para>
    /// </remarks>
    internal async ValueTask<SshExitStatus> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _closedSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        // 收到 CLOSE 时可能还没有退出状态 —— 对端实现不规范，或者连接断了。
        // 那时 ExitCode 是 null，那不是 bug 而是事实：进程到底怎么结束的，我们不知道。
        return Volatile.Read(ref _exitEvent) switch
        {
            SshChannelEvent.ExitStatus status => new SshExitStatus(status.Code, null, false, null),
            SshChannelEvent.ExitSignal signal => new SshExitStatus(null, signal.SignalName, signal.CoreDumped, signal.ErrorMessage),
            _ => new SshExitStatus(null, null, false, null),
        };
    }

    /// <summary>对端报来的那一份退出状态 / 退出信号（只收第一份）。在进事件流之前写下。</summary>
    private SshChannelEvent? _exitEvent;

    /// <summary>给远端进程发信号（<see cref="SshCommand.SendSignalAsync"/> 与 <see cref="SshShell.SendSignalAsync"/> 共用）。</summary>
    internal async ValueTask SendSignalAsync(string signalName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(signalName);

        if (signalName.StartsWith("SIG", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"信号名不带 SIG 前缀：用 \"{signalName[3..]}\" 而不是 \"{signalName}\"（RFC 4254 §6.9）。",
                nameof(signalName));
        }

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String(signalName);

        // RFC 4254 §6.9 明确要求 want_reply 为假。
        await SendRequestAsync(
            SshProtocolNames.RequestSignal, buffer.WrittenMemory, wantReply: false, cancellationToken)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 发送

    /// <summary>发一个通道请求。</summary>
    /// <param name="requestType">请求类型。</param>
    /// <param name="payload">类型相关的数据。</param>
    /// <param name="wantReply">要不要等应答。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 对端是否接受；<paramref name="wantReply"/> 为假时，发出去了就是 <see langword="true"/>。
    /// 通道已经关了（<c>CHANNEL_CLOSE</c> 已发）时请求不会上线，返回 <see langword="false"/>。
    /// </returns>
    public async ValueTask<bool> SendRequestAsync(
        string requestType,
        ReadOnlyMemory<byte> payload = default,
        bool wantReply = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelRequest);
        writer.WriteUInt32(RemoteId);
        writer.WriteUtf8String(requestType);
        writer.WriteBoolean(wantReply);
        writer.WriteRaw(payload.Span);

        // ⚠️ 关了之后不许再发（RFC 4254 §5.3）。对端双向 CLOSE 走完之后就可以复用它的通道号，
        //    一个迟到的 window-change / signal 会落到**另一个会话**上；要应答的请求还会把
        //    那条新通道的应答队列搅乱。
        if (!wantReply)
        {
            bool sent = false;
            await _host.SendIfAsync(buffer.WrittenMemory, () => sent = MayStillSend(), IsInteractive, cancellationToken)
                .ConfigureAwait(false);
            return sent;
        }

        // 登记与入队是**同一个动作**：应答靠 FIFO 对齐。
        // 先发后登记，一个快到的应答会发现账本是空的；先登记后发（中间隔着背压等待），
        // 并发的两个请求可能登记顺序与上线顺序相反，而那会把应答安到对方头上。
        Task<bool>? reply = null;
        await _host.SendIfAsync(
            buffer.WrittenMemory,
            () =>
            {
                // 账本已经关了（通道在本端收尾了）也不发：发出去的话对端的应答回来时没人认领。
                if (!MayStillSend())
                {
                    return false;
                }
                reply = _pendingRequests.TryRegister();
                return reply is not null;
            },
            IsInteractive, cancellationToken).ConfigureAwait(false);

        return reply is not null && await reply.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>这条通道上还能不能发报文：<c>CHANNEL_CLOSE</c> 入队之后就不能了。</summary>
    /// <remarks>在会话的入队锁里被调用（见 <see cref="ISshChannelHost.SendIfAsync"/>）。</remarks>
    private bool MayStillSend()
    {
        lock (_stateLock)
        {
            return !_closeSent;
        }
    }

    /// <summary>
    /// stdin 还收不收：本端开始关闭、任一方向的 CLOSE 已经入队、或者通道已经收尾（含连接断了）之后就不收了。
    /// </summary>
    /// <remarks>
    /// 交给调用方的 <see cref="StandardInput"/> 写之前问它（见 <see cref="StandardInputWriter"/>）。
    /// 只看 <c>_closeSent</c> 不够：连接断了时通道直接收尾，一个 CLOSE 也没发。
    /// </remarks>
    private bool AcceptsInput()
    {
        lock (_stateLock)
        {
            return !_closeSent && _state is not (SshChannelState.Closing or SshChannelState.Closed);
        }
    }

    /// <summary>CLOSE 是否已经入队。接收循环回复对端请求之前要看它。</summary>
    internal bool CloseSent
    {
        get
        {
            lock (_stateLock)
            {
                return _closeSent;
            }
        }
    }

    /// <summary>
    /// 这边不再要对端的标准输出了：本端把 <see cref="StandardOutput"/> 收尾、之后到的数据丢弃，并告诉对端（<c>eow@openssh.com</c>）。
    /// </summary>
    /// <returns>
    /// 告诉对端的请求有没有发出去：对端不是 OpenSSH、或者通道已经在关时为 <see langword="false"/> —— 那时只在本端丢弃，
    /// 远端进程照常跑完。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/05 §5.2〕场景是只要输出的前几行（<c>head</c>、<c>grep -m 1</c> 那样）：读够了就不读了。
    /// OpenSSH 的服务端收到之后关掉远端进程的输出端，进程再写就收到 <c>SIGPIPE</c> 提前结束，不再白跑完、白占带宽；
    /// 通道仍然开着，退出状态照常回来，标准输入也还能写。与 <c>CHANNEL_EOF</c> 方向相反、同样不是关闭通道。
    /// </para>
    /// <para>
    /// <b>只发给 OpenSSH</b>（按服务端的版本标识串）：这是 OpenSSH 的扩展，有的实现收到不认识的通道请求会直接断开连接。
    /// 不要应答、不占窗口。本端丢弃的数据照常回补窗口，对端不会因此停住。标准错误不受影响。
    /// </para>
    /// </remarks>
    public async ValueTask<bool> StopStandardOutputAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 读端完成之后，到达的数据在 TryDeliver 里就被判为「消费者不读了」：丢弃并立刻回补窗口。
        await StandardOutput.CompleteAsync().ConfigureAwait(false);

        return _host.PeerAcceptsEndOfWrite
            && await SendRequestAsync(SshProtocolNames.RequestEndOfWrite, default, wantReply: false, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>发 <c>CHANNEL_EOF</c>：我们不再发数据了。</summary>
    /// <remarks>
    /// <b>这不是关闭通道。</b>发完之后仍然可以继续收对端的数据。
    /// </remarks>
    public async ValueTask SendEofAsync(CancellationToken cancellationToken = default)
    {
        if (!TryMarkLocalEof())
        {
            return;
        }

        // 先把 stdin 里还没发出去的内容冲干净，再发 EOF ——
        // 反过来会让最后一段数据排在 EOF 之后，对端多半已经不读了。
        // 〔velashell-docs/zh/ssh/spec/05 §4.3〕EOF 由 stdin 泵在冲干净之后发（用通道自己的生命周期，不用这里的令牌）：
        // 这里被取消只是不再等，EOF 照样会发出去。曾经由这里发：等泵的时候被取消，EOF 就永远不会再发，
        // 远端的 cat / sort 一直等输入。
        await _stdinPipe.Writer.CompleteAsync().ConfigureAwait(false);
        if (_stdinPump is not null)
        {
            try
            {
                await _stdinPump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 泵自己出的错已经反映在通道状态上了，这里不重复报。
            }
        }

        // 泵正常收尾时已经发了（_eofOwed 已是 0）。泵从没起来（通道还没确认）、或者是出错退出的：在这里补发。
        // 对端可能已经 CLOSE 了 —— 那时不发，入队时一并判定。
        if (Interlocked.Exchange(ref _eofOwed, 0) == 1)
        {
            await _host.SendIfAsync(SimplePacket(SshMessageNumber.ChannelEof), _mayStillSend, IsInteractive, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>本端已经推进到 EOF、而 <c>CHANNEL_EOF</c> 还没发：1；否则 0。谁把它换成 0 谁发（只发一次）。</summary>
    private int _eofOwed;

    /// <summary>把状态推进到「本端已 EOF」；已经 EOF 过或者通道在关，返回 <see langword="false"/>。</summary>
    /// <remarks>
    /// EOF 只发一次：<see cref="SendEofAsync"/> 与 stdin 泵（调用方直接完成了 <see cref="StandardInput"/>）
    /// 都从这里过，谁先推进状态谁发。
    /// </remarks>
    private bool TryMarkLocalEof()
    {
        lock (_stateLock)
        {
            if (_state is SshChannelState.LocalEof or SshChannelState.BothEof
                or SshChannelState.Closing or SshChannelState.Closed)
            {
                return false;
            }
            _state = _state == SshChannelState.RemoteEof
                ? SshChannelState.BothEof
                : SshChannelState.LocalEof;
            Volatile.Write(ref _eofOwed, 1);
            return true;
        }
    }

    /// <summary>等 stdin 泵把累计 <paramref name="totalBytes"/> 字节都交给会话发送。</summary>
    /// <exception cref="IOException">通道先关了，还有字节没发出去。</exception>
    /// <remarks>
    /// 给 <see cref="SshChannelStream.FlushAsync(CancellationToken)"/> 用：写入 stdin 只是进了本地管道，
    /// 窗口不够时它们会在管道里一直等 —— 「冲刷」要等的正是这一段。
    /// </remarks>
    internal async ValueTask WaitStandardInputSentAsync(long totalBytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            // 先取票、再查条件、最后等票（见 AsyncGate）。
            Task ticket = _stdinProgressGate.NextChange();

            long sent = Interlocked.Read(ref _stdinSentBytes);
            if (sent >= totalBytes)
            {
                return;
            }

            // 看标记而不是看泵的 Task：泵在 finally 里响铃时，它的 Task 还没完成。
            if (_stdinPump is null || _stdinPumpExited)
            {
                throw new IOException(
                    $"通道 {LocalId} 已经关闭，还有 {totalBytes - sent} 字节没有发出去。");
            }

            await ticket.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>关闭通道：发出 <c>CHANNEL_CLOSE</c>，不等对端的那一个。</summary>
    /// <remarks>
    /// <para>
    /// <b><c>CLOSE</c> 必须双向</b>，只有双方都发过之后通道号才可以回收 —— 对端的那一个到了，
    /// 通道号才还给会话（见 <see cref="ReleaseId"/>）。
    /// </para>
    /// <para>
    /// 取消只在 CLOSE 入队之前生效；被取消的话它还没发，之后再调用一次（或者释放通道）会重新发。
    /// </para>
    /// </remarks>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_closeSent)
            {
                return;
            }

            // 先把状态改掉：泵看到它就不再取新的数据。
            if (_state != SshChannelState.Closed)
            {
                _state = SshChannelState.Closing;
            }
        }

        try
        {
            // 「已发」这个标记在入队锁里、与入队同一时刻设上（TryCommitClose）。
            // 曾经是先设标记、后发 —— 发送被取消（释放通道有 5 秒时限）的话，标记已经是真的，
            // CLOSE 却永远不会再发：服务端那条通道一直开着，远端进程也一直跑着。
            await _host.SendIfAsync(SimplePacket(SshMessageNumber.ChannelClose), TryCommitClose, IsInteractive, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 会话已经没了。那样的话通道当然也关了，不必再报一次；号也不必再等对端。
            FinishClose(SshChannelCloseReason.SessionClosed, ex);
            ReleaseId(force: true);
            return;
        }

        MaybeFinishClose(SshChannelCloseReason.ClosedLocally);
    }

    /// <summary>在入队锁里把「CLOSE 已发」设上；已经发过就不再发。</summary>
    private bool TryCommitClose()
    {
        lock (_stateLock)
        {
            if (_closeSent)
            {
                return false;
            }
            _closeSent = true;
            return true;
        }
    }

    private byte[] SimplePacket(SshMessageNumber number)
    {
        byte[] packet = new byte[5];
        packet[0] = (byte)number;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), RemoteId);
        return packet;
    }

    // ------------------------------------------------------------ 会话回调

    /// <summary>我们开的通道被对端确认了。</summary>
    internal void OnOpenConfirmed(uint remoteId, uint initialWindow, uint maxPacket)
    {
        OnOpenAccepted(remoteId, initialWindow, maxPacket);
        StartPumps();
    }

    /// <summary>记下对端的通道号、初始窗口与包上限，通道进入 Open（泵还没起）。</summary>
    /// <remarks>
    /// <para>
    /// <b>对端开过来的通道，确认发出去之前就要做完这一步。</b>曾经是先发确认、后设对端的号：
    /// 对端收到确认立刻 EOF + CLOSE（端口扫描、健康检查），而决定开通道的后台任务恰好在两步之间被抢占，
    /// 接收循环先处理了那个 CLOSE —— 我们的 CLOSE 带着还是 0 的对端号发了出去，关掉的是对端的 0 号通道
    /// （往往是用户的第一条 shell），真正那条通道永远收不到 CLOSE。
    /// </para>
    /// <para>
    /// 已经收尾了的（会话断开与确认撞在一起）不改回 Open —— 曾经无条件改，关掉的通道又被「复活」、泵照样起来。
    /// </para>
    /// </remarks>
    internal void OnOpenAccepted(uint remoteId, uint initialWindow, uint maxPacket)
    {
        RemoteId = remoteId;

        // 对端宣告的 max packet 按本端的上限截断：照单全收的话，宣告 4 GiB 的对端会让 stdin 泵把管道里攒着的
        // 几 MiB 拼成一个报文发出去 —— 超过任何实现愿意收的报文长度（包括我们自己收的上限）。
        RemoteMaxPacketBytes = (int)Math.Min(maxPacket, MaxSendDataBytes);
        _sendWindow.Add(initialWindow);

        bool opened = false;
        lock (_stateLock)
        {
            if (_state == SshChannelState.Opening)
            {
                _state = SshChannelState.Open;
                Volatile.Write(ref _openedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                opened = _activeMetered = MetricsHost is not null;
            }
        }

        if (opened && MetricsHost is { } host)
        {
            KeyValuePair<string, object?> type = Diagnostics.SshMetrics.ChannelTypeTag(ChannelType);
            Diagnostics.SshMetrics.ChannelsActive.Add(1, Diagnostics.SshMetrics.HostTag(host), type);
            Diagnostics.SshMetrics.ChannelWindow.Record(_receiveWindow.Size, Diagnostics.SshMetrics.HostTag(host), type);
        }
    }

    /// <summary>起 stdin 泵与回补泵。已经收尾了就不起。</summary>
    /// <remarks>
    /// 与 <see cref="FinishClose"/> 在同一把锁里对「泵起没起」达成一致：收尾时泵没起，由收尾替它完成 stdin 的 reader；
    /// 起了就归泵自己完成 —— 两边各自判断的话，收尾刚完成 reader、泵就起来去读它。
    /// </remarks>
    internal void StartPumps()
    {
        lock (_stateLock)
        {
            if (_state == SshChannelState.Closed)
            {
                return;
            }

            _stdinPump = Task.Run(() => PumpStandardInputAsync(_lifetime.Token));
            _windowAdjustPump = Task.Run(() => PumpWindowAdjustAsync(_lifetime.Token));
        }

        _sendWindowGate.Signal();
    }

    internal void OnOpenFailed()
    {
        FinishClose(SshChannelCloseReason.ClosedByPeer);

        // 对端从没建起这条通道，不会再有发往这个号的报文。
        ReleaseId(force: true);
    }

    /// <summary>窗口额定大小变了：记进 <c>velashell.ssh.channel.window</c>。</summary>
    private void RecordWindow(int size)
    {
        if (MetricsHost is { } host && Diagnostics.SshMetrics.ChannelWindow.Enabled)
        {
            Diagnostics.SshMetrics.ChannelWindow.Record(
                size, Diagnostics.SshMetrics.HostTag(host), Diagnostics.SshMetrics.ChannelTypeTag(ChannelType));
        }
    }

    /// <summary>收到 <c>CHANNEL_DATA</c>。</summary>
    /// <returns>窗口够不够。<see langword="false"/> 是对端的协议违规。</returns>
    internal bool OnData(ReadOnlySequence<byte> data)
    {
        int length = (int)data.Length;
        if (!_receiveWindow.TryConsume(length))
        {
            return false;
        }
        Interlocked.Add(ref _bytesReceived, length);

        NoteWindowPressure();

        if (!TryDeliver(_stdoutPipe.Writer, _stdoutReader, data))
        {
            // 没交出去（通道在关、对端已 EOF、消费者不读了）—— 丢弃，
            // 且**立刻回补窗口**：不然「丢弃」就变成了让对端停住的死锁。
            NoteConsumed(length);
        }
        else
        {
            Interlocked.Add(ref _unreadBytes, length);
        }
        return true;
    }

    /// <summary>收到 <c>CHANNEL_EXTENDED_DATA</c>。</summary>
    internal bool OnExtendedData(uint dataTypeCode, ReadOnlySequence<byte> data)
    {
        int length = (int)data.Length;

        // 扩展数据**同样计入窗口**。忘了这一点的症状是
        // stderr 大量输出时窗口被吃空，整条通道停住。
        if (!_receiveWindow.TryConsume(length))
        {
            return false;
        }
        Interlocked.Add(ref _bytesReceived, length);

        NoteWindowPressure();

        // 〔决策 velashell-docs/zh/ssh/spec/05 §4.2〕非 stderr 的类型码：丢弃、计入窗口、不报错。
        // 保留值的语义未来可能被定义，为它断开会让我们无法与新实现共处。
        if (dataTypeCode != ExtendedDataStderr || _stderrPipe is null || _stderrReader is null
            || !TryDeliver(_stderrPipe.Writer, _stderrReader, data))
        {
            if (dataTypeCode == ExtendedDataStderr && _stderrTailLimit > 0)
            {
                KeepStderrTail(data);
            }

            // 丢弃也要立刻回补窗口 —— 不然「丢弃」就变成了死锁。
            NoteConsumed(length);
        }
        else
        {
            Interlocked.Add(ref _unreadBytes, length);
        }
        return true;
    }

    /// <summary>丢弃 stderr 时留住的末尾上限（<see cref="SshChannelOptions.DiscardedStderrTailBytes"/>）；0 不留。</summary>
    private readonly int _stderrTailLimit;

    private readonly Lock _stderrTailLock = new();
    private byte[]? _stderrTail;
    private int _stderrTailLength;

    /// <summary>把丢弃的 stderr 记进末尾缓冲：只留最后 <see cref="_stderrTailLimit"/> 字节。</summary>
    private void KeepStderrTail(ReadOnlySequence<byte> data)
    {
        lock (_stderrTailLock)
        {
            _stderrTail ??= new byte[_stderrTailLimit];
            ReadOnlySequence<byte> incoming = data.Length > _stderrTailLimit ? data.Slice(data.Length - _stderrTailLimit) : data;
            int keep = Math.Min(_stderrTailLength, _stderrTailLimit - (int)incoming.Length);
            _stderrTail.AsSpan(_stderrTailLength - keep, keep).CopyTo(_stderrTail);
            incoming.CopyTo(_stderrTail.AsSpan(keep));
            _stderrTailLength = keep + (int)incoming.Length;
        }
    }

    /// <summary>丢弃的 stderr 的末尾，按 UTF-8 解、照对端文本清洗过；没留或没有时为 <see langword="null"/>。</summary>
    internal string? DiscardedStderrTail
    {
        get
        {
            lock (_stderrTailLock)
            {
                if (_stderrTail is null || _stderrTailLength == 0)
                {
                    return null;
                }
                string text = System.Text.Encoding.UTF8.GetString(_stderrTail, 0, _stderrTailLength);
                return Diagnostics.PeerText.SanitizeTail(text) is { Length: > 0 } clean ? clean : null;
            }
        }
    }

    /// <summary>收到 <c>CHANNEL_WINDOW_ADJUST</c>。</summary>
    /// <returns>加完有没有溢出 <c>uint32</c>。溢出是协议违规。</returns>
    internal bool OnWindowAdjust(uint bytes)
    {
        if (!_sendWindow.Add(bytes))
        {
            return false;
        }
        _sendWindowGate.Signal();
        return true;
    }

    /// <summary>收到 <c>CHANNEL_EOF</c>。</summary>
    internal void OnEof()
    {
        lock (_stateLock)
        {
            // 重复的 EOF 什么也不做：曾经它把 BothEof 退回 RemoteEof（本端随后可能再发一次 EOF），
            // 每来一份还往事件流里塞一条 Eof —— 同样不受积压上限约束。
            if (_state is SshChannelState.Closing or SshChannelState.Closed
                or SshChannelState.RemoteEof or SshChannelState.BothEof)
            {
                return;
            }
            _state = _state == SshChannelState.LocalEof
                ? SshChannelState.BothEof
                : SshChannelState.RemoteEof;
        }

        // 对端不再发数据 —— 把读这一侧收尾，消费者读到 IsCompleted。
        // 状态已经在锁里改掉了，TryDeliver 不会再往这两条管道里写（见它的说明）。
        CompleteReceivePipes();
        _events.Writer.TryWrite(new SshChannelEvent.Eof());
    }

    /// <summary>收尾两条接收管道。调用前状态必须已经在锁里改成不再接收的值。</summary>
    /// <param name="failure">
    /// 不是对端收的尾（连接断了、被释放了）时的原因：读的一方拿到它，而不是一个像 EOF 的「读完」。
    /// 已经因为对端的 EOF 收过尾的管道不受影响 —— 第二次完成是空操作。
    /// </param>
    private void CompleteReceivePipes(Exception? failure = null)
    {
        _stdoutPipe.Writer.Complete(failure);
        _stderrPipe?.Writer.Complete(failure);
    }

    /// <summary>收到 <c>CHANNEL_CLOSE</c>。</summary>
    /// <returns>要不要回一个 <c>CHANNEL_CLOSE</c>。</returns>
    internal bool OnClose()
    {
        bool mustReply;
        lock (_stateLock)
        {
            _closeReceived = true;
            mustReply = !_closeSent;
            _peerClosedFirst = mustReply;
            _closeSent = true;
        }

        return mustReply;
    }

    /// <summary>双向 CLOSE 都走完了（收到对端的那一个之后调用）。</summary>
    internal void OnCloseCompleted()
    {
        // 原因看谁先发的 CLOSE。曾经一律记成 ClosedByPeer：本端 CloseAsync 先发、对端照规矩回一个，
        // 事件流里也说是「对端关的」。
        bool peerClosedFirst;
        lock (_stateLock)
        {
            peerClosedFirst = _peerClosedFirst;
        }

        // 本端先收尾过（释放了通道）的话，状态早就是 Closed，FinishClose 什么都不做 ——
        // 但号一直扣着在等的正是这一个 CLOSE。
        FinishClose(peerClosedFirst ? SshChannelCloseReason.ClosedByPeer : SshChannelCloseReason.ClosedLocally);
        ReleaseId(force: false);
    }

    /// <summary>收到通道请求的应答。</summary>
    /// <returns><see langword="false"/> 说明 FIFO 失步了：账本还开着，却没有人在等。</returns>
    /// <remarks>
    /// <para>
    /// 账本已经关了（本端收尾过这条通道）时，迟到的应答照单吸收，不算失步。
    /// 典型的时序：exec 已经发出，调用方等应答时取消了 → 释放通道，CLOSE 发出、账本关掉、
    /// 通道号还扣着等对端的 CLOSE → 对端按顺序先回 exec 的应答，再回 CLOSE。
    /// 那个应答是合法的在途报文（RFC 4254 §5.3；velashell-docs/zh/ssh/spec/05 §1 规则 4），
    /// 在途的请求在关账时已经以「没成」结算过了。
    /// </para>
    /// <para>
    /// 曾经它被判成失步，整条连接以 PROTOCOL_ERROR 断开 —— 同一连接上的终端、SFTP、隧道一起陪葬，
    /// 而起因只是一个带超时的探测命令。
    /// </para>
    /// </remarks>
    internal bool OnRequestReply(bool success) =>
        _pendingRequests.TryComplete(success) || _pendingRequests.IsClosed;

    /// <summary>收到对端发来的通道请求。</summary>
    /// <returns>我们是否「认得」它。不认得且对端要应答时，调用方要回 <c>CHANNEL_FAILURE</c>。</returns>
    internal bool OnPeerRequest(string requestType, ReadOnlyMemory<byte> payload)
    {
        // 退出状态 / 退出信号每条通道只有一次（RFC 4254 §6.10）：只收第一份，之后的照样「认得」但丢掉。
        // 曾经每来一份都进事件流，而它们不受下面那个积压上限约束 —— 对端不停地发 exit-signal
        // （每条带两个最长 64 KiB 的字符串），没人读事件流时内存无界增长，完全绕过窗口流控。
        if (requestType is SshProtocolNames.RequestExitStatus or SshProtocolNames.RequestExitSignal
            && Interlocked.Exchange(ref _exitReported, 1) != 0)
        {
            return true;
        }

        if (requestType == SshProtocolNames.RequestExitStatus)
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            SshChannelEvent.ExitStatus status = new((int)reader.ReadUInt32());
            Volatile.Write(ref _exitEvent, status);
            _events.Writer.TryWrite(status);
            return true;
        }

        if (requestType == SshProtocolNames.RequestExitSignal)
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            string signalName = reader.ReadUtf8String(MaxFieldBytes);
            bool coreDumped = reader.ReadBoolean();
            string message = reader.ReadUtf8String(MaxFieldBytes);
            SshChannelEvent.ExitSignal signal = new(signalName, coreDumped, message);
            Volatile.Write(ref _exitEvent, signal);
            _events.Writer.TryWrite(signal);
            return true;
        }

        if (requestType == SshProtocolNames.RequestXonXoff)
        {
            // 〔velashell-docs/zh/ssh/spec/05 §5.4〕类型化成一条事件，最近一次的值留在属性上。
            // 事件与未知请求一起受下面那个积压上限约束：对端可以来回翻个不停。
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            bool clientCanDo = reader.ReadBoolean();
            Volatile.Write(ref _clientFlowControl, clientCanDo ? 1 : 0);
            if (Volatile.Read(ref _queuedPeerRequests) < MaxQueuedEvents
                && _events.Writer.TryWrite(new SshChannelEvent.FlowControl(clientCanDo)))
            {
                Interlocked.Increment(ref _queuedPeerRequests);
            }
            return true;
        }

        // 不认识的请求攒成事件等使用者去读。**有上限**：没人读事件流的话（大多数使用者只读 stdout），
        // 对端每发一条就白占一份内存（载荷最长 256 KiB）—— 它绕过了窗口流控，
        // 是一条不花对端任何代价的内存放大。超出上限的直接丢掉，照样回 FAILURE（调用方会回）。
        // 自己计数：单读者的无界 Channel 不支持 Count（CanCount 为假）。
        if (Volatile.Read(ref _queuedPeerRequests) >= MaxQueuedEvents)
        {
            return false;
        }

        // ⚠️ **必须复制。**载荷背后是传输的接收缓冲，下一次读包就会被覆盖，
        //    而事件的消费者是在那之后才去读它的。
        if (_events.Writer.TryWrite(new SshChannelEvent.PeerRequest(requestType, payload.ToArray())))
        {
            Interlocked.Increment(ref _queuedPeerRequests);
        }
        return false;
    }

    /// <summary>事件流里最多积压多少条没读的对端未知请求，超过就不再收。</summary>
    /// <remarks>
    /// 退出状态、EOF、关闭这几件不受它限制 —— 它们每条通道只进事件流一次（重复的照样丢掉，
    /// 见 <see cref="OnPeerRequest"/> 与 <see cref="OnEof"/>）。
    /// </remarks>
    internal const int MaxQueuedEvents = 64;

    /// <summary>已经收到过退出状态或退出信号了。</summary>
    private int _exitReported;

    /// <summary>最近一次 <c>xon-xoff</c> 的值：-1 没收到过，0 / 1。</summary>
    private int _clientFlowControl = -1;

    /// <summary>
    /// 服务端最近一次说的「客户端能不能在本地做 ^S / ^Q 流控」（<c>xon-xoff</c>，RFC 4254 §6.8）；没说过时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>每次变化同时以 <see cref="SshChannelEvent.FlowControl"/> 进事件流。</remarks>
    public bool? ClientMayDoFlowControl => Volatile.Read(ref _clientFlowControl) switch
    {
        0 => false,
        1 => true,
        _ => null,
    };

    /// <summary>事件流里还没被读走的 <see cref="SshChannelEvent.PeerRequest"/> 条数。</summary>
    private int _queuedPeerRequests;

    /// <summary>会话没了。</summary>
    /// <param name="reason">连接的故障；本端释放连接时是 <see cref="ObjectDisposedException"/>。</param>
    internal void OnSessionClosed(Exception reason)
    {
        FinishClose(SshChannelCloseReason.SessionClosed, reason);
        ReleaseId(force: true);
    }

    // ------------------------------------------------------------ 内部

    private bool IsClosedOrClosing()
    {
        lock (_stateLock)
        {
            return _state is SshChannelState.Closing or SshChannelState.Closed;
        }
    }

    /// <summary>把收到的数据交给消费者。</summary>
    /// <returns>
    /// 交出去了为 <see langword="true"/>；因为通道在关、对端已经 EOF、
    /// 或者消费者已经不读了而丢弃为 <see langword="false"/>。
    /// </returns>
    /// <remarks>
    /// ⚠️ <b>「看状态」与「写管道」必须在同一把锁里。</b>
    /// 分开的话，接收循环刚看完「还开着」，用户线程就 Dispose 了通道、
    /// 把管道收了尾 —— 接着那一次写就撞上一个已完成的 <see cref="PipeWriter"/>，
    /// 异常冒到接收循环里，<b>整条会话</b>因为一条通道的关闭而被判死。
    /// 收尾一侧（<see cref="FinishClose"/>、<see cref="OnEof"/>）总是先在锁里改状态、
    /// 再完成管道，所以锁里看到「还开着」时，管道一定还没被完成。
    /// </remarks>
    private bool TryDeliver(PipeWriter writer, WindowedPipeReader reader, ReadOnlySequence<byte> data)
    {
        lock (_stateLock)
        {
            // CLOSE 之后收到的是在途数据 —— 丢弃，且**不报错**（velashell-docs/zh/ssh/spec/05 §1 规则 4）。
            // EOF 之后再来数据是对端的错，但为它断开整条会话不值得 —— 丢弃即可。
            if (_state is SshChannelState.Closing or SshChannelState.Closed
                or SshChannelState.RemoteEof or SshChannelState.BothEof)
            {
                return false;
            }

            // 读的一方在收尾：它清点完就完成读端，这时再写进去的会随读端一起丢掉、没人回补。
            if (reader.IsAbandoned)
            {
                return false;
            }

            return WriteToPipe(writer, data);
        }
    }

    /// <summary>把收到的数据写进管道。</summary>
    /// <returns>消费者已经不读了（读端完成）时为 <see langword="false"/>。</returns>
    /// <exception cref="InvalidOperationException">
    /// 管道顶到了暂停水位 —— 说明窗口记账坏了，见方法体里的说明。
    /// </exception>
    private static bool WriteToPipe(PipeWriter writer, ReadOnlySequence<byte> data)
    {
        foreach (ReadOnlyMemory<byte> segment in data)
        {
            writer.Write(segment.Span);
        }

        // 不等 FlushAsync：接收循环是所有通道共用的，等在一条通道上
        // 会把其它通道一起饿死。管道的水位定得比窗口高，这里不该真的阻塞。
        ValueTask<FlushResult> flush = writer.FlushAsync();
        if (flush.IsCompletedSuccessfully)
        {
            // 读端完成了：消费者不要这条流了。这批字节永远不会被「消费」，
            // 调用方要替它回补窗口，不然对端会停在一个再也不会涨的窗口上。
            return !flush.Result.IsCompleted;
        }

        // 走到这里说明水位被顶住了 —— **按上面的水位设置，这不该发生**：
        // 未消费的数据受接收窗口限制，而水位是窗口上限的两倍。
        //
        // 真走到了，说明我们自己的窗口记账坏了。此时**绝不能接着写** ——
        // 上一次 flush 还没完成就再写同一个 PipeWriter 是对 Pipe 的误用，
        // 它不会当场报错，只会让管道在之后的某个时刻抛出一句
        // 看不出所以然的「Reading is not allowed after reader was completed」。
        //
        // 所以这里把它变成一条**指着真正原因**的错误，而不是继续糊下去。
        _ = ObserveFlushAsync(flush);
        throw new InvalidOperationException(
            $"通道接收管道顶到了暂停水位 —— 这意味着未消费数据超过了接收窗口上限，" +
            $"是窗口记账的 bug。本次写入 {data.Length} 字节。请带着这条消息提 issue。");
    }

    private static async Task ObserveFlushAsync(ValueTask<FlushResult> flush)
    {
        try
        {
            _ = await flush.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 管道的读那一侧已经收尾了。写不进去不是错误，是消费者不要了。
        }
    }

    /// <summary>消费者消费掉了一些字节 —— 该给对端补窗口了。</summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/05 §3.2〕<b>窗口挂在消费上，不挂在接收上。</b>
    /// 消费者不读，窗口就不补，对端自然停下来 —— 背压是结构性的，
    /// 不需要额外的限流器，也不会出现「内部队列无限涨」。
    /// </remarks>
    private void NoteConsumed(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        // 〔CH-P2〕攒够回补阈值才叫醒回补泵。曾经每消费一次都叫：消费者读得碎（逐行读几十字节）时，
        // 每一次都是新建票、注册令牌、一次线程池调度，而绝大多数都是白醒 —— 离阈值还远。
        // 不会漏醒：阈值只随窗口大小变，而窗口只由回补泵自己改，改完它接着重查。
        if (Interlocked.Add(ref _consumedPendingAdjust, bytes) >= AdjustThreshold)
        {
            _consumedGate.Signal();
        }
    }

    /// <summary>
    /// 攒够这么多已消费的字节再发 <c>WINDOW_ADJUST</c>：半个窗口。
    /// </summary>
    /// <remarks>
    /// 每消费几十字节就发一个是在拿控制报文淹没链路。攒着不发也不会卡住对端：
    /// 本地未消费的数据接近 0 时，对端手上至少还剩半个窗口。
    /// </remarks>
    private long AdjustThreshold => _receiveWindow.Size / 2;

    private async Task PumpWindowAdjustAsync(CancellationToken cancellationToken)
    {
        try
        {
            // ⚠️ 已消费、但还没告诉对端的字节数必须**跨轮累加**（_consumedPendingAdjust 本身就是那个累加器，
            //    够了阈值才整笔取走）。攒不够阈值就把这一轮的 consumed 丢掉的话，
            //    对端视角的窗口会一点一点缩小，最后归零 —— 症状是传了一阵子之后
            //    通道永久停住，而本地账面上看窗口明明是满的。
            //    消费者每次只读几十字节（逐行读）时最容易撞上。
            while (!cancellationToken.IsCancellationRequested)
            {
                Task ticket = _consumedGate.NextChange();

                if (Volatile.Read(ref _consumedPendingAdjust) >= AdjustThreshold)
                {
                    long unreported = Interlocked.Exchange(ref _consumedPendingAdjust, 0);

                    // 扩窗要**当场把多出来的额度授予对端**。
                    //
                    // 只改本地的 Size 而不多授一点，结果是：阈值（Size/2）涨了，
                    // 而对端手上的窗口没涨 —— 它发不出更多数据，我们也就攒不够
                    // 下一次回补的量。两边一起停住，谁都不动。
                    long extra = ResizeWindowIfNeeded();

                    long grant = unreported + extra;
                    if (grant > 0)
                    {
                        uint delta = (uint)Math.Min(grant, uint.MaxValue);
                        _receiveWindow.Add(delta);
                        await SendWindowAdjustAsync(delta, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                await ticket.WaitAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _windowAdjustPumpWakeups);
            }
        }
        catch (OperationCanceledException)
        {
            // 通道关了，正常收场。
        }
        catch (Exception)
        {
            // 会话没了。窗口回补没有补救动作可做 —— 通道本来也用不下去了。
        }
    }

    /// <summary>对端把窗口吃到见底了 —— 记下来，给伸缩用。</summary>
    /// <remarks>
    /// <para>
    /// 「见底」不要求恰好为 0：剩下不到 1/8 的时候，对端已经在拿「还能发多少」当限制了。
    /// </para>
    /// <para>
    /// ⚠️ <b>但要分清是谁让它见底的。</b>读的一方慢，数据堆在管道里没人读，回补就不发，窗口一样见底 ——
    /// 那时窗口不是瓶颈，扩窗换不来吞吐，只是让这条通道多缓着几十 MiB 没读的数据。
    /// 曾经不分：消费者一慢，窗口就一路翻到上限。
    /// </para>
    /// <para>
    /// 分得清的信号是「读的一方有没有读空了在等」：窗口不够（BDP 大于窗口）时，读得快的一方把数据读空，
    /// 要空等一个往返下一轮才到；读得慢时管道里总有没读完的，永远读不空。
    /// 所以额度见了底、而且最近读空过，才算窗口太小（在 <see cref="ResizeWindowIfNeeded"/> 里合起来判）。
    /// 通道的第一包不算读空 —— 那时本来就空着。
    /// 按比例划一条「未读少于几分之几」的线不行：那条线会被时序偶然跨过，窗口就时涨时落。
    /// </para>
    /// </remarks>
    private void NoteWindowPressure()
    {
        long now = Stopwatch.GetTimestamp();
        if (_receivedAnyData && Interlocked.Read(ref _unreadBytes) <= 0)
        {
            Volatile.Write(ref _adjustsSinceStarved, 0);
            NoteIdleWait(now - _lastArrivalAt);
        }
        _receivedAnyData = true;
        _lastArrivalAt = now;

        if (_receiveWindow.Remaining <= (uint)(_receiveWindow.Size / 8))
        {
            _windowWasExhausted = true;
        }
    }

    /// <summary>读的一方读空之后等了 <paramref name="waited"/>（<see cref="Stopwatch"/> 刻度）才来下一包：留住这一轮最长的一段。</summary>
    private void NoteIdleWait(long waited)
    {
        long longest = Volatile.Read(ref _roundLongestWait);
        while (waited > longest)
        {
            long seen = Interlocked.CompareExchange(ref _roundLongestWait, waited, longest);
            if (seen == longest)
            {
                return;
            }
            longest = seen;   // 回补泵刚取走清零，或者别的写入抢先：按新值再比
        }
    }

    /// <summary>
    /// 这一轮读的一方有没有<b>空等</b>额度：这一轮与上一轮里最长的一段空等，占两轮时长（上上次回补到这一次）的 1/4 以上。
    /// 每次回补调一次（顺带把这一轮的记录挪成「上一轮」）。窗口不到 4 个报文时恒为 false —— 那时只认见底。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/05 §3.3〕窗口有好几个报文、数据按带宽一包一包陆续到达时，「见底」看不出窗口是瓶颈：
    /// 回补在半个窗口处就发出去，对端空等这笔额度的那一个往返里，账上的剩余已经被它涨回去了，到不了 1/8。
    /// 曾经只认见底：Docker 的端口转发（有效往返约 8 ms）上窗口停在 256 KiB，SSH 只有 30 MB/s。
    /// </para>
    /// <para>
    /// 分得清「等额度」与「等带宽」的是节奏：等额度时数据成批到、批与批之间空着接近一个往返；等带宽时匀速到，
    /// 最长的空档只是一个报文的传输时间，占两轮（一个窗口）的「报文 / 窗口」—— 4 个报文以上的窗口里不到 1/4。
    /// </para>
    /// </remarks>
    private bool IdledForCredit(int window)
    {
        long now = Stopwatch.GetTimestamp();
        long thisRound = Interlocked.Exchange(ref _roundLongestWait, 0);
        long longest = Math.Max(thisRound, _previousRoundLongestWait);
        long span = _adjustBeforeLastAt == 0 ? 0 : now - _adjustBeforeLastAt;
        _previousRoundLongestWait = thisRound;
        _adjustBeforeLastAt = _lastAdjustAt;
        _lastAdjustAt = now;
        return window >= 4 * ReceiveMaxPacketBytes && span > 0 && longest * 4 >= span;
    }

    /// <summary>读的一方读走了这么多（经 <see cref="WindowedPipeReader"/>）。</summary>
    private void NoteReaderConsumed(long bytes)
    {
        Interlocked.Add(ref _unreadBytes, -bytes);
        NoteConsumed(bytes);
    }

    /// <summary>按链路情况调整窗口。</summary>
    /// <returns>因为扩窗而<b>额外要授予对端</b>的字节数。</returns>
    /// <remarks>
    /// <para>
    /// 固定窗口的问题在于它<b>同时决定了吞吐上限</b>（<c>窗口 / RTT</c>）：
    /// 2 MiB / 200 ms ≈ 10 MB/s，跨洋链路上怎么也跑不过这个数。
    /// 所以窗口要能长到带宽时延积那么大。
    /// </para>
    /// <para>
    /// <b>判据是「窗口有没有吃紧」</b>（被吃到见底，或者读的一方空等额度，见 <see cref="IdledForCredit"/>），
    /// 不是「两次回补隔了多久」。后者在局域网上永远成立，会让窗口一路涨到上限白占内存 ——
    /// 而那条链路上窗口根本不是瓶颈。
    /// </para>
    /// <para>
    /// 反向也要有：连续几轮都没吃紧，说明窗口开大了，缩回去。
    /// 缩窗的做法是<b>少授一点</b> —— 已经给出去的额度收不回来，
    /// 只能靠「这一轮少给」让它慢慢回落。
    /// </para>
    /// </remarks>
    private long ResizeWindowIfNeeded()
    {
        if (!_windowPolicy.IsAdaptive)
        {
            return 0;
        }

        int current = _receiveWindow.Size;

        // 「最近读空过」看的是这一轮与上一轮：回补常常发生在一轮数据还没收完的时候，
        // 读空（在一轮的开头）与见底（在一轮的末尾）就被那次回补隔在了两边。
        int sinceStarved = Volatile.Read(ref _adjustsSinceStarved);
        Interlocked.CompareExchange(ref _adjustsSinceStarved, Math.Min(sinceStarved + 1, int.MaxValue / 2), sinceStarved);
        bool starvedLately = sinceStarved <= 1;

        // 吃紧：见底，或者读的一方空等了额度（见 IdledForCredit）。它每轮都得调（顺带把这一轮的记录挪成「上一轮」），
        // 所以先单独调，不放进会短路的条件里。
        bool idled = IdledForCredit(current);
        if (idled || _windowWasExhausted)
        {
            _windowWasExhausted = false;
            _idleRounds = 0;

            if (!starvedLately)
            {
                return 0;   // 见底是读的一方跟不上造成的：扩窗只会多缓一堆没读的数据
            }

            int grown = (int)Math.Min((long)current * 2, _windowPolicy.MaximumBytes);
            if (grown <= current)
            {
                return 0;
            }

            // 扩出来的每一个字节都是对端可以塞进来、我们得缓着的内存 —— 先向会话的窗口总预算申请。
            // 曾经扩窗从不计预算：开通道时只计初始窗口，之后每条通道都能长到上限（默认 64 MiB），
            // 256 MiB 的会话总上限就只管得住「开通道那一刻」。
            if (!TryChargeBudget(grown - current))
            {
                return 0;
            }

            _receiveWindow.Resize(grown);
            RecordWindow(grown);
            _previousRoundLongestWait = 0;   // 再扩要有新一轮的空等作证：一段空档不连扩两回
            return grown - current;
        }

        if (++_idleRounds < 3)
        {
            return 0;
        }

        _idleRounds = 0;
        int shrunk = (int)Math.Max((long)(current * 0.75), _windowPolicy.MinimumBytes);

        if (shrunk >= current)
        {
            return 0;
        }

        _receiveWindow.Resize(shrunk);
        RefundBudget(current - shrunk);
        RecordWindow(shrunk);

        // 负数：这一轮少授这么多，窗口就此回落。
        return shrunk - current;
    }

    /// <summary>向会话追加预算；号已经还回去（通道收尾了）时不再追加。</summary>
    private bool TryChargeBudget(int bytes)
    {
        if (!_host.TryReserveWindowBudget(bytes))
        {
            return false;
        }

        lock (_stateLock)
        {
            if (!_idReleased)
            {
                _budgetCharged += bytes;
                return true;
            }
        }

        // 申请与收尾撞在一起：号已经按旧数退过了，这一笔当场还回去。
        _host.ReleaseWindowBudget(bytes);
        return false;
    }

    /// <summary>缩窗后把多出来的预算还给会话。</summary>
    private void RefundBudget(int bytes)
    {
        lock (_stateLock)
        {
            if (_idReleased)
            {
                return;   // 收尾时已经整笔退过了
            }
            _budgetCharged -= bytes;
        }

        _host.ReleaseWindowBudget(bytes);
    }

    private async ValueTask SendWindowAdjustAsync(uint bytes, CancellationToken cancellationToken)
    {
        if (bytes == 0 || IsClosedOrClosing())
        {
            return;
        }

        byte[] packet = new byte[9];
        packet[0] = (byte)SshMessageNumber.ChannelWindowAdjust;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), RemoteId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5), bytes);
        await _host.SendIfAsync(packet, _mayStillSend, IsInteractive, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpStandardInputAsync(CancellationToken cancellationToken)
    {
        PipeReader reader = _stdinPipe.Reader;
        Exception? failure = null;

        try
        {
            while (true)
            {
                ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = read.Buffer;

                while (!buffer.IsEmpty)
                {
                    // 对端宣告的 max packet 是 0：它不收任何数据。曾经算出的块是 0、被当成「通道关了」，泵悄悄退出 ——
                    // stdin 静默失效，之后连 EOF 也不再发。现在照实失败：写入方的 FlushAsync 拿到这个异常。
                    if (RemoteMaxPacketBytes == 0)
                    {
                        throw new SshChannelException(Diagnostics.SshFailureReason.ProtocolError,
                            $"对端给通道 {LocalId} 宣告的 max packet 是 0 —— 它不收任何数据，写进 stdin 的内容发不出去。");
                    }

                    // 单个数据段**必须**不超过对端宣告的上限 —— 超了对端会断连。
                    int chunk = (int)Math.Min(buffer.Length, RemoteMaxPacketBytes);

                    // 窗口不够就等回补。这不是错误，是背压。
                    chunk = await WaitForSendWindowAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (chunk == 0)
                    {
                        return;   // 通道关了
                    }

                    if (!await SendDataAsync(buffer.Slice(0, chunk), cancellationToken).ConfigureAwait(false))
                    {
                        // CLOSE 已经入队，这一帧没有发出去：通道关了。不能计进「已发」——
                        // 曾经照计，FlushAsync 对被丢掉的数据报了成功。
                        return;
                    }
                    buffer = buffer.Slice(chunk);

                    Interlocked.Add(ref _stdinSentBytes, chunk);
                    _stdinProgressGate.Signal();
                }

                reader.AdvanceTo(read.Buffer.End);

                if (read.IsCompleted)
                {
                    // writer 完成了：调用方直接完成了 StandardInput（PipeWriter 表达「写完了」的惯用法），
                    // 或者 SendEofAsync 完成了它。两种都是 EOF —— 数据已经冲干净，现在发。
                    // 不发的话，远端等着读完的程序（cat、sort）会一直挂着。通道在关时状态推进不了，什么也不发。
                    _ = TryMarkLocalEof();
                    if (Interlocked.Exchange(ref _eofOwed, 0) == 1)
                    {
                        await _host.SendIfAsync(SimplePacket(SshMessageNumber.ChannelEof), _mayStillSend, IsInteractive, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 通道关了。
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            // reader 归泵所有，由泵自己收尾 —— 见 FinishClose 里的说明。
            //
            // ⚠️ **每一条出口都要走到这里**，包括循环中间那个「通道关了」的 return：
            //    那时泵手上还捏着一段没 AdvanceTo 的数据，而写入方的 FlushAsync 正卡在背压上。
            //    FinishClose 完成的是 writer —— **那放不出挂起的 FlushAsync**，只有 reader
            //    前进或完成才行。曾经这条 return 跳过了收尾，写入方就永远等下去。
            await reader.CompleteAsync(failure).ConfigureAwait(false);

            // 等着「发完」的人不会再等到进展了 —— 叫醒他们去看结局。
            _stdinPumpExited = true;
            _stdinProgressGate.Signal();
        }
    }

    /// <summary>等到发送窗口能放下至少一个字节，返回这次能发多少。</summary>
    /// <returns>可发字节数；通道关闭时返回 0。</returns>
    private async ValueTask<int> WaitForSendWindowAsync(int wanted, CancellationToken cancellationToken)
    {
        while (true)
        {
            // 先取票、再查条件、最后等票 —— 顺序反过来会丢通知，症状是挂死。
            Task ticket = _sendWindowGate.NextChange();

            if (IsClosedOrClosing())
            {
                return 0;
            }

            uint available = _sendWindow.Remaining;
            if (available > 0)
            {
                int take = (int)Math.Min((uint)wanted, available);
                if (_sendWindow.TryConsume(take))
                {
                    return take;
                }
            }

            await ticket.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>发一个 <c>CHANNEL_DATA</c>。</summary>
    /// <returns>发出去了（入队了）为 <see langword="true"/>；CLOSE 已经入队、这一帧不再发时为 <see langword="false"/>。</returns>
    private async ValueTask<bool> SendDataAsync(ReadOnlySequence<byte> data, CancellationToken cancellationToken)
    {
        // 缓冲从池里租：这是上传路径上每一块都要走的一步，曾经每块新分配一个数组（最大一个 max packet），
        // 高速上传时就是每秒几千次分配。发送方的 await 返回时发送泵已经不再引用它
        // （见 ISshChannelHost.SendBorrowedIfAsync），所以 finally 里就可以还回去。
        int length = (int)data.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(9 + length);
        try
        {
            rented[0] = (byte)SshMessageNumber.ChannelData;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rented.AsSpan(1), RemoteId);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rented.AsSpan(5), (uint)length);
            data.CopyTo(rented.AsSpan(9));

            _dataAdmitted = false;
            await _host.SendBorrowedIfAsync(rented.AsMemory(0, 9 + length), _admitData, IsInteractive, cancellationToken)
                .ConfigureAwait(false);
            if (_dataAdmitted)
            {
                Interlocked.Add(ref _bytesSent, length);
            }
            return _dataAdmitted;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void MaybeFinishClose(SshChannelCloseReason reason)
    {
        lock (_stateLock)
        {
            if (!_closeSent || !_closeReceived)
            {
                return;
            }
        }
        FinishClose(reason);
        ReleaseId(force: false);
    }

    /// <summary>把通道号还给会话。</summary>
    /// <param name="force">
    /// 不等双向 CLOSE：会话没了、或者对端从没建起这条通道 —— 都不会再有发往这个号的报文。
    /// </param>
    /// <remarks>
    /// ⚠️ <b>双向 CLOSE 走完之前不能还</b>（RFC 4254 §5.3，velashell-docs/zh/ssh/spec/05 §1 第 2 条）。
    /// 曾经释放通道时本端一收尾就还号：对端那条通道还开着，它发来的 DATA / exit-status / CLOSE
    /// 过了回收延迟就落到复用了这个号的新通道上；要回应答的请求还会被我们用一个不相干的号回 FAILURE。
    /// 号一直扣着的这段时间里，通道已经在本端收尾，迟到的数据照常计窗口、丢弃，不会回任何报文。
    /// </remarks>
    private void ReleaseId(bool force)
    {
        int charged;
        lock (_stateLock)
        {
            if (_idReleased || (!force && !(_closeSent && _closeReceived)))
            {
                return;
            }
            _idReleased = true;
            charged = _budgetCharged;
        }

        // 退的是**计过的**数，不是此刻的窗口大小（见 _budgetCharged）。
        _host.OnChannelClosed(LocalId, charged);
    }

    private void FinishClose(SshChannelCloseReason reason, Exception? failure = null)
    {
        bool pumpStarted;
        bool wasMetered;
        lock (_stateLock)
        {
            if (_state == SshChannelState.Closed)
            {
                return;
            }
            _state = SshChannelState.Closed;

            // 与 StartPumps 在同一把锁里看：这之后泵不会再起来。
            pumpStarted = _stdinPump is not null;
            wasMetered = _activeMetered;
            _activeMetered = false;
        }

        if (wasMetered && MetricsHost is { } host)
        {
            Diagnostics.SshMetrics.ChannelsActive.Add(
                -1, Diagnostics.SshMetrics.HostTag(host), Diagnostics.SshMetrics.ChannelTypeTag(ChannelType));
        }

        SshChannelEvent.Closed closed = new(reason);
        Volatile.Write(ref _closedEvent, closed);
        _events.Writer.TryWrite(closed);
        _events.Writer.TryComplete();
        _closedSignal.TrySetResult();

        // 还在等应答的请求不会再有应答了。让它们返回 false 而不是永远挂着 ——
        // 挂死没有堆栈也没有日志，只有一个再也不返回的 await。
        _pendingRequests.Close(false);

        CompleteReceivePipes(failure);

        // 〔velashell-docs/zh/ssh/spec/05 §4.4〕stdin 的 writer 归调用方，这里**不替它完成**。
        // 曾经在这里完成它：调用方随后再写，拿到的是 BCL 的 InvalidOperationException（「writer 已完成」）——
        // 不是 SshException、不带断开原因；而这里常常跑在接收循环上，调用方可能正在别的线程写，
        // 本身就是跨线程动了别人的 PipeWriter。改为让 reader 一侧收尾：reader 完成之后，
        // 写入方的 FlushAsync 返回 IsCompleted（PipeWriter 表达「读的一方不要了」的惯用法）。
        //
        // stdin 的 reader 在泵起来之后**归泵所有**，这里不能替它完成：
        // 泵可能正读到一半，或者刚 AdvanceTo 完要回头再读 —— 从外面完成 reader
        // 会让它撞上「reader 完成后不许再读」。取消 _lifetime（下面）、放开发送窗口的闸之后，
        // 泵的每条路径都会退出，并在退出时自己完成 reader。
        // 泵从没起来（通道没开成，或者确认之前就收尾了）时才由这里完成。
        if (!pumpStarted)
        {
            _stdinPipe.Reader.Complete();
        }

        _sendWindowGate.Signal();
        _consumedGate.Signal();

        // ⚠️ **不能用 Cancel()。**这里常常跑在接收循环上（收到 CHANNEL_CLOSE、会话断开），
        //    而 Cancel() 会在**当前线程上同步**执行回调：泵被唤醒、结束，等着泵的
        //    DisposeAsync 恢复，再往上是使用者的 await —— 一整串同步完成的续体
        //    就在接收循环的线程上跑了起来。使用者接着同步阻塞一下（轮询、Wait），
        //    接收循环就被整个挂住，别的通道的数据躺在套接字里没人读。
        //    CancelAsync 把回调放到线程池上执行（velashell-docs/zh/ssh/spec/05 §8：接收循环上不跑使用者的代码）。
        Session.Lifecycle.CancelInBackground(_lifetime);

        // 号不在这里还 —— 见 ReleaseId。
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // ⚠️ **释放有时限。**半死的链路上 CLOSE 可能永远刷不出去（发送缓冲满着、
        //    对端不读），泵里在途的那一帧也一样。等不到就不等了 —— 通道在本端照样收尾，
        //    剩下的由会话自己的判死（保活、TCP）去收场。
        using CancellationTokenSource deadline = new(DisposeTimeout);

        // CLOSE 本身**不带时限**地发：等不及的只是释放这一步，CLOSE 留在后台，
        // 背压一松就上线（会话先没了的话它自己收场，不抛）。带着时限发的话，超时就等于
        // 这条通道在服务端永远开着 —— 远端进程接着跑，MaxSessions 名额也一直占着。
        Task closing = CloseAsync(CancellationToken.None).AsTask();
        try
        {
            await closing.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛 —— 否则每条 await using 的错误路径都会被次要异常盖住。
        }

        // 本端收尾；号要等对端的 CLOSE（见 ReleaseId）。
        FinishClose(SshChannelCloseReason.ClosedLocally);
        ReleaseId(force: false);

        foreach (Task? pump in new[] { _stdinPump, _windowAdjustPump })
        {
            if (pump is null)
            {
                continue;
            }
            try
            {
                await pump.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 同上。
            }
        }
    }

    /// <summary>释放一条通道最多等多久（发 CLOSE、等泵收尾）。</summary>
    internal static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
}
