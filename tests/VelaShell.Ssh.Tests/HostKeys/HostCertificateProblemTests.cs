// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.5;velashell-docs/zh/ssh/spec/08-failures.md §一

using VelaShell.Ssh.HostKeys;

namespace VelaShell.Ssh.Tests.HostKeys;

/// <summary>
/// 主机证书不合格的结构化出处（<see cref="HostCertificateProblem"/>）：每一种的中文原文与结构化之前
/// <c>OpenSshCertificate.CheckHostCertificate</c> 写的句子一字不差。拿真证书走一遍的在 <see cref="HostCertificateTests"/>。
/// </summary>
[TestClass]
[TestCategory("HostKeys")]
public sealed class HostCertificateProblemTests
{
    private static readonly DateTimeOffset Moment = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> Texts =>
    [
        [new HostCertificateProblem(HostCertificateProblemKind.NotHostCertificate, "k"), "这是一张用户证书(Key ID「k」),不能用来证明主机身份。"],
        [new HostCertificateProblem(HostCertificateProblemKind.AuthorityKeyUnreadable, "k"), "证书(Key ID「k」)的签发 CA 公钥读不出来(类型不支持,或者它自己也是一张证书)。"],
        [new HostCertificateProblem(HostCertificateProblemKind.AuthorityKeyTooShort, "k") { KeyBits = 1024, MinimumKeyBits = 2048 }, "签发证书的 RSA CA 只有 1024 位,低于 2048 位。"],
        [new HostCertificateProblem(HostCertificateProblemKind.SignatureMalformed, "k"), "证书(Key ID「k」)的 CA 签名格式非法。"],
        [new HostCertificateProblem(HostCertificateProblemKind.SignatureAlgorithmRejected, "k") { Algorithm = "ssh-rsa" }, "证书(Key ID「k」)的 CA 签名用的是 ssh-rsa,不接受(SHA-1 的 ssh-rsa 签名已经不安全)。"],
        [new HostCertificateProblem(HostCertificateProblemKind.SignatureInvalid, "k"), "证书(Key ID「k」)的 CA 签名验不过 —— 证书被改过,或者不是这个 CA 签的。"],
        [new HostCertificateProblem(HostCertificateProblemKind.Expired, "k") { Time = Moment }, "主机证书(Key ID「k」)已于 2027-01-01 00:00:00Z 过期。"],
        [new HostCertificateProblem(HostCertificateProblemKind.NotYetValid, "k") { Time = Moment }, "主机证书(Key ID「k」)要到 2027-01-01 00:00:00Z 才生效。"],
        [new HostCertificateProblem(HostCertificateProblemKind.NoPrincipals, "k"), "主机证书(Key ID「k」)没有列出任何主机名 —— 那等于对所有主机有效,不予接受。"],
        [new HostCertificateProblem(HostCertificateProblemKind.HostNotListed, "k") { Principals = ["a.example", "b.example"], Host = "c.example" }, "主机证书(Key ID「k」)签给的是 a.example、b.example,不含 c.example。"],
        [new HostCertificateProblem(HostCertificateProblemKind.UnknownCriticalOptions, "k") { CriticalOptions = ["force-command"] }, "主机证书(Key ID「k」)带着不认识的关键选项:force-command。"],
    ];

    [TestMethod]
    [DynamicData(nameof(Texts))]
    public void 每一种问题的原文与结构化之前一致(HostCertificateProblem problem, string expected) =>
        Assert.AreEqual(expected, problem.ToString());

    /// <summary>
    /// 证书来自对端:Key ID、主体、关键选项、算法名里的控制字符与双向文本控制符换成 <c>?</c> 才进这句话(spec 08 §一)。
    /// 曾经原样拼进来,而这句话会进 <c>KnownHostsPolicy</c> 的拒绝消息与日志。正常证书的文本不变(上面的原文用例)。
    /// </summary>
    [TestMethod]
    public void 对端证书里的名字清洗过才进原文()
    {
        const string Evil = "x\u001b]52;c;AAAA\u0007‮";

        Assert.AreEqual("这是一张用户证书(Key ID「x?]52;c;AAAA??」),不能用来证明主机身份。",
            new HostCertificateProblem(HostCertificateProblemKind.NotHostCertificate, Evil).ToString());
        Assert.AreEqual("主机证书(Key ID「k」)签给的是 a?b、x?]52;c;AAAA??,不含 c.example。",
            new HostCertificateProblem(HostCertificateProblemKind.HostNotListed, "k") { Principals = ["a\rb", Evil], Host = "c.example" }.ToString());
        Assert.AreEqual("主机证书(Key ID「k」)带着不认识的关键选项:x?]52;c;AAAA??。",
            new HostCertificateProblem(HostCertificateProblemKind.UnknownCriticalOptions, "k") { CriticalOptions = [Evil] }.ToString());
        Assert.AreEqual("证书(Key ID「k」)的 CA 签名用的是 x?]52;c;AAAA??,不接受(SHA-1 的 ssh-rsa 签名已经不安全)。",
            new HostCertificateProblem(HostCertificateProblemKind.SignatureAlgorithmRejected, "k") { Algorithm = Evil }.ToString());

        HostCertificateProblem raw = new(HostCertificateProblemKind.NotHostCertificate, Evil);
        Assert.AreEqual(Evil, raw.KeyId, "原值照样留在属性里,由显示的一方决定怎么清");
    }

    [TestMethod]
    public void 每一种问题都有原文()
    {
        foreach (HostCertificateProblemKind kind in Enum.GetValues<HostCertificateProblemKind>().Where(static k => k != HostCertificateProblemKind.Unknown))
        {
            Assert.Contains(kind, Texts.Select(static t => ((HostCertificateProblem)t[0]).Kind),
                $"{kind} 没有原文用例 —— 新加的种类要在这里写下它的中文原文");
        }
    }
}
