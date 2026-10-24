using System.Globalization;
using VelaShell.Core.Resources;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 库的结构化诊断 → 界面语言:认证的逐条尝试记录、通道失败的建议、私钥 / 证书读不出来的原因、主机证书不合格的原因。
/// </summary>
/// <remarks>
/// <para>
/// 这几类以前直接透传库的中文原文(库 AGENTS 4.8)。库现在给出了结构化的出处 —— <see cref="SshAuthDetail" />、
/// <see cref="SshChannelException.FailureKind" />、<see cref="SshKeyProblemKind" />、<see cref="HostCertificateProblem" /> ——
/// 这里只按种类与参数拼句子,<b>不解析库的句子</b>(库 AGENTS 4.5)。
/// </para>
/// <para>
/// 每个种类都要有一条文案:<c>SshInteropDiagnosticsTests</c> 逐个枚举值核对映射与五份 resx,库里加了新值而这里没跟上就红。
/// 没归类(<c>Unknown</c>)的照旧退回原因码那一层(<see cref="Localize" />)或库的原文。
/// </para>
/// <para>对端或文件里来的名字(Key ID、主体、算法名、通道类型、服务端原话)拼进来之前一律先过 <see cref="PeerText.Sanitize" />。</para>
/// </remarks>
internal static partial class SshInterop
{
    /// <summary>认证尝试记录里 <c>none</c> 探测那一步的方法名(RFC 4252 §5.2 的协议常量)。</summary>
    private const string AuthNoneMethod = "none";

    /// <summary>证书主体最多列几个,多出来的用省略号带过(与库的原文同一个数)。</summary>
    private const int MaxPrincipalsShown = 8;

    // ------------------------------------------------------------ 认证尝试记录

    /// <summary>把认证失败的逐条尝试记录拼成多行界面语言(替代库的 <see cref="SshAuthenticationException.DescribeAttempts" />)。</summary>
    internal static string DescribeAttempts(SshAuthenticationException ex) =>
        ex.Attempts.Count == 0
            ? Strings.Get("SshErr_AuthNoAttempts")
            : string.Join(Environment.NewLine, ex.Attempts.Select(DescribeAttempt));

    /// <summary>一条尝试记录:凭据(签名算法)、结果、说明。</summary>
    internal static string DescribeAttempt(SshAuthAttempt attempt)
    {
        string label = attempt.BridgedCredentialLabel is { } bridged
            ? Strings.Format("SshErr_AuthLabelViaKbd", bridged)
            : attempt.Method == AuthNoneMethod
                ? Strings.Get("SshErr_AuthLabelProbe")
                : attempt.CredentialLabel;
        if (attempt.SignatureAlgorithm is { } algorithm)
        {
            label = Strings.Format("SshErr_AuthLabelAlgorithm", label, algorithm);
        }

        string outcome = Strings.Get(AuthOutcomeKey(attempt.Outcome));
        string? detail = DescribeAttemptDetails(attempt);
        return detail is null
            ? Strings.Format("SshErr_AuthAttemptLine", label, outcome)
            : Strings.Format("SshErr_AuthAttemptLineDetail", label, outcome, detail);
    }

    /// <summary>
    /// 一步的几条说明。没有结构化说明、只有句子的记录(使用者自己造的)照原样显示 ——
    /// 那句话本来就是它给的,不是库的中文。
    /// </summary>
    private static string? DescribeAttemptDetails(SshAuthAttempt attempt)
    {
        if (attempt.Details.Count == 0)
        {
            return string.IsNullOrWhiteSpace(attempt.Detail) ? null : attempt.Detail;
        }

        string[] parts = [.. attempt.Details.Select(LocalizeAuthDetail).Where(static p => p.Length > 0)];
        return parts.Length == 0
            ? null
            : parts.Aggregate(static (joined, next) => Strings.Format("SshErr_AuthDetailJoin", joined, next));
    }

    /// <summary>尝试结果的文案键。</summary>
    internal static string AuthOutcomeKey(SshAuthOutcome outcome) => outcome switch
    {
        SshAuthOutcome.Success => "SshErr_AuthOutcomeSuccess",
        SshAuthOutcome.PartialSuccess => "SshErr_AuthOutcomePartialSuccess",
        SshAuthOutcome.SkippedNotOffered => "SshErr_AuthOutcomeSkippedNotOffered",
        SshAuthOutcome.SkippedNoMaterial => "SshErr_AuthOutcomeSkippedNoMaterial",
        _ => "SshErr_AuthOutcomeFailure",
    };

