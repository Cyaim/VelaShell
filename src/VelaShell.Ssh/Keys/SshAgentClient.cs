// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-miller-ssh-agent  SSH Agent Protocol
//   OpenSSH PROTOCOL.agent  实现口径
//   行为规格:               velashell-docs/zh/ssh/design/architecture.md §8 第 5 项;velashell-docs/zh/ssh/spec/07-forwarding.md §七(加钥见 §7.3)

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Keys;

/// <summary>本机 ssh-agent 的客户端。</summary>
/// <remarks>
/// <para>
/// <b>私钥从不进入本进程。</b>我们只是把「请用这把公钥签这段数据」递过去。
/// 这也是加密私钥的推荐出路：把密钥加进 agent，本库通过 agent 用它。
/// </para>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/07 §7.2〕<b>我们只做客户端，不做 agent 服务端。</b>
/// 本机 agent 由操作系统或别的软件提供（OpenSSH agent / Pageant / 1Password…）。
/// </para>
/// </remarks>
public sealed class SshAgentClient : IAsyncDisposable
{
    /// <summary>单个 agent 报文的长度上限。</summary>
    private const int MaxMessageLength = 256 * 1024;

    /// <summary>Windows 上等 agent 的命名管道出现的上限。</summary>
    /// <remarks>
    /// 管道不存在时不带时限的连接会<b>一直重试</b>，直到管道出现 —— agent 服务没起，调用方就永远挂在那里
    /// （spec/07 §7.1）。本机 IPC 用不了多久，三秒足够分辨「在跑」与「没在跑」。
    /// 文件系统的存在性检查判断不了管道（对一个存在的管道也返回「不存在」），所以只能靠时限。
    /// </remarks>
    internal static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromSeconds(3);

    // 只在会话声明把连接弄断、要重开时换掉（见 DeclareSessionAsync），换的时候持有 _lock。
    private Stream _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>一次问答做到一半被打断过：这条连接上的问答已经错位，不能再用（持有 <see cref="_lock"/> 时读写）。</summary>
    private bool _outOfStep;

    /// <summary>由 <see cref="ConnectAsync"/> 连上的才能重开；<see cref="FromStream"/> 交来的流不知道怎么重开。</summary>
    private readonly bool _canReopen;

    /// <summary>释放时是否一并释放流（<see cref="FromStream"/> 的 <c>ownsStream</c>；自己连上的总是）。</summary>
    private readonly bool _ownsStream;

    // 已经声明过的会话。同一个会话只声明一次 —— 同一条 agent 连接上的几把钥共用一次声明。
    private byte[]? _declaredSessionId;
    private bool _declarationAccepted;
    private SshAgentConnectionPurpose _declaredPurpose;
    private bool _disposed;

    private SshAgentClient(Stream stream, string endpoint, bool canReopen, bool ownsStream = true)
    {
        _stream = stream;
        Endpoint = endpoint;
        _canReopen = canReopen;
        _ownsStream = ownsStream;
    }

    /// <summary>连到的是哪个端点（套接字路径或命名管道名）。</summary>
    public string Endpoint { get; }

    /// <summary>在一条现成的流上说 agent 协议。</summary>
    /// <param name="stream">双工流。</param>
    /// <param name="label">给人看的端点名，进 <see cref="Endpoint"/>、日志与异常；<see langword="null"/> 时为 <c>(stream)</c>。</param>
    /// <param name="ownsStream">释放本对象时是否一并释放 <paramref name="stream"/>。默认 <see langword="true"/>：交进来就归它。</param>
    /// <remarks>
    /// 不只是为了测试：agent 也可能在一条隧道的另一头，
    /// 或者由别的软件以自定义方式提供（1Password、YubiKey 代理…）。
    /// 那些情形下调用方自己把流准备好，交给我们说协议。
    /// </remarks>
    public static SshAgentClient FromStream(Stream stream, string? label = null, bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SshAgentClient(stream, label ?? "(stream)", canReopen: false, ownsStream);
    }

    /// <summary>本机 agent 的默认端点。</summary>
    /// <remarks>
    /// <para>其它平台看 <c>SSH_AUTH_SOCK</c>。</para>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/07 §7.1〕Windows 上 <c>SSH_AUTH_SOCK</c> 是命名管道（<c>\\.\pipe\…</c>）时采纳它 ——
    /// 1Password、KeePassXC 这类 agent 会这样配；否则用 OpenSSH agent 服务的管道。它更常指向 Git Bash / WSL 的
    /// Unix 套接字，那是另一套 agent，.NET 连不上，所以不是管道就不认。曾经在 Windows 上一律无视它，
    /// 宿主只好自己再判断一遍。
    /// </para>
    /// <para>
    /// OpenSSH agent 服务没在跑（它的管道不在）而当前用户开着 Pageant（PuTTY 0.75 起在 <c>\\.\pipe\pageant.用户名.…</c>
    /// 上说同一套 agent 协议）时用 Pageant —— PuTTY 用户的钥都在那里，不必为了 VelaShell 换 agent。
    /// 两个都在时仍用 OpenSSH 的（与以前一致）。Pageant 的管道名后半截随机器而变，所以按前缀在管道列表里找。
    /// </para>
    /// </remarks>
    public static string? DefaultEndpoint => DefaultEndpointFor(
        OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("SSH_AUTH_SOCK"), ListPipes, Environment.UserName);

