// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §7    TCP/IP 端口转发
//   行为规格:      velashell-docs/zh/ssh/spec/07-forwarding.md §五、§八

using System.Collections.Concurrent;
using System.Net;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Forwarding;

/// <summary>一条正在跑的端口转发：本地（<c>-L</c>）、动态（<c>-D</c>）或远程（<c>-R</c>）。</summary>
/// <remarks>
/// <para>
/// 三种转发的计量、事件、释放完全一样，差别只在谁监听、目标从哪来 ——
/// 所以面板只认这一个类型就够了，不必按形态各写一遍分支。
/// 起一条转发用 <see cref="LocalPortForwarder"/> 或 <see cref="RemotePortForwarder"/> 上的静态方法。
/// </para>
/// <para>
/// <b>字节数一律从本机的视角说</b>：<see cref="BytesSent"/> 是本机这头送进隧道的，
/// <see cref="BytesReceived"/> 是从隧道那头收回来的 —— 不论连接是谁发起的。
/// </para>
/// </remarks>
public abstract class PortForwarder : IAsyncDisposable
{
    private long _nextConnectionId;
    private long _activeConnections;
    private long _totalConnections;
    private long _bytesSent;
    private long _bytesReceived;

    private readonly TimeProvider _time;
    private readonly ThroughputMeter _sentMeter;
    private readonly ThroughputMeter _receivedMeter;

    /// <summary>正在搬的连接（<see cref="Connections"/> 的来源）。</summary>
    private readonly ConcurrentDictionary<long, LiveConnection> _live = new();

    /// <summary>两个方向的限速；不限速时为 <see langword="null"/>。</summary>
    private ByteRateLimiter? _sendLimiter;
    private ByteRateLimiter? _receiveLimiter;

    private readonly TaskCompletionSource<SshException> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 转发器停下时完成，结果是停下的原因：SSH 连接结束了是连接的结束原因（与 <see cref="SshConnection.Completion"/> 同一个），
    /// 本端释放是 <see cref="SshFailureReason.Aborted"/>，看哪个先到。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 转发器只会因为这两件事停下 —— 单条连接的失败报 <see cref="Error"/>，转发器照跑。
    /// 以<b>成功</b>完成、结果是原因（与 <see cref="SshConnection.Completion"/> 同一个形状）：没人等也不会变成未观察的任务异常。
    /// </para>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/07 §五〕曾经没有：宿主的隧道面板要把「运行中」换成带原因的状态，只好自己去挂连接的结束。
    /// </para>
    /// </remarks>
    public Task<SshException> Completion => _completion.Task;