    /// <summary>尝试说明的文案键;没归类时为 <see langword="null" />。</summary>
    internal static string? AuthDetailKey(SshAuthDetailKind kind) => kind switch
    {
        SshAuthDetailKind.ServerAccepts => "SshErr_AuthDetailServerAccepts",
        SshAuthDetailKind.MethodNotOffered => "SshErr_AuthDetailMethodNotOffered",
        SshAuthDetailKind.MethodFailureLimit => "SshErr_AuthDetailMethodFailureLimit",
        SshAuthDetailKind.PublicKeyNotAccepted => "SshErr_AuthDetailPublicKeyNotAccepted",
        SshAuthDetailKind.PasswordChanged => "SshErr_AuthDetailPasswordChanged",
        SshAuthDetailKind.PasswordChangeFailed => "SshErr_AuthDetailPasswordChangeFailed",
        SshAuthDetailKind.PasswordChangeNotConfigured => "SshErr_AuthDetailPasswordChangeNotConfigured",
        SshAuthDetailKind.NewPasswordRejected => "SshErr_AuthDetailNewPasswordRejected",
        SshAuthDetailKind.NewPasswordNotProvided => "SshErr_AuthDetailNewPasswordNotProvided",
        SshAuthDetailKind.CredentialFailed => "SshErr_AuthDetailCredentialFailed",
        SshAuthDetailKind.NoUsableSignatureAlgorithm => "SshErr_AuthDetailNoUsableSignatureAlgorithm",
        SshAuthDetailKind.Sha1Fallback => "SshErr_AuthDetailSha1Fallback",
        SshAuthDetailKind.Sha1Signature => "SshErr_AuthDetailSha1Signature",
        SshAuthDetailKind.CertificateExpired => "SshErr_AuthDetailCertificateExpired",
        SshAuthDetailKind.CertificateNotYetValid => "SshErr_AuthDetailCertificateNotYetValid",
        SshAuthDetailKind.HostCertificateForLogin => "SshErr_AuthDetailHostCertificateForLogin",
        SshAuthDetailKind.PrincipalNotListed => "SshErr_AuthDetailPrincipalNotListed",
        _ => null,
    };

    /// <summary>一条尝试说明的界面语言;没归类的照库的原文。</summary>
    internal static string LocalizeAuthDetail(SshAuthDetail detail)
    {
        if (AuthDetailKey(detail.Kind) is not { } key)
        {
            return detail.ToString();
        }

        return detail.Kind switch
        {
            SshAuthDetailKind.ServerAccepts or SshAuthDetailKind.MethodNotOffered =>
                Strings.Format(key, PeerText.Sanitize(string.Join(", ", detail.Methods), 256)),
            SshAuthDetailKind.MethodFailureLimit => Strings.Format(key, detail.Method, detail.Count),
            SshAuthDetailKind.NewPasswordRejected => Strings.Format(key, detail.Count, PeerText.Sanitize(detail.ServerText, 256)),
            SshAuthDetailKind.CredentialFailed => Strings.Format(key, DescribeCredentialError(detail.Error)),
            SshAuthDetailKind.NoUsableSignatureAlgorithm => Strings.Format(key, detail.KeyType),
            SshAuthDetailKind.Sha1Fallback => Strings.Format(key, detail.Algorithm, detail.FallbackAlgorithm),
            SshAuthDetailKind.Sha1Signature => Strings.Format(key, detail.Algorithm),
            SshAuthDetailKind.CertificateExpired or SshAuthDetailKind.CertificateNotYetValid =>
                Strings.Format(key, PeerText.Sanitize(detail.KeyId, 128), FormatTime(detail.Time)),
            SshAuthDetailKind.HostCertificateForLogin => Strings.Format(key, PeerText.Sanitize(detail.KeyId, 128)),
            SshAuthDetailKind.PrincipalNotListed => Strings.Format(key, JoinPeerNames(detail.Principals), detail.UserName),
            _ => Strings.Get(key),
        };
    }

