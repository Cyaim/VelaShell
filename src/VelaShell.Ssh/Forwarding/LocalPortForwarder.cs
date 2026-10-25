// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §7.2  direct-tcpip
//   RFC 1928       SOCKS5（动态转发）
//   OpenSSH PROTOCOL §2.4  direct-streamlocal@openssh.com（转到远端的 Unix 套接字）
//   行为规格:      velashell-docs/zh/ssh/spec/07-forwarding.md §二、§三、§五、§六、§八

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Forwarding;

/// <summary>本地 / 动态转发的参数。</summary>
/// <remarks>
/// 〔FW-E16〕非法值在设值时就抛（AGENTS 4.3）。曾经不拦：<c>MaxConnections = 0</c> 时监听已经 Bind + Listen、
/// 随后构造转发器才抛 —— 套接字没人释放，端口一直占到 GC。
/// </remarks>
public sealed record LocalPortForwardOptions
{
    /// <summary>
    /// 监听地址；<see langword="null"/>（默认）是<b>两个环回</b>：<c>127.0.0.1</c> 与 <c>::1</c>，同一个端口。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 〔决策 velashell-docs/zh/ssh/spec/07 §2.3〕<b>默认绑环回。</b>
    /// 一条隧道的另一端往往是内网数据库或管理接口；默认绑 <c>0.0.0.0</c>
    /// 等于把它暴露给同网段的所有人。要对外开放，使用者得<b>显式</b>写出来。
    /// </para>
    /// <para>
    /// 〔Q5〕<b>两个环回都听。</b>不少运行时把 <c>localhost</c> 先解析成 <c>::1</c>，只听 <c>127.0.0.1</c> 的话它们连不上、或者先去试 <c>::1</c>；
    /// 而 <c>[::1]:同一端口</c> 空着，同机任何进程都能抢先绑上，先试 <c>::1</c> 的客户端就把数据库口令之类交给了它 ——
    /// 正是「默认绑环回」想防的同机暴露。这台机器没有 IPv6 时只听 <c>127.0.0.1</c>；<c>[::1]</c> 上那个端口已经被别的进程占着时不起这个转发。
    /// 曾经默认只听 <c>127.0.0.1</c>。只想听一个就显式给（<see cref="IPAddress.Loopback"/>）。
    /// </para>
    /// </remarks>
    public IPAddress? BindAddress { get; init; }

    /// <summary>监听端口。<c>0</c> 表示由系统分配，结果看 <see cref="LocalPortForwarder.BoundEndPoint"/>。</summary>
    /// <exception cref="ArgumentOutOfRangeException">不在 0–65535 之间。</exception>
    public int BindPort
    {
        get;
        init => field = value is >= 0 and <= 65535 ? value : throw new ArgumentOutOfRangeException(nameof(BindPort), value, "监听端口要在 0–65535 之间（0 由系统分配）。");
    }

    /// <summary>
    /// 设了就<b>在本机的 Unix 域套接字上</b>监听（<c>-L /路径/local.sock:…</c>），<see cref="BindAddress"/> 与
    /// <see cref="BindPort"/> 不再起作用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 〔决策 velashell-docs/zh/ssh/spec/07 §2.5〕套接字文件按权限隔离：多用户机器上别的用户连不上（环回端口则对所有用户开放）。
    /// 非 Windows 上起监听之后把文件权限设成 0600（同 OpenSSH 默认的 <c>StreamLocalBindMask 0177</c>）；
    /// 从创建到改权限之间有一小段窗口，放在只有自己能进的目录里（如 <c>$XDG_RUNTIME_DIR</c>）就没有这个问题。
    /// Windows 上套接字文件沿用所在目录的 ACL。
    /// </para>
    /// <para>路径太长（Linux 108 字节、macOS 104 字节，含结尾的 0）当场报。转发器释放时删掉它创建的套接字文件。</para>
    /// </remarks>
    public string? ListenSocketPath { get; init; }

