using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// VelaShell.Ssh 与 Core 中立类型之间的异常翻译。库类型只在 Infrastructure 内流动;
/// 更换底层库时替换本文件的对应映射。
/// </summary>
/// <remarks>
/// <para>
/// 与它替掉的 <c>TmdsSshInterop</c> 相比,这里**没有一处字符串解析**。
/// 旧实现只能靠 <c>"The connection could not be established - {reason} - ..."</c>
/// 这个消息前缀去切原因 —— 因为上游把 <c>ConnectFailedException</c> 声明成了
/// <c>internal</c>,原因码根本拿不到。那段切字符串的代码是**上游一次措辞改动就会静默失效**的,
/// 而失效的表现是「认证失败」被当成「连接失败」,重试与提示全部走错分支。
/// </para>
/// <para>
/// 现在原因是强类型的 <see cref="SshFailureReason" /> + <see cref="SshPhase" />,
/// 按类型和枚举匹配即可。
/// </para>
/// </remarks>
internal static partial class SshInterop
{
    /// <summary>
    /// VelaShell.Ssh 异常 → Core 中立异常;不认识的类型返回 <see langword="null" />
    /// (调用方原样上抛)。
    /// </summary>
    /// <remarks>
    /// 若异常是取消且 <paramref name="callerToken" /> 已请求取消,同样返回
    /// <see langword="null" />:调用方主动取消必须保留 <see cref="OperationCanceledException" />
    /// 语义,只有库内部超时(调用方未取消)才翻译成
    /// <see cref="VelaSshOperationTimeoutException" />。
    /// </remarks>
    public static Exception? Translate(Exception ex, CancellationToken callerToken = default)
    {
        if (ex is OperationCanceledException && callerToken.IsCancellationRequested)
        {
            return null;
        }

        return ex switch
        {
            // 派生类型放在基类型前面,否则后面的分支永远到不了。
            SshAuthenticationException auth => new VelaSshAuthenticationException(Describe(auth), auth),
            SshPrivateKeyException key => TranslatePrivateKey(key),
            SshCertificateException cert => new VelaSshAuthenticationException(Localize(cert), cert),
            // 内层是 SFTP 状态码 = 服务端拒写(磁盘满、配额、权限,规格 08 §二);没有内层或内层是连接错误 = 断线。
            // 传输失败重试只重试后一种。
            SftpTransferInterruptedException sftp => new VelaSftpTransferInterruptedException(LocalizeInterrupted(sftp), sftp.DurableLength, sftp)
            {
                RefusedByServer = sftp.InnerException is SftpException,
            },
            SftpUnavailableException unavailable => new VelaSshClientException(LocalizeUnavailable(unavailable), unavailable),
            SftpException sftp => TranslateSftp(sftp),
            SshNegotiationException negotiation => new VelaSshConnectionException(Describe(negotiation), negotiation),
            SshChannelException channel => new VelaSshClientException(Localize(channel), channel),
            SshForwardException forward => new VelaSshClientException(Localize(forward), forward),
            SshConnectionClosedException closed => new VelaSshConnectionException(Localize(closed), closed),
            SshConnectException connect => TranslateConnect(connect),
            SshProtocolException protocol => new VelaSshConnectionException(Localize(protocol), protocol),
            OperationCanceledException => new VelaSshOperationTimeoutException(ex.Message, ex),
            SshException other => new VelaSshClientException(Localize(other), other),
            _ => null,
        };
    }

    /// <summary>
    /// 建链阶段的失败按 <see cref="SshFailureReason" /> 分流。
    /// </summary>
    /// <remarks>
    /// 超时单独分出来,是因为主窗口的自动重连只该对这一类生效:
    /// 「连不上」值得再试一次,「认证不过」再试一百次也一样。
    /// 主机指纹被拒同理单独分出来:自动重连据此停下,等用户手动重连、在弹窗里核对。
    /// </remarks>
    private static VelaSshClientException TranslateConnect(SshConnectException ex) =>
        ex.Reason switch
        {
            SshFailureReason.Timeout or SshFailureReason.TcpTimeout or SshFailureReason.KeepAliveTimeout =>
                new VelaSshOperationTimeoutException(Localize(ex), ex),
            SshFailureReason.HostKeyRejected or SshFailureReason.HostKeyChanged =>
                new VelaSshHostKeyRejectedException(Localize(ex), ex),
            _ => new VelaSshConnectionException(Localize(ex), ex),
        };

