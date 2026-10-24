// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/04-authentication.md §3.4;velashell-docs/zh/ssh/spec/08-failures.md §一

using VelaShell.Ssh.Auth;

namespace VelaShell.Ssh.Tests.Auth;

/// <summary>
/// 尝试记录的结构化说明（<see cref="SshAuthDetail"/>）：每一种的中文原文与结构化之前的句子一字不差，
/// 几条拼成 <see cref="SshAuthAttempt.Detail"/> 的样子也不变。
/// </summary>
/// <remarks>
/// 原文的句子照抄自结构化之前的认证器；跑真实认证的那几条（<see cref="AuthenticationTests"/>）在旧实现上同样通过 ——
/// 改了措辞就会在这里红，而界面文案在宿主那边按种类另拼，不受这里影响。
/// </remarks>
[TestClass]
[TestCategory("Auth")]
public sealed class AuthDetailTests
{
    private static readonly DateTimeOffset Moment = new(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static IEnumerable<object[]> Texts =>
    [
        [new SshAuthDetail(SshAuthDetailKind.ServerAccepts) { Methods = ["publickey", "password"] }, "服务端接受：publickey, password"],
        [new SshAuthDetail(SshAuthDetailKind.MethodNotOffered) { Methods = ["password"] }, "服务端只接受：password"],
        [new SshAuthDetail(SshAuthDetailKind.MethodFailureLimit) { Method = "password", Count = 3 }, "password 在这次认证里已经失败 3 次，不再重试。"],
        [new SshAuthDetail(SshAuthDetailKind.PublicKeyNotAccepted), "服务端不接受这把公钥。"],
        [new SshAuthDetail(SshAuthDetailKind.PasswordChanged), "已按服务端的要求修改了密码。"],
        [new SshAuthDetail(SshAuthDetailKind.PasswordChangeFailed), "服务端要求先修改密码，但没有改成：服务端不支持改密码，或旧密码不对。"],
        [new SshAuthDetail(SshAuthDetailKind.PasswordChangeNotConfigured), "服务端要求先修改密码（没有配置取新密码的回调）。"],
        [new SshAuthDetail(SshAuthDetailKind.NewPasswordRejected) { Count = 3, ServerText = "太短\u001b[2J" }, "服务端要求先修改密码，连续 3 次不接受新密码：太短?[2J"],
        [new SshAuthDetail(SshAuthDetailKind.NewPasswordNotProvided), "服务端要求先修改密码，这次没有给出新密码。"],
        [new SshAuthDetail(SshAuthDetailKind.CredentialFailed) { Error = new IOException("读不出来") }, "读不出来"],
        [new SshAuthDetail(SshAuthDetailKind.NoUsableSignatureAlgorithm) { KeyType = "ssh-rsa" }, "这把 ssh-rsa 密钥没有可用的签名算法（若需要 SHA-1 的 ssh-rsa，请显式开启 AllowSha1RsaSignatures）。"],
        [new SshAuthDetail(SshAuthDetailKind.Sha1Fallback) { Algorithm = "rsa-sha2-512", FallbackAlgorithm = "ssh-rsa" }, "服务端没有宣告 server-sig-algs，rsa-sha2-512 被拒后降级为 ssh-rsa（SHA-1）重试"],
        [new SshAuthDetail(SshAuthDetailKind.Sha1Signature) { Algorithm = "ssh-rsa" }, "用的是 SHA-1 签名（ssh-rsa）"],
        [new SshAuthDetail(SshAuthDetailKind.CertificateExpired) { KeyId = "joe", Time = Moment }, "证书（Key ID「joe」）按本机时钟已于 2020-01-02 03:04:05Z 过期"],
        [new SshAuthDetail(SshAuthDetailKind.CertificateNotYetValid) { KeyId = "joe", Time = Moment }, "证书（Key ID「joe」）按本机时钟要到 2020-01-02 03:04:05Z 才生效"],
        [new SshAuthDetail(SshAuthDetailKind.HostCertificateForLogin) { KeyId = "host\u0007" }, "证书（Key ID「host?」）是主机证书，不能用来登录"],
        [new SshAuthDetail(SshAuthDetailKind.PrincipalNotListed) { Principals = ["joe", "deploy"], UserName = "alice" }, "证书签给的用户是 joe、deploy，不含登录用户 alice"],
        [new SshAuthDetail(SshAuthDetailKind.PrincipalNotListed) { Principals = [.. Enumerable.Range(1, 9).Select(static i => $"u{i}")], UserName = "alice" },
            "证书签给的用户是 u1、u2、u3、u4、u5、u6、u7、u8……，不含登录用户 alice"],
    ];

    [TestMethod]
    [DynamicData(nameof(Texts))]
    public void 每一种说明的原文与结构化之前一致(SshAuthDetail detail, string expected) =>
        Assert.AreEqual(expected, detail.ToString());

    [TestMethod]
    public void 每一种说明都有原文()
    {
        foreach (SshAuthDetailKind kind in Enum.GetValues<SshAuthDetailKind>().Where(static k => k != SshAuthDetailKind.Unknown))
        {
            Assert.Contains(kind, Texts.Select(static t => ((SshAuthDetail)t[0]).Kind),
                $"{kind} 没有原文用例 —— 新加的种类要在这里写下它的中文原文");
        }
    }

    [TestMethod]
    public void 几条说明拼成Detail时第一段照写后面的括起来()
    {
        SshAuthDetail rejected = new(SshAuthDetailKind.PublicKeyNotAccepted);
        SshAuthDetail sha1 = new(SshAuthDetailKind.Sha1Signature) { Algorithm = "ssh-rsa" };
        SshAuthDetail expired = new(SshAuthDetailKind.CertificateExpired) { KeyId = "joe", Time = Moment };
        SshAuthDetail principals = new(SshAuthDetailKind.PrincipalNotListed) { Principals = ["joe"], UserName = "alice" };

        Assert.IsNull(SshAuthDetail.Render([]));
        Assert.AreEqual("服务端不接受这把公钥。", SshAuthDetail.Render([rejected]));
        Assert.AreEqual("服务端不接受这把公钥。（用的是 SHA-1 签名（ssh-rsa））", SshAuthDetail.Render([rejected, sha1]));

        // 证书线索是同一次「被拒」的几条原因：合成一段、用「；」隔开，整段括起来。
        Assert.AreEqual(
            "服务端不接受这把公钥。（用的是 SHA-1 签名（ssh-rsa））（证书（Key ID「joe」）按本机时钟已于 2020-01-02 03:04:05Z 过期；证书签给的用户是 joe，不含登录用户 alice）",
            SshAuthDetail.Render([rejected, sha1, expired, principals]));
        Assert.AreEqual(
            "证书（Key ID「joe」）按本机时钟已于 2020-01-02 03:04:05Z 过期；证书签给的用户是 joe，不含登录用户 alice",
            SshAuthDetail.Render([expired, principals]));
    }

    [TestMethod]
    public void 默认构造的尝试记录没有说明()
    {
        SshAuthAttempt attempt = default;
        Assert.IsEmpty(attempt.Details);
        Assert.IsNull(attempt.BridgedCredentialLabel);

        SshAuthAttempt legacy = new("password", "password", SshAuthOutcome.Failure, [], "使用者自己写的说明");
        Assert.IsEmpty(legacy.Details, "只给了句子的记录照旧可以构造，结构化说明为空");
        Assert.AreEqual("password：失败 —— 使用者自己写的说明", legacy.ToString());
    }
}
