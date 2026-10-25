// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §6、§7
//   OpenSSH PROTOCOL              SFTP 扩展章节
//   行为规格:                     velashell-docs/zh/ssh/spec/06-sftp.md 全部

using System.Buffers;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Sftp;

/// <summary>建立 SFTP 时的参数。</summary>
public sealed record SftpOptions
{
    /// <summary>底层通道的参数。</summary>
    public SshChannelOptions Channel { get; init; } = SshChannelOptions.Default with
    {
        // SFTP 是吞吐型负载，窗口给大一些；stderr 上只会来服务端的诊断噪音。
        WindowPolicy = SshWindowPolicy.Adaptive(2 * 1024 * 1024, 64 * 1024 * 1024),
        StderrMode = SshStderrMode.Discard,
    };

    /// <summary>在途请求数上限。</summary>
    /// <remarks>
    /// 在途请求数 × 块大小就是 SFTP 层的「窗口」。
    /// 和通道窗口一样，太小会在高 RTT 链路上直接封死吞吐。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">小于 1。</exception>
    public int MaxInFlight
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxInFlight), value, "在途请求数至少为 1。");
    } = 64;

    /// <summary>
    /// 块大小；<c>0</c> 表示按服务端宣告的 <c>limits@openssh.com</c> 定。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">为负。</exception>
    public int BlockSize
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(BlockSize), value, "块大小不能为负（0 表示按服务端宣告的定）。");
    }

    /// <summary>在途请求数是否按「深度有没有成为瓶颈」自动伸缩。</summary>
    /// <remarks>
    /// 关掉它可以得到确定性的内存占用（在途数 × 块大小），
    /// 代价是高 RTT 链路上吞吐被 <c>深度 × 块大小 / RTT</c> 封死。
    /// </remarks>
    public bool IsPipelineDepthAdaptive { get; init; } = true;

    /// <summary>自适应时在途请求数的上限；不能小于 <see cref="MaxInFlight"/>（连接时核对）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">小于 1。</exception>
    public int MaxPipelineDepth
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxPipelineDepth), value, "在途请求数的上限至少为 1。");
    } = 256;

    /// <summary>列目录时过滤掉 <c>.</c> 与 <c>..</c>。</summary>
    /// <remarks>它们**会**出现在服务端返回的结果里。</remarks>
    public bool IsFilteringDotEntries { get; init; } = true;

    /// <summary>服务端的文件名用什么编码；<see langword="null"/>（默认）为 UTF-8。</summary>
    /// <remarks>
    /// <para>
    /// SFTP v3 没规定文件名编码。默认按 UTF-8，<b>解不开的字节无损往返</b>：列出来的名字拿回去开、删、改名，
    /// 到服务端的还是原来那串字节（界面上显示成替换字符）—— 见 velashell-docs/zh/ssh/spec/06 §4.7。
    /// </para>
    /// <para>
    /// 服务端用 GBK、Shift-JIS 之类的老编码时，在这里给那个编码（宿主通常复用这个会话的终端编码），名字才显示得对。
    /// 那种编码下非法的字节不保证往返。
    /// </para>
    /// </remarks>
    public System.Text.Encoding? FileNameEncoding { get; init; }

    /// <summary>
    /// 握手（<c>INIT</c> → <c>VERSION</c>、查 limits、取工作目录）最多等多久；到点以 <see cref="SshFailureReason.Timeout"/> 失败。
    /// </summary>
    /// <remarks>
    /// sftp-server 是经登录 shell 起的：启动文件卡住（等输入、挂在一个网络盘上）时它永远不会回 VERSION。
    /// 曾经只靠调用方的令牌，没给就一直挂着。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">不为正。</exception>
    public TimeSpan HandshakeTimeout
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout), value, "握手时限必须为正。");
    } = TimeSpan.FromSeconds(30);

    /// <summary>关闭一个文件流时，等在途写入确认与 <c>CLOSE</c> 应答各最多等多久。</summary>
    /// <remarks>
    /// 服务端不再应答（卡死、链路半断）时，释放曾经一直等下去 —— 关一个标签页、取消一次上传都会挂住。
    /// 到点了照样发 <c>CLOSE</c>、还句柄额度，写入没确认完的话报带续传点的中断。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">不为正。</exception>
    public TimeSpan CloseTimeout
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(CloseTimeout), value, "关闭的时限必须为正。");
    } = TimeSpan.FromSeconds(30);

    /// <summary>默认参数。</summary>
    public static SftpOptions Default { get; } = new();

    /// <summary>跨字段的核对：在开通道<b>之前</b>做，不自洽就抛，不留下一条开了没人关的通道。</summary>
    /// <exception cref="ArgumentException">参数不自洽。</exception>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Channel, nameof(Channel));
        if (MaxPipelineDepth < MaxInFlight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxPipelineDepth), MaxPipelineDepth, $"在途请求数的上限不能小于起始值 MaxInFlight（{MaxInFlight}）。");
        }

        // 接收窗口至少装得下一整个 SFTP 报文（长度前缀 + 最长的报文）：报文收齐之前收包循环一个字节都不消费，
        // 而窗口只随消费回补 —— 窗口比报文小，就是对端等窗口、我们等报文，谁也动不了，也没有任何报错。
        // 通道的默认窗口（256 KiB）恰好装不下一块 256 KiB 的 DATA 应答。
        int frame = 4 + SftpProtocol.MaxMessageLength;
        if (Channel.WindowPolicy.MinimumBytes < frame)
        {
            throw new ArgumentException(
                $"SFTP 通道的接收窗口至少要 {frame} 字节（一整个 SFTP 报文），现在是 {Channel.WindowPolicy.MinimumBytes} 字节。",
                nameof(Channel));
        }
    }
}

