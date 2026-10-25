// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL        auth-agent-req@openssh.com / auth-agent@openssh.com
//   OpenSSH PROTOCOL.agent  agent 协议;session-bind@openssh.com
//   RFC 4254 §5.1           CHANNEL_OPEN_FAILURE
//   行为规格:               velashell-docs/zh/ssh/spec/07-forwarding.md §七

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Forwarding;

/// <summary>把远端的 agent 请求桥到本机 ssh-agent。</summary>
/// <remarks>
/// <para>
/// 机制是三步：
/// </para>
/// <list type="number">
///   <item>在 <b>session 通道</b>上发 <c>auth-agent-req@openssh.com</c>。</item>
///   <item>服务端随后可以发起 <c>auth-agent@openssh.com</c> 通道。</item>
///   <item>我们把那条通道上的字节流桥到本机 agent。</item>
/// </list>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/07 §7.2〕<b>我们只做转发，不做 agent 服务端。</b>
/// 本机 agent 由操作系统或别的软件提供。
/// </para>
/// </remarks>
public sealed class AgentForwarder : IIncomingChannelHandler, IAsyncDisposable
{
    private const int MaxAgentMessageLength = 256 * 1024;

    /// <summary>agent 通道的接收窗口：一整条最长报文（4 字节长度 + 内容）。</summary>
    internal const int AgentChannelWindowBytes = 4 + MaxAgentMessageLength;

    /// <summary>一条 agent 通道上最多记几条验过的会话声明；转发链再长也用不了这么多，多出来的挤掉最早的。</summary>
    internal const int MaxSessionBindingsPerChannel = 16;

    private readonly Session.SshConnection _connection;
    private readonly Func<CancellationToken, ValueTask<SshAgentClient>> _connectAgent;
    private readonly AgentForwardOptions _options;
    private readonly SemaphoreSlim _slots;
    private bool _disposed;

    // 计数器会被最多 MaxConnections 条 agent 通道同时改 —— 一律走 Interlocked。
    private int _signatureRequests;
    private int _signaturesDenied;
    private int _identityListings;
    private int _keysHidden;

    /// <summary>转发器自己的生命周期：释放时取消，已经打开的 agent 通道随之断开。</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// <c>GetOptionsAsync</c> 里连好、声明好会话的本机 agent 连接，等 <c>HandleAsync</c> 取走（或 <c>OnOpenAborted</c> 丢掉）。
    /// </summary>
    /// <remarks>
    /// 这些连接连的是同一个 agent、对同一个会话做了同样的声明，彼此可以互换 —— 所以不必把某一条与某一条通道对上号，
    /// 只要「每放进一条，就恰好有一次 <c>HandleAsync</c> 或 <c>OnOpenAborted</c> 来取」：连接对每一次成功的
    /// <c>GetOptionsAsync</c> 恰好调用其中之一，取的时候队列里一定有。
    /// </remarks>
    private readonly ConcurrentQueue<SshAgentClient> _readyAgents = new();

    private AgentForwarder(Session.SshConnection connection, AgentForwardOptions options)
    {
        _connection = connection;
        _options = options;
        _connectAgent = options.LocalConnector ?? (ct => SshAgentClient.ConnectAsync(options.AgentEndpoint, ct));
        _slots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
    }

    /// <summary>远端请求过几次签名。</summary>
    public int SignatureRequests => Volatile.Read(ref _signatureRequests);

    /// <summary>有几次因为策略被拒。</summary>
    public int SignaturesDenied => Volatile.Read(ref _signaturesDenied);

    /// <summary>远端列过几次身份。</summary>
    public int IdentityListings => Volatile.Read(ref _identityListings);

    /// <summary>被隐藏掉的密钥数（不在 <see cref="AgentForwardOptions.AllowedKeys"/> 里的）。</summary>
    public int KeysHidden => Volatile.Read(ref _keysHidden);