    /// <summary>凭据自己抛的异常:库的异常按原因码与结构化属性本地化,别的(宿主自己的回调)它的消息本来就是界面语言。</summary>
    private static string DescribeCredentialError(Exception? error) => error switch
    {
        null => "",
        SshException ssh => Localize(ssh),
        _ => error.Message,
    };

    // ------------------------------------------------------------ 通道

    /// <summary>通道失败的文案键;没归类时为 <see langword="null" />。sftp 子系统被拒另有一句(见 <see cref="LocalizeChannel" />)。</summary>
    internal static string? ChannelFailureKey(SshChannelFailureKind kind) => kind switch
    {
        SshChannelFailureKind.ForwardingProhibited => "SshErr_ChanForwardingProhibited",
        SshChannelFailureKind.OpenProhibited => "SshErr_ChanOpenProhibited",
        SshChannelFailureKind.ForwardTargetUnreachable => "SshErr_ChanForwardTargetUnreachable",
        SshChannelFailureKind.UnknownChannelType => "SshErr_ChanUnknownType",
        SshChannelFailureKind.ResourceShortage => "SshErr_ChanResourceShortage",
        SshChannelFailureKind.OpenRejected => "SshErr_ChanOpenRejected",
        SshChannelFailureKind.ClosedBeforeConfirmation => "SshErr_ChanClosedBeforeConfirmation",
        SshChannelFailureKind.ChannelLimitReached => "SshErr_ChanLimitReached",
        SshChannelFailureKind.WindowBudgetExhausted => "SshErr_ChanWindowBudgetExhausted",
        SshChannelFailureKind.ExecRejected => "SshErr_ChanExecRejected",
        SshChannelFailureKind.PtyRejected => "SshErr_ChanPtyRejected",
        SshChannelFailureKind.ShellRejected => "SshErr_ChanShellRejected",
        SshChannelFailureKind.SubsystemRejected => "SshErr_ChanSubsystemRejected",
        SshChannelFailureKind.ZeroMaxPacket => "SshErr_ChanZeroMaxPacket",
        _ => null,
    };

    /// <summary>通道失败:按种类给出「下一步该查什么」,服务端给了说明就带上(清洗过)。</summary>
    internal static string LocalizeChannel(SshChannelException ex)
    {
        if (ChannelFailureKey(ex.FailureKind) is not { } key)
        {
            return LocalizeByReason(ex);
        }

        string text = ex.FailureKind switch
        {
            SshChannelFailureKind.UnknownChannelType => Strings.Format(key, PeerText.Sanitize(ex.ChannelType, 64)),
            SshChannelFailureKind.OpenRejected => Strings.Format(key,
                ex.OpenFailureReason is { } code ? ((uint)code).ToString(CultureInfo.InvariantCulture) : "?"),
            SshChannelFailureKind.ChannelLimitReached => Strings.Format(key, ex.Limit),
            SshChannelFailureKind.WindowBudgetExhausted => Strings.Format(key, (ex.Limit ?? 0) / (1024 * 1024)),
            // sftp 被拒最常见,给它指到 sshd_config 的那一行;子系统名是本端请求的,不来自对端。
            SshChannelFailureKind.SubsystemRejected when ex.SubsystemName == "sftp" => Strings.Get("SshErr_ChanSftpSubsystemRejected"),
            SshChannelFailureKind.SubsystemRejected => Strings.Format(key, ex.SubsystemName),
            _ => Strings.Get(key),
        };
        if (!string.IsNullOrWhiteSpace(ex.PeerDescription))
        {
            text = Strings.Format("SshErr_PeerSaid", text, PeerText.Sanitize(ex.PeerDescription));
        }
        return $"{text} [{ex.Reason} @ {ex.Phase}]";
    }

    // ------------------------------------------------------------ 私钥 / 证书