    /// <summary>
    /// <see cref="ListenSocketPath"/> 那里已经有文件时先删掉它再监听（OpenSSH 的 <c>StreamLocalBindUnlink yes</c>）。
    /// 默认 <see langword="false"/>：已经有文件就报错 —— 那多半是上一次没收拾干净，也可能是别人的套接字。
    /// </summary>
    public bool AllowSocketReplacement { get; init; }

    /// <summary>并发连接数上限。</summary>
    /// <exception cref="ArgumentOutOfRangeException">小于 1。</exception>
    public int MaxConnections
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxConnections), value, "并发连接数上限至少为 1。");
    } = 1024;

    /// <summary>动态转发里，SOCKS 握手要在多久之内完成。</summary>
    /// <remarks>
    /// 连上来一句不说的客户端每个都占一个并发名额；没有时限的话，
    /// 占满 <see cref="MaxConnections"/> 之后正经的连接一条也进不来。
    /// 浏览器与 curl 连上就发握手，30 秒绰绰有余。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">不为正。</exception>
    public TimeSpan SocksHandshakeTimeout
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(SocksHandshakeTimeout), value, "SOCKS 握手时限必须为正。");
    } = TimeSpan.FromSeconds(30);

    /// <summary>开隧道通道时等服务端应答最多等多久（服务端要先连上目标才确认）。</summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/07 §3.2〕到点就放弃这一条：动态转发回 SOCKS <c>0x06</c>（TTL expired），本地转发重置本机那条连接。
    /// 服务端去连一个不通的目标时，要等它自己的 TCP 连接超时（常见的是两分钟上下）才回拒绝 —— 浏览器与 curl 等不了那么久，也看不出原因。
    /// 迟到的确认由连接收尾（立刻关掉），不占服务端的会话名额。不限时写 <see cref="Timeout.InfiniteTimeSpan"/>。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">不为正，也不是 <see cref="Timeout.InfiniteTimeSpan"/>。</exception>
    public TimeSpan ChannelOpenTimeout
    {
        get;
        init => field = value > TimeSpan.Zero || value == Timeout.InfiniteTimeSpan
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ChannelOpenTimeout), value, "开通道的时限必须为正；不限时写 Timeout.InfiniteTimeSpan。");
    } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 每个方向每秒最多搬多少应用字节；<see langword="null"/>（默认）不限。这个转发器的全部连接共用这个额度。
    /// </summary>
    /// <remarks>慢链路上开着好几条隧道时，不让一条下载把交互终端挤满。允许一秒额度的突发。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">不为正。</exception>
    public long? MaxBytesPerSecond
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxBytesPerSecond), value, "限速必须为正；不限就给 null。");
    }

    /// <summary>每条隧道通道的参数。</summary>
    public SshChannelOptions Channel { get; init; } = SshChannelOptions.Default with
    {
        // 隧道上 stderr 不会有东西。
        StderrMode = SshStderrMode.Discard,
    };

    /// <summary>默认参数。</summary>
    public static LocalPortForwardOptions Default { get; } = new();
}

/// <summary>在本机监听、把连接送进 SSH 隧道的转发器（<c>-L</c> / <c>-D</c>）。</summary>
/// <remarks>
/// 本地转发与动态转发共用这一个类型 ——
/// 它们的差别只有一处：<b>目标是配置死的，还是客户端在 SOCKS 握手里给的</b>。
/// 搬运、计量、半关闭、错误收尾完全一样。
/// </remarks>
public sealed class LocalPortForwarder : PortForwarder
{
    private readonly SshConnection _connection;
    private readonly LocalPortForwardOptions _options;
    private readonly Socket[] _listeners;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectionSlots;

    private readonly string? _targetHost;
    private readonly int _targetPort;

    /// <summary>转到远端的 Unix 套接字时那个路径（<c>direct-streamlocal@openssh.com</c>）；否则为 <see langword="null"/>。</summary>
    private readonly string? _targetSocketPath;

    /// <summary>我们在本机建出来的监听套接字文件；收工时删掉。</summary>
    private readonly string? _createdSocketPath;

    /// <summary>还在处理中的连接。释放要等它们收完尾，之后才能放掉槽位信号量。</summary>
    private readonly ConcurrentDictionary<long, Task> _connections = new();