    /// <summary>
    /// 按原因码给出界面语言的一句话,原文留在 <see cref="Exception.InnerException" /> 里。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 库的异常消息是写给开发者看的(而且是中文);上一版底层库给的是英文。直接透传的话,
    /// 英 / 日 / 韩界面的用户在换库之后会突然看到中文报错。
    /// </para>
    /// <para>
    /// 只翻有把握的那几类。<see cref="SshFailureReason.ProxyUnreachable" />、<see cref="SshFailureReason.ProxyRefused" />、
    /// <see cref="SshFailureReason.ProxyAuthRequired" /> 与 <see cref="SshFailureReason.ProxyAuthFailed" /> 不在其中:它们的消息是 <c>ProxyTransportDialer</c>
    /// 用界面语言拼的,带着「经哪个代理去哪」、换 SOCKS5 的提示,并且分得清「没配凭据」与「凭据不对」,
    /// 比一句泛泛的「代理拒绝」有用得多(后半句的代理应答按 <see cref="SshConnectException.FailureKind" /> 说)。
    /// <see cref="SshFailureReason.HostKeyRejected" /> 由种类分开:策略拒的(<see cref="SshConnectFailureKind.HostKeyPolicyRejected" />)消息是
    /// <c>VelaHostKeyPolicy</c> 用本地化文案写的拒绝理由(含新旧指纹),原样用;库自己验不过的几种按种类说。认不出的原因照旧用原文。
    /// </para>
    /// <para>
    /// 通道失败、私钥 / 证书读不出来、建连失败(主机密钥、代理握手、跳板)这几类,库给了比原因码更细的结构化出处
    /// (<see cref="SshChannelException.FailureKind" />、<see cref="SshKeyProblemKind" /> 与路径、算法名、Key ID、两边的公钥,
    /// <see cref="SshConnectException.FailureKind" /> 与经过的那一跳、代理的应答),先按它们说(<c>SshInterop.Diagnostics.cs</c>);
    /// 没归类的(使用者自己构造的异常)才落到下面按原因码的那一层。
    /// </para>
    /// <para>
    /// 转发器起不来(端口占用 <see cref="SshFailureReason.ForwardBindFailed" />、X11 转发起不来 <see cref="SshFailureReason.ForwardSetupFailed" />、
    /// 服务端拒绝监听……)按 <see cref="SshForwardException.FailureKind" /> 与监听端点、xauth 的退出码与输出说。
    /// 配置不成立(<see cref="SshFailureReason.InvalidConfiguration" />,ssh_config 那一类)仍不翻:库的消息里带着具体的主机名、端口号,
    /// 还没有结构化的出处,换成一句通用文案反而帮不上忙;代理握手里放不进请求的主机名、凭据超长已有种类,不在此列。
    /// <see cref="SshFailureReason.Unsupported" /> 除私钥 / 证书以外(消息里是不支持的算法、格式版本)同理。
    /// <see cref="SshFailureReason.HostKeyStoreFailed" /> 同理(消息里是 known_hosts 的路径与 IO 错误);
    /// 而且宿主的信任库不走库的 known_hosts,它只在调用方换用 <c>KnownHostsPolicy</c> 时才会出现。
    /// </para>
    /// <para>
    /// 尾巴上的 <c>[原因 @ 阶段]</c> 不翻译 —— 那是给提 issue 时贴日志用的,跨语言一致才好搜。
    /// </para>
    /// </remarks>
    internal static string Localize(SshException ex) => ex switch
    {
        SshChannelException { FailureKind: not SshChannelFailureKind.Unknown } channel => LocalizeChannel(channel),
        SshPrivateKeyException { ProblemKind: not SshKeyProblemKind.Unknown } key => LocalizeKeyProblem(key),
        SshCertificateException { ProblemKind: not SshKeyProblemKind.Unknown } certificate => LocalizeKeyProblem(certificate),
        SshConnectException { FailureKind: not SshConnectFailureKind.Unknown } connect => LocalizeConnect(connect),
        SshForwardException { FailureKind: not SshForwardFailureKind.Unknown } forward => LocalizeForward(forward),
        SshAgentException { FailureKind: not SshAgentFailureKind.Unknown } agent => LocalizeAgent(agent),
        // 重协商时密钥交换按首次交换的口径报的失败(验签失败、主机密钥类型不符),库把它包成「建好的连接断了」,原来那条在内层。
        SshConnectionClosedException
        {
            Phase: SshPhase.Rekeying,
            InnerException: SshConnectException { FailureKind: not SshConnectFailureKind.Unknown } rekey,
        } => $"{Strings.Format("SshErr_RekeyFailed", DescribeConnectFailure(rekey))} [{ex.Reason} @ {ex.Phase}]",
        // 会话因为一个意外的(不是 IO、帧格式、协议)异常判死:库把它包一层、原因码是 Unknown(说不清就报不知道),原异常在内层。
        // 内层的类型名与消息(按系统语言)就是全部线索;曾经透传「连接因意外错误中断:…」这句中文。
        SshConnectionClosedException { Reason: SshFailureReason.Unknown, InnerException: { } fault } =>
            $"{Strings.Format("SshErr_ConnectionFaulted", $"{fault.GetType().Name}: {fault.Message}")} [{ex.Reason} @ {ex.Phase}]",
        _ => LocalizeByReason(ex),
    };

