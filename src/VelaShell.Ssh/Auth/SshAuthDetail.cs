// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/04-authentication.md §3.4、§4.4、§4.5、§5.1;velashell-docs/zh/ssh/spec/08-failures.md §一

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Auth;

/// <summary>尝试记录里的一条说明是哪一种（<see cref="SshAuthDetail.Kind"/>）。</summary>
/// <remarks>
/// 每个值用到 <see cref="SshAuthDetail"/> 的哪几个属性，写在各值的说明里；没写到的属性不填。
/// 零值 <see cref="Unknown"/> 是「没归类」：库自己不产生它，使用者遇到时只能照 <see cref="SshAuthDetail.ToString"/> 显示。
/// </remarks>
public enum SshAuthDetailKind
{
    /// <summary>没归类的说明。</summary>
    Unknown = 0,

    /// <summary>开头的 <c>none</c> 探测被拒，服务端报出它接受的方法（<see cref="SshAuthDetail.Methods"/>）。</summary>
    ServerAccepts,

    /// <summary>服务端不接受这条凭据的方法，跳过；它只接受 <see cref="SshAuthDetail.Methods"/>。</summary>
    MethodNotOffered,

    /// <summary>
    /// <see cref="SshAuthDetail.Method"/> 在这次认证里已经失败 <see cref="SshAuthDetail.Count"/> 次，不再重试
    /// （本端对同一个秘密的重试上限，只管 password 与 keyboard-interactive）。
    /// </summary>
    MethodFailureLimit,

    /// <summary>探测时服务端不接受这把公钥（没有去签名）。</summary>
    PublicKeyNotAccepted,

    /// <summary>服务端要求先改密码，已经改成了。</summary>
    PasswordChanged,

    /// <summary>服务端要求先改密码，改密码请求没有成功：服务端不支持改密码，或者旧密码不对。</summary>
    PasswordChangeFailed,

    /// <summary>服务端要求先改密码，但这条凭据没有配置取新密码的回调（<see cref="PasswordCredential.NewPasswordProvider"/>）。</summary>
    PasswordChangeNotConfigured,

    /// <summary>
    /// 服务端连续 <see cref="SshAuthDetail.Count"/> 次不接受新密码；它最后的提示在 <see cref="SshAuthDetail.ServerText"/>
    /// （<b>对端原文，未清洗</b>）。
    /// </summary>
    NewPasswordRejected,

    /// <summary>服务端要求先改密码，取新密码的回调这次没有给出新密码。</summary>
    NewPasswordNotProvided,

    /// <summary>凭据自己出了问题（取口令、签名、回答挑战的回调抛了异常），异常在 <see cref="SshAuthDetail.Error"/>。</summary>
    CredentialFailed,

    /// <summary>这把 <see cref="SshAuthDetail.KeyType"/> 密钥挑不出可用的签名算法（只剩被禁用的 SHA-1 <c>ssh-rsa</c>）。</summary>
    NoUsableSignatureAlgorithm,

    /// <summary>
    /// 服务端没有宣告 <c>server-sig-algs</c>，<see cref="SshAuthDetail.Algorithm"/> 被拒后降级为
    /// <see cref="SshAuthDetail.FallbackAlgorithm"/>（SHA-1）重试。
    /// </summary>
    Sha1Fallback,

    /// <summary>直接用了 SHA-1 签名（<see cref="SshAuthDetail.Algorithm"/>）。</summary>
    Sha1Signature,

    /// <summary>证书 <see cref="SshAuthDetail.KeyId"/> 按本机时钟已于 <see cref="SshAuthDetail.Time"/> 过期。</summary>
    CertificateExpired,

    /// <summary>证书 <see cref="SshAuthDetail.KeyId"/> 按本机时钟要到 <see cref="SshAuthDetail.Time"/> 才生效。</summary>
    CertificateNotYetValid,

    /// <summary>证书 <see cref="SshAuthDetail.KeyId"/> 是主机证书，不能用来登录。</summary>
    HostCertificateForLogin,