    /// <summary>在一条 session 通道上请求 agent 转发。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="channel">要在哪条 session 通道上请求。</param>
    /// <param name="options">参数与安全约束。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshForwardException">本机 agent 连不上，或者服务端拒绝了转发请求。</exception>
    /// <remarks>
    /// <c>internal</c>：对外的入口是 <see cref="SshSessionRequestOptions.AgentForwarding"/> ——
    /// 请求要夹在 <c>x11-req</c> 与 <c>env</c> 之间发（velashell-docs/zh/ssh/spec/07 §7.5.3），
    /// 让调用方自己在一条裸通道上调它，那个时序就交给了调用方。
    /// </remarks>
    internal static async ValueTask<AgentForwarder> RequestAsync(
        Session.SshConnection connection,
        SshChannel channel,
        AgentForwardOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);

        AgentForwarder forwarder = new(connection, options);

        // 〔velashell-docs/zh/ssh/spec/07 §7.1〕**先试连本机 agent，连不上就不宣告。**宣告了，远端的 SSH_AUTH_SOCK
        // 就指向一个永远连不通的 agent：远端每个想用它的程序都要白走一遍，使用者却看不出是本机这边没开 agent。
        await forwarder.ProbeLocalAgentAsync(cancellationToken).ConfigureAwait(false);

        // 先登记处理器再发请求：服务端可能在应答之后立刻就发起通道，
        // 那时处理器必须已经在位，否则第一条会被拒。
        //
        // 用 Add 而不是替换：同一条连接上几个会话都开了 agent 转发时，
        // 各挂各的，释放一个不会把别人的摘掉。
        connection.AddIncomingChannelHandler(SshProtocolNames.ChannelAuthAgent, forwarder);