/// <summary>面向使用者的 SFTP 客户端。</summary>
public sealed partial class SftpFileSystem : IAsyncDisposable
{
    private readonly SshChannel _channel;
    private readonly SftpRequestPipeline _pipeline;
    private readonly SftpOptions _options;

    /// <summary>文件名与路径怎么编解码（见 <see cref="SftpOptions.FileNameEncoding"/>）。</summary>
    private readonly SftpNameCodec _names;

    // 列目录可以在几个枚举里并发跑 —— 走 Interlocked。
    private int _malformedEntriesSkipped;

    /// <summary>
    /// 服务端宣告了 <c>max-open-handles</c> 时的句柄额度：每个开着的文件、目录占一个，关了还回来。
    /// 没宣告时为 <see langword="null"/>（不限）。
    /// </summary>
    private SemaphoreSlim? _handleSlots;
    private bool _disposed;

    private SftpFileSystem(
        SshChannel channel, SftpRequestPipeline pipeline, SftpOptions options, SftpCapabilities capabilities)
    {
        _channel = channel;
        _pipeline = pipeline;
        _options = options;
        _names = SftpNameCodec.For(options.FileNameEncoding);
        Capabilities = capabilities;
        WorkingDirectory = ".";
    }

    /// <summary>这台服务端支持什么。</summary>
    public SftpCapabilities Capabilities { get; }

    /// <summary>连上时的工作目录（通常就是家目录）。</summary>
    public string WorkingDirectory { get; private set; }

    /// <summary>实际使用的块大小。</summary>
    public int BlockSize { get; private set; }

    /// <summary>这条 SFTP 会话还能用吗。</summary>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/06 §5.4〕为假时之后的每个请求都会失败，<b>这个对象不会自己恢复</b> —— 丢掉它、
    /// 在同一条连接上再 <see cref="ConnectAsync"/> 一个。为假的原因：sftp-server 退出了、服务端按 <c>ChannelTimeout</c>
    /// 关掉了闲置的通道、收到了畸形帧、整条连接断了，或者已经释放。原因在 <see cref="Closed"/> 里。
    /// </para>
    /// <para>
    /// 曾经没有这个信号：宿主只能看「对象还在不在」，通道死了之后这个会话的文件面板一直坏着，直到整条 SSH 连接重连。
    /// </para>
    /// </remarks>
    public bool IsConnected => !_pipeline.IsFaulted;

    /// <summary>这条 SFTP 会话结束时完成，结果是结束的原因（释放时是 <see cref="SftpUnavailableException"/>）。</summary>
    /// <remarks>
    /// 以<b>成功</b>完成、结果是原因，而不是以异常完成：没人等它时也不会变成未观察的任务异常。
    /// </remarks>
    public Task<Exception> Closed => _pipeline.Closed;

