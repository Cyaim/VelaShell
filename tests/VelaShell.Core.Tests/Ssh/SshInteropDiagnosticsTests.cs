using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 库的结构化诊断(尝试记录的说明、通道失败、私钥 / 证书问题、主机证书问题)在宿主这边按种类出五语言文案。
/// </summary>
/// <remarks>
/// <para>
/// 两道守卫:① 每个枚举值都有映射、映射到的键五份 resx 都有、占位符一致 —— 库里加了新值而宿主没跟上就红
/// (否则英 / 日 / 韩界面悄悄回到库的中文原文,没有任何别的用例会发现);
/// ② 英文界面下每一种的文案里没有一个汉字(参数用的都是 ASCII),也就是真的没有透传库的句子。
/// </para>
/// <para>库的中文原文本身不变,由 VelaShell.Ssh.Tests 的原文用例守着。</para>
/// </remarks>
[TestClass]
[TestCategory("i18n")]
public sealed partial class SshInteropDiagnosticsTests : IDisposable
{
    private static readonly string[] Cultures = ["", "zh-Hans", "zh-Hant", "ja", "ko"];

    private static readonly DateTimeOffset Moment = new(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.CurrentCulture = _culture;
    }

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder { get; }

    [GeneratedRegex(@"[㐀-鿿]")]
    private static partial Regex Cjk { get; }

    // ------------------------------------------------------------ 守卫一:映射与五份 resx

    [TestMethod]
    public void 每一种尝试说明都有五语言文案() =>
        AssertKeys(Enum.GetValues<SshAuthDetailKind>().Where(static k => k != SshAuthDetailKind.Unknown)
            .Select(static k => (k.ToString(), SshInterop.AuthDetailKey(k))));

    [TestMethod]
    public void 每一种尝试结果都有五语言文案() =>
        AssertKeys(Enum.GetValues<SshAuthOutcome>().Select(static o => (o.ToString(), (string?)SshInterop.AuthOutcomeKey(o))));

    [TestMethod]
    public void 每一种通道失败都有五语言文案() =>
        AssertKeys(Enum.GetValues<SshChannelFailureKind>().Where(static k => k != SshChannelFailureKind.Unknown)
            .Select(static k => (k.ToString(), SshInterop.ChannelFailureKey(k))));

    [TestMethod]
    public void 每一种私钥与证书问题都有五语言文案() =>
        AssertKeys(Enum.GetValues<SshKeyProblemKind>().Where(static k => k != SshKeyProblemKind.Unknown)
            .SelectMany(static k => new[]
            {
                ($"{k}(私钥)", SshInterop.KeyProblemKey(k, certificate: false)),
                ($"{k}(证书)", SshInterop.KeyProblemKey(k, certificate: true)),
            }));

    [TestMethod]
    public void 每一种主机证书问题都有五语言文案() =>
        AssertKeys(Enum.GetValues<HostCertificateProblemKind>().Where(static k => k != HostCertificateProblemKind.Unknown)
            .Select(static k => (k.ToString(), SshInterop.HostCertificateProblemKey(k))));

    [TestMethod]
    public void 拼句子用的几条文案也五语言齐全() =>
        AssertKeys(new[]
        {
            "SshErr_AuthAttemptLine", "SshErr_AuthAttemptLineDetail", "SshErr_AuthNoAttempts", "SshErr_AuthLabelProbe",
            "SshErr_AuthLabelViaKbd", "SshErr_AuthLabelAlgorithm", "SshErr_AuthDetailJoin", "SshErr_ChanSftpSubsystemRejected",
            "SshErr_PeerSaid", "SshErr_KeyOrigin",
        }.Select(static k => (k, (string?)k)));