    /// <summary>转发器建好了：连接结束时跟着停（子类的工厂在成功返回之前调一次）。</summary>
    private protected void TrackConnection(SshConnection connection) =>
        _ = connection.Completion.ContinueWith(
            static (ended, state) => ((PortForwarder)state!)._completion.TrySetResult(ended.Result),
            this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>本端在释放（子类的 <see cref="DisposeAsync"/> 一进来就调）。</summary>
    private protected void CompleteAsStopped() =>
        _completion.TrySetResult(new SshForwardException(SshFailureReason.Aborted, "转发器已由本端停止。"));

    /// <summary>只给本库的派生类型用。</summary>
    /// <param name="kind">转发的形态。</param>
    /// <param name="time">吞吐与限速用的时钟；只有测试会换。</param>
    private protected PortForwarder(ForwardKind kind, TimeProvider? time = null)
    {
        Kind = kind;
        _time = time ?? TimeProvider.System;
        _sentMeter = new ThroughputMeter(_time);
        _receivedMeter = new ThroughputMeter(_time);
    }

    /// <summary>每个方向每秒最多搬多少应用字节；<see langword="null"/> 不限（构造时由派生类按参数设）。</summary>
    private protected void ConfigureRateLimit(long? bytesPerSecond)
    {
        if (bytesPerSecond is { } rate)
        {
            _sendLimiter = new ByteRateLimiter(rate, _time);
            _receiveLimiter = new ByteRateLimiter(rate, _time);
        }
    }

    /// <summary>
    /// 此刻的吞吐（最近三个整秒的平均，应用字节 / 秒）。隧道面板的实时速率就是它。
    /// </summary>
    public ForwardThroughput Throughput => new(_sentMeter.PerSecond, _receivedMeter.PerSecond);

    /// <summary>正在搬的连接（快照，按序号排）：来源、目标、开始时刻、到目前为止的字节数。</summary>
    public IReadOnlyList<ForwardConnectionSnapshot> Connections =>
        [.. _live.Values.OrderBy(c => c.Id).Select(c => c.Snapshot())];

    /// <summary>一条正在搬的连接；字节数在搬运循环里累加。</summary>
    private sealed class LiveConnection(long id, EndPoint? source, string target, DateTimeOffset startedAt)
    {
        public long Id => id;

        public long Sent;

        public long Received;

        public ForwardConnectionSnapshot Snapshot() =>
            new(id, source, target, startedAt, Interlocked.Read(ref Sent), Interlocked.Read(ref Received));
    }

    /// <summary>转发的形态。</summary>
    public ForwardKind Kind { get; }

    /// <summary>转发器还在跑吗。</summary>
    /// <remarks>释放之后、或者 SSH 连接断了之后是 <see langword="false"/>。</remarks>
    public abstract bool IsActive { get; }

    /// <summary>当前活跃的连接数。</summary>
    public int ActiveConnections => (int)Volatile.Read(ref _activeConnections);

    /// <summary>累计的连接数。</summary>
    public long TotalConnections => Volatile.Read(ref _totalConnections);

    /// <summary>本机这头送进隧道的应用字节数。</summary>
    public long BytesSent => Volatile.Read(ref _bytesSent);

    /// <summary>从隧道那头收回本机的应用字节数。</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);

    /// <summary>一条连接建立了。</summary>
    public event EventHandler<ForwardConnectionEventArgs>? ConnectionOpened;

    /// <summary>一条连接结束了（参数里带着它的字节数与时长）。</summary>
    public event EventHandler<ForwardConnectionEventArgs>? ConnectionClosed;

    /// <summary>单条连接出错了 —— <b>转发器仍在跑</b>。</summary>
    public event EventHandler<ForwardErrorEventArgs>? Error;

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>这一条连接在本转发器内的序号。</summary>
    private protected long NextConnectionId() => Interlocked.Increment(ref _nextConnectionId);

    /// <summary>撞并发上限时 <see cref="Error"/> 事件最多多久报一次（度量里的错误计数照常每条都记）。</summary>
    private const long ConnectionLimitReportIntervalMs = 1000;

    /// <summary>上一次报「撞并发上限」的时刻（<c>Environment.TickCount64</c>）；0 表示还没报过。</summary>
    private long _connectionLimitReportedAt;

    /// <summary>上一次报过之后又拒了几条。</summary>
    private int _connectionLimitRejected;

    /// <summary>撞并发上限、拒了一条：度量每条都记；<see cref="Error"/> 事件节流 —— 每秒至多一次，带上这期间拒了几条。</summary>
    /// <remarks>
    /// 〔FW-D6〕曾经每拒一条就发一次事件：上限撞满时往往是一大波连接同时涌进来，宿主随之把每一条都推到界面上。
    /// </remarks>
    private protected void ReportConnectionLimit(int maxConnections)
    {
        ForwardEvents.RecordError(Kind, ForwardErrorReason.ConnectionLimit);
        Interlocked.Increment(ref _connectionLimitRejected);

        long now = Environment.TickCount64;
        long last = Volatile.Read(ref _connectionLimitReportedAt);
        if ((last != 0 && now - last < ConnectionLimitReportIntervalMs)
            || Interlocked.CompareExchange(ref _connectionLimitReportedAt, now, last) != last)
        {
            return;
        }

        int rejected = Interlocked.Exchange(ref _connectionLimitRejected, 0);
        string message = rejected <= 1
            ? $"并发连接数已达上限 {maxConnections}，这一条被拒绝。"
            : $"并发连接数已达上限 {maxConnections}，又拒绝了 {rejected} 条（这类错误每秒至多报一次）。";
        ForwardEvents.Raise(Error, this, new ForwardErrorEventArgs(ForwardErrorReason.ConnectionLimit, message, null));
    }

    /// <summary>把本机一头与隧道通道对接起来搬运，计量、事件、错误都在这里。</summary>
    /// <param name="connectionId">连接序号。</param>
    /// <param name="source">来源端点（远程转发没有）。</param>
    /// <param name="target">目标的名字（进事件与错误消息）。</param>
    /// <param name="local">本机那一头。</param>
    /// <param name="channel">隧道通道。</param>
    /// <param name="cancellationToken">转发器的生命周期。</param>
    private protected async Task RelayAsync(
        long connectionId,
        EndPoint? source,
        string target,
        IRelayEndpoint local,
        SshChannel channel,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _totalConnections);
        ForwardMetrics.ActiveConnections.Add(1, ForwardEvents.KindTag(Kind));
        ForwardMetrics.TotalConnections.Add(1, ForwardEvents.KindTag(Kind));

        // 活跃数只减一次：正常收尾时在报「关了」之前减，出了异常由 finally 兜底。
        bool active = true;
        void Release()
        {
            if (active)
            {
                active = false;
                Interlocked.Decrement(ref _activeConnections);
                ForwardMetrics.ActiveConnections.Add(-1, ForwardEvents.KindTag(Kind));
            }
        }

        LiveConnection live = new(connectionId, source, target, _time.GetUtcNow());
        _live[connectionId] = live;
        try
        {
            ForwardEvents.Raise(ConnectionOpened, this, new ForwardConnectionEventArgs(connectionId, source, target));

            ChannelRelayEndpoint remote = new(channel);

            RelayResult result = await DuplexRelay.RunAsync(
                local, remote,
                onBytesFromLeft: bytes =>
                {
                    Interlocked.Add(ref _bytesSent, bytes);
                    Interlocked.Add(ref live.Sent, bytes);
                    _sentMeter.Record(bytes);
                    ForwardMetrics.Bytes.Add(bytes, ForwardEvents.KindTag(Kind), ForwardEvents.DirectionSent);
                },
                onBytesFromRight: bytes =>
                {
                    Interlocked.Add(ref _bytesReceived, bytes);
                    Interlocked.Add(ref live.Received, bytes);
                    _receivedMeter.Record(bytes);
                    ForwardMetrics.Bytes.Add(bytes, ForwardEvents.KindTag(Kind), ForwardEvents.DirectionReceived);
                },
                throttleFromLeft: _sendLimiter is { } send ? send.AcquireAsync : null,
                throttleFromRight: _receiveLimiter is { } receive ? receive.AcquireAsync : null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // 先减活跃数、再报「关了」：订阅者在事件里读到的 ActiveConnections 不该还算着这一条。
            // 曾经反过来 —— 在 ConnectionClosed 里刷新界面上的连接数，总比实际多一。
            Release();
            ForwardEvents.Raise(ConnectionClosed, this, new ForwardConnectionEventArgs(
                connectionId, source, target, result.BytesFromLeft, result.BytesFromRight, result.Duration));

            // 转发器自己在收工（释放、连接断了）时的取消不是这条连接的错。
            if (result.Error is { } error
                && !(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                Report(ForwardErrorReason.Relay, $"到 {target} 的搬运中断：{error.Message}", error);
            }
        }
        finally
        {
            _live.TryRemove(connectionId, out _);
            Release();
        }
    }

    /// <summary>一条连接失败了：记一笔错误计数，发 <see cref="Error"/> 事件。</summary>
    private protected void Report(ForwardErrorReason reason, string message, Exception? exception)
    {
        ForwardEvents.RecordError(Kind, reason);
        ForwardEvents.Raise(Error, this, new ForwardErrorEventArgs(reason, message, exception));
    }
}