    /// <summary>连接断开时停下监听（见 <see cref="OnConnectionLost"/>）。</summary>
    private CancellationTokenRegistration _disconnectedRegistration;

    private Task[] _acceptLoops = [];
    private bool _disposed;

    private LocalPortForwarder(
        SshConnection connection,
        LocalPortForwardOptions options,
        ForwardKind kind,
        IReadOnlyList<Socket> listeners,
        string? targetHost,
        int targetPort,
        string? targetSocketPath)
        : base(kind)
    {
        _connection = connection;
        _options = options;
        _listeners = [.. listeners];
        _targetHost = targetHost;
        _targetPort = targetPort;
        _targetSocketPath = targetSocketPath;
        _createdSocketPath = options.ListenSocketPath;
        ConfigureRateLimit(options.MaxBytesPerSecond);
        BoundEndPoints = Array.AsReadOnly([.. listeners.Select(listener => listener.LocalEndPoint)]);
        BoundEndPoint = BoundEndPoints[0];
        _connectionSlots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
    }

    /// <summary>实际监听的端点（同时听两个环回时是 <c>127.0.0.1</c> 那个）。<b>端口给 0 时，实际端口在这里。</b></summary>
    public EndPoint? BoundEndPoint { get; }

    /// <summary>实际监听的全部端点：默认是 <c>127.0.0.1</c> 与 <c>::1</c> 上的同一个端口（没有 IPv6 时只有前一个）。</summary>
    public IReadOnlyList<EndPoint> BoundEndPoints { get; }

    /// <inheritdoc />
    /// <remarks>SSH 连接断了之后是 <see langword="false"/>：那时监听已经关掉，端口也放出来了。</remarks>
    public override bool IsActive => !_disposed && !_lifetime.IsCancellationRequested;

    // ------------------------------------------------------------ 建立

    /// <summary>起一个本地转发（<c>-L</c>）。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="targetHost">远端目标主机（<b>从服务端视角解析</b>）。</param>
    /// <param name="targetPort">远端目标端口。</param>
    /// <param name="options">参数。</param>
    /// <exception cref="SshForwardException">本地端口起不来。</exception>
    public static LocalPortForwarder Start(
        SshConnection connection,
        string targetHost,
        int targetPort,
        LocalPortForwardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(targetHost);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPort, 65535);