    /// <summary>OpenSSH agent 服务的命名管道。</summary>
    private const string OpenSshAgentPipe = @"\\.\pipe\openssh-ssh-agent";

    /// <summary><see cref="DefaultEndpoint"/> 的判断本身（平台、环境变量与管道列表作参数，测试两个平台都能覆盖）。</summary>
    /// <param name="windows">是不是 Windows。</param>
    /// <param name="authSock"><c>SSH_AUTH_SOCK</c>。</param>
    /// <param name="listPipes">列出本机的命名管道（<c>\\.\pipe\名字</c>）；<see langword="null"/> 时不找 Pageant。</param>
    /// <param name="userName">当前用户名（Pageant 的管道名里有它）。</param>
    internal static string? DefaultEndpointFor(
        bool windows, string? authSock, Func<IEnumerable<string>>? listPipes = null, string? userName = null)
    {
        if (!windows)
        {
            return authSock;
        }
        if (authSock is not null && authSock.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            return authSock;
        }
        if (listPipes is not null && !string.IsNullOrEmpty(userName))
        {
            string[] pipes = [.. listPipes()];
            if (!pipes.Contains(OpenSshAgentPipe, StringComparer.OrdinalIgnoreCase))
            {
                string prefix = $@"\\.\pipe\pageant.{userName}.";
                if (pipes.FirstOrDefault(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } pageant)
                {
                    return pageant;
                }
            }
        }
        return OpenSshAgentPipe;
    }

    /// <summary>本机的命名管道；列不出来时为空（找不到 Pageant 不该让连 agent 这件事本身失败）。</summary>
    private static IEnumerable<string> ListPipes()
    {
        try
        {
            return Directory.GetFiles(@"\\.\pipe\");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>连本机 agent。</summary>
    /// <param name="endpoint">端点；<see langword="null"/> 表示用 <see cref="DefaultEndpoint"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshAgentException">
    /// 连不上。agent 没在跑（<c>SSH_AUTH_SOCK</c> 没设、套接字不在、命名管道在 <see cref="PipeConnectTimeout"/>
    /// 内没出现）时 <see cref="SshException.Reason"/> 是 <see cref="SshFailureReason.AgentNotRunning"/>，
    /// 其余是 <see cref="SshFailureReason.AgentUnavailable"/>。
    /// </exception>
    public static async ValueTask<SshAgentClient> ConnectAsync(
        string? endpoint = null, CancellationToken cancellationToken = default)
    {
        string? actual = endpoint ?? DefaultEndpoint;

        if (string.IsNullOrEmpty(actual))
        {
            throw new SshAgentException(SshFailureReason.AgentNotRunning,
                OperatingSystem.IsWindows()
                    ? "找不到 ssh-agent。Windows 上它是一个服务，用" +
                      "「Get-Service ssh-agent」看状态，「Start-Service ssh-agent」起它。"
                    : "环境变量 SSH_AUTH_SOCK 没有设 —— 本机没有在跑 ssh-agent，" +
                      "或者当前会话没继承到它。")
            {
                FailureKind = SshAgentFailureKind.NotRunning,
            };
        }

        Stream stream = await OpenStreamAsync(actual, cancellationToken).ConfigureAwait(false);
        return new SshAgentClient(stream, actual, canReopen: true);
    }

    private static async ValueTask<Stream> OpenStreamAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsWindows() && endpoint.StartsWith(@"\\.\pipe\", StringComparison.Ordinal))
            {
                string pipeName = endpoint[@"\\.\pipe\".Length..];
                NamedPipeClientStream pipe = new(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                try
                {
                    // ⚠️ **必须带时限**（见 PipeConnectTimeout）。曾经只带取消令牌：
                    //    agent 转发的通道没有别的期限，服务没起时远端的 ssh / git 一直挂到 shell 关掉。
                    await pipe.ConnectAsync((int)PipeConnectTimeout.TotalMilliseconds, cancellationToken)
                        .ConfigureAwait(false);
                    EnsureTrustedPipeServer(pipe, endpoint);
                    return pipe;
                }
                catch (Exception)
                {
                    // 连不上、超时或属主不对：句柄当场关掉，不留给终结器。
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            // Unix 套接字。
            //
            // ⚠️ Windows 上 SSH_AUTH_SOCK 常常指向 msys / WSL 的套接字，
            //    那是另一套东西，.NET 连不上去 —— 所以上面的命名管道分支在前。
            Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception)
            {
                socket.Dispose();
                throw;
            }
        }
        catch (TimeoutException ex)
        {
            throw new SshAgentException(SshFailureReason.AgentNotRunning,
                $"ssh-agent 的命名管道（{endpoint}）在 {PipeConnectTimeout.TotalSeconds:0} 秒内没有出现 —— agent 服务多半没在跑。" +
                "用「Get-Service ssh-agent」看状态，「Start-Service ssh-agent」起它；用 PuTTY 的话开着 Pageant 也行。", ex)
            {
                FailureKind = SshAgentFailureKind.NotRunning,
                Endpoint = endpoint,
            };
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.ConnectionRefused)
        {
            // 前者是套接字文件不在（ENOENT），后者是文件还在、没人监听 —— agent 退出时没删掉它。
            throw new SshAgentException(SshFailureReason.AgentNotRunning,
                $"ssh-agent 的套接字（{endpoint}）不存在或没人监听 —— agent 多半已经退出：{ex.SocketErrorCode}。", ex)
            {
                FailureKind = SshAgentFailureKind.NotRunning,
                Endpoint = endpoint,
            };
        }
        catch (Exception ex) when (ex is not SshAgentException and not OperationCanceledException)
        {
            throw new SshAgentException(SshFailureReason.AgentUnavailable, $"连不上 ssh-agent（{endpoint}）：{ex.Message}", ex)
            {
                FailureKind = SshAgentFailureKind.ConnectFailed,
                Endpoint = endpoint,
            };
        }
    }

    /// <summary>确认命名管道的另一头是可信的 agent，而不是抢先占了这个管道名的别的用户。</summary>
    /// <remarks>
    /// <para>
    /// Windows 上 OpenSSH agent 的管道名是固定的（<c>openssh-ssh-agent</c>）。服务没在跑的时候，
    /// 本机任何一个用户都能先把这个名字建出来 —— 之后我们发过去的就是签名请求，
    /// 开了「自动加载密钥到 Agent」的话，还有**明文私钥**。
    /// </para>
    /// <para>
    /// 判据是管道对象的属主：抢先建管道的人只能把属主设成自己（或自己所在、可以当属主的组）。
    /// 可信的只有三种 —— 当前用户（1Password、Pageant、KeePassXC 之类以当前用户身份跑的 agent）、
    /// SYSTEM 与 Administrators（OpenSSH 的 agent 服务；提权的进程建的管道属主也是 Administrators）。
    /// </para>
    /// <para>
    /// 不靠降低模拟级别（<c>TokenImpersonationLevel.Identification</c>）来防：OpenSSH 的 agent 服务
    /// 要以连进来的用户身份保存密钥，降级会把正常的 agent 一起弄坏。
    /// </para>
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EnsureTrustedPipeServer(NamedPipeClientStream pipe, string endpoint)
    {
        System.Security.Principal.SecurityIdentifier? owner;
        try
        {
            owner = pipe.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier))
                as System.Security.Principal.SecurityIdentifier;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                       or System.Security.Principal.IdentityNotMappedException)
        {
            throw new SshAgentException(SshFailureReason.AgentUnavailable,
                $"读不到 ssh-agent 命名管道（{endpoint}）的属主，不能确认另一头是可信的 agent：{ex.Message}", ex)
            {
                FailureKind = SshAgentFailureKind.PipeOwnerUnreadable,
                Endpoint = endpoint,
            };
        }

        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (!IsTrustedPipeOwner(owner, current.User))
        {
            throw new SshAgentException(SshFailureReason.AgentUnavailable,
                $"ssh-agent 命名管道（{endpoint}）的属主是 {owner?.Value ?? "（空）"}，" +
                "既不是当前用户也不是系统 —— 可能是别的用户抢先占了这个管道名。已拒绝连接。")
            {
                FailureKind = SshAgentFailureKind.PipeOwnerUntrusted,
                Endpoint = endpoint,
                PipeOwner = owner?.Value,
            };
        }
    }

    /// <summary>管道属主可信吗（见 <see cref="EnsureTrustedPipeServer"/>）。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool IsTrustedPipeOwner(
        System.Security.Principal.SecurityIdentifier? owner, System.Security.Principal.SecurityIdentifier? currentUser) =>
        owner is not null
        && (owner == currentUser
            || owner.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid)
            || owner.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid));