    /// <summary>只按原因码给出界面语言的一句话(<see cref="Localize" /> 的兜底层);认不出的原因照用原文。</summary>
    private static string LocalizeByReason(SshException ex)
    {
        string? key = ex.Reason switch
        {
            SshFailureReason.DnsFailure => "SshErr_DnsFailure",
            SshFailureReason.TcpRefused => "SshErr_TcpRefused",
            SshFailureReason.TcpTimeout => "SshErr_TcpTimeout",
            SshFailureReason.TcpUnreachable => "SshErr_TcpUnreachable",
            SshFailureReason.NotAnSshServer => "SshErr_NotAnSshServer",
            SshFailureReason.VersionMismatch => "SshErr_VersionMismatch",
            // 建连时的「变了」由 VelaHostKeyPolicy 写好了本地化的拒绝理由(含新旧指纹),不能拿泛泛的一句盖掉;
            // 只有重协商途中换钥这种没有策略参与的情形才用通用文案。
            SshFailureReason.HostKeyChanged when ex.Phase == SshPhase.Rekeying => "SshErr_HostKeyChanged",
            SshFailureReason.Timeout => "SshErr_Timeout",
            SshFailureReason.KeepAliveTimeout => "SshErr_KeepAliveTimeout",
            SshFailureReason.ClosedByPeer => "SshErr_ClosedByPeer",
            SshFailureReason.Disconnected => "SshErr_Disconnected",
            SshFailureReason.KeyPassphraseRequired => "SshErr_KeyPassphraseRequired",
            SshFailureReason.KeyPassphraseIncorrect => "SshErr_KeyPassphraseIncorrect",
            SshFailureReason.AgentRefused => "SshErr_AgentRefused",
            // agent 没在跑:原因码足以说清下一步(把它起来)。库的消息里带着套接字路径 / 管道名,这里不需要。
            // AgentUnavailable 按库的种类说(管道属主不可信带着 SID、连不上带着 IO 错误,见 LocalizeAgent);到这一层的是没归类的。
            SshFailureReason.AgentNotRunning => "SshErr_AgentNotRunning",
            SshFailureReason.ChannelRequestRejected => "SshErr_ChannelRequestRejected",
            SshFailureReason.ForwardRejected => "SshErr_ForwardRejected",
            // 对端违反了协议:具体是哪一条(Terrapin 的报文位置、截断的名单……)对用户没有下一步可言,原文留在内层。
            SshFailureReason.ProtocolError => "SshErr_ProtocolError",
            SshFailureReason.LimitExceeded => "SshErr_LimitExceeded",
            SshFailureReason.Aborted => "SshErr_Aborted",
            _ => null,
        };

        return key is null ? ex.Message : $"{Strings.Get(key)} [{ex.Reason} @ {ex.Phase}]";
    }