    /// <summary>在一条会话上起 SFTP。</summary>
    /// <exception cref="SftpUnavailableException">服务端没有 sftp 子系统，或版本太低。</exception>
    /// <exception cref="SshChannelException">
    /// session 通道都没开成（<see cref="SshFailureReason.ChannelOpenFailed"/>：服务端 <c>MaxSessions</c> 满了、
    /// 管理上禁止，或者本端的通道数 / 窗口预算用尽）—— 原样抛出，可以稍后重试。
    /// </exception>
    public static async ValueTask<SftpFileSystem> ConnectAsync(
        SshConnection connection,
        SftpOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SftpOptions effective = options ?? SftpOptions.Default;

        // 〔velashell-docs/zh/ssh/spec/06 §一〕参数先核对、再开通道：曾经是通道开了才在建流水线时抛，
        // 那条 sftp 通道就一直挂在连接上，没人关。
        effective.Validate();

        SshChannel channel;
        try
        {
            // 丢弃 stderr 时也留住最后 1 KiB：sftp-server 起不来时，它在 stderr 上说的那一句是唯一的线索。
            SshChannelOptions channelOptions = effective.Channel.StderrMode == SshStderrMode.Discard
                ? effective.Channel with { DiscardedStderrTailBytes = StderrTailBytes }
                : effective.Channel;
            channel = await connection
                .OpenSubsystemAsync(SshProtocolNames.SubsystemSftp, channelOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (SshChannelException ex) when (ex.Reason == SshFailureReason.ChannelRequestRejected)
        {
            // 只有「通道开了、subsystem 请求被拒」才是没有 sftp 子系统。通道都没开成（服务端 MaxSessions 满、
            // 管理上禁止、本端通道数或窗口预算用尽）原样抛出：曾经一律改写成「sshd_config 缺 Subsystem」，
            // 用户去改一个本来没问题的配置，也丢了「稍后可以重试」这个信息。
            //
            // 〔决策 velashell-docs/zh/ssh/spec/06 §一〕**不自动回退到 `exec sftp-server`。**
            // 回退等于在管理员明确禁用 subsystem 的情况下绕过他的配置。
            throw new SftpUnavailableException(
                "服务端没有提供 sftp 子系统。常见原因是 sshd_config 里缺少或注释掉了 " +
                "「Subsystem sftp ...」那一行。（本库不会自动改用「exec sftp-server」绕开它 —— " +
                "那等于绕过管理员的配置。）", ex)
            {
                FailureKind = SftpUnavailableKind.SubsystemRejected,
            };
        }

        SftpRequestPipeline pipeline;
        try
        {
            pipeline = new(channel, effective.MaxInFlight, effective.IsPipelineDepthAdaptive, effective.MaxPipelineDepth);
        }
        catch (Exception)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        pipeline.Start();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effective.HandshakeTimeout);
        bool versionReceived = false;
        try
        {
            SftpCapabilities capabilities = await HandshakeAsync(pipeline, deadline.Token).ConfigureAwait(false);
            versionReceived = true;
            SftpFileSystem fileSystem = new(channel, pipeline, effective, capabilities);

            await fileSystem.InitializeAsync(deadline.Token).ConfigureAwait(false);
            return fileSystem;
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
            throw new SftpUnavailableException(
                SshFailureReason.Timeout,
                $"SFTP 握手在 {effective.HandshakeTimeout.TotalSeconds:0} 秒内没有完成：sftp-server 没有回应。" +
                "常见原因是服务端登录 shell 的启动文件卡住了（等输入、挂在一个连不上的网络盘上）。", ex)
            {
                FailureKind = SftpUnavailableKind.HandshakeTimeout,
            };
        }
        catch (SftpTransferInterruptedException ex) when (!versionReceived && connection.CloseReason is null && !cancellationToken.IsCancellationRequested)
        {
            // 〔velashell-docs/zh/ssh/spec/06 §一〕连接好好的，通道却在 VERSION 之前就关了：sftp-server 没起来
            // （Subsystem 指向的程序不存在、没有执行权限、被 ForceCommand 顶替……）。曾经报「SFTP 通道在还有在途请求时就关闭了」，
            // 退出码与它在 stderr 上说的话都丢了 —— 而那是唯一的线索。
            SftpUnavailableException exited = await DescribeServerExitAsync(channel, ex).ConfigureAwait(false);
            await pipeline.DisposeAsync().ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
            throw exited;
        }
        catch (Exception)
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>丢弃 stderr 时为说明「sftp-server 为什么没起来」留住的末尾字节数。</summary>
    private const int StderrTailBytes = 1024;

    /// <summary>退出状态常在 EOF 之后才到（stdout 读完时还没来），等它的上限。</summary>
    private static readonly TimeSpan ExitStatusGrace = TimeSpan.FromSeconds(2);

    /// <summary>sftp-server 没等握手就退出了：取退出状态与 stderr 的末尾，拼成说得清原因的异常。</summary>
    private static async ValueTask<SftpUnavailableException> DescribeServerExitAsync(SshChannel channel, Exception inner)
    {
        SshExitStatus exit;
        using (CancellationTokenSource grace = new(ExitStatusGrace))
        {
            try
            {
                exit = await channel.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                exit = new SshExitStatus(null, null, false, null);
            }
        }

        string? stderr = channel.DiscardedStderrTail;
        string how = exit.ExitCode is { } code ? $"退出码 {code}"
            : exit.ExitSignalName is { } signal ? $"信号 {PeerText.Sanitize(signal, 32)}"
            : "没有报退出码";
        string said = stderr is null ? "" : $"它在 stderr 上说：{stderr}。";
        return new SftpUnavailableException(
            SshFailureReason.CommandFailed,
            $"服务端的 sftp-server 没等 SFTP 建立就退出了（{how}）。{said}" +
            "常见原因是 sshd_config 里「Subsystem sftp」指向的程序不存在或没有执行权限。", inner)
        {
            ServerExitStatus = exit.ExitCode,
            ServerErrorOutput = stderr,
            FailureKind = SftpUnavailableKind.ServerExited,
        };
    }

    private static async ValueTask<SftpCapabilities> HandshakeAsync(
        SftpRequestPipeline pipeline, CancellationToken cancellationToken)
    {
        Task<SftpResponse> versionTask = pipeline.WaitForVersionAsync();

        ArrayBufferWriter<byte> init = new();
        SftpWire.WriteInit(init);
        await pipeline.SendRawAsync(init.WrittenMemory, cancellationToken).ConfigureAwait(false);

        using SftpResponse response = await versionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        (uint version, IReadOnlyDictionary<string, byte[]> extensions) = SftpWire.ReadVersion(response.Payload);

        if (version < SftpProtocol.Version)
        {
            // v0–v2 与 v3 的差异过大（没有 ATTRS 的部分字段、没有 handle 语义保证）。
            throw new SftpUnavailableException(
                $"服务端只支持 SFTP v{version}，本库要求至少 v{SftpProtocol.Version}。")
            {
                FailureKind = SftpUnavailableKind.VersionTooOld,
                ServerVersion = version,
            };
        }

        if (version > SftpProtocol.Version)
        {
            // 〔Q9，velashell-docs/zh/ssh/spec/06 §一〕draft-02 §4：服务端回双方版本里较小的那个 —— 我们发的是 3，回得比 3 大的服务端要么有缺陷，
            // 要么会按自己的版本说话（v4 起 ATTRS 的结构不同），按 v3 解析就是静默错位：大小、时间、权限读成别的字段。
            // 曾经「降到 3 继续」。
            throw new SftpUnavailableException(
                SshFailureReason.ProtocolError,
                $"服务端回的 SFTP 版本是 v{version}：按 draft-02 §4 它应当回双方版本里较小的那个（我们发的是 v{SftpProtocol.Version}）。" +
                "按 v3 去解析一个说更高版本的服务端会把文件属性读错，所以不连。")
            {
                FailureKind = SftpUnavailableKind.VersionTooNew,
                ServerVersion = version,
            };
        }

        return new SftpCapabilities(version, extensions);
    }

    private async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        // 服务端宣告了上限就按它来，而不是写死。
        if (Capabilities.HasLimits)
        {
            try
            {
                Capabilities.Limits = await QueryLimitsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SshException)
            {
                // 宣告了却查不出来 —— 用保守默认继续，不因此连不上。
            }
        }

        BlockSize = ChooseBlockSize(_options.BlockSize, Capabilities.Limits);

        // 〔velashell-docs/zh/ssh/spec/06 §7.1〕宣告了同时能开几个句柄，就按它排队：曾经读了不用，
        // 并发传输撞上服务端的句柄上限时只会得到一个随机的「操作失败」。
        if (Capabilities.Limits.MaxOpenHandles > 0)
        {
            int slots = (int)Math.Min(Capabilities.Limits.MaxOpenHandles, int.MaxValue);
            _handleSlots = new SemaphoreSlim(slots, slots);
        }

        // 〔决策 velashell-docs/zh/ssh/spec/06 §4.6〕连上就对 "." 做一次 REALPATH。
        // 这是唯一可靠的「用户家目录在哪」的答案 —— 比拼 /home/{user} 靠谱得多。
        try
        {
            WorkingDirectory = await GetRealPathAsync(".", cancellationToken).ConfigureAwait(false);
        }
        catch (SftpException)
        {
            // 少数服务端会拒绝对 "." 做 REALPATH。那不该让整个连接失败。
            WorkingDirectory = ".";
        }
    }

    /// <summary>
    /// 块大小 = min(想要的, 服务端的读写上限, 服务端的报文上限减去请求头, 本端肯收的最大数据块)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>服务端给 0 是「没说」，不是「一个字节」。</b>曾经按字面取，块大小就成了 1 ——
    /// 一个 1 MB 的文件要一百万个请求。
    /// </para>
    /// <para>
    /// 读上限也要算进来：比它长的 <c>READ</c> 会被截短，顺序读要先为每个缺口补发请求、学到服务端实际给的长度之后才顺（spec/06 §5.5）。
    /// 使用者指定的块大小同样要服从这些上限 ——
    /// 超长的 <c>WRITE</c> 会被拒，OpenSSH 甚至直接断开 SFTP 会话。
    /// </para>
    /// </remarks>
    internal static int ChooseBlockSize(int requested, SftpLimits limits)
    {
        long serverCap = long.MaxValue;
        if (limits.MaxWriteLength > 0)
        {
            serverCap = Math.Min(serverCap, (long)Math.Min(limits.MaxWriteLength, int.MaxValue));
        }
        if (limits.MaxReadLength > 0)
        {
            serverCap = Math.Min(serverCap, (long)Math.Min(limits.MaxReadLength, int.MaxValue));
        }
        if (limits.MaxPacketLength > SftpProtocol.RequestHeaderAllowance)
        {
            serverCap = Math.Min(
                serverCap, (long)Math.Min(limits.MaxPacketLength, int.MaxValue) - SftpProtocol.RequestHeaderAllowance);
        }

        long size = requested > 0
            ? requested
            : serverCap == long.MaxValue ? SftpProtocol.DefaultBlockSize : serverCap;

        return (int)Math.Clamp(Math.Min(size, serverCap), 1, SftpProtocol.MaxBlockSize);
    }

    private async ValueTask<SftpLimits> QueryLimitsAsync(CancellationToken cancellationToken)
    {
        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteExtended(output, id, SftpExtensionNames.Limits, []),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path: null, SftpOperation.QueryLimits, SftpMessageType.ExtendedReply);
        return SftpWire.ReadLimits(response.Payload);
    }