        try
        {
            bool accepted = await channel.SendRequestAsync(
                SshProtocolNames.RequestAuthAgent, default, wantReply: true, cancellationToken)
                .ConfigureAwait(false);

            if (!accepted)
            {
                throw new SshForwardException(SshFailureReason.ForwardRejected,
                    "服务端拒绝了 agent 转发请求。常见原因是 sshd_config 里 AllowAgentForwarding no。")
                {
                    FailureKind = SshForwardFailureKind.AgentForwardRejected,
                };
            }

            return forwarder;
        }
        catch (Exception)
        {
            connection.RemoveIncomingChannelHandler(SshProtocolNames.ChannelAuthAgent, forwarder);
            throw;
        }
    }

    // ------------------------------------------------------------ 本机 agent

    /// <summary>试连一次本机 agent，连上就关掉 —— 只为确认它在。</summary>
    /// <exception cref="SshForwardException">连不上；<see cref="SshException.Reason"/> 沿用 agent 那边的原因。</exception>
    private async ValueTask ProbeLocalAgentAsync(CancellationToken cancellationToken)
    {
        try
        {
            SshAgentClient probe = await _connectAgent(cancellationToken).ConfigureAwait(false);
            await probe.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SshForwardException(
                ex is SshException agentFailure ? agentFailure.Reason : SshFailureReason.AgentUnavailable,
                $"本机 ssh-agent 不可用，没有请求 agent 转发：{ex.Message}", ex)
            {
                FailureKind = SshForwardFailureKind.LocalAgentUnavailable,
            };
        }
    }

    /// <summary>为一条 agent 通道连本机 agent，并向它声明会话（转发用）。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/07 §7.4〕声明必须先于转发任何远端请求 —— 远端那一跳自己的声明要接在它后面。
    /// agent 不支持声明时照常转发。声明把连接弄断时（只有自定义连接器给的流才会把这个失败交上来）重连一次、不再声明：
    /// 不能让远端为一个可有可无的扩展付出「agent 不可用」的代价。
    /// </remarks>
    private async ValueTask<SshAgentClient> ConnectForChannelAsync(CancellationToken cancellationToken)
    {
        SshAgentClient agent = await _connectAgent(cancellationToken).ConfigureAwait(false);
        try
        {
            await agent.DeclareSessionAsync(
                    _connection.SessionProof, SshAgentConnectionPurpose.Forwarding, cancellationToken)
                .ConfigureAwait(false);
            return agent;
        }
        catch (SshAgentException) when (!cancellationToken.IsCancellationRequested)
        {
            await agent.DisposeAsync().ConfigureAwait(false);
            return await _connectAgent(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await agent.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------ 入站通道

    /// <inheritdoc />
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/07 §7.1〕<b>本机 agent 在确认通道之前连好</b>：连不上就回 <c>CHANNEL_OPEN_FAILURE</c>，
    /// 远端的程序当场就知道 agent 用不了。曾经是先确认、再在后台连 —— Windows 上 agent 服务没起时那一步没有时限，
    /// 远端的 ssh / git 就一直挂着。这里不在接收循环上（连接把「问处理器」放在后台做），等一次本机 IPC 不碍事。
    /// </remarks>
    async ValueTask<SshChannelOptions> IIncomingChannelHandler.GetOptionsAsync(
        string channelType, ReadOnlyMemory<byte> typeSpecificPayload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_slots.Wait(0, CancellationToken.None))
        {
            throw new SshForwardException(SshFailureReason.LimitExceeded,
                $"同时打开的 agent 通道已达上限 {_options.MaxConnections}。")
            {
                FailureKind = SshForwardFailureKind.ChannelLimitReached,
                Limit = _options.MaxConnections,
            };
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _readyAgents.Enqueue(await ConnectForChannelAsync(linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            _slots.Release();
            throw;
        }
        catch (Exception ex)
        {
            _slots.Release();

            // 消息会作为拒绝理由发给服务端 —— 只说「本机 agent 不可用」，本机的路径、管道名不往外送。
            throw new SshForwardException(
                ex is SshException agentFailure ? agentFailure.Reason : SshFailureReason.AgentUnavailable,
                "本机 ssh-agent 不可用。", ex)
            {
                FailureKind = SshForwardFailureKind.LocalAgentUnavailable,
            };
        }

        // ⚠️ 窗口至少要装得下**一整条**最长的 agent 报文（长度前缀 + 256 KiB）。
        //    报文收齐之前读的一方一个字节都不消费（见 ReadAgentMessageAsync），而窗口只随消费回补：
        //    窗口比报文小，就是对端等窗口、我们等报文，谁也动不了。曾经给的是 32 KiB，
        //    签一段稍长的数据（ssh-keygen -Y sign、证书）就卡死在那里。
        //    窗口只是额度，不是预先分配的内存；平常的报文只有几百字节。
        return SshChannelOptions.Default with
        {
            WindowPolicy = SshWindowPolicy.Fixed(AgentChannelWindowBytes),
            StderrMode = SshStderrMode.Discard,
        };
    }

    /// <inheritdoc />
    /// <remarks>还回 <c>GetOptionsAsync</c> 占的并发槽位，丢掉为它连好的那条本机 agent 连接。</remarks>
    void IIncomingChannelHandler.OnOpenAborted(string channelType, ReadOnlyMemory<byte> typeSpecificPayload)
    {
        if (_readyAgents.TryDequeue(out SshAgentClient? agent))
        {
            // 释放只是关一条本机 IPC，不抛；这里要同步返回，不等它。
            _ = agent.DisposeAsync().AsTask();
        }
        _slots.Release();
    }

    /// <inheritdoc />
    async Task IIncomingChannelHandler.HandleAsync(
        SshChannel channel, ReadOnlyMemory<byte> typeSpecificPayload, CancellationToken cancellationToken)
    {
        // 取不到只会发生在转发器刚被释放、把队列清空了的时候 —— 那这条通道本来也不该再转发了。
        _readyAgents.TryDequeue(out SshAgentClient? agent);

        try
        {
            if (agent is null)
            {
                return;
            }

            // ⚠️ 连上转发器自己的生命周期，不只是连接的。传进来的令牌属于整条连接 ——
            //    只看它的话，释放转发器（比如关掉开了 agent 转发的那个 shell）之后，
            //    已经打开的 agent 通道照样逐条转发签名，远端主机上的 root 可以一直拿着它用，
            //    直到整条连接断开（而连接上可能还开着 SFTP 或别的会话）。
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);

            await BridgeAsync(channel, agent, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 收工。
        }
        catch (SshException)
        {
            // 本机 agent 中途断了，或者远端发了畸形请求。
            // **单条通道的失败不影响会话** —— 关掉它就好。
        }
        finally
        {
            if (agent is not null)
            {
                await agent.DisposeAsync().ConfigureAwait(false);
            }
            _slots.Release();
        }
    }

    /// <summary>把远端的 agent 请求逐条转给本机 agent。</summary>
    /// <remarks>
    /// <b>不是字节级的直通。</b>我们要把每一条请求解出来看一眼 ——
    /// 只有这样才能实现「只转发指定的密钥」与「逐次签名确认」。
    /// 直通管子省事，但那两条安全约束就无从谈起。
    /// </remarks>
    private async Task BridgeAsync(
        SshChannel channel, SshAgentClient agent, CancellationToken cancellationToken)
    {
        PipeReader input = channel.StandardOutput;
        PipeWriter output = channel.StandardInput;

        // 远端那一跳在这条通道上声明过的会话（验过签名的），给逐次确认认出「要登录哪里」。
        // 跟着通道走：远端的 ssh 每次连下一跳都开一条新的 agent 连接，声明与签名在同一条上。
        List<AgentSignedData.SessionBinding> bindings = [];

        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? request = await ReadAgentMessageAsync(input, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;   // 远端关了
            }

            // ⚠️ 再看一眼源头。释放是把 _lifetime 放到线程池上取消的：它自己的 IsCancellationRequested
            //    当场变真，链接出来的 cancellationToken 却要等回调跑完才变 —— 恰好夹在中间到的一条请求
            //    会被读出来，释放都返回了还替远端签一次名。
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            byte[] response = await HandleAgentRequestAsync(request, agent, bindings, cancellationToken)
                .ConfigureAwait(false);

            byte[] framed = new byte[4 + response.Length];
            BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)response.Length);
            response.CopyTo(framed.AsSpan(4));

            await output.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<byte[]> HandleAgentRequestAsync(
        byte[] request,
        SshAgentClient agent,
        List<AgentSignedData.SessionBinding> bindings,
        CancellationToken cancellationToken)
    {
        if (request.Length == 0)
        {
            return [SshAgentMessage.Failure];
        }

        return request[0] switch
        {
            SshAgentMessage.RequestIdentities =>
                await HandleListAsync(agent, cancellationToken).ConfigureAwait(false),
            SshAgentMessage.SignRequest =>
                await HandleSignAsync(request, agent, bindings, cancellationToken).ConfigureAwait(false),

            // 〔velashell-docs/zh/ssh/spec/07 §7.4〕远端那一跳自己的会话声明照转：转发链上每一跳都把声明接在
            // 我们的声明后面，agent 才认得出整条路径、执行 ssh-add -h 的约束。声明只会让 agent 对这条连接更严，
            // 放行它不放大远端的权限 —— 别的扩展我们说不准它能做什么，一律不转。
            SshAgentMessage.Extension when SshAgentClient.IsSessionDeclaration(request) =>
                await RelaySessionDeclarationAsync(request, agent, bindings, cancellationToken).ConfigureAwait(false),

            // 增删密钥、锁定 agent 之类的请求**一律不转发**。
            // 远端没有任何理由改动我们本机 agent 的状态。
            _ => [SshAgentMessage.Failure],
        };
    }

    /// <summary>照转远端的会话声明；开着逐次确认时，验过签名的那些顺手记下来。</summary>
    private ValueTask<byte[]> RelaySessionDeclarationAsync(
        byte[] request,
        SshAgentClient agent,
        List<AgentSignedData.SessionBinding> bindings,
        CancellationToken cancellationToken)
    {
        // 只有确认框用得上：没开逐次确认就不花这次验签。
        if (_options.ApproveSignature is not null
            && AgentSignedData.TryReadSessionBinding(request) is { } binding)
        {
            if (bindings.Count == MaxSessionBindingsPerChannel)
            {
                bindings.RemoveAt(0);
            }
            bindings.Add(binding);
        }

        return agent.RelaySessionDeclarationAsync(request, cancellationToken);
    }

    private async ValueTask<byte[]> HandleListAsync(SshAgentClient agent, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _identityListings);

        IReadOnlyList<SshAgentIdentity> all =
            await agent.ListIdentitiesAsync(cancellationToken).ConfigureAwait(false);

        List<SshAgentIdentity> visible = [];
        foreach (SshAgentIdentity identity in all)
        {
            if (IsAllowed(identity.PublicKey))
            {
                visible.Add(identity);
            }
            else
            {
                Interlocked.Increment(ref _keysHidden);
            }
        }

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteByte(SshAgentMessage.IdentitiesAnswer);
        writer.WriteUInt32((uint)visible.Count);

        foreach (SshAgentIdentity identity in visible)
        {
            writer.WriteString(identity.PublicKey.Blob.Span);
            writer.WriteUtf8String(identity.Comment);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private async ValueTask<byte[]> HandleSignAsync(
        byte[] request,
        SshAgentClient agent,
        IReadOnlyList<AgentSignedData.SessionBinding> bindings,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _signatureRequests);

        SshDataReader reader = new(new ReadOnlySequence<byte>(request));
        reader.ReadByte();

        byte[] keyBlob;
        byte[] data;
        uint flags;

        try
        {
            keyBlob = reader.ReadStringAsArray(MaxAgentMessageLength);
            data = reader.ReadStringAsArray(MaxAgentMessageLength);
            flags = reader.ReadUInt32();
        }
        catch (SshWireFormatException)
        {
            return [SshAgentMessage.Failure];
        }

        SshPublicKey key;
        try
        {
            key = SshPublicKey.Decode(keyBlob);
        }
        catch (Exception)
        {
            return [SshAgentMessage.Failure];
        }

        if (!IsAllowed(key))
        {
            Interlocked.Increment(ref _signaturesDenied);
            return [SshAgentMessage.Failure];
        }

        if (_options.ApproveSignature is { } confirm)
        {
            IReadOnlyList<SshAgentIdentity> identities =
                await agent.ListIdentitiesAsync(cancellationToken).ConfigureAwait(false);

            // 注释是给使用者看的（通常是私钥文件路径）——
            // 弹窗里只说「有人要用某把 ed25519 签名」没什么用。
            string comment = "";
            foreach (SshAgentIdentity candidate in identities)
            {
                if (candidate.PublicKey.Blob.Span.SequenceEqual(keyBlob))
                {
                    comment = candidate.Comment;
                    break;
                }
            }

            // 〔velashell-docs/zh/ssh/spec/07 §7.2.1〕只给钥和注释，使用者分不出这是自己在远端敲的 git pull，
            // 还是那台机器上有人在拿这把钥登录别处：被签的数据认得出来时，把「以谁登录、登录哪台」一并交上去。
            AgentSignatureRequest context = AgentSignedData.Describe(key, comment, data, bindings);

            bool approved = await confirm(context, cancellationToken).ConfigureAwait(false);

            if (!approved)
            {
                Interlocked.Increment(ref _signaturesDenied);
                return [SshAgentMessage.Failure];
            }
        }

        // 标志位决定 RSA 用哪种摘要。把它翻回算法名交给 agent 客户端。
        string algorithm = ChooseAlgorithm(key, flags);

        try
        {
            byte[] signature = await agent
                .SignAsync(keyBlob, data, algorithm, cancellationToken).ConfigureAwait(false);

            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteByte(SshAgentMessage.SignResponse);
            writer.WriteString(signature);
            return buffer.WrittenSpan.ToArray();
        }
        catch (SshAgentException)
        {
            return [SshAgentMessage.Failure];
        }
    }

    /// <remarks>
    /// 按 <see cref="SshPublicKey.PlainKeyType"/> 判断：RSA 证书的类型串是 <c>ssh-rsa-cert-v01@openssh.com</c>，
    /// 按 <c>KeyType</c> 比的话远端要的 SHA-2 标志位被忽略，证书就被签成 SHA-1。
    /// 返回的是普通签名算法名 —— 签名 blob 里写的本来就是它（OpenSSH PROTOCOL.certkeys）。
    /// </remarks>
    private static string ChooseAlgorithm(SshPublicKey key, uint flags)
    {
        if (key.PlainKeyType != SshAlgorithmNames.SshRsa)
        {
            return key.PlainKeyType;
        }

        return ((SshAgentSignFlags)flags).HasFlag(SshAgentSignFlags.RsaSha2_512)
            ? SshAlgorithmNames.RsaSha512
            : ((SshAgentSignFlags)flags).HasFlag(SshAgentSignFlags.RsaSha2_256)
                ? SshAlgorithmNames.RsaSha256
                : SshAlgorithmNames.SshRsa;
    }

    private bool IsAllowed(SshPublicKey key)
    {
        if (_options.AllowedKeys is not { } allowedKeys)
        {
            return true;   // 没给名单：整个 agent 都暴露（使用者显式打开 agent 转发时的默认）
        }

        // 按证书里那把钥比：证书用的是同一把私钥，放行了钥就等于放行了它的证书（反过来也一样）。
        foreach (SshPublicKey allowed in allowedKeys)
        {
            if (allowed.PlainKey.Blob.Span.SequenceEqual(key.PlainKey.Blob.Span))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>读一条 agent 报文（4 字节长度 + 内容）。</summary>
    private static async ValueTask<byte[]?> ReadAgentMessageAsync(
        PipeReader input, CancellationToken cancellationToken)
    {
        // 缓冲在循环外面：循环里 stackalloc 会把栈一点点吃光。
        byte[] lengthBytes = new byte[4];

        while (true)
        {
            ReadResult read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = read.Buffer;

            if (buffer.Length >= 4)
            {
                buffer.Slice(0, 4).CopyTo(lengthBytes);
                uint length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);

                if (length is 0 or > MaxAgentMessageLength)
                {
                    input.AdvanceTo(buffer.End);
                    return null;   // 畸形 —— 关掉这条通道
                }

                if (buffer.Length >= 4 + length)
                {
                    byte[] message = buffer.Slice(4, length).ToArray();
                    input.AdvanceTo(buffer.GetPosition(4 + length));
                    return message;
                }
            }

            if (read.IsCompleted)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                return null;
            }

            input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;

        _connection.RemoveIncomingChannelHandler(SshProtocolNames.ChannelAuthAgent, this);

        // 已经打开的 agent 通道一起断：它们的转发循环挂在 _lifetime 上，退出后连接会关掉通道。
        // 回调放到线程池上（与会话收尾同一条规矩）。令牌源不释放 —— 还在收尾的循环要读它。
        Session.Lifecycle.CancelInBackground(_lifetime);

        // 已经连好、还没交给通道的本机 agent 连接一并关掉；随后到的 HandleAsync 取不到，直接收工。
        while (_readyAgents.TryDequeue(out SshAgentClient? ready))
        {
            _ = ready.DisposeAsync().AsTask();
        }

        // 不 Dispose 信号量：还在跑的 agent 通道收尾时要 Release 它。
        // 它没有用到等待句柄，不释放也不漏任何非托管资源。
        return ValueTask.CompletedTask;
    }
}