    /// <summary>证书签给的用户（<see cref="SshAuthDetail.Principals"/>）不含登录用户 <see cref="SshAuthDetail.UserName"/>。</summary>
    PrincipalNotListed,
}

/// <summary>认证尝试记录里的一条说明：种类加上拼句子用的参数。</summary>
/// <remarks>
/// <para>
/// 〔velashell-docs/zh/ssh/spec/08 §一〕<see cref="SshAuthAttempt.Detail"/> 是拼好的中文句子，写给开发者与日志；
/// 界面要用别的语言说同一件事，就按 <see cref="Kind"/> 与这里的参数自己拼 —— 不去解析那句话。
/// <see cref="ToString"/> 给出的就是 <see cref="SshAuthAttempt.Detail"/> 里的那一段原文。
/// </para>
/// <para>
/// <b>参数都是原值</b>：来自服务端（<see cref="ServerText"/>）或证书（<see cref="KeyId"/>、<see cref="Principals"/>）的文本没有清洗，
/// 拼进界面之前先过 <see cref="PeerText.Sanitize"/>（<see cref="ToString"/> 自己会清）。
/// </para>
/// </remarks>
/// <param name="Kind">说明的种类。</param>
public sealed record SshAuthDetail(SshAuthDetailKind Kind)
{
    /// <summary>服务端接受的方法（<see cref="SshAuthDetailKind.ServerAccepts"/>、<see cref="SshAuthDetailKind.MethodNotOffered"/>）。</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    /// <summary>说到的认证方法（<see cref="SshAuthDetailKind.MethodFailureLimit"/>）。</summary>
    public string? Method { get; init; }

    /// <summary>次数（<see cref="SshAuthDetailKind.MethodFailureLimit"/>、<see cref="SshAuthDetailKind.NewPasswordRejected"/>）。</summary>
    public int Count { get; init; }

    /// <summary>服务端的原话（<see cref="SshAuthDetailKind.NewPasswordRejected"/> 时是改密码的提示）。<b>不可信文本，未清洗。</b></summary>
    public string? ServerText { get; init; }

    /// <summary>凭据抛出的异常（<see cref="SshAuthDetailKind.CredentialFailed"/>）：私钥、agent、外部签名器或使用者回调的。</summary>
    public Exception? Error { get; init; }

    /// <summary>密钥类型，如 <c>ssh-rsa</c>（<see cref="SshAuthDetailKind.NoUsableSignatureAlgorithm"/>）。</summary>
    public string? KeyType { get; init; }

    /// <summary>签名算法（<see cref="SshAuthDetailKind.Sha1Fallback"/> 里是被拒的那个，<see cref="SshAuthDetailKind.Sha1Signature"/> 里是用的那个）。</summary>
    public string? Algorithm { get; init; }

    /// <summary>降级重试用的 SHA-1 算法（<see cref="SshAuthDetailKind.Sha1Fallback"/>）。</summary>
    public string? FallbackAlgorithm { get; init; }

    /// <summary>证书的 Key ID（证书的几种说明）。来自证书，<b>未清洗</b>。</summary>
    public string? KeyId { get; init; }

    /// <summary>证书的失效时刻（<see cref="SshAuthDetailKind.CertificateExpired"/>）或生效时刻（<see cref="SshAuthDetailKind.CertificateNotYetValid"/>）。</summary>
    public DateTimeOffset? Time { get; init; }

    /// <summary>证书签给的用户，完整名单（<see cref="SshAuthDetailKind.PrincipalNotListed"/>）。来自证书，<b>未清洗</b>。</summary>
    public IReadOnlyList<string> Principals { get; init; } = [];

    /// <summary>登录用户（<see cref="SshAuthDetailKind.PrincipalNotListed"/>）。</summary>
    public string? UserName { get; init; }

    /// <summary>证书被拒时的本地线索：几条这样的说明在 <see cref="SshAuthAttempt.Detail"/> 里合成一段、用「；」隔开。</summary>
    internal bool IsCertificateHint =>
        Kind is SshAuthDetailKind.CertificateExpired or SshAuthDetailKind.CertificateNotYetValid
            or SshAuthDetailKind.HostCertificateForLogin or SshAuthDetailKind.PrincipalNotListed;