    // ------------------------------------------------------------ 内部

    /// <summary>等一个句柄额度（服务端宣告了 <c>max-open-handles</c> 时）。</summary>
    private Task AcquireHandleSlotAsync(CancellationToken cancellationToken) =>
        _handleSlots?.WaitAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>还一个句柄额度。</summary>
    private void ReleaseHandleSlot() => _handleSlots?.Release();

    /// <summary>当前空着的句柄额度（测试用）；不限时为 <see langword="null"/>。</summary>
    internal int? FreeHandleSlots => _handleSlots?.CurrentCount;

    /// <summary>取消之后迟到的 <c>HANDLE</c> 应答 —— 句柄不关就泄漏在服务端。</summary>
    private void CloseLateHandle(SftpResponse response)
    {
        if (response.Type != SftpMessageType.Handle)
        {
            return;
        }

        byte[] handle;
        try
        {
            handle = SftpWire.ReadHandle(response.Payload);
        }
        catch (Exception)
        {
            return;
        }

        // 不等应答：这是善后，不是业务路径。
        _ = CloseHandleQuietlyAsync(handle).AsTask();
    }

    private async ValueTask CloseHandleQuietlyAsync(byte[] handle)
    {
        try
        {
            using SftpResponse response = await _pipeline.SendAsync(
                (output, id) => SftpWire.WriteHandleRequest(output, SftpMessageType.Close, id, handle))
                .ConfigureAwait(false);
            _ = response;
        }
        catch (Exception)
        {
            // 通道已经没了的话服务端会自己回收句柄。
        }
    }

    /// <summary>目录项的名字是不是一个单纯的名字：非空、不含 <c>/</c> 与 NUL。</summary>
    internal static bool IsPlainName(string name) =>
        name.Length > 0 && !name.Contains('/', StringComparison.Ordinal) && !name.Contains('\0', StringComparison.Ordinal);

    /// <summary>拼路径。<b>SFTP 的路径分隔符永远是 <c>/</c></b>，与本机平台无关。</summary>
    internal static string CombinePath(string directory, string name)
    {
        if (string.IsNullOrEmpty(directory) || directory == ".")
        {
            return name;
        }
        return directory.EndsWith('/') ? directory + name : $"{directory}/{name}";
    }

    private static void ValidatePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // 含 NUL 的路径**在本地就拒掉**，不发给服务端：
        // 它在不同服务端上的行为从「截断」到「拒绝」都有，全是意外。
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("路径里不能含有 NUL 字符。", nameof(path));
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

        await _pipeline.DisposeAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
