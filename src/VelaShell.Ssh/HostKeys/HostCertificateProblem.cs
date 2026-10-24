// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.certkeys —— 证书类型、有效期、valid principals、critical options、CA 签名
//   行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.5（主机证书的验证）;velashell-docs/zh/ssh/spec/08-failures.md §一

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.HostKeys;

/// <summary>主机证书不合格的是哪一条（<see cref="HostCertificateProblem.Kind"/>）。</summary>
/// <remarks>
/// 顺序就是验证的顺序（velashell-docs/zh/ssh/spec/03 §5.5）：先看类型，再验 CA 签名，再看有效期、主体与关键选项，报的是第一条不过的。
/// 零值 <see cref="Unknown"/> 是「没归类」：库自己不产生它。
/// </remarks>
public enum HostCertificateProblemKind
{
    /// <summary>没归类的问题。</summary>
    Unknown = 0,

    /// <summary>出示的是用户证书，不能用来证明主机身份。</summary>
    NotHostCertificate,

    /// <summary>签发 CA 的公钥读不出来（类型不支持，或者它自己也是一张证书）。</summary>
    AuthorityKeyUnreadable,

    /// <summary>签发证书的 RSA CA 只有 <see cref="HostCertificateProblem.KeyBits"/> 位，低于 <see cref="HostCertificateProblem.MinimumKeyBits"/> 位。</summary>
    AuthorityKeyTooShort,

    /// <summary>CA 签名的格式非法。</summary>
    SignatureMalformed,

    /// <summary>CA 签名用的算法（<see cref="HostCertificateProblem.Algorithm"/>）不接受 —— SHA-1 的 <c>ssh-rsa</c>。</summary>
    SignatureAlgorithmRejected,

    /// <summary>CA 签名验不过：证书被改过，或者不是这个 CA 签的。</summary>
    SignatureInvalid,

    /// <summary>证书已于 <see cref="HostCertificateProblem.Time"/> 过期。</summary>
    Expired,

    /// <summary>证书要到 <see cref="HostCertificateProblem.Time"/> 才生效。</summary>
    NotYetValid,

    /// <summary>证书没有列出任何主机名 —— 那等于对所有主机有效，不予接受。</summary>
    NoPrincipals,

    /// <summary>证书签给的主机（<see cref="HostCertificateProblem.Principals"/>）不含被连的 <see cref="HostCertificateProblem.Host"/>。</summary>
    HostNotListed,

    /// <summary>证书带着不认识的关键选项（<see cref="HostCertificateProblem.CriticalOptions"/>）—— 主机证书没有定义任何关键选项。</summary>
    UnknownCriticalOptions,
}

/// <summary>主机证书哪里不合格：种类加上拼句子用的参数。</summary>
/// <remarks>
/// <para>
/// 〔velashell-docs/zh/ssh/spec/08 §一〕<see cref="KnownHostLookup.CertificateProblem"/> 是拼好的中文句子（写给开发者、进日志与拒绝理由）；
/// 界面要用别的语言说同一件事，就按 <see cref="Kind"/> 与这里的参数自己拼，不去解析那句话。
/// <see cref="ToString"/> 给出的就是那句原文。
/// </para>
/// <para>
/// <b>证书来自对端</b>：<see cref="KeyId"/>、<see cref="Principals"/>、<see cref="CriticalOptions"/>、<see cref="Algorithm"/> 都是原值，
/// 拼进界面之前先过 <see cref="PeerText.Sanitize"/>（<see cref="ToString"/> 自己会清）。
/// </para>
/// </remarks>
/// <param name="Kind">哪一条不合格。</param>
/// <param name="KeyId">证书的 Key ID（<c>ssh-keygen -I</c>）。来自对端，<b>未清洗</b>。</param>
public sealed record HostCertificateProblem(HostCertificateProblemKind Kind, string KeyId)
{
    /// <summary>失效时刻（<see cref="HostCertificateProblemKind.Expired"/>）或生效时刻（<see cref="HostCertificateProblemKind.NotYetValid"/>）。</summary>
    public DateTimeOffset? Time { get; init; }

    /// <summary>证书签给的主机名（<see cref="HostCertificateProblemKind.HostNotListed"/>）。来自对端，<b>未清洗</b>。</summary>
    public IReadOnlyList<string> Principals { get; init; } = [];