    /// <summary>每个种类都映射到了键;键在五份 resx 里都有、不是空的,占位符与英文一致。</summary>
    private static void AssertKeys(IEnumerable<(string What, string? Key)> mappings)
    {
        ResourceManager manager = new("VelaShell.Core.Resources.Strings", typeof(Strings).Assembly);
        List<string> problems = [];
        foreach ((string what, string? key) in mappings)
        {
            if (key is null)
            {
                problems.Add($"  {what}:宿主 SshInterop 里没有映射");
                continue;
            }

            string? neutral = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)?.GetString(key);
            foreach (string name in Cultures)
            {
                string? value = manager.GetResourceSet(CultureInfo.GetCultureInfo(name), true, false)?.GetString(key);
                if (string.IsNullOrWhiteSpace(value))
                {
                    problems.Add($"  {what}:{key} 在 Strings{(name.Length == 0 ? "" : "." + name)}.resx 里没有");
                }
                else if (neutral is not null && !Placeholders(value).SetEquals(Placeholders(neutral)))
                {
                    problems.Add($"  {what}:{key} 在 {name} 里的占位符与英文不一致");
                }
            }
        }
        Assert.IsEmpty(problems, "库的结构化诊断在宿主这边缺文案(新加的枚举值要在 SshInterop.Diagnostics.cs 配映射、五份 resx 配文案):\n" +
            string.Join("\n", problems));
    }

    private static HashSet<string> Placeholders(string text) => [.. Placeholder.Matches(text).Select(static m => m.Groups[1].Value)];

    // ------------------------------------------------------------ 守卫二:英文界面里不再有库的中文

    [TestMethod]
    public void 英文界面下每一种尝试说明都不透传中文()
    {
        UseEnglish();
        foreach (SshAuthDetailKind kind in Enum.GetValues<SshAuthDetailKind>().Where(static k => k != SshAuthDetailKind.Unknown))
        {
            SshAuthDetail detail = new(kind)
            {
                Methods = ["publickey", "password"],
                Method = "password",
                Count = 3,
                ServerText = "too simple",
                Error = new IOException("access denied"),
                KeyType = "ssh-rsa",
                Algorithm = "rsa-sha2-512",
                FallbackAlgorithm = "ssh-rsa",
                KeyId = "joe@example",
                Time = Moment,
                Principals = ["joe", "deploy"],
                UserName = "alice",
            };
            AssertNoChinese(kind.ToString(), SshInterop.LocalizeAuthDetail(detail));
        }
    }

    [TestMethod]
    public void 英文界面下每一种通道失败都不透传中文()
    {
        UseEnglish();
        foreach (SshChannelFailureKind kind in Enum.GetValues<SshChannelFailureKind>().Where(static k => k != SshChannelFailureKind.Unknown))
        {
            SshChannelException ex = new(SshFailureReason.ChannelOpenFailed, "中文原文", SshChannelOpenFailureReason.ConnectFailed)
            {
                FailureKind = kind,
                ChannelType = "direct-tcpip",
                SubsystemName = "netconf",
                Limit = 4 * 1024 * 1024,
            };
            AssertNoChinese(kind.ToString(), SshInterop.Localize(ex));
        }
    }

    [TestMethod]
    public void 英文界面下每一种私钥与证书问题都不透传中文()
    {
        UseEnglish();
        foreach (SshKeyProblemKind kind in Enum.GetValues<SshKeyProblemKind>().Where(static k => k != SshKeyProblemKind.Unknown))
        {
            SshPrivateKeyException key = new(SshFailureReason.KeyFormatInvalid, "中文原文", new IOException("io"))
            {
                ProblemKind = kind,
                AlgorithmName = "ssh-dss",
            };
            AssertNoChinese($"{kind}(私钥)", SshInterop.Localize(key));

            SshCertificateException certificate = new(SshFailureReason.KeyFormatInvalid, "中文原文", new IOException("io"))
            {
                ProblemKind = kind,
                AlgorithmName = "ssh-ed25519",
                KeyId = "joe@example",
            };
            AssertNoChinese($"{kind}(证书)", SshInterop.Localize(certificate));
        }
    }

    [TestMethod]
    public void 英文界面下每一种主机证书问题都不透传中文()
    {
        UseEnglish();
        foreach (HostCertificateProblemKind kind in Enum.GetValues<HostCertificateProblemKind>().Where(static k => k != HostCertificateProblemKind.Unknown))
        {
            HostCertificateProblem problem = new(kind, "host@example")
            {
                Time = Moment,
                Principals = ["a.example"],
                Host = "b.example",
                CriticalOptions = ["force-command"],
                Algorithm = "ssh-rsa",
                KeyBits = 1024,
                MinimumKeyBits = 2048,
            };
            AssertNoChinese(kind.ToString(), SshInterop.LocalizeHostCertificateProblem(problem));
        }
    }

    private static void UseEnglish()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
    }

    private static void AssertNoChinese(string what, string text)
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(text), $"{what}:文案是空的");
        Assert.IsFalse(Cjk.IsMatch(text), $"{what}:英文界面里还有中文 —— {text}");
    }

    // ------------------------------------------------------------ 拼出来的样子

    /// <summary>认证失败时的逐条记录按界面语言拼:凭据、签名算法、结果、说明;none 探测与口令桥接都有自己的说法。</summary>
    [TestMethod]
    public void 认证失败的逐条记录按界面语言拼()
    {
        UseEnglish();
        SshAuthenticationException original = new(
            SshFailureReason.AuthenticationMethodExhausted,
            "所有认证方法都已尝试且未成功。",
            [
                new SshAuthAttempt("none", "none（探测可用方法）", SshAuthOutcome.Failure, ["publickey", "keyboard-interactive"],
                    "服务端接受：publickey, keyboard-interactive")
                {
                    Details = [new SshAuthDetail(SshAuthDetailKind.ServerAccepts) { Methods = ["publickey", "keyboard-interactive"] }],
                },
                new SshAuthAttempt("publickey", "~/.ssh/id_rsa", SshAuthOutcome.Failure, ["publickey"], "…", "ssh-rsa")
                {
                    Details =
                    [
                        new SshAuthDetail(SshAuthDetailKind.PublicKeyNotAccepted),
                        new SshAuthDetail(SshAuthDetailKind.Sha1Signature) { Algorithm = "ssh-rsa" },
                    ],
                },
                new SshAuthAttempt("keyboard-interactive", "password（经 keyboard-interactive）", SshAuthOutcome.Failure, ["publickey"])
                {
                    BridgedCredentialLabel = "password",
                },
            ],
            ["publickey"]);

        string message = SshInterop.Translate(original)!.Message;

        Assert.IsInstanceOfType<VelaSshAuthenticationException>(SshInterop.Translate(original));
        string[] lines = message.Split(Environment.NewLine);
        Assert.HasCount(4, lines, message);
        Assert.AreEqual(
            Strings.Format("SshErr_AuthAttemptLineDetail", Strings.Get("SshErr_AuthLabelProbe"), Strings.Get("SshErr_AuthOutcomeFailure"),
                Strings.Format("SshErr_AuthDetailServerAccepts", "publickey, keyboard-interactive")),
            lines[1]);
        Assert.AreEqual(
            Strings.Format("SshErr_AuthAttemptLineDetail",
                Strings.Format("SshErr_AuthLabelAlgorithm", "~/.ssh/id_rsa", "ssh-rsa"),
                Strings.Get("SshErr_AuthOutcomeFailure"),
                Strings.Format("SshErr_AuthDetailJoin",
                    Strings.Get("SshErr_AuthDetailPublicKeyNotAccepted"), Strings.Format("SshErr_AuthDetailSha1Signature", "ssh-rsa"))),
            lines[2]);
        Assert.AreEqual(
            Strings.Format("SshErr_AuthAttemptLine", Strings.Format("SshErr_AuthLabelViaKbd", "password"), Strings.Get("SshErr_AuthOutcomeFailure")),
            lines[3]);
        Assert.IsFalse(Cjk.IsMatch(message), message);
    }

    /// <summary>只给了句子的记录(使用者自己造的)照原样显示 —— 那句话本来就是它给的。</summary>
    [TestMethod]
    public void 没有结构化说明的记录照原样显示句子()
    {
        SshAuthAttempt legacy = new("publickey", "~/.ssh/id_ed25519", SshAuthOutcome.Failure, ["publickey"], "服务端不接受这把钥");

        string line = SshInterop.DescribeAttempt(legacy);

        Assert.Contains("~/.ssh/id_ed25519", line);
        Assert.Contains("服务端不接受这把钥", line);
    }

    [TestMethod]
    public void 没有任何尝试时说没有尝试()
    {
        SshAuthenticationException original = new(SshFailureReason.AuthenticationMethodExhausted, "x", [], ["publickey"]);
        Assert.AreEqual(Strings.Get("SshErr_AuthNoAttempts"), SshInterop.DescribeAttempts(original));
    }

    /// <summary>凭据自己抛的库异常按它的种类说(私钥读不出来),而不是透传它的中文消息。</summary>
    [TestMethod]
    public void 凭据出错时按内层异常的种类说()
    {
        SshPrivateKeyException inner = new(SshFailureReason.KeyFormatInvalid, "私钥文件的内容不完整或格式不对。")
        {
            ProblemKind = SshKeyProblemKind.Malformed,
        };

        string text = SshInterop.LocalizeAuthDetail(new SshAuthDetail(SshAuthDetailKind.CredentialFailed) { Error = inner });

        Assert.AreEqual(Strings.Format("SshErr_AuthDetailCredentialFailed", SshInterop.Localize(inner)), text);
        Assert.Contains(Strings.Get("SshErr_KeyMalformed"), text);
    }

    /// <summary>通道失败按种类给建议,服务端的原话清洗之后带上,尾巴照旧是原因码。</summary>
    [TestMethod]
    public void 通道失败按种类给建议并带上服务端原话()
    {
        SshChannelException ex = SshChannelException.FromOpenFailure("direct-tcpip", 1, "no\u001b[2J");

        Exception? translated = SshInterop.Translate(ex);

        Assert.IsInstanceOfType<VelaSshClientException>(translated);
        Assert.AreEqual(
            $"{Strings.Format("SshErr_PeerSaid", Strings.Get("SshErr_ChanForwardingProhibited"), "no?[2J")} [ChannelOpenFailed @ Open]",
            translated.Message);
        Assert.AreSame(ex, translated.InnerException);
    }

    [TestMethod]
    public void 通道失败的参数拼进文案()
    {
        Assert.StartsWith(Strings.Format("SshErr_ChanLimitReached", 64L),
            SshInterop.Localize(new SshChannelException(SshFailureReason.LimitExceeded, "x")
            {
                FailureKind = SshChannelFailureKind.ChannelLimitReached,
                Limit = 64,
            }));
        Assert.StartsWith(Strings.Format("SshErr_ChanWindowBudgetExhausted", 300L),
            SshInterop.Localize(new SshChannelException(SshFailureReason.LimitExceeded, "x")
            {
                FailureKind = SshChannelFailureKind.WindowBudgetExhausted,
                Limit = 300L * 1024 * 1024,
            }));
        Assert.StartsWith(Strings.Get("SshErr_ChanSftpSubsystemRejected"),
            SshInterop.Localize(new SshChannelException(SshFailureReason.ChannelRequestRejected, "x")
            {
                FailureKind = SshChannelFailureKind.SubsystemRejected,
                SubsystemName = "sftp",
            }), "sftp 被拒指到 sshd_config 的那一行");
        Assert.StartsWith(Strings.Format("SshErr_ChanSubsystemRejected", "netconf"),
            SshInterop.Localize(new SshChannelException(SshFailureReason.ChannelRequestRejected, "x")
            {
                FailureKind = SshChannelFailureKind.SubsystemRejected,
                SubsystemName = "netconf",
            }));
    }

    /// <summary>私钥问题按种类说,前面带上是哪个文件;尾巴照旧是原因码。</summary>
    [TestMethod]
    public void 私钥问题按种类说并带上来源()
    {
        SshPrivateKeyException ex = new(SshFailureReason.Unsupported, "不支持的曲线 brainpoolP256r1（~/.ssh/id_ec）。")
        {
            ProblemKind = SshKeyProblemKind.UnsupportedCurve,
            AlgorithmName = "brainpoolP256r1",
            Origin = "~/.ssh/id_ec",
        };

        Exception? translated = SshInterop.Translate(ex);

        Assert.IsInstanceOfType<VelaSshAuthenticationException>(translated, "私钥读不出来是认证的一种失败");
        Assert.AreEqual(
            $"{Strings.Format("SshErr_KeyOrigin", "~/.ssh/id_ec", Strings.Format("SshErr_KeyUnsupportedCurve", "brainpoolP256r1"))} [Unsupported @ None]",
            translated.Message);
    }

    /// <summary>证书与私钥不是一对:两边的指纹由结构化的公钥摆出来。</summary>
    [TestMethod]
    public void 证书与私钥不配对时摆出两边的指纹()
    {
        using InMemorySshSigner certified = InMemorySshSigner.GenerateEd25519();
        using InMemorySshSigner other = InMemorySshSigner.GenerateEd25519();
        SshCertificateException ex = new(SshFailureReason.KeyMismatch, "证书里的公钥与这把私钥不是一对。")
        {
            ProblemKind = SshKeyProblemKind.CertificateKeyMismatch,
            KeyId = "joe@example",
            CertificateKey = certified.PublicKey,
            SignerKey = other.PublicKey,
        };

        string text = SshInterop.Localize(ex);

        Assert.Contains(certified.PublicKey.Sha256Fingerprint, text);
        Assert.Contains(other.PublicKey.Sha256Fingerprint, text);
        Assert.Contains("joe@example", text);
        Assert.EndsWith("[KeyMismatch @ None]", text);
    }

    /// <summary>主机证书来自对端:Key ID、主体都清洗过才进文案。</summary>
    [TestMethod]
    public void 主机证书问题里对端的名字清洗过才进文案()
    {
        HostCertificateProblem problem = new(HostCertificateProblemKind.HostNotListed, "evil\u001b]0;x\u0007")
        {
            Principals = ["a.example‮"],
            Host = "b.example",
        };

        string text = SshInterop.LocalizeHostCertificateProblem(problem);

        Assert.AreEqual(Strings.Format("SshErr_HostCertHostNotListed", "evil?]0;x?", "a.example?", "b.example"), text);
    }
}