    /// <summary>列出 agent 里的密钥。</summary>
    public async ValueTask<IReadOnlyList<SshAgentIdentity>> ListIdentitiesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] response = await ExchangeAsync(
            new byte[] { SshAgentMessage.RequestIdentities }, cancellationToken).ConfigureAwait(false);

        try
        {
            return ParseIdentities(response);
        }
        catch (SshWireFormatException ex)
        {
            throw Malformed("身份列表", ex);
        }
    }

    /// <summary>agent 的应答格式不对：曾经让解析层的 internal 异常原样漏出去，调用方只接 <see cref="SshAgentException"/>。</summary>
    private static SshAgentException Malformed(string what, SshWireFormatException inner) =>
        new(SshFailureReason.ProtocolError, $"agent 回的{what}格式不对：{inner.Message}", inner)
        {
            FailureKind = SshAgentFailureKind.MalformedReply,
        };

    private static List<SshAgentIdentity> ParseIdentities(byte[] response)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(response));
        byte type = reader.ReadByte();

        if (type != SshAgentMessage.IdentitiesAnswer)
        {
            throw new SshAgentException(SshFailureReason.ProtocolError, $"agent 回了 {type} 而不是身份列表。")
            {
                FailureKind = SshAgentFailureKind.MalformedReply,
            };
        }

        uint count = reader.ReadUInt32();
        List<SshAgentIdentity> identities = [];

        for (uint i = 0; i < count && i < 1024; i++)
        {
            byte[] blob = reader.ReadStringAsArray(MaxMessageLength);
            string comment = reader.ReadUtf8String(MaxMessageLength);

            try
            {
                identities.Add(new SshAgentIdentity(SshPublicKey.Decode(blob), comment));
            }
            catch (SshPublicKeyException)
            {
                // agent 里可能有我们不认识类型的密钥（FIDO、DSA、厂商私有，或者本库不支持的证书类型）。
                // **跳过它就好** —— 为其中一把报错等于让整个 agent 用不了。
                //
                // ⚠️ 要接的是 SshPublicKeyException：Parse 把「不支持的类型」与
                //    「blob 格式非法」都包成它抛出来，不会让 SshWireFormatException 漏到这里。
                //    曾经只接了后者，结果 agent 里只要有一张证书，整个列表就抛异常 ——
                //    agent 认证、agent 转发、自动加钥一起用不了。
            }
        }

        return identities;
    }

    /// <summary>让 agent 用某把密钥签一段数据。</summary>
    /// <param name="publicKeyBlob">要用哪把钥（公钥 blob）。</param>
    /// <param name="data">被签名的数据。</param>
    /// <param name="algorithm">签名算法名；RSA 时决定用 SHA-256 还是 SHA-512。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>SSH 格式的签名 blob。</returns>
    public async ValueTask<byte[]> SignAsync(
        ReadOnlyMemory<byte> publicKeyBlob,
        ReadOnlyMemory<byte> data,
        string algorithm,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 证书的算法名带 -cert-v01 后缀，标志位按去掉后缀的名字定 —— 否则 RSA 证书会被签成 SHA-1。
        SshAgentSignFlags flags = SshPublicKey.StripCertificateSuffix(algorithm) switch
        {
            SshAlgorithmNames.RsaSha512 => SshAgentSignFlags.RsaSha2_512,
            SshAlgorithmNames.RsaSha256 => SshAgentSignFlags.RsaSha2_256,

            // 其它类型（ed25519、ecdsa）没有可选项，标志位留空。
            _ => SshAgentSignFlags.None,
        };

        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        writer.WriteByte(SshAgentMessage.SignRequest);
        writer.WriteString(publicKeyBlob.Span);
        writer.WriteString(data.Span);
        writer.WriteUInt32((uint)flags);

        byte[] response = await ExchangeAsync(request.WrittenMemory, cancellationToken).ConfigureAwait(false);

        byte[] signature;
        string signedWith;
        try
        {
            signature = ParseSignature(response);
            signedWith = new SshDataReader(new ReadOnlySequence<byte>(signature)).ReadUtf8String(MaxMessageLength);
        }
        catch (SshWireFormatException ex)
        {
            throw Malformed("签名", ex);
        }

        // 〔velashell-docs/zh/ssh/spec/04 §4.3〕签名里的算法名要与请求的一致。老版本的 agent（不认 SHA-2 标志位的 Pageant 之类）
        // 会照旧回一个 ssh-rsa（SHA-1）签名：交出去的话，认证器把它当 rsa-sha2-512 发给服务端，用户只看到 Permission denied；
        // AllowSha1RsaSignatures = false 的意图也被悄悄绕过了。证书的签名算法名不带证书后缀。
        string requested = SshPublicKey.StripCertificateSuffix(algorithm);
        if (!string.Equals(signedWith, requested, StringComparison.Ordinal))
        {
            throw new SshAgentException(SshFailureReason.Unsupported,
                $"ssh-agent 用 {PeerText.Sanitize(signedWith, 64)} 签了名，而请求的是 {requested} —— " +
                "这个 agent 多半太老，不认 SHA-2 的签名标志位。升级 agent，或者改用私钥文件。")
            {
                FailureKind = SshAgentFailureKind.SignatureAlgorithmMismatch,
                RequestedAlgorithm = requested,
                SignedAlgorithm = signedWith,
            };
        }
        return signature;
    }

    private static byte[] ParseSignature(byte[] response)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(response));
        byte type = reader.ReadByte();

        if (type == SshAgentMessage.Failure)
        {
            // agent 拒签的原因它不会告诉我们 —— 但最常见的几种值得点出来。
            throw new SshAgentException(SshFailureReason.AgentRefused,
                "ssh-agent 拒绝签名。常见原因：这把密钥已经不在 agent 里了，" +
                "agent 配了确认（ssh-add -c）而使用者没有批准，" +
                "或者这把钥带目的地约束（ssh-add -h），不许用在这台主机、这个用户或这条转发路径上。")
            {
                FailureKind = SshAgentFailureKind.SignRefused,
            };
        }

        if (type != SshAgentMessage.SignResponse)
        {
            throw new SshAgentException(SshFailureReason.ProtocolError, $"agent 回了 {type} 而不是签名。")
            {
                FailureKind = SshAgentFailureKind.MalformedReply,
            };
        }

        return reader.ReadStringAsArray(MaxMessageLength);
    }

    /// <summary>把一把进程内私钥加进 agent（<c>ssh-add</c>）。</summary>
    /// <param name="key">要加的私钥。只接受进程内私钥 —— 背后是 agent / 硬件的签名器手里根本没有私钥。</param>
    /// <param name="comment">注释，<c>ssh-add -l</c> 显示的那一列，通常写私钥文件路径。</param>
    /// <param name="constraints">约束；<see langword="null"/> 或全空表示不带约束。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshAgentException">agent 拒绝了，或者通信失败。</exception>
    /// <remarks>
    /// <para>
    /// 〔决策 velashell-docs/zh/ssh/spec/07 §7.3〕<b>库从不自动调用它。</b>
    /// 往使用者的 agent 里放东西是使用者的决定。
    /// </para>
    /// <para>
    /// 不查重：同一把钥加两次由 agent 处理（OpenSSH 会更新注释与约束）。
    /// 加进去的钥活多久由 agent 决定 —— Windows 的 OpenSSH agent 会把它存进注册表，重启后仍在。
    /// </para>
    /// </remarks>
    public ValueTask AddIdentityAsync(
        InMemorySshSigner key,
        string comment,
        SshAgentKeyConstraints? constraints = null,
        CancellationToken cancellationToken = default) =>
        AddIdentityCoreAsync(key, certificate: null, comment, constraints, cancellationToken);

    /// <summary>把「证书 + 它的私钥」一起加进 agent（<c>ssh-add</c> 遇到同名 <c>-cert.pub</c> 时做的事）。</summary>
    /// <param name="key">私钥。</param>
    /// <param name="certificate">这把私钥的证书（<c>*-cert-v01@openssh.com</c>）。</param>
    /// <param name="comment">注释。</param>
    /// <param name="constraints">约束；<see langword="null"/> 或全空表示不带约束。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentException"><paramref name="certificate"/> 不是证书，或者证的不是这把私钥。</exception>
    /// <exception cref="SshAgentException">agent 拒绝了，或者通信失败。</exception>
    /// <remarks>
    /// 加进去之后 agent 的身份列表里出现的是证书（<see cref="ListIdentitiesAsync"/> 列出 <c>*-cert-v01</c> 类型），
    /// 用它认证时出示整张证书、由 agent 签名 —— 证书登录不必再带着私钥文件。
    /// </remarks>
    public ValueTask AddIdentityAsync(
        InMemorySshSigner key,
        SshPublicKey certificate,
        string comment,
        SshAgentKeyConstraints? constraints = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(certificate);
        if (certificate.Certificate is not { } cert)
        {
            throw new ArgumentException($"{certificate.KeyType} 不是证书。", nameof(certificate));
        }
        if (!cert.Key.Blob.Span.SequenceEqual(key.PublicKey.Blob.Span))
        {
            throw new ArgumentException(
                $"证书证的是 {cert.Key.Sha256Fingerprint}，而私钥是 {key.PublicKey.Sha256Fingerprint} —— 不是同一把。", nameof(certificate));
        }
        return AddIdentityCoreAsync(key, certificate, comment, constraints, cancellationToken);
    }

    /// <summary>把一个证书签名器（证书 + 进程内私钥）加进 agent。</summary>
    /// <param name="certificate">证书签名器；证书与私钥是不是一对，造它时已经核对过。</param>
    /// <param name="comment">注释。</param>
    /// <param name="constraints">约束；<see langword="null"/> 或全空表示不带约束。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentException">证书背后的私钥不在进程里（agent / 硬件里的钥加不了）。</exception>
    /// <exception cref="SshAgentException">agent 拒绝了，或者通信失败。</exception>
    public ValueTask AddIdentityAsync(
        SshCertificateSigner certificate,
        string comment,
        SshAgentKeyConstraints? constraints = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.Inner is InMemorySshSigner key
            ? AddIdentityCoreAsync(key, certificate.PublicKey, comment, constraints, cancellationToken)
            : throw new ArgumentException("证书背后的私钥不在进程里（在 agent 或硬件里），没有私钥可加。", nameof(certificate));
    }

    private async ValueTask AddIdentityCoreAsync(
        InMemorySshSigner key,
        SshPublicKey? certificate,
        string comment,
        SshAgentKeyConstraints? constraints,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(comment);

        bool constrained = constraints is { IsEmpty: false };

        // 报文里是明文私钥。按上限一次性预留，免得扩容在堆上留下没清零的旧副本；
        // 用完整块清零（spec/07 §7.3 决策 3）。
        byte[] buffer = new byte[MaxMessageLength];
        try
        {
            FixedBufferWriter output = new(buffer);
            SshDataWriter writer = new(output);

            // 没有约束就发 17：有的 agent 认 17 却不认 25（决策 2）。反过来有约束一定发 25 ——
            // agent 不读 17 末尾多出来的字节，约束接在 17 后面会被静默丢掉（spec/07 §7.3.2 的 ⚠️）。
            writer.WriteByte(constrained ? SshAgentMessage.AddIdentityConstrained : SshAgentMessage.AddIdentity);
            key.WriteAgentPrivateKey(output, certificate);
            writer.WriteUtf8String(comment);

            if (constrained)
            {
                SshAgentConstraintWriter.Write(writer, constraints!);
            }

            byte[] response = await ExchangeAsync(
                buffer.AsMemory(0, output.WrittenCount), cancellationToken).ConfigureAwait(false);

            if (response[0] == SshAgentMessage.Success)
            {
                return;
            }

            // 〔spec/07 §7.3.2〕EXTENSION_FAILURE 也按拒绝处理：加钥本该只回 SUCCESS / FAILURE，别的 agent 若回扩展失败，那也是拒绝。
            // 被拒就是被拒 —— 绝不去掉约束重试，那会把「失败也是安全的」这层保险拆掉。
            if (response[0] is SshAgentMessage.Failure or SshAgentMessage.ExtensionFailure)
            {
                // agent 不说原因 —— 点出最常见的几种。
                throw new SshAgentException(SshFailureReason.AgentRefused,
                    "ssh-agent 拒绝加入这把密钥。常见原因：agent 不支持约束（有效期 / 逐次确认）、" +
                    "agent 已被锁定（ssh-add -x），或者 agent 不支持这种密钥类型。" +
                    (constraints?.AllowedHops is not null
                        ? "这次还带着目的地约束：agent 可能不支持它（restrict-destination-v00@openssh.com，OpenSSH 8.9 起才有；" +
                          "非 OpenSSH 的 agent 多半不支持）。"
                        : ""))
                {
                    FailureKind = SshAgentFailureKind.AddRefused,
                    HasDestinationConstraints = constraints?.AllowedHops is not null,
                };
            }

            throw new SshAgentException(SshFailureReason.ProtocolError, $"agent 回了 {response[0]} 而不是成功 / 失败。")
            {
                FailureKind = SshAgentFailureKind.MalformedReply,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>从 agent 里删掉一把钥（<c>ssh-add -d</c>）。</summary>
    /// <param name="key">要删的钥（证书就删那张证书的身份）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删掉了为 <see langword="true"/>；agent 回 FAILURE（没有这把钥、agent 已锁）为 <see langword="false"/>。</returns>
    /// <exception cref="SshAgentException">通信失败，或者应答不是成功 / 失败。</exception>
    public async ValueTask<bool> RemoveIdentityAsync(SshPublicKey key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);

        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        writer.WriteByte(SshAgentMessage.RemoveIdentity);
        writer.WriteString(key.Blob.Span);
        return await SucceededAsync(request.WrittenMemory, "删钥", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>清空 agent（<c>ssh-add -D</c>）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshAgentException">agent 拒绝了（多半是锁着），或者通信失败。</exception>
    public async ValueTask RemoveAllIdentitiesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await SucceededAsync(new[] { SshAgentMessage.RemoveAllIdentities }, "清空", cancellationToken).ConfigureAwait(false))
        {
            throw new SshAgentException(SshFailureReason.AgentRefused, "ssh-agent 拒绝清空。常见原因：agent 已被锁定（ssh-add -x）。")
            {
                FailureKind = SshAgentFailureKind.RemoveAllRefused,
            };
        }
    }

    /// <summary>用口令锁住 agent（<c>ssh-add -x</c>）：锁着时 agent 不列钥、不签名、不加钥，直到用同一个口令解锁。</summary>
    /// <param name="passphrase">口令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>锁上了为 <see langword="true"/>；agent 回 FAILURE（已经锁着）为 <see langword="false"/>。</returns>
    /// <exception cref="SshAgentException">通信失败，或者应答不是成功 / 失败。</exception>
    /// <remarks>离开座位时用：转发出去的 agent 在锁着期间也签不了名。</remarks>
    public ValueTask<bool> LockAsync(string passphrase, CancellationToken cancellationToken = default) =>
        LockOrUnlockAsync(SshAgentMessage.Lock, passphrase, "加锁", cancellationToken);

    /// <summary>用加锁时的口令解锁 agent（<c>ssh-add -X</c>）。</summary>
    /// <param name="passphrase">口令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解开了为 <see langword="true"/>；口令不对或者本来没锁为 <see langword="false"/>（agent 不区分这两种）。</returns>
    /// <exception cref="SshAgentException">通信失败，或者应答不是成功 / 失败。</exception>
    public ValueTask<bool> UnlockAsync(string passphrase, CancellationToken cancellationToken = default) =>
        LockOrUnlockAsync(SshAgentMessage.Unlock, passphrase, "解锁", cancellationToken);

    private async ValueTask<bool> LockOrUnlockAsync(byte message, string passphrase, string what, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(passphrase);

        // 报文里是明文口令：用完清零。
        byte[] buffer = new byte[1 + 4 + System.Text.Encoding.UTF8.GetMaxByteCount(passphrase.Length)];
        try
        {
            FixedBufferWriter output = new(buffer);
            SshDataWriter writer = new(output);
            writer.WriteByte(message);
            writer.WriteUtf8String(passphrase);
            return await SucceededAsync(buffer.AsMemory(0, output.WrittenCount), what, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>发一条只回 SUCCESS / FAILURE 的请求。</summary>
    private async ValueTask<bool> SucceededAsync(ReadOnlyMemory<byte> request, string what, CancellationToken cancellationToken)
    {
        byte[] response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        return response[0] switch
        {
            SshAgentMessage.Success => true,
            SshAgentMessage.Failure => false,
            _ => throw new SshAgentException(SshFailureReason.ProtocolError, $"{what}：agent 回了 {response[0]} 而不是成功 / 失败。")
            {
                FailureKind = SshAgentFailureKind.MalformedReply,
            },
        };
    }

    /// <summary>把 agent 里的一把密钥包成签名器。</summary>
    public ISshSigner CreateSigner(SshAgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new AgentSigner(this, identity);
    }

    /// <summary>把 agent 里的密钥全部包成公钥凭据。</summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/04 §2.2〕库<b>不会自动</b>去连 agent ——
    /// 这个方法要由使用者显式调用，结果也要由使用者显式加进凭据列表。
    /// </remarks>
    public async ValueTask<IReadOnlyList<SshCredential>> GetCredentialsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SshAgentIdentity> identities =
            await ListIdentitiesAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. identities.Select(id =>
                new PublicKeyCredential(CreateSigner(id), $"agent: {id.Comment}")),
        ];
    }

    // ------------------------------------------------------------ 会话声明

    /// <summary>
    /// 向 agent 声明这条 agent 连接属于哪个 SSH 会话、拿来做什么（<c>session-bind@openssh.com</c>）。
    /// </summary>
    /// <param name="proof">首次密钥交换里服务端的身份证明。</param>
    /// <param name="purpose">认证还是转发 —— 决定 <c>is_forwarding</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>agent 接受了声明时为 <see langword="true"/>；agent 不支持或拒绝时为 <see langword="false"/>。</returns>
    /// <remarks>
    /// <para>
    /// 〔spec/07 §7.4〕agent 靠它执行 <c>ssh-add -h</c> 给钥加的目的地约束。不声明的连接在 agent 看来就是
    /// 「本机自己在用」—— 经我们转发出去之后，远端拿受约束的钥也能当成源头机器来用，约束形同虚设。
    /// </para>
    /// <para>
    /// <b>声明失败不是错误</b>：agent 回 FAILURE（不认识这个扩展、或拒绝这次声明）时照常列钥、签名，
    /// 只是约束不生效 —— 与 OpenSSH 的客户端一致。同一个会话只发一次，重复调用交回第一次的结果。
    /// </para>
    /// <para>
    /// ⚠️ <b>agent 因为这条声明断开连接时</b>（个别实现收到不认识的报文就断），由 <see cref="ConnectAsync"/>
    /// 连上的客户端会重开连接、不再声明，交回 <see langword="false"/>：后面的列钥与签名不能跟着一起失败。
    /// <see cref="FromStream"/> 交来的流不知道怎么重开，那时照常抛 <see cref="SshAgentException"/>，由调用方重连。
    /// </para>
    /// </remarks>
    internal async ValueTask<bool> DeclareSessionAsync(
        SshSessionProof proof, SshAgentConnectionPurpose purpose, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(proof);

        if (_declaredSessionId is { } declared && declared.AsSpan().SequenceEqual(proof.SessionId))
        {
            return _declarationAccepted;
        }

        // 〔spec/07 §7.3.2〕一条 agent 连接只替一个会话做认证：agent 不接受在已经为认证声明过的连接上再声明另一个会话，
        // 后一个会话的声明被拒、连接上记着的仍是前一个，受目的地约束的钥在后一个会话上拒签 —— 按 ssh_config 的 ProxyJump 连时，
        // 跳板与目标用的正是同一份 agent 凭据。上一次被接受的认证声明属于别的会话时，先在同一个端点重开一条连接再声明。
        // 上一次没被接受（agent 不认声明）时不重开：重开了也一样。FromStream 交来的流重开不了，照旧在原连接上声明。
        if (purpose == SshAgentConnectionPurpose.Authentication && _canReopen
            && _declaredSessionId is not null && _declarationAccepted
            && _declaredPurpose == SshAgentConnectionPurpose.Authentication)
        {
            await ReopenAsync(cancellationToken).ConfigureAwait(false);
        }

        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        writer.WriteByte(SshAgentMessage.Extension);
        writer.WriteUtf8String(SshAgentMessage.SessionBindExtension);
        writer.WriteString(proof.HostKeyBlob);
        writer.WriteString(proof.SessionId);
        writer.WriteString(proof.Signature);
        writer.WriteBoolean(purpose == SshAgentConnectionPurpose.Forwarding);

        bool accepted;
        try
        {
            byte[] response = await ExchangeAsync(request.WrittenMemory, cancellationToken).ConfigureAwait(false);

            // 只有 SUCCESS 算接受。FAILURE（5）、EXTENSION_FAILURE（28）以及任何别的回答都按「不支持」处理。
            accepted = response[0] == SshAgentMessage.Success;
        }
        catch (SshAgentException ex) when (ex.Reason == SshFailureReason.AgentUnavailable && _canReopen)
        {
            await ReopenAsync(cancellationToken).ConfigureAwait(false);
            accepted = false;
        }

        _declaredSessionId = proof.SessionId;
        _declarationAccepted = accepted;
        _declaredPurpose = purpose;
        return accepted;
    }

    /// <summary>把远端那一跳经转发发来的会话声明原样交给 agent，交回 agent 的应答。</summary>
    /// <param name="request">一条完整的 agent 报文（不含长度前缀），必须是 <c>session-bind@openssh.com</c> 扩展。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 〔spec/07 §7.4〕转发链上的每一跳都要把自己的声明接在我们的声明后面，agent 才认得出整条路径。
    /// 这里只放行这一个扩展：声明只会让 agent 对这条连接<b>更严</b>，而别的扩展能做什么我们说不准。
    /// </remarks>
    internal ValueTask<byte[]> RelaySessionDeclarationAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsSessionDeclaration(request.Span))
        {
            throw new ArgumentException("只转交 session-bind@openssh.com 扩展。", nameof(request));
        }

        return ExchangeAsync(request, cancellationToken);
    }

    /// <summary>这条 agent 报文是不是一条会话声明（<c>SSH_AGENTC_EXTENSION</c> + <c>session-bind@openssh.com</c>）。</summary>
    internal static bool IsSessionDeclaration(ReadOnlySpan<byte> request)
    {
        if (request.Length < 5 || request[0] != SshAgentMessage.Extension)
        {
            return false;
        }

        uint nameLength = BinaryPrimitives.ReadUInt32BigEndian(request[1..]);
        ReadOnlySpan<byte> expected = SshAgentMessage.SessionBindExtensionUtf8;
        return nameLength == expected.Length
            && request.Length >= 5 + expected.Length
            && request.Slice(5, expected.Length).SequenceEqual(expected);
    }

    /// <summary>
    /// 在同一个端点重开一条 agent 连接、换下旧的（只有 <see cref="ConnectAsync"/> 连上的才走到这里）：
    /// 声明把连接弄断之后；或者要为另一个会话做认证声明时（spec/07 §7.3.2）。
    /// </summary>
    private async ValueTask ReopenAsync(CancellationToken cancellationToken)
    {
        Stream fresh = await OpenStreamAsync(Endpoint, cancellationToken).ConfigureAwait(false);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Stream broken;
        try
        {
            broken = _stream;
            _stream = fresh;
            _outOfStep = false;   // 新连接上的问答从头开始
        }
        finally
        {
            _lock.Release();
        }

        try
        {
            await broken.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 它本来就断了。
        }
    }

    // ------------------------------------------------------------ 收发

    private async ValueTask<byte[]> ExchangeAsync(
        ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        // agent 协议是严格的一问一答，没有 id —— 所以并发调用必须串起来。
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool answered = false;
        try
        {
            // 〔AU-E3〕上一次问答做到一半被打断（取消、读写出错、长度不合理）：请求可能只写了一半，
            // 或者应答还在路上 —— 没有 id，下一问读到的会是上一问的答案。曾经照常接着用，
            // 那个答案被当成这一问的；现在这条连接作废，照实失败。由 ConnectAsync 连上的客户端在
            // 会话声明那条路上会重开连接（见 ReopenAsync），其余情形由调用方重连。
            if (_outOfStep)
            {
                throw new SshAgentException(SshFailureReason.AgentUnavailable,
                    "与 ssh-agent 的上一次问答中途被打断（取消或出错），这条连接上的问答已经错位，不能再用 —— 请重新连接 agent。")
                {
                    FailureKind = SshAgentFailureKind.ConversationOutOfStep,
                    Endpoint = Endpoint,
                };
            }

            byte[] header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)request.Length);

            await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            await _stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);

            if (length is 0 or > MaxMessageLength)
            {
                throw new SshAgentException(SshFailureReason.ProtocolError, $"agent 报文长度不合理：{length}。")
                {
                    FailureKind = SshAgentFailureKind.MalformedReply,
                };
            }

            byte[] response = new byte[length];
            await _stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
            answered = true;
            return response;
        }
        catch (EndOfStreamException ex)
        {
            throw new SshAgentException(SshFailureReason.AgentUnavailable, "ssh-agent 在应答之前就断开了。", ex)
            {
                FailureKind = SshAgentFailureKind.ClosedBeforeReply,
                Endpoint = Endpoint,
            };
        }
        catch (IOException ex)
        {
            throw new SshAgentException(SshFailureReason.AgentUnavailable, $"与 ssh-agent 通信失败：{ex.Message}", ex)
            {
                FailureKind = SshAgentFailureKind.CommunicationFailed,
                Endpoint = Endpoint,
            };
        }
        finally
        {
            if (!answered && !_outOfStep)
            {
                _outOfStep = true;
                try
                {
                    await _stream.DisposeAsync().ConfigureAwait(false);   // 不再用了：连接还给 agent
                }
                catch (Exception)
                {
                    // 它可能本来就断了。
                }
            }
            _lock.Release();
        }
    }

    /// <summary>一把在 agent 里的密钥。</summary>
    /// <remarks>
    /// <see cref="ISshSigner.IsLocalAndCheap"/> 为 <see langword="false"/>：
    /// agent 可能配了「每次签名都要确认」（<c>ssh-add -c</c>），
    /// 那会弹窗。为一把服务端根本不认的密钥去打扰使用者是不可接受的，
    /// 所以公钥认证会先探测再签。
    /// </remarks>
    private sealed class AgentSigner(SshAgentClient client, SshAgentIdentity identity) : ISshSigner, ISessionAwareSigner
    {
        public SshPublicKey PublicKey => identity.PublicKey;

        public IReadOnlyList<string> SignatureAlgorithms => identity.PublicKey.SignatureAlgorithms;

        public bool IsLocalAndCheap => false;

        public ValueTask<byte[]> SignAsync(
            ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default) =>
            client.SignAsync(identity.PublicKey.Blob, data, algorithm, cancellationToken);

        /// <remarks>同一个 agent 客户端上的几把钥共用一次声明（见 <see cref="DeclareSessionAsync"/>）。</remarks>
        public async ValueTask PrepareForSessionAsync(SshSessionProof proof, CancellationToken cancellationToken) =>
            await client.DeclareSessionAsync(proof, SshAgentConnectionPurpose.Authentication, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>写进一块固定缓冲、写满即抛的写入器。</summary>
    /// <remarks>
    /// 加钥报文装的是明文私钥：<see cref="ArrayBufferWriter{T}"/> 扩容时会把旧数组
    /// 原样丢给 GC，那里面的私钥没人清零。这里宁可报错也不扩容。
    /// </remarks>
    private sealed class FixedBufferWriter(byte[] buffer) : IBufferWriter<byte>
    {
        public int WrittenCount { get; private set; }

        public void Advance(int count) => WrittenCount += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => buffer.AsMemory(Reserve(sizeHint));

        public Span<byte> GetSpan(int sizeHint = 0) => buffer.AsSpan(Reserve(sizeHint));

        private int Reserve(int sizeHint)
        {
            if (buffer.Length - WrittenCount < Math.Max(sizeHint, 1))
            {
                throw new SshAgentException(SshFailureReason.LimitExceeded, "要加入 agent 的密钥或目的地约束太大，超出了 agent 报文的长度上限（256 KiB）。")
                {
                    FailureKind = SshAgentFailureKind.RequestTooLarge,
                };
            }
            return WrittenCount;
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

        try
        {
            if (_ownsStream)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 释放路径不抛。
        }

        _lock.Dispose();
    }
}