    /// <summary>被连的主机名（<see cref="HostCertificateProblemKind.HostNotListed"/>）。</summary>
    public string? Host { get; init; }

    /// <summary>证书带着的关键选项名（<see cref="HostCertificateProblemKind.UnknownCriticalOptions"/>）。来自对端，<b>未清洗</b>。</summary>
    public IReadOnlyList<string> CriticalOptions { get; init; } = [];

    /// <summary>CA 签名的算法名（<see cref="HostCertificateProblemKind.SignatureAlgorithmRejected"/>）。来自对端，<b>未清洗</b>。</summary>
    public string? Algorithm { get; init; }

    /// <summary>RSA CA 的位数（<see cref="HostCertificateProblemKind.AuthorityKeyTooShort"/>）。</summary>
    public int KeyBits { get; init; }

    /// <summary>RSA CA 至少要多少位（<see cref="HostCertificateProblemKind.AuthorityKeyTooShort"/>）。</summary>
    public int MinimumKeyBits { get; init; }

    /// <summary>这一条问题的中文原文（写给开发者，进日志与拒绝理由，不是界面文案）。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/08 §一〕证书来自对端：Key ID、主体、关键选项、算法名先过 <see cref="PeerText.Sanitize"/> 再进这句话
    /// （控制字符、双向文本控制符换成 <c>?</c>，超长截断）。正常证书的文本与清洗之前一字不差；原值照样在各属性里。
    /// 曾经原样拼进来 —— 而这句话会进 <c>KnownHostsPolicy</c> 的拒绝消息与日志。
    /// </remarks>
    public override string ToString() => Kind switch
    {
        HostCertificateProblemKind.NotHostCertificate => $"这是一张用户证书(Key ID「{SafeKeyId}」),不能用来证明主机身份。",
        HostCertificateProblemKind.AuthorityKeyUnreadable =>
            $"证书(Key ID「{SafeKeyId}」)的签发 CA 公钥读不出来(类型不支持,或者它自己也是一张证书)。",
        HostCertificateProblemKind.AuthorityKeyTooShort => $"签发证书的 RSA CA 只有 {KeyBits} 位,低于 {MinimumKeyBits} 位。",
        HostCertificateProblemKind.SignatureMalformed => $"证书(Key ID「{SafeKeyId}」)的 CA 签名格式非法。",
        HostCertificateProblemKind.SignatureAlgorithmRejected =>
            $"证书(Key ID「{SafeKeyId}」)的 CA 签名用的是 {PeerText.Sanitize(Algorithm, 64)},不接受(SHA-1 的 ssh-rsa 签名已经不安全)。",
        HostCertificateProblemKind.SignatureInvalid => $"证书(Key ID「{SafeKeyId}」)的 CA 签名验不过 —— 证书被改过,或者不是这个 CA 签的。",
        HostCertificateProblemKind.Expired => $"主机证书(Key ID「{SafeKeyId}」)已于 {Time:u} 过期。",
        HostCertificateProblemKind.NotYetValid => $"主机证书(Key ID「{SafeKeyId}」)要到 {Time:u} 才生效。",
        HostCertificateProblemKind.NoPrincipals => $"主机证书(Key ID「{SafeKeyId}」)没有列出任何主机名 —— 那等于对所有主机有效,不予接受。",
        HostCertificateProblemKind.HostNotListed => $"主机证书(Key ID「{SafeKeyId}」)签给的是 {JoinNames(Principals)},不含 {Host}。",
        HostCertificateProblemKind.UnknownCriticalOptions =>
            $"主机证书(Key ID「{SafeKeyId}」)带着不认识的关键选项:{JoinNames(CriticalOptions)}。",
        _ => $"主机证书(Key ID「{SafeKeyId}」)不合格。",
    };

    private string SafeKeyId => PeerText.Sanitize(KeyId);

    /// <summary>证书里的一串名字逐个清洗后用「、」接起来（个数由证书解析的字段上限管着，这里不再截）。</summary>
    private static string JoinNames(IReadOnlyList<string> names) => string.Join("、", names.Select(static n => PeerText.Sanitize(n)));
}