    /// <summary>这一条说明的中文原文（写给开发者与日志，不是界面文案）。</summary>
    public override string ToString() => Kind switch
    {
        SshAuthDetailKind.ServerAccepts => $"服务端接受：{string.Join(", ", Methods)}",
        SshAuthDetailKind.MethodNotOffered => $"服务端只接受：{string.Join(", ", Methods)}",
        SshAuthDetailKind.MethodFailureLimit => $"{Method} 在这次认证里已经失败 {Count} 次，不再重试。",
        SshAuthDetailKind.PublicKeyNotAccepted => "服务端不接受这把公钥。",
        SshAuthDetailKind.PasswordChanged => "已按服务端的要求修改了密码。",
        SshAuthDetailKind.PasswordChangeFailed => "服务端要求先修改密码，但没有改成：服务端不支持改密码，或旧密码不对。",
        SshAuthDetailKind.PasswordChangeNotConfigured => "服务端要求先修改密码（没有配置取新密码的回调）。",
        SshAuthDetailKind.NewPasswordRejected =>
            $"服务端要求先修改密码，连续 {Count} 次不接受新密码：" + PeerText.Sanitize(ServerText, 256),
        SshAuthDetailKind.NewPasswordNotProvided => "服务端要求先修改密码，这次没有给出新密码。",
        SshAuthDetailKind.CredentialFailed => Error?.Message ?? "",
        SshAuthDetailKind.NoUsableSignatureAlgorithm =>
            $"这把 {KeyType} 密钥没有可用的签名算法" + "（若需要 SHA-1 的 ssh-rsa，请显式开启 AllowSha1RsaSignatures）。",
        SshAuthDetailKind.Sha1Fallback => $"服务端没有宣告 server-sig-algs，{Algorithm} 被拒后降级为 {FallbackAlgorithm}（SHA-1）重试",
        SshAuthDetailKind.Sha1Signature => $"用的是 SHA-1 签名（{Algorithm}）",
        SshAuthDetailKind.CertificateExpired => $"证书（Key ID「{SanitizedKeyId}」）按本机时钟已于 {Time:u} 过期",
        SshAuthDetailKind.CertificateNotYetValid => $"证书（Key ID「{SanitizedKeyId}」）按本机时钟要到 {Time:u} 才生效",
        SshAuthDetailKind.HostCertificateForLogin => $"证书（Key ID「{SanitizedKeyId}」）是主机证书，不能用来登录",
        SshAuthDetailKind.PrincipalNotListed =>
            $"证书签给的用户是 {string.Join("、", Principals.Take(MaxPrincipalsShown).Select(static p => PeerText.Sanitize(p, 64)))}" +
            $"{(Principals.Count > MaxPrincipalsShown ? "……" : "")}，不含登录用户 {UserName}",
        _ => "",
    };

    /// <summary>原文里最多列出几个证书主体，多出来的用「……」带过。</summary>
    internal const int MaxPrincipalsShown = 8;

    private string SanitizedKeyId => PeerText.Sanitize(KeyId, 128);

    /// <summary>把一步认证的几条说明拼成 <see cref="SshAuthAttempt.Detail"/>：第一段照写，后面的各用全角括号括起来接在后面。</summary>
    /// <remarks>
    /// 证书线索连在一起时合成一段、用「；」隔开 —— 它们是同一次「证书被拒」的几条原因。
    /// 没有说明时为 <see langword="null"/>。
    /// </remarks>
    internal static string? Render(IReadOnlyList<SshAuthDetail> details)
    {
        string? text = null;
        for (int i = 0; i < details.Count; i++)
        {
            string segment = details[i].ToString();
            while (details[i].IsCertificateHint && i + 1 < details.Count && details[i + 1].IsCertificateHint)
            {
                segment += "；" + details[++i].ToString();
            }
            text = text is null ? segment : $"{text}（{segment}）";
        }
        return text;
    }
}