    /// <summary>
    /// 私钥或证书问题的文案键;没归类时为 <see langword="null" />。
    /// 两边都会出现的几种(读不出、没权限、格式不对、类型不支持)按 <paramref name="certificate" /> 分开说是私钥还是证书。
    /// </summary>
    internal static string? KeyProblemKey(SshKeyProblemKind kind, bool certificate) => kind switch
    {
        SshKeyProblemKind.FileUnreadable => certificate ? "SshErr_CertFileUnreadable" : "SshErr_KeyFileUnreadable",
        SshKeyProblemKind.FileAccessDenied => certificate ? "SshErr_CertFileAccessDenied" : "SshErr_KeyFileAccessDenied",
        SshKeyProblemKind.Malformed => certificate ? "SshErr_CertMalformed" : "SshErr_KeyMalformed",
        SshKeyProblemKind.UnsupportedKeyType => certificate ? "SshErr_CertUnsupportedKeyType" : "SshErr_KeyUnsupportedType",
        SshKeyProblemKind.FormatUnrecognized => "SshErr_KeyFormatUnrecognized",
        SshKeyProblemKind.PassphraseRequired => "SshErr_KeyPassphraseRequired",
        SshKeyProblemKind.PassphraseIncorrect => "SshErr_KeyPassphraseIncorrect",
        SshKeyProblemKind.UnsupportedCurve => "SshErr_KeyUnsupportedCurve",
        SshKeyProblemKind.UnsupportedCipher => "SshErr_KeyUnsupportedCipher",
        SshKeyProblemKind.UnsupportedKdf => "SshErr_KeyUnsupportedKdf",
        SshKeyProblemKind.UnsupportedFormatVersion => "SshErr_KeyUnsupportedFormatVersion",
        SshKeyProblemKind.UnsupportedPointFormat => "SshErr_KeyUnsupportedPointFormat",
        SshKeyProblemKind.MultipleKeys => "SshErr_KeyMultipleKeys",
        SshKeyProblemKind.LegacyPemEncryption => "SshErr_KeyLegacyPemEncryption",
        SshKeyProblemKind.KdfCostTooHigh => "SshErr_KeyKdfCostTooHigh",
        SshKeyProblemKind.KeyStoreUnavailable => "SshErr_KeyStoreUnavailable",
        SshKeyProblemKind.NotACertificate => "SshErr_CertNotACertificate",
        SshKeyProblemKind.HostCertificateForLogin => "SshErr_CertHostCertificateForLogin",
        SshKeyProblemKind.CertificateKeyMismatch => "SshErr_CertKeyMismatch",
        SshKeyProblemKind.CertificateKeyNotLoaded => "SshErr_CertKeyNotLoaded",
        _ => null,
    };

    /// <summary>私钥读不出来:按种类说原因,前面带上是哪个文件(<see cref="SshPrivateKeyException.Origin" />)。</summary>
    internal static string LocalizeKeyProblem(SshPrivateKeyException ex) =>
        KeyProblemText(ex, ex.ProblemKind, certificate: false, ex.Origin, ex.AlgorithmName, keyId: null, certificateKey: null, signerKey: null);

    /// <summary>证书读不出来或用不了:同上;证书与私钥不是一对时带上两边的指纹。</summary>
    internal static string LocalizeKeyProblem(SshCertificateException ex) =>
        KeyProblemText(ex, ex.ProblemKind, certificate: true, ex.Origin, ex.AlgorithmName, ex.KeyId, ex.CertificateKey, ex.SignerKey);

    private static string KeyProblemText(
        SshException ex, SshKeyProblemKind kind, bool certificate, string? origin, string? algorithmName,
        string? keyId, SshPublicKey? certificateKey, SshPublicKey? signerKey)
    {
        if (KeyProblemKey(kind, certificate) is not { } key)
        {
            return LocalizeByReason(ex);
        }

        string text = kind switch
        {
            // IO 错误的原话来自 .NET(按系统语言),路径在前面的来源里。
            SshKeyProblemKind.FileUnreadable => Strings.Format(key, ex.InnerException?.Message ?? ex.Reason.ToString()),
            SshKeyProblemKind.UnsupportedKeyType or SshKeyProblemKind.UnsupportedCurve or SshKeyProblemKind.UnsupportedCipher
                or SshKeyProblemKind.UnsupportedKdf or SshKeyProblemKind.UnsupportedPointFormat or SshKeyProblemKind.NotACertificate =>
                Strings.Format(key, PeerText.Sanitize(algorithmName, 128)),
            SshKeyProblemKind.HostCertificateForLogin => Strings.Format(key, PeerText.Sanitize(keyId, 128)),
            SshKeyProblemKind.CertificateKeyMismatch => Strings.Format(key,
                PeerText.Sanitize(keyId, 128), DescribeKey(certificateKey), DescribeKey(signerKey)),
            _ => Strings.Get(key),
        };
        if (!string.IsNullOrEmpty(origin))
        {
            text = Strings.Format("SshErr_KeyOrigin", origin, text);
        }
        return $"{text} [{ex.Reason} @ {ex.Phase}]";
    }