        LocalPortForwardOptions effective = options ?? LocalPortForwardOptions.Default;
        return StartListening(connection, effective, ForwardKind.Local, targetHost, targetPort);
    }

    /// <summary>起一个转到<b>远端 Unix 套接字</b>的本地转发（<c>-L 8080:/var/run/docker.sock</c>）。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="remoteSocketPath">服务端上的套接字路径。</param>
    /// <param name="options">参数；要在本机也用套接字监听就设 <see cref="LocalPortForwardOptions.ListenSocketPath"/>。</param>
    /// <exception cref="SshForwardException">本地监听起不来。</exception>
    /// <remarks>
    /// 本机的 Docker 客户端、数据库工具直接操作远端，远端不必开任何 TCP 端口。出站走 <c>direct-streamlocal@openssh.com</c>
    /// （服务端要 <c>AllowStreamLocalForwarding</c>），开不开得成要等第一条连接来了才知道 —— 与 TCP 目标一样。
    /// </remarks>
    public static LocalPortForwarder StartToUnixSocket(
        SshConnection connection,
        string remoteSocketPath,
        LocalPortForwardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(remoteSocketPath);

        LocalPortForwardOptions effective = options ?? LocalPortForwardOptions.Default;
        return StartListening(connection, effective, ForwardKind.Local, targetHost: null, targetPort: 0, remoteSocketPath);
    }

    /// <summary>起一个动态转发（<c>-D</c>，SOCKS5）。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="options">参数。</param>
    /// <exception cref="SshForwardException">本地端口起不来。</exception>
    public static LocalPortForwarder StartDynamic(SshConnection connection, LocalPortForwardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        LocalPortForwardOptions effective = options ?? LocalPortForwardOptions.Default;
        return StartListening(connection, effective, ForwardKind.Dynamic, targetHost: null, targetPort: 0);
    }

    /// <summary>起监听、建转发器；起监听之后出了任何错，监听当场关掉 —— 不留一个占着端口、没人管的套接字。</summary>
    private static LocalPortForwarder StartListening(
        SshConnection connection, LocalPortForwardOptions options, ForwardKind kind, string? targetHost, int targetPort,
        string? targetSocketPath = null)
    {
        // 〔FW-E17〕连接已经断了（或释放了）就照实失败，与远程转发一致。曾经照样起监听、「成功」返回一个
        // IsActive = false 的转发器 —— 调用方以为转发生效了，连上来的只会被立刻关掉。
        connection.ThrowIfUnusable();

        IReadOnlyList<Socket> listeners = Bind(options);
        try
        {
            LocalPortForwarder forwarder = new(connection, options, kind, listeners, targetHost, targetPort, targetSocketPath);
            forwarder.Run();
            forwarder.TrackConnection(connection);
            return forwarder;
        }
        catch
        {
            foreach (Socket listener in listeners)
            {
                listener.Dispose();
            }
            DeleteSocketFile(options.ListenSocketPath);
            throw;
        }
    }

    private static IReadOnlyList<Socket> Bind(LocalPortForwardOptions options)
    {
        if (options.ListenSocketPath is { } socketPath)
        {
            return [BindUnixSocket(socketPath, options.AllowSocketReplacement)];
        }

        return options.BindAddress is { } address
            ? [BindTcp(address, options.BindPort)]
            : BindBothLoopbacks(options.BindPort);
    }

    /// <summary>系统分的端口在 <c>::1</c> 上恰好被占时，换一个端口最多再试几次。</summary>
    private const int LoopbackPortRetries = 8;

    /// <summary>
    /// 〔Q5，velashell-docs/zh/ssh/spec/07 §2.3〕默认同时听 <c>127.0.0.1</c> 与 <c>::1</c>，同一个端口（见 <see cref="LocalPortForwardOptions.BindAddress"/>）。
    /// </summary>
    private static IReadOnlyList<Socket> BindBothLoopbacks(int port)
    {
        for (int attempt = 0; ; attempt++)
        {
            Socket v4 = BindTcp(IPAddress.Loopback, port);
            int bound = ((IPEndPoint)v4.LocalEndPoint!).Port;
            if (!Socket.OSSupportsIPv6)
            {
                return [v4];
            }

            try
            {
                return [v4, Listen(IPAddress.IPv6Loopback, bound)];
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported
                or SocketError.AddressNotAvailable or SocketError.ProtocolNotSupported)
            {
                return [v4];   // 这台机器没有 IPv6 环回：只听 IPv4
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && port == 0 && attempt < LoopbackPortRetries)
            {
                v4.Dispose();   // 系统分的端口在 ::1 上恰好被占：换一个再来
            }
            catch (SocketException ex)
            {
                v4.Dispose();
                throw new SshForwardException(SshFailureReason.ForwardBindFailed,
                    $"在 [::1]:{bound} 上起监听失败：{ex.SocketErrorCode}。" +
                    (ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                        ? "同一端口的 IPv6 环回被别的进程占着 —— 先试 ::1 的客户端（把 localhost 先解析成 ::1 的那些）会连到它那里，" +
                          "所以不起这个转发。换一个端口，或者明确只听 127.0.0.1。"
                        : ""),
                    ex);
            }
        }
    }

    /// <summary>在一个 TCP 地址上起监听；起不来报 <see cref="SshFailureReason.ForwardBindFailed"/>。</summary>
    private static Socket BindTcp(IPAddress address, int port)
    {
        try
        {
            return Listen(address, port);
        }
        catch (SocketException ex)
        {
            // 不留半挂的监听。起不来就是起不来，别让调用方以为转发生效了。
            throw new SshForwardException(SshFailureReason.ForwardBindFailed,
                $"在 {new IPEndPoint(address, port)} 上起监听失败：{ex.SocketErrorCode}。" +
                (port != 0 ? "端口可能已被占用。" : ""),
                ex);
        }
    }

    /// <summary>绑定并监听；失败时套接字已经释放，原样抛 <see cref="SocketException"/>。</summary>
    private static Socket Listen(IPAddress address, int port)
    {
        Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(new IPEndPoint(address, port));
            listener.Listen(backlog: 128);
            return listener;
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    /// <summary>在本机的 Unix 域套接字上监听（velashell-docs/zh/ssh/spec/07 §2.5）。</summary>
    private static Socket BindUnixSocket(string path, bool replaceExisting)
    {
        if (File.Exists(path))
        {
            if (!replaceExisting)
            {
                throw new SshForwardException(SshFailureReason.ForwardBindFailed,
                    $"{path} 已经存在 —— 多半是上一次没收拾干净，也可能是别人的套接字。确认无用后删掉它，或者打开「替换已有的套接字」。");
            }
            File.Delete(path);
        }

        UnixDomainSocketEndPoint endPoint;
        try
        {
            endPoint = new UnixDomainSocketEndPoint(path);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new SshForwardException(SshFailureReason.ForwardBindFailed,
                $"套接字路径太长（{System.Text.Encoding.UTF8.GetByteCount(path)} 字节；Linux 上限 107、macOS 103）：{path}", ex);
        }

        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        bool bound = false;
        try
        {
            listener.Bind(endPoint);
            bound = true;
            listener.Listen(backlog: 128);

            // 只有自己能连（OpenSSH 默认的 StreamLocalBindMask 0177）。Windows 上沿用目录的 ACL。
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return listener;
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            listener.Dispose();

            // 只删自己绑出来的那个文件：绑定本身失败（被别人抢先建了）时那个文件不是我们的。
            if (bound)
            {
                DeleteSocketFile(path);
            }
            throw new SshForwardException(SshFailureReason.ForwardBindFailed,
                $"在 {path} 上起监听失败：{(ex is SocketException se ? se.SocketErrorCode.ToString() : ex.Message)}。", ex);
        }
    }

    /// <summary>删掉我们建的套接字文件；删不掉不抛（收工路径）。</summary>
    private static void DeleteSocketFile(string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 收工路径不抛：留下的文件下次起监听时会报「已经存在」。
        }
    }

    private void Run()
    {
        _acceptLoops = [.. _listeners.Select(listener => Task.Run(() => AcceptLoopAsync(listener, _lifetime.Token)))];

        // 连接断了就不再监听：留着的话端口一直被占，重连之后同一个转发起不来（端口已被占用），
        // 而这里每接一条都只会换来一次「隧道打不开」。回调在线程池上跑（见 SshConnection.Disconnected）。
        _disconnectedRegistration = _connection.Disconnected.Register(
            static state => ((LocalPortForwarder)state!).OnConnectionLost(), this);
    }

    private void OnConnectionLost()
    {
        // 先关监听再标记不在跑：看到 IsActive 为假的人，可以确信端口已经放出来了。
        foreach (Socket listener in _listeners)
        {
            listener.Dispose();
        }
        DeleteSocketFile(_createdSocketPath);
        Lifecycle.CancelInBackground(_lifetime);
    }

    // ------------------------------------------------------------ 主循环

    /// <summary>接受失败之后最长退避多久。</summary>
    private static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(1);

    private async Task AcceptLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        TimeSpan backoff = TimeSpan.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            Socket inbound;
            try
            {
                inbound = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.Zero;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                // 〔FW-D6〕对端在 accept 之前就把这一条重置了：是这一条自己的事，监听本身好好的 —— 不退避，也不报。
                // 曾经照「接受失败」处理：报一次错误、整个监听退避 50 ms 起步，别的连接跟着等。
                continue;
            }
            catch (SocketException ex)
            {
                Report(ForwardErrorReason.Accept, "接受入站连接失败。", ex);

                // 要退避。文件句柄耗尽（EMFILE）这类失败会立刻、反复地再来一次：
                // 不等一下就是一个满核空转、每秒上万条错误事件的循环，而它恰恰发生在
                // 机器已经吃紧的时候。
                backoff = backoff == TimeSpan.Zero
                    ? TimeSpan.FromMilliseconds(50)
                    : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxAcceptBackoff.Ticks));
                try
                {
                    await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            // 并发上限撞满：拒掉这一条，已有连接不受影响。
            if (!_connectionSlots.Wait(0, CancellationToken.None))
            {
                ReportConnectionLimit(_options.MaxConnections);
                inbound.Dispose();
                continue;
            }

            long connectionId = NextConnectionId();
            var handling = Task.Run(
                () => HandleConnectionAsync(connectionId, inbound, cancellationToken), CancellationToken.None);
            _connections[connectionId] = handling;

            // 先登记、后挂摘除：任务要是已经跑完了，续体也排在登记之后执行。
            _ = handling.ContinueWith(
                (_, state) => ((LocalPortForwarder)state!)._connections.TryRemove(connectionId, out Task? _),
                this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(long connectionId, Socket inbound, CancellationToken cancellationToken)
    {
        EndPoint? source = SafeRemoteEndPoint(inbound);

        NetworkStream stream = new(inbound, ownsSocket: true);
        StreamRelayEndpoint local = new(
            stream, () => StreamRelayEndpoint.ShutdownSend(inbound), abort: () => StreamRelayEndpoint.Reset(inbound));

        SshChannel? channel = null;
        string target = "?";

        try
        {
            // 转到远端的 Unix 套接字：出站是 direct-streamlocal，没有主机与端口。
            if (_targetSocketPath is { } socketPath)
            {
                target = socketPath;
                channel = await OpenTunnelAsync(
                    token => _connection.OpenUnixSocketTunnelAsync(socketPath, _options.Channel, token),
                    local, $"远端套接字 {target}", target, socksAddressType: null, cancellationToken).ConfigureAwait(false);
                if (channel is null)
                {
                    return;
                }
                await RelayAsync(connectionId, source, target, local, channel, cancellationToken).ConfigureAwait(false);
                return;
            }

            string host;
            int port;
            byte socksAddressType = 0x01;

            if (Kind == ForwardKind.Dynamic)
            {
                // 握手有时限：连上来一句不说的客户端，每个都白占一个并发名额，
                // 占满 MaxConnections 之后正经的连接一条也进不来。
                SocksTarget? socks;
                using (var handshake =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    handshake.CancelAfter(_options.SocksHandshakeTimeout);
                    try
                    {
                        socks = await SocksHandshake
                            .ReadRequestAsync(local.Input, local.Output, handshake.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        Report(ForwardErrorReason.SocksHandshake,
                            $"SOCKS 握手在 {_options.SocksHandshakeTimeout.TotalSeconds:0.#} 秒内没有完成，这一条被关掉。", null,
                            timeout: _options.SocksHandshakeTimeout);
                        return;
                    }
                }

                if (socks is not { } parsed)
                {
                    Report(ForwardErrorReason.SocksHandshake, "SOCKS 握手非法或命令不支持，这一条被关掉。", null);
                    return;
                }

                host = parsed.Host;
                port = parsed.Port;
                socksAddressType = parsed.AddressType;
            }
            else
            {
                host = _targetHost!;
                port = _targetPort;
            }

            target = $"{host}:{port}";

            channel = await OpenTunnelAsync(
                token => _connection.OpenTcpTunnelAsync(
                    host, port,
                    originatorHost: (source as IPEndPoint)?.Address.ToString() ?? "127.0.0.1",
                    originatorPort: (source as IPEndPoint)?.Port ?? 0,
                    _options.Channel,
                    token),
                local, target, target, Kind == ForwardKind.Dynamic ? socksAddressType : null, cancellationToken).ConfigureAwait(false);
            if (channel is null)
            {
                return;
            }

            if (Kind == ForwardKind.Dynamic)
            {
                await SocksHandshake.WriteReplyAsync(
                    local.Output, SocksReply.Succeeded, socksAddressType, cancellationToken)
                    .ConfigureAwait(false);
            }

            await RelayAsync(connectionId, source, target, local, channel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 转发器在收工。
        }
        catch (Exception ex)
        {
            // 单条连接的失败绝不影响转发器本身。一条隧道要能跑几天，
            // 期间必然有连不上的目标、被重置的连接。
            Report(ForwardErrorReason.Relay, $"到 {target} 的连接出错：{ex.Message}", ex, target);
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            await local.DisposeAsync().ConfigureAwait(false);
            _connectionSlots.Release();
        }
    }

    /// <summary>
    /// 开隧道通道，带 <see cref="LocalPortForwardOptions.ChannelOpenTimeout"/>。开不成就地收尾这一条、返回 <see langword="null"/>：
    /// 动态转发回 SOCKS 失败应答（<paramref name="socksAddressType"/> 不为空时），否则重置本机那条连接。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/07 §二〕<b>拒绝也是出错</b>：本机应用该收到 RST，而不是一个像正常结束的 FIN（与 §2.2「出错不是 EOF」同一条规则）。
    /// 曾经正常关闭，本机应用读到的是一个没有任何数据的结尾，分不出「服务端拒了」与「目标什么也没回」。
    /// SOCKS 有自己的失败应答告诉客户端原因（§3.2），回完正常关闭。
    /// </remarks>
    private async ValueTask<SshChannel?> OpenTunnelAsync(
        Func<CancellationToken, ValueTask<SshChannel>> open,
        StreamRelayEndpoint local,
        string description,
        string target,
        byte? socksAddressType,
        CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.ChannelOpenTimeout != Timeout.InfiniteTimeSpan)
        {
            limit.CancelAfter(_options.ChannelOpenTimeout);
        }

        SocksReply reply;
        string message;
        Exception? error;
        TimeSpan? timedOutAfter = null;
        try
        {
            return await open(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 服务端还在连目标。迟到的确认由连接收尾（立刻关掉），迟到的拒绝没人等。
            reply = SocksReply.TtlExpired;
            message = $"到 {description} 的隧道在 {_options.ChannelOpenTimeout.TotalSeconds:0.#} 秒内没有打开（服务端还没应答），这一条被放弃。";
            error = null;
            timedOutAfter = _options.ChannelOpenTimeout;
        }
        catch (SshChannelException ex)
        {
            // 应答码对不对是有实际后果的：curl 与浏览器会据此决定要不要重试、
            // 以及报给用户哪句话。一律回 0x01 等于把信息丢了。
            reply = SocksHandshake.MapFailure(ex.OpenFailureReason);
            message = $"到 {description} 的隧道打不开：{ex.Message}";
            error = ex;
        }

        if (socksAddressType is { } addressType)
        {
            await SocksHandshake.WriteReplyAsync(local.Output, reply, addressType, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await local.AbortAsync().ConfigureAwait(false);
        }

        Report(ForwardErrorReason.ChannelOpen, message, error, target, timedOutAfter);
        return null;
    }

    private static EndPoint? SafeRemoteEndPoint(Socket socket)
    {
        try
        {
            return socket.RemoteEndPoint;
        }
        catch (Exception)
        {
            return null;   // 连接可能已经没了
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CompleteAsStopped();
        await _disconnectedRegistration.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 释放路径不抛。
        }

        foreach (Socket listener in _listeners)
        {
            listener.Dispose();
        }
        DeleteSocketFile(_createdSocketPath);

        try
        {
            await Task.WhenAll(_acceptLoops).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 同上。
        }

        // 等每条连接收完尾，之后才能释放槽位信号量与令牌源：
        // 曾经是直接释放 —— 还在收尾的连接随后 Release 一个已释放的信号量，
        // 异常落在没人观察的任务里；释放返回时连接也还开着。
        // 取消之后搬运会立刻中止，通道释放本身有时限，所以这里等得到头。
        try
        {
            await Task.WhenAll(_connections.Values).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 同上（HandleConnectionAsync 自己不抛）。
        }

        _connectionSlots.Dispose();
        _lifetime.Dispose();
    }
}
