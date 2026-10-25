using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Sftp;

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

    /// <summary>
    /// 建连失败的每一种(主机密钥、代理、跳板)都有文案 —— 只有「策略拒的」例外:那句话是 <c>VelaHostKeyPolicy</c> 用界面语言写好的,原样用。
    /// </summary>
    [TestMethod]
    public void 每一种建连失败都有五语言文案()
    {
        Assert.IsNull(SshInterop.ConnectFailureKey(SshConnectFailureKind.HostKeyPolicyRejected), "策略拒的那句原样用,不另配文案");
        AssertKeys(Enum.GetValues<SshConnectFailureKind>()
            .Where(static k => k is not (SshConnectFailureKind.Unknown or SshConnectFailureKind.HostKeyPolicyRejected))
            .Select(static k => (k.ToString(), SshInterop.ConnectFailureKey(k))));
    }

    [TestMethod]
    public void 每一个SOCKS5应答码都有五语言文案() =>
        AssertKeys(Enumerable.Range(0, 11).Select(static code => ($"REP {code}", (string?)SshInterop.Socks5ReplyKey(code))));

    [TestMethod]
    public void 每一种转发器起不来都有五语言文案() =>
        AssertKeys(Enum.GetValues<SshForwardFailureKind>().Where(static k => k != SshForwardFailureKind.Unknown)
            .SelectMany(static k => new[]
            {
                (k.ToString(), SshInterop.ForwardFailureKey(k)),
                ($"{k}(特权端口)", SshInterop.ForwardFailureKey(k, privilegedPort: true)),
            })
            .Append(("端口被占", "SshErr_FwdListenInUse"))
            .Append(("xauth 的输出", "SshErr_FwdXAuthSaid")));

    [TestMethod]
    public void 每一种agent失败都有五语言文案() =>
        AssertKeys(Enum.GetValues<SshAgentFailureKind>().Where(static k => k != SshAgentFailureKind.Unknown)
            .SelectMany(static k => new[]
            {
                (k.ToString(), SshInterop.AgentFailureKey(k)),
                ($"{k}(目的地约束)", SshInterop.AgentFailureKey(k, destinationConstraints: true)),
            }));

    [TestMethod]
    public void 每一种转发连接失败都有五语言文案() =>
        AssertKeys(Enum.GetValues<ForwardErrorReason>().SelectMany(static r => new[]
        {
            ($"{r}", (string?)SshInterop.ForwardErrorKey(r)),
            ($"{r}(超时)", SshInterop.ForwardErrorKey(r, timedOut: true)),
            ($"{r}(多条)", SshInterop.ForwardErrorKey(r, many: true)),
        }));

    [TestMethod]
    public void 拼句子用的几条文案也五语言齐全() =>
        AssertKeys(new[]
        {
            "SshErr_AuthAttemptLine", "SshErr_AuthAttemptLineDetail", "SshErr_AuthNoAttempts", "SshErr_AuthLabelProbe",
            "SshErr_AuthLabelViaKbd", "SshErr_AuthLabelAlgorithm", "SshErr_AuthDetailJoin", "SshErr_ChanSftpSubsystemRejected",
            "SshErr_PeerSaid", "SshErr_KeyOrigin", "SshErr_ProxyCommandSaid", "SshErr_RekeyFailed",
            "SftpErr_VersionTooOld", "SftpErr_VersionTooNew", "SftpErr_SessionClosed", "SftpErr_MissingExtension", "SftpErr_MissingPosixRename",
        }.Select(static k => (k, (string?)k)));

    /// <summary>
    /// SFTP 起不来的每一种(<see cref="SftpUnavailableKind" />)在英文界面下都有界面语言的说法,没有一种透传库的中文。
    /// 曾经「版本太低」「流水线已释放」都显示成「服务端没开 SFTP」。
    /// </summary>
    [TestMethod]
    public void 英文界面下每一种SFTP起不来都不透传中文()
    {
        UseEnglish();
        foreach (SftpUnavailableKind kind in Enum.GetValues<SftpUnavailableKind>().Where(static k => k != SftpUnavailableKind.Unknown))
        {
            SftpUnavailableException ex = new("中文原文") { FailureKind = kind, ServerVersion = 2, ServerExitStatus = 127 };
            AssertNoChinese(kind.ToString(), SshInterop.LocalizeUnavailable(ex));
        }
    }

    [TestMethod]
    public void SFTP起不来按种类说()
    {
        Assert.AreEqual($"{Strings.Format("SftpErr_VersionTooOld", "2")} [Unsupported @ Open]",
            SshInterop.LocalizeUnavailable(new SftpUnavailableException("服务端只支持 SFTP v2。")
            {
                FailureKind = SftpUnavailableKind.VersionTooOld,
                ServerVersion = 2,
            }));
        Assert.AreEqual($"{Strings.Get("SftpErr_SessionClosed")} [Unsupported @ Open]",
            SshInterop.LocalizeUnavailable(new SftpUnavailableException("SFTP 流水线已释放。") { FailureKind = SftpUnavailableKind.SessionClosed }),
            "本端收工,不说成服务端没开 SFTP");
        Assert.StartsWith(Strings.Get("SftpErr_Unavailable"),
            SshInterop.LocalizeUnavailable(new SftpUnavailableException("x") { FailureKind = SftpUnavailableKind.SubsystemRejected }));
    }

    /// <summary>服务端缺扩展(库自己报的「不支持」):说出缺的是哪个;缺 posix-rename 时带上「先删再改名不是原子的」。</summary>
    [TestMethod]
    public void SFTP缺扩展时说出是哪一个()
    {
        SftpException statvfs = new(SftpStatusCode.OperationUnsupported, "", "/data", SftpOperation.GetFileSystemInfo)
        {
            MissingExtensions = ["statvfs@openssh.com"],
        };
        Assert.AreEqual(
            Strings.Format("SftpErr_MissingExtension",
                Strings.Format("SftpErr_WithPath", Strings.Get("SftpErr_Unsupported"), "/data"), "statvfs@openssh.com"),
            SshInterop.LocalizeSftp(statvfs));

        SftpException rename = new(SftpStatusCode.OperationUnsupported, "", "/a", SftpOperation.PosixRename)
        {
            MissingExtensions = ["posix-rename@openssh.com"],
        };
        Assert.AreEqual(
            Strings.Format("SftpErr_MissingPosixRename", Strings.Format("SftpErr_WithPath", Strings.Get("SftpErr_Unsupported"), "/a")),
            SshInterop.LocalizeSftp(rename));

        // 服务端自己回的「不支持」没有这一段。
        Assert.AreEqual(Strings.Get("SftpErr_Unsupported"),
            SshInterop.LocalizeSftp(new SftpException(SftpStatusCode.OperationUnsupported, "")));
    }

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

    [TestMethod]
    public void 英文界面下每一种建连失败都不透传中文()
    {
        UseEnglish();
        foreach (SshConnectFailureKind kind in Enum.GetValues<SshConnectFailureKind>()
                     .Where(static k => k is not (SshConnectFailureKind.Unknown or SshConnectFailureKind.HostKeyPolicyRejected)))
        {
            // 内层(那一跳自己的失败)也是库的中文消息:要按它的原因码 / 种类说,不能原样拼进来。
            Exception inner = kind == SshConnectFailureKind.JumpForwardRefused
                ? SshChannelException.FromOpenFailure("direct-tcpip", 1, "")
                : new SshConnectException(SshFailureReason.TcpRefused, SshPhase.Dialing, "中文原文");
            SshConnectException ex = new(SshFailureReason.ProxyRefused, SshPhase.Dialing, "中文原文", inner)
            {
                FailureKind = kind,
                Via = "proxy.example:1080",
                Target = "target.example:22",
                HostKeyType = "ssh-rsa",
                HostKeyAlgorithm = "rsa-sha2-512",
                HostKeyBits = 1024,
                MinimumHostKeyBits = 2048,
                ProxyReplyCode = 5,
                ProxyReply = "HTTP/1.1 403 Forbidden",
            };
            AssertNoChinese(kind.ToString(), SshInterop.Localize(ex));
            AssertNoChinese($"{kind}(经 Translate)", SshInterop.Translate(ex)!.Message);
        }
    }

    [TestMethod]
    public void 英文界面下每一种转发器起不来都不透传中文()
    {
        UseEnglish();
        foreach (SshForwardFailureKind kind in Enum.GetValues<SshForwardFailureKind>().Where(static k => k != SshForwardFailureKind.Unknown))
        {
            // 转述内层的两种:内层是库的中文消息(agent 没在跑、那一条转发绑不上),要按它的原因码说。
            // xauth 跑不起来、临时目录建不了时转述 .NET 的消息(按系统语言),这里给一句英文的。
            Exception inner = kind switch
            {
                SshForwardFailureKind.LocalAgentUnavailable or SshForwardFailureKind.ConfigForwardFailed =>
                    new SshAgentException(SshFailureReason.AgentNotRunning, "中文原文"),
                SshForwardFailureKind.XAuthTempDirectoryFailed or SshForwardFailureKind.XAuthStartFailed => new IOException("no such file"),
                _ => new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressAlreadyInUse),
            };
            SshForwardException ex = new(SshFailureReason.ForwardSetupFailed, "中文原文", inner)
            {
                FailureKind = kind,
                Endpoint = "127.0.0.1:8080",
                Port = 80,
                Limit = 8,
                Timeout = TimeSpan.FromSeconds(30),
                DisplayName = "localhost:10",
                ExitCode = 1,
                ErrorOutput = "xauth: unable to open display",
                ConfigLine = "LocalForward 8080 db:5432",
            };
            AssertNoChinese(kind.ToString(), SshInterop.Localize(ex));
        }
    }

    [TestMethod]
    public void 英文界面下每一种agent失败都不透传中文()
    {
        UseEnglish();
        foreach (SshAgentFailureKind kind in Enum.GetValues<SshAgentFailureKind>().Where(static k => k != SshAgentFailureKind.Unknown))
        {
            foreach (bool destination in new[] { false, true })
            {
                SshAgentException ex = new(SshFailureReason.AgentUnavailable, "中文原文", new IOException("pipe broken"))
                {
                    FailureKind = kind,
                    Endpoint = @"\\.\pipe\openssh-ssh-agent",
                    PipeOwner = "S-1-5-21-1-2-3-1001",
                    RequestedAlgorithm = "rsa-sha2-512",
                    SignedAlgorithm = "ssh-rsa",
                    HasDestinationConstraints = destination,
                };
                AssertNoChinese($"{kind}/{destination}", SshInterop.Localize(ex));
            }
        }
    }

    [TestMethod]
    public void 英文界面下每一种转发连接失败都不透传中文()
    {
        UseEnglish();
        foreach (ForwardErrorReason reason in Enum.GetValues<ForwardErrorReason>())
        {
            foreach (TimeSpan? timeout in new TimeSpan?[] { null, TimeSpan.FromSeconds(10) })
            {
                ForwardErrorEventArgs e = new(reason, "中文原文")
                {
                    Target = "db.internal:5432",
                    Timeout = timeout,
                    Limit = 64,
                    RejectedCount = 3,
                };
                AssertNoChinese($"{reason}/{timeout}", SshInterop.LocalizeForwardError(e));
            }
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

    /// <summary>
    /// 同是 HostKeyRejected:库自己验不过的按种类说(带算法、尾巴是原因码),策略拒的那句原样用(VelaHostKeyPolicy 已经用界面语言写好)。
    /// 曾经两种都当成策略的话照用 —— 英 / 日 / 韩界面上签名验不过显示的是库的中文。
    /// </summary>
    [TestMethod]
    public void 主机密钥被拒分清库自己拒的与策略拒的()
    {
        SshConnectException signature = new(SshFailureReason.HostKeyRejected, SshPhase.KeyExchange, "服务端对交换哈希的签名验证失败。")
        {
            FailureKind = SshConnectFailureKind.HostKeySignatureInvalid,
            HostKeyType = "ssh-ed25519",
            HostKeyAlgorithm = "ssh-ed25519",
        };
        Exception? translated = SshInterop.Translate(signature);
        Assert.IsInstanceOfType<VelaSshHostKeyRejectedException>(translated, "自动重连照旧据此停下");
        Assert.AreEqual($"{Strings.Format("SshErr_HostKeySignatureInvalid", "ssh-ed25519")} [HostKeyRejected @ KeyExchange]", translated.Message);

        SshConnectException tooShort = new(SshFailureReason.HostKeyRejected, SshPhase.KeyExchange, "x")
        {
            FailureKind = SshConnectFailureKind.HostKeyTooShort,
            HostKeyBits = 1024,
            MinimumHostKeyBits = 2048,
        };
        Assert.StartsWith(Strings.Format("SshErr_HostKeyTooShort", 1024, 2048), SshInterop.Localize(tooShort));

        const string policyText = "Host key for example.com was rejected (SHA256:abc).";
        SshConnectException policy = new(SshFailureReason.HostKeyRejected, SshPhase.KeyExchange, policyText)
        {
            FailureKind = SshConnectFailureKind.HostKeyPolicyRejected,
        };
        Assert.AreEqual(policyText, SshInterop.Translate(policy)!.Message, "策略写的理由原样用,不加尾巴(与原来一样)");
    }

    /// <summary>对端出示的主机密钥类型来自对端:清洗过才进文案。</summary>
    [TestMethod]
    public void 主机密钥类型来自对端清洗过才进文案()
    {
        SshConnectException ex = new(SshFailureReason.HostKeyRejected, SshPhase.KeyExchange, "x")
        {
            FailureKind = SshConnectFailureKind.HostKeyAlgorithmMismatch,
            HostKeyType = "evil\u001b[2J",
            HostKeyAlgorithm = "ssh-ed25519",
        };

        Assert.StartsWith(Strings.Format("SshErr_HostKeyAlgorithmMismatch", "evil?[2J", "ssh-ed25519"), SshInterop.Localize(ex));
    }

    /// <summary>跳板这一跳的失败:外层说「连不上跳板 X」,原因按内层(那一跳自己的异常)的完整译法说;异常类型照内层的原因码分流。</summary>
    [TestMethod]
    public void 跳板失败按内层的译法说并说清是哪一跳()
    {
        const string policyText = "Host key for jump.example was rejected.";
        SshConnectException hop = new(SshFailureReason.HostKeyRejected, SshPhase.KeyExchange, policyText)
        {
            FailureKind = SshConnectFailureKind.HostKeyPolicyRejected,
        };
        SshConnectException wrapped = new(SshFailureReason.HostKeyRejected, SshPhase.Dialing, $"连不上跳板 jump.example:22：{policyText}", hop)
        {
            FailureKind = SshConnectFailureKind.JumpConnectFailed,
            Via = "jump.example:22",
        };

        Exception? translated = SshInterop.Translate(wrapped);

        Assert.IsInstanceOfType<VelaSshHostKeyRejectedException>(translated, "跳板的指纹被拒,自动重连同样要停");
        Assert.AreEqual(Strings.Format("SshErr_JumpConnectFailed", "jump.example:22", policyText), translated.Message);

        SshConnectException refused = new(SshFailureReason.ProxyRefused, SshPhase.Dialing, "跳板 jump.example:22 不肯转发……",
            SshChannelException.FromOpenFailure("direct-tcpip", 1, ""))
        {
            FailureKind = SshConnectFailureKind.JumpForwardRefused,
            Via = "jump.example:22",
            Target = "db.internal:22",
        };
        Assert.AreEqual(
            Strings.Format("SshErr_JumpForwardRefused", "jump.example:22", "db.internal:22",
                $"{Strings.Get("SshErr_ChanForwardingProhibited")} [ChannelOpenFailed @ Open]"),
            SshInterop.Localize(refused), "跳板回的原因码按通道失败的种类说");
    }

    /// <summary>代理回的原话(HTTP 状态行)来自对端:清洗过才进文案;SOCKS5 应答码按 RFC 1928 的含义说。</summary>
    [TestMethod]
    public void 代理的应答按种类说且原话清洗过()
    {
        SshConnectException http = new(SshFailureReason.ProxyRefused, SshPhase.Dialing, "x")
        {
            FailureKind = SshConnectFailureKind.HttpConnectRefused,
            Via = "proxy.example:3128",
            Target = "target.example:22",
            ProxyReplyCode = 403,
            ProxyReply = "HTTP/1.1 403 \u001b]0;pwn\u0007",
        };
        Assert.AreEqual(
            Strings.Format("SshErr_HttpConnectRefused", "proxy.example:3128", "target.example:22", "HTTP/1.1 403 ?]0;pwn?"),
            SshInterop.DescribeConnectFailure(http));

        SshConnectException socks = new(SshFailureReason.ProxyRefused, SshPhase.Dialing, "x")
        {
            FailureKind = SshConnectFailureKind.Socks5ConnectRefused,
            Via = "proxy.example:1080",
            Target = "target.example:22",
            ProxyReplyCode = 2,
        };
        Assert.AreEqual(
            $"{Strings.Format("SshErr_Socks5ConnectRefused", "proxy.example:1080", "target.example:22", Strings.Get("SshErr_Socks5Reply2"), "2")} [ProxyRefused @ Dialing]",
            SshInterop.Localize(socks));
        Assert.AreEqual("SshErr_Socks5ReplyOther", SshInterop.Socks5ReplyKey(42));
    }

    /// <summary>重协商时验签失败:库把首次交换口径的那条包在内层,界面照它的种类说,并点明是重协商。</summary>
    [TestMethod]
    public void 重协商时主机密钥被拒按内层的种类说()
    {
        SshConnectException kex = new(SshFailureReason.HostKeyRejected, SshPhase.KeyExchange, "x")
        {
            FailureKind = SshConnectFailureKind.HostKeySignatureInvalid,
            HostKeyAlgorithm = "ssh-ed25519",
        };
        SshConnectionClosedException closed = new(SshFailureReason.HostKeyRejected, SshPhase.Rekeying, "密钥重协商失败：x", kex);

        Assert.AreEqual(
            $"{Strings.Format("SshErr_RekeyFailed", Strings.Format("SshErr_HostKeySignatureInvalid", "ssh-ed25519"))} [HostKeyRejected @ Rekeying]",
            SshInterop.Translate(closed)!.Message);
    }

    /// <summary>
    /// 转发器起不来:端口被占说「被占」、别的套接字错误照错误码说;服务端拒绝监听特权端口时换一句;xauth 的输出清洗过才进文案;
    /// 本机 agent 不可用转述 agent 那边按原因码的那句。曾经端口占用、X11 起不来直接透传库的中文。
    /// </summary>
    [TestMethod]
    public void 转发器起不来按种类说()
    {
        SshForwardException inUse = new(SshFailureReason.ForwardBindFailed, "在 127.0.0.1:8080 上起监听失败。",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressAlreadyInUse))
        {
            FailureKind = SshForwardFailureKind.ListenFailed,
            Endpoint = "127.0.0.1:8080",
        };
        Assert.AreEqual($"{Strings.Format("SshErr_FwdListenInUse", "127.0.0.1:8080")} [ForwardBindFailed @ Open]", SshInterop.Localize(inUse));

        SshForwardException denied = new(SshFailureReason.ForwardBindFailed, "x",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AccessDenied))
        {
            FailureKind = SshForwardFailureKind.ListenFailed,
            Endpoint = "0.0.0.0:80",
        };
        Assert.StartsWith(Strings.Format("SshErr_FwdListenFailed", "0.0.0.0:80", "AccessDenied"), SshInterop.Localize(denied));

        SshForwardException privileged = new(SshFailureReason.ForwardRejected, "x")
        {
            FailureKind = SshForwardFailureKind.RemoteListenRejected,
            Endpoint = "0.0.0.0:80",
            Port = 80,
        };
        Assert.StartsWith(Strings.Format("SshErr_FwdRemoteListenRejectedPrivileged", "0.0.0.0:80"), SshInterop.Localize(privileged));

        SshForwardException xauth = new(SshFailureReason.ForwardSetupFailed, "x")
        {
            FailureKind = SshForwardFailureKind.XAuthFailed,
            ExitCode = 1,
            ErrorOutput = "bad\u001b]0;x\u0007display",
        };
        Assert.AreEqual(
            $"{Strings.Format("SshErr_FwdXAuthSaid", Strings.Format("SshErr_FwdXAuthFailed", "1"), "bad?]0;x?display")} [ForwardSetupFailed @ Open]",
            SshInterop.Localize(xauth));

        SshForwardException agent = new(SshFailureReason.AgentNotRunning, "本机 ssh-agent 不可用,没有请求 agent 转发:……",
            new SshAgentException(SshFailureReason.AgentNotRunning, "找不到 ssh-agent。"))
        {
            FailureKind = SshForwardFailureKind.LocalAgentUnavailable,
        };
        Assert.AreEqual(
            Strings.Format("SshErr_FwdLocalAgentUnavailable", $"{Strings.Get("SshErr_AgentNotRunning")} [AgentNotRunning @ Authenticating]"),
            SshInterop.Localize(agent));
    }

    /// <summary>
    /// agent:没在跑仍是原来那句(按原因码的文案,一字不变);管道属主不可信带上 SID;太老的 agent 签错算法时两个算法名清洗过才进文案。
    /// 曾经 AgentUnavailable 与 agent 的 Unsupported 透传库的中文。
    /// </summary>
    [TestMethod]
    public void agent失败按种类说()
    {
        SshAgentException notRunning = new(SshFailureReason.AgentNotRunning, "环境变量 SSH_AUTH_SOCK 没有设。")
        {
            FailureKind = SshAgentFailureKind.NotRunning,
        };
        Assert.AreEqual($"{Strings.Get("SshErr_AgentNotRunning")} [AgentNotRunning @ Authenticating]", SshInterop.Localize(notRunning));

        SshAgentException untrusted = new(SshFailureReason.AgentUnavailable, "x")
        {
            FailureKind = SshAgentFailureKind.PipeOwnerUntrusted,
            Endpoint = @"\\.\pipe\openssh-ssh-agent",
            PipeOwner = "S-1-5-21-1-2-3-1001",
        };
        Assert.AreEqual(
            $"{Strings.Format("SshErr_AgentPipeOwnerUntrusted", @"\\.\pipe\openssh-ssh-agent", "S-1-5-21-1-2-3-1001")} [AgentUnavailable @ Authenticating]",
            SshInterop.Localize(untrusted));

        SshAgentException mismatch = new(SshFailureReason.Unsupported, "x")
        {
            FailureKind = SshAgentFailureKind.SignatureAlgorithmMismatch,
            RequestedAlgorithm = "rsa-sha2-512",
            SignedAlgorithm = "ssh-rsa\u001b[2J",
        };
        Assert.StartsWith(Strings.Format("SshErr_AgentSignatureAlgorithmMismatch", "ssh-rsa?[2J", "rsa-sha2-512"), SshInterop.Localize(mismatch));

        SshAgentException add = new(SshFailureReason.AgentRefused, "x")
        {
            FailureKind = SshAgentFailureKind.AddRefused,
            HasDestinationConstraints = true,
        };
        Assert.StartsWith(Strings.Get("SshErr_AgentAddRefusedDestination"), SshInterop.Localize(add));
    }

    /// <summary>转发上没带异常的失败:按原因、目标与时限说;撞上限时说拒了几条。</summary>
    [TestMethod]
    public void 没带异常的转发失败按原因与参数说()
    {
        Assert.AreEqual(Strings.Format("SshErr_FwdChannelOpenTimeout", "db.internal:5432", "30"),
            SshInterop.LocalizeForwardError(new ForwardErrorEventArgs(ForwardErrorReason.ChannelOpen, "x")
            {
                Target = "db.internal:5432",
                Timeout = TimeSpan.FromSeconds(30),
            }));
        Assert.AreEqual(Strings.Format("SshErr_FwdConnectionLimitMany", "8", 5),
            SshInterop.LocalizeForwardError(new ForwardErrorEventArgs(ForwardErrorReason.ConnectionLimit, "x") { Limit = 8, RejectedCount = 5 }));
        Assert.AreEqual(Strings.Format("SshErr_FwdConnectionLimit", "8"),
            SshInterop.LocalizeForwardError(new ForwardErrorEventArgs(ForwardErrorReason.ConnectionLimit, "x") { Limit = 8, RejectedCount = 1 }));
        Assert.AreEqual(Strings.Get("SshErr_FwdSocksInvalid"),
            SshInterop.LocalizeForwardError(new ForwardErrorEventArgs(ForwardErrorReason.SocksHandshake, "x")));
        Assert.AreEqual(Strings.Format("SshErr_FwdChannelOpenFailed", "a?b"),
            SshInterop.LocalizeForwardError(new ForwardErrorEventArgs(ForwardErrorReason.ChannelOpen, "x") { Target = "a\u001bb" }),
            "SOCKS 客户端给的目标名清洗过才进文案");
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