    private static string DescribeKey(SshPublicKey? key) => key is null ? "?" : $"{key.KeyType} {key.Sha256Fingerprint}";

    // ------------------------------------------------------------ 主机证书

    /// <summary>主机证书不合格的文案键;没归类时为 <see langword="null" />。</summary>
    internal static string? HostCertificateProblemKey(HostCertificateProblemKind kind) => kind switch
    {
        HostCertificateProblemKind.NotHostCertificate => "SshErr_HostCertNotHostCertificate",
        HostCertificateProblemKind.AuthorityKeyUnreadable => "SshErr_HostCertAuthorityKeyUnreadable",
        HostCertificateProblemKind.AuthorityKeyTooShort => "SshErr_HostCertAuthorityKeyTooShort",
        HostCertificateProblemKind.SignatureMalformed => "SshErr_HostCertSignatureMalformed",
        HostCertificateProblemKind.SignatureAlgorithmRejected => "SshErr_HostCertSignatureAlgorithmRejected",
        HostCertificateProblemKind.SignatureInvalid => "SshErr_HostCertSignatureInvalid",
        HostCertificateProblemKind.Expired => "SshErr_HostCertExpired",
        HostCertificateProblemKind.NotYetValid => "SshErr_HostCertNotYetValid",
        HostCertificateProblemKind.NoPrincipals => "SshErr_HostCertNoPrincipals",
        HostCertificateProblemKind.HostNotListed => "SshErr_HostCertHostNotListed",
        HostCertificateProblemKind.UnknownCriticalOptions => "SshErr_HostCertUnknownCriticalOptions",
        _ => null,
    };

    /// <summary>
    /// 主机证书哪里不合格(<see cref="KnownHostLookup.CertificateProblemDetail" />)的界面语言;没归类的照库的原文。
    /// </summary>
    /// <remarks>受信主机 CA 签的证书不合格时,<see cref="VelaHostKeyPolicy" /> 的拒绝理由用它 —— 同一个证书问题只有这一套文案;证书来自对端,名字一律清洗。</remarks>
    internal static string LocalizeHostCertificateProblem(HostCertificateProblem problem)
    {
        if (HostCertificateProblemKey(problem.Kind) is not { } key)
        {
            return problem.ToString();
        }

        string keyId = PeerText.Sanitize(problem.KeyId, 128);
        return problem.Kind switch
        {
            HostCertificateProblemKind.AuthorityKeyTooShort => Strings.Format(key, problem.KeyBits, problem.MinimumKeyBits),
            HostCertificateProblemKind.SignatureAlgorithmRejected => Strings.Format(key, keyId, PeerText.Sanitize(problem.Algorithm, 64)),
            HostCertificateProblemKind.Expired or HostCertificateProblemKind.NotYetValid =>
                Strings.Format(key, keyId, FormatTime(problem.Time)),
            HostCertificateProblemKind.HostNotListed => Strings.Format(key, keyId, JoinPeerNames(problem.Principals), problem.Host),
            HostCertificateProblemKind.UnknownCriticalOptions => Strings.Format(key, keyId, JoinPeerNames(problem.CriticalOptions)),
            _ => Strings.Format(key, keyId),
        };
    }

    // ------------------------------------------------------------ 工具

    /// <summary>证书里的时刻按 UTC 的「可排序」格式写(与库的原文一致,跨语言不变,也不受本机时区影响)。</summary>
    private static string FormatTime(DateTimeOffset? time) =>
        time is { } t ? t.ToString("u", CultureInfo.InvariantCulture) : "?";

    /// <summary>证书里的一串名字:逐个清洗,最多列 <see cref="MaxPrincipalsShown" /> 个。</summary>
    private static string JoinPeerNames(IReadOnlyList<string> names)
    {
        string joined = string.Join(", ", names.Take(MaxPrincipalsShown).Select(static n => PeerText.Sanitize(n, 64)));
        return names.Count > MaxPrincipalsShown ? $"{joined}, …" : joined;
    }
}