    /// <summary>传输中断:界面语言的一句话带上续传点;服务端拒写(磁盘满、配额、权限)时再带上 SFTP 的那一句。</summary>
    /// <remarks>
    /// 中断的原因在 <see cref="SshException.Reason" /> 与内层异常上(库按实情报,见规格 08 §二):
    /// 内层是 SFTP 错误时它比「中断了」更有用 —— 「磁盘满」与「断线」的下一步不一样。
    /// </remarks>
    internal static string LocalizeInterrupted(SftpTransferInterruptedException ex)
    {
        string text = Strings.Format("SftpErr_TransferInterrupted", ex.DurableLength);
        if (ex.InnerException is SftpException sftp)
        {
            text = $"{text} {LocalizeSftp(sftp)}";
        }
        return $"{text} [{ex.Reason} @ {ex.Phase}]";
    }

    /// <summary>SFTP 子系统起不来:握手超时、「服务端没开 SFTP」与「sftp-server 没起来就退出了」分开说;别的原因(库内收尾)按原因码。</summary>
    /// <remarks>
    /// 版本不对、本端已经收工按库给的种类(<see cref="SftpUnavailableException.FailureKind" />)另说 —— 版本不对的原因码是
    /// <see cref="SshFailureReason.Unsupported" /> / <see cref="SshFailureReason.ProtocolError" />,曾经一律说成「服务端没开 SFTP」;
    /// 本端收工(关闭文件系统、连接释放)是 <see cref="SshFailureReason.Aborted" />,尾巴照实写 <c>[Aborted @ Open]</c>。
    /// </remarks>
    internal static string LocalizeUnavailable(SftpUnavailableException ex)
    {
        string? kindKey = ex.FailureKind switch
        {
            SftpUnavailableKind.VersionTooOld => "SftpErr_VersionTooOld",
            SftpUnavailableKind.VersionTooNew => "SftpErr_VersionTooNew",
            SftpUnavailableKind.SessionClosed => "SftpErr_SessionClosed",
            _ => null,
        };
        if (kindKey is not null)
        {
            string version = ex.ServerVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
            return $"{Strings.Format(kindKey, version)} [{ex.Reason} @ {ex.Phase}]";
        }

        if (ex.Reason == SshFailureReason.CommandFailed)
        {
            // 退出码与 sftp-server 在 stderr 上说的话都是结构化的(库已按对端文本清洗过),不解析消息句子。
            string exited = Strings.Format("SftpErr_ServerExited",
                ex.ServerExitStatus is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?");
            if (!string.IsNullOrWhiteSpace(ex.ServerErrorOutput))
            {
                exited = Strings.Format("SftpErr_ServerSaid", exited, ex.ServerErrorOutput);
            }
            return $"{exited} [{ex.Reason} @ {ex.Phase}]";
        }

        string? key = ex.Reason switch
        {
            SshFailureReason.Timeout => "SftpErr_HandshakeTimeout",
            SshFailureReason.Unsupported or SshFailureReason.ChannelRequestRejected => "SftpErr_Unavailable",
            _ => null,
        };
        return key is null ? Localize(ex) : $"{Strings.Get(key)} [{ex.Reason} @ {ex.Phase}]";
    }

    /// <summary>
    /// SFTP 的失败要分出「没这个文件」与「没权限」—— 上层据此决定是提示用户还是静默跳过。
    /// </summary>
    private static VelaSftpOperationException TranslateSftp(SftpException ex)
    {
        string message = LocalizeSftp(ex);

        return ex.StatusCode switch
        {
            SftpStatusCode.NoSuchFile or SftpStatusCode.NoSuchPath => new VelaSftpPathNotFoundException(message, ex),
            SftpStatusCode.PermissionDenied => new VelaSftpPermissionDeniedException(message, ex),
            _ => new VelaSftpOperationException(message, ex),
        };
    }

    /// <summary>SFTP 的失败按状态码换成界面语言,带上路径与服务端原话。</summary>
    /// <remarks>
    /// <para>
    /// <b>服务端原话(<see cref="SftpException.ServerMessage" />)必须带上。</b>
    /// SFTP v3 只有 9 个状态码,而码 4(Failure)承载了绝大多数真实错误 ——
    /// 「目录非空」「文件已存在」「磁盘满」「配额超限」全是同一个码,
    /// 服务端给的那段文本是唯一能区分它们的信息。
    /// </para>
    /// <para>
    /// ⚠️ 原话与路径都来自服务端,<b>拼进来之前先清一遍</b>(<see cref="PeerText.Sanitize" />)。
    /// 曾经是在库的消息(里面已经有清洗过的「服务端说:…」)后面再追加一遍原文:同一句话显示两遍,
    /// 第二遍绕过了清洗 —— 终端转义序列、双向控制符照样进了界面。
    /// </para>
    /// <para>不认识的状态码沿用库的消息(已经清洗过)。</para>
    /// </remarks>
    internal static string LocalizeSftp(SftpException ex)
    {
        string? key = ex.StatusCode switch
        {
            SftpStatusCode.NoSuchFile or SftpStatusCode.NoSuchPath => "SftpErr_NoSuchFile",
            SftpStatusCode.PermissionDenied => "SftpErr_PermissionDenied",
            SftpStatusCode.OperationUnsupported => "SftpErr_Unsupported",
            SftpStatusCode.BadMessage => "SftpErr_BadMessage",
            SftpStatusCode.NoConnection or SftpStatusCode.ConnectionLost => "SftpErr_ConnectionLost",
            SftpStatusCode.Failure => "SftpErr_Failure",
            _ => null,
        };

        if (key is null)
        {
            return ex.Message;
        }

        string text = Strings.Get(key);
        if (!string.IsNullOrEmpty(ex.Path))
        {
            text = Strings.Format("SftpErr_WithPath", text, PeerText.Sanitize(ex.Path));
        }
        if (!string.IsNullOrWhiteSpace(ex.ServerMessage))
        {
            text = Strings.Format("SftpErr_ServerSaid", text, PeerText.Sanitize(ex.ServerMessage));
        }
        if (ex.MissingExtensions.Count > 0)
        {
            text = WithMissingExtensions(text, ex);
        }
        return text;
    }

    /// <summary>
    /// 服务端缺的扩展(库在没宣告时自己报「不支持」,<see cref="SftpException.MissingExtensions" />)接在句子后面:说出是哪个;
    /// 覆盖式改名(<see cref="SftpOperation.PosixRename" />,缺的是 posix-rename)另带「先删再改名不是原子的」那句提醒。
    /// 曾经只在库的消息里,按状态码翻的时候丢了。扩展名是协议常量(本端名单里的),不来自对端。
    /// </summary>
    private static string WithMissingExtensions(string text, SftpException ex) =>
        ex.Operation == SftpOperation.PosixRename
            ? Strings.Format("SftpErr_MissingPosixRename", text)
            : Strings.Format("SftpErr_MissingExtension", text, string.Join(" / ", ex.MissingExtensions));

    /// <summary>
    /// 私钥读不出来是**认证**失败的一种,而不是连接失败 —— 上层据此弹的是凭据对话框。
    /// </summary>
    private static VelaSshAuthenticationException TranslatePrivateKey(SshPrivateKeyException ex) =>
        new(Localize(ex), ex);

    /// <summary>
    /// 认证失败时把逐条尝试记录摊开。
    /// </summary>
    /// <remarks>
    /// 这是旧实现拿不到的东西。Tmds 只给一句
    /// <c>"... - AuthenticationFailed - ..."</c>,用户看不出是密钥被跳过了、
    /// 服务端根本不接受这种方法、还是口令真的错了。记录按结构化的说明用界面语言拼(<see cref="DescribeAttempts" />)。
    /// </remarks>
    private static string Describe(SshAuthenticationException ex)
    {
        string main = LocalizeAuth(ex);
        string attempts = DescribeAttempts(ex);
        return string.IsNullOrWhiteSpace(attempts) ? main : $"{main}{Environment.NewLine}{attempts}";
    }

    /// <summary>认证失败的主句:原因码加上服务端接受的方法、有没有过一关,足够拼出界面语言的那句话。</summary>
    /// <remarks>
    /// 逐条尝试记录另由 <see cref="DescribeAttempts" /> 拼。方法名来自服务端,拼进来之前先清一遍。
    /// </remarks>
    internal static string LocalizeAuth(SshAuthenticationException ex)
    {
        string offered = PeerText.Sanitize(string.Join(", ", ex.ServerOffered), 256);
        string? main = ex.Reason switch
        {
            SshFailureReason.TwoFactorRequired => Strings.Format("SshErr_TwoFactorRequired", offered),
            SshFailureReason.PasswordExpired => Strings.Get("SshErr_PasswordExpired"),
            SshFailureReason.AuthenticationMethodExhausted => Strings.Format("SshErr_AuthExhausted", offered),
            _ => null,
        };
        if (main is null)
        {
            return ex.Message;
        }
        if (ex.PartialSuccessAchieved)
        {
            main = $"{main} {Strings.Get("SshErr_AuthPartialSuccess")}";
        }
        return $"{main} [{ex.Reason} @ {ex.Phase}]";
    }

    /// <summary>
    /// 算法协商失败时把双方名单摊开。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 旧实现为了同一件事要**另开一条 TCP 连接回探对端的 KEXINIT**
    /// (<c>SshAlgorithmProbe</c> + <c>SshAlgorithmDiagnostics</c>,共 384 行),
    /// 因为上游没把对端名单暴露出来。现在异常自带。
    /// </para>
    /// <para>
    /// 证书变体(<c>*-cert-v01@openssh.com</c>)不列进「本端支持」:会走到这条诊断的对端
    /// 出示的是普通主机密钥,列出来既不能指导用户改配置,还把可用算法淹没在十几行里。
    /// 交集判断仍用完整名单 —— 少显示不改变判断结果。
    /// </para>
    /// </remarks>
    private static string Describe(SshNegotiationException ex)
    {
        string ours = string.Join(", ", ex.OfferedByUs.Where(IsWorthShowing));

        List<string> lines =
        [
            Strings.Format("Ssh_AlgoMismatchTitle", ex.PeerVersion),
            DescribeCategory(ex.Category),
            Strings.Format("Ssh_AlgoMismatchPeer", string.Join(", ", ex.OfferedByPeer)),
            Strings.Format("Ssh_AlgoMismatchOurs", ours),
        ];
        if (DescribeRemedy(ex) is { } remedy)
        {
            lines.Add(remedy);
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 下一步能做什么:对端提供的算法里有本版实现了、只是没在清单里的 → 指路去连接配置里放开;
    /// 一个都没有 → 如实说放开也没用(常见的是只剩 CBC 的老设备)。
    /// </summary>
    private static string? DescribeRemedy(SshNegotiationException ex)
    {
        // 与覆盖层上的「放开这几个算法并重连」按钮同一份计算(SshAlgorithmRemedy):诊断点名的就是按钮会写进去的。
        (SshAlgorithmKind? kind, IReadOnlyList<string> enableable) = SshAlgorithmRemedy.Enableable(ex);
        if (kind is null)
        {
            return null;
        }
        return enableable.Count > 0
            ? Strings.Format("Ssh_AlgoMismatchEnable", string.Join(", ", enableable))
            : Strings.Get("Ssh_AlgoMismatchUnsupported");
    }

    /// <summary>
    /// 哪一类算法没谈成。
    /// </summary>
    /// <remarks>
    /// 收发两个方向在界面上合成一类:现实里两份名单几乎总是一样,分开说只会把同一件事讲两遍。
    /// 真不一样时异常消息本身已经点明了是哪个方向。
    /// </remarks>
    private static string DescribeCategory(SshNegotiationCategory category) => category switch
    {
        SshNegotiationCategory.KeyExchange => Strings.Get("Ssh_AlgoKindKex"),
        SshNegotiationCategory.HostKey => Strings.Get("Ssh_AlgoKindHostKey"),
        SshNegotiationCategory.EncryptionClientToServer or SshNegotiationCategory.EncryptionServerToClient =>
            Strings.Get("Ssh_AlgoKindEncryption"),
        SshNegotiationCategory.MacClientToServer or SshNegotiationCategory.MacServerToClient =>
            Strings.Get("Ssh_AlgoKindMac"),
        _ => category.ToString(),
    };

    private static bool IsWorthShowing(string algorithm) =>
        !algorithm.Contains("-cert-", StringComparison.Ordinal);

    /// <summary>
    /// 从异常链里抽一句能放进界面的失败原因。
    /// </summary>
    /// <remarks>
    /// 优先用强类型的 <see cref="SshException.Reason" />;一路找不到才退回消息拼接。
    /// </remarks>
    public static string GetFailureDiagnostic(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SshException { Reason: not SshFailureReason.Unknown } known)
            {
                return known.Reason.ToString();
            }
        }

        List<string> parts = [];
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message is { Length: > 0 } message && !parts.Contains(message))
            {
                parts.Add(message);
            }
        }
        return parts.Count > 0 ? string.Join(" → ", parts) : ex.Message;
    }
}
