// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/04-authentication.md 全部
//
// 这是 M1 的第二块决定性测试：握手之后真的把认证跑完。
//
// 这里最值得看的两条：
//   · **部分成功（2FA）**。SSH 里没有「2FA 报文」，多因素就是
//     「USERAUTH_FAILURE 且 partial_success = true」。把它当失败处理，
//     库就永远连不上堡垒机 —— 而且症状是「密码明明是对的却说认证失败」。
//   · **公钥签名输入**。session_id 与请求载荷必须拼成一份连续字节再签；
//     分两处各拼一遍是这里唯一的出错方式，而它的症状只有一句「签名验证失败」。

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Auth;

[TestClass]
[TestCategory("Auth")]
public sealed class AuthenticationTests
{
    private sealed record AuthRun(
        SshAuthenticationResult? Result,
        SshAuthenticationException? Error,
        TestAuthObservation Observation,
        bool ServerSucceeded)
    {
        /// <summary>成功时的结果；失败时直接让断言看到那条异常的说明。</summary>
        public SshAuthenticationResult Succeeded =>
            Result ?? throw new AssertFailedException(
                $"认证本应成功，却失败了：{Error?.Message}{Environment.NewLine}{Error?.DescribeAttempts()}");

        public SshAuthenticationException Failed =>
            Error ?? throw new AssertFailedException("认证本应失败，却成功了。");
    }

    /// <summary>跑完握手 + 认证，两侧都收工。</summary>
    private static async Task<AuthRun> RunAsync(
        IReadOnlyList<SshCredential> credentials,
        TestAuthPolicy? policy = null,
        string userName = "joe",
        Func<SshPacketTransport, string, byte[], SshAuthenticator>? authenticatorFactory = null)
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();

        await using TestSshServer server = new(serverStream);
        SshPacketTransport clientTransport = new(clientStream);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        try
        {
            Task<TestSshServerHandshake> serverHandshake = server.HandshakeAsync(cts.Token);

            SshVersionExchangeResult versions =
                await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);

            SshKeyExchangeRunner runner = new(
                clientTransport, SshAlgorithmSet.Default, new DangerousAcceptAnyHostKeyPolicy());
            SshKeyExchangeResult kex =
                await runner.RunAsync(versions, "test.invalid", 22, cancellationToken: cts.Token);

            TestSshServerHandshake handshake = await serverHandshake;

            // 服务端的认证侧与客户端**并发**跑：中间有多个「我发你答」的往返。
            TestAuthServer authServer = new(server.Transport, handshake.ExchangeHash, policy);
            Task<bool> serverAuth = authServer.RunAsync(cts.Token);

            // 与连接工厂一样带上会话证明：背后是 agent 的钥签名之前要拿它向 agent 声明会话。
            SshAuthenticator authenticator = authenticatorFactory is null
                ? new SshAuthenticator(clientTransport, userName, kex.SessionId) { SessionProof = kex.CreateSessionProof() }
                : authenticatorFactory(clientTransport, userName, kex.SessionId);

            SshAuthenticationResult? result = null;
            SshAuthenticationException? error = null;
            try
            {
                result = await authenticator.AuthenticateAsync(credentials, cts.Token);
            }
            catch (SshAuthenticationException ex)
            {
                error = ex;
            }

            // 客户端收工后关掉自己这一半，让服务端读到 EOF 退出循环；
            // 不关的话认证失败的用例会把服务端永远晾在 ReadPacketAsync 上。
            await clientTransport.DisposeAsync();
            bool serverSucceeded = await serverAuth;

            return new AuthRun(result, error, authServer.Observation, serverSucceeded);
        }
        finally
        {
            await clientTransport.DisposeAsync();
        }
    }

    // ------------------------------------------------------------ 密码

    [TestMethod]
    public async Task 密码正确时认证成功()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2")],
            new TestAuthPolicy { AcceptPassword = "hunter2" });

        Assert.AreEqual(SshProtocolNames.AuthPassword, run.Succeeded.Method);
        Assert.IsTrue(run.ServerSucceeded, "服务端也应当认为认证成功了");

        // 第一条永远是 none —— 那是问出「服务端接受哪些方法」的唯一途径。
        Assert.AreSequenceEqual(
            [SshProtocolNames.AuthNone, SshProtocolNames.AuthPassword], run.Observation.RequestedMethods);
    }

    [TestMethod]
    public async Task 密码错误时抛出带逐条记录的异常()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("wrong")],
            new TestAuthPolicy { AcceptPassword = "hunter2" });

        SshAuthenticationException error = run.Failed;
        Assert.AreEqual(SshFailureReason.AuthenticationMethodExhausted, error.Reason);
        Assert.IsFalse(error.PartialSuccessAchieved);

        // 记录里要能分清「试过并失败」与「没试」。这正是它存在的理由：
        // 没有它，界面上只剩一句「用户名或密码不正确」。
        Assert.Contains(
            a => a.Method == SshProtocolNames.AuthPassword
                                    && a.Outcome == SshAuthOutcome.Failure, error.Attempts,
            $"应当记下 password 失败过：{Environment.NewLine}{error.DescribeAttempts()}");
    }

    [TestMethod]
    public async Task 用户名被如实发送()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2")],
            new TestAuthPolicy { AcceptPassword = "hunter2", ExpectedUserName = "张三" },
            userName: "张三");

        Assert.AreEqual(SshProtocolNames.AuthPassword, run.Succeeded.Method);
        Assert.AreSequenceEqual(["张三"], run.Observation.UserNames, "用户名是 UTF-8 string，非 ASCII 必须能原样过去");
    }

    [TestMethod]
    public async Task 按需取值的密码回调只在真要用时才被调用()
    {
        int calls = 0;
        PasswordCredential credential = new(_ =>
        {
            calls++;
            return ValueTask.FromResult("hunter2");
        });

        AuthRun run = await RunAsync([credential], new TestAuthPolicy { AcceptPassword = "hunter2" });

        Assert.AreEqual(SshProtocolNames.AuthPassword, run.Succeeded.Method);
        Assert.AreEqual(1, calls, "密码只该在真正发出请求时解出来一次");
    }

    [TestMethod]
    public async Task 服务端要求改密码时给出可读的原因()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2")],
            new TestAuthPolicy { AcceptPassword = "hunter2", RequestPasswordChange = true });

        // 本库不实现改密码流程，但**必须说清楚为什么连不上** ——
        // 「直接断开且不说原因」是用户最难自救的一种失败。
        SshAuthenticationException error = run.Failed;
        Assert.AreEqual(SshFailureReason.PasswordExpired, error.Reason);
        Assert.Contains(
            a => a.Detail is not null && a.Detail.Contains("修改密码", StringComparison.Ordinal), error.Attempts,
            $"应当说明服务端要求改密码：{Environment.NewLine}{error.DescribeAttempts()}");
        Assert.IsEmpty(run.Observation.PasswordChanges, "没配取新密码的回调，不该发改密码请求");
    }

    /// <summary>配了取新密码的回调：带着旧密码与新密码发改密码请求，改成了就登录成功（RFC 4252 §8）。</summary>
    [TestMethod]
    public async Task 服务端要求改密码时按回调给的新密码改_改成了就登录成功()
    {
        List<SshPasswordChangeRequest> asked = [];
        PasswordCredential credential = new("hunter2")
        {
            NewPasswordProvider = (request, _) =>
            {
                asked.Add(request);
                return ValueTask.FromResult<string?>("correct horse");
            },
        };

        AuthRun run = await RunAsync([credential], new TestAuthPolicy { AcceptPassword = "hunter2", RequestPasswordChange = true });

        Assert.AreEqual(SshProtocolNames.AuthPassword, run.Succeeded.Method);
        Assert.AreSequenceEqual([("hunter2", "correct horse")], run.Observation.PasswordChanges);
        Assert.HasCount(1, asked);
        Assert.AreEqual("你的密码已过期。", asked[0].Prompt);
        Assert.AreEqual(1, asked[0].Attempt);
    }

    /// <summary>服务端嫌新密码不好会再回 PASSWD_CHANGEREQ：再问一次（提示是服务端的新说法），给对了就成。</summary>
    [TestMethod]
    public async Task 新密码不被接受时再问一次()
    {
        List<SshPasswordChangeRequest> asked = [];
        PasswordCredential credential = new("hunter2")
        {
            NewPasswordProvider = (request, _) =>
            {
                asked.Add(request);
                return ValueTask.FromResult<string?>(request.Attempt == 1 ? "123" : "correct horse");
            },
        };

        AuthRun run = await RunAsync(
            [credential], new TestAuthPolicy { AcceptPassword = "hunter2", RequestPasswordChange = true, RejectNewPasswords = 1 });

        Assert.AreEqual(SshProtocolNames.AuthPassword, run.Succeeded.Method);
        Assert.AreSequenceEqual([1, 2], [.. asked.Select(a => a.Attempt)]);
        Assert.AreEqual("新密码太简单。", asked[1].Prompt);
        Assert.AreSequenceEqual([("hunter2", "123"), ("hunter2", "correct horse")], run.Observation.PasswordChanges);
    }

    /// <summary>服务端一直不接受：最多问 <see cref="PasswordCredential.MaxNewPasswordAttempts"/> 次，然后报「要先改密码」。</summary>
    [TestMethod]
    public async Task 新密码一直不被接受时问够次数就停()
    {
        int calls = 0;
        PasswordCredential credential = new("hunter2")
        {
            NewPasswordProvider = (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<string?>($"try{calls}");
            },
        };

        AuthRun run = await RunAsync(
            [credential], new TestAuthPolicy { AcceptPassword = "hunter2", RequestPasswordChange = true, RejectNewPasswords = 100 });

        Assert.AreEqual(PasswordCredential.MaxNewPasswordAttempts, calls);
        Assert.HasCount(PasswordCredential.MaxNewPasswordAttempts, run.Observation.PasswordChanges);
        Assert.AreEqual(SshFailureReason.PasswordExpired, run.Failed.Reason);
        Assert.Contains(a => a.Detail?.Contains("新密码太简单", StringComparison.Ordinal) == true, run.Failed.Attempts);
    }

    /// <summary>回调返回 null（用户这次不改）：不发改密码请求，报「要先改密码」。</summary>
    [TestMethod]
    public async Task 回调不给新密码时不发改密码请求()
    {
        PasswordCredential credential = new("hunter2") { NewPasswordProvider = (_, _) => ValueTask.FromResult<string?>(null) };

        AuthRun run = await RunAsync([credential], new TestAuthPolicy { AcceptPassword = "hunter2", RequestPasswordChange = true });

        Assert.AreEqual(SshFailureReason.PasswordExpired, run.Failed.Reason);
        Assert.IsEmpty(run.Observation.PasswordChanges);
    }

    /// <summary>改密码请求被回 FAILURE（不支持改密码，或旧密码不对）：说清楚没改成。</summary>
    [TestMethod]
    public async Task 服务端不支持改密码时说清楚没改成()
    {
        PasswordCredential credential = new("hunter2") { NewPasswordProvider = (_, _) => ValueTask.FromResult<string?>("correct horse") };

        AuthRun run = await RunAsync(
            [credential], new TestAuthPolicy { AcceptPassword = "hunter2", RequestPasswordChange = true, PasswordChangeUnsupported = true });

        Assert.AreEqual(SshFailureReason.PasswordExpired, run.Failed.Reason);
        Assert.HasCount(1, run.Observation.PasswordChanges);
        Assert.Contains(a => a.Detail?.Contains("没有改成", StringComparison.Ordinal) == true, run.Failed.Attempts,
            run.Failed.DescribeAttempts());
    }

    // ------------------------------------------------------------ 公钥

    [TestMethod]
    public async Task 公钥认证成功且服务端验签通过()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
            });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);

        // 签名输入拼错了就走不到这里 —— 服务端是**独立**重算被签名数据的。
        Assert.IsTrue(run.Observation.AllSignaturesValid, "服务端必须能验证客户端的签名");
        Assert.AreEqual(1, run.Observation.PublicKeySignedCount);
    }

    [TestMethod]
    public async Task 本地私钥走一段式不做多余的探测往返()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
            });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreEqual(0, run.Observation.PublicKeyProbeCount,
            "本地签名很便宜，不值得为它多花一个往返（velashell-docs/zh/ssh/spec/04 §4.1）");
    }

    [TestMethod]
    public async Task 外部签名器先探测再签名()
    {
        using var inner = InMemorySshSigner.GenerateEd25519();
        ExpensiveSigner signer = new(inner);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [inner.PublicKey.Blob.ToArray()],
            });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreEqual(1, run.Observation.PublicKeyProbeCount,
            "外部签名要先问「你认这把钥吗」——不能为一把服务端不认的钥让用户去按硬件键");
        Assert.AreEqual(1, signer.SignCalls, "探测被接受之后才真的签一次");
    }

    [TestMethod]
    public async Task 外部签名器在服务端不认这把钥时一次都不签()
    {
        using var inner = InMemorySshSigner.GenerateEd25519();
        ExpensiveSigner signer = new(inner);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [],   // 一把都不认
            });

        _ = run.Failed;
        Assert.AreEqual(1, run.Observation.PublicKeyProbeCount);
        Assert.AreEqual(0, signer.SignCalls,
            "两段式存在的全部意义就是这一条：不认就不签，用户不用白按一次硬件键");
    }

    /// <summary>
    /// 〔AU-E5〕服务端对探测直接回 SUCCESS（不合规，个别实现会这样）：认证就此完成，不再签名、也不再试下一条凭据。
    /// 曾经记成「不接受这把公钥」接着发下一条请求 —— 成功之后的请求服务端一律忽略（RFC 4252 §5.1），一直等到认证超时。
    /// </summary>
    [TestMethod]
    public async Task 探测时服务端直接回SUCCESS就算认证完成()
    {
        using var inner = InMemorySshSigner.GenerateEd25519();
        ExpensiveSigner signer = new(inner);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer), new PasswordCredential("hunter2")],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [inner.PublicKey.Blob.ToArray()],
                AcceptPassword = "hunter2",
                SucceedOnPublicKeyProbe = true,
            });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreEqual(0, signer.SignCalls, "认证已经完成，不该再签");
        Assert.DoesNotContain(SshProtocolNames.AuthPassword, run.Observation.RequestedMethods, "认证已经完成，不该再试下一条凭据");
    }

    /// <summary>〔AU-E5〕PK_OK 回显的公钥不是我们问的那一把：协议错误 —— 签下去的会是服务端没认过的钥。</summary>
    [TestMethod]
    public async Task PK_OK回显的公钥不是我们问的那一把时报协议错误()
    {
        using var inner = InMemorySshSigner.GenerateEd25519();
        using var other = InMemorySshSigner.GenerateEd25519();

        SshProtocolException error = await Assert.ThrowsExactlyAsync<SshProtocolException>(() => RunAsync(
            [new PublicKeyCredential(new ExpensiveSigner(inner))],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [inner.PublicKey.Blob.ToArray()],
                PublicKeyOkEchoBlob = other.PublicKey.Blob.ToArray(),
            }));

        Assert.Contains("PK_OK", error.Message);
    }

    /// <summary>PK_OK 回显的算法名是这把钥自己的类型名（而不是请求里的签名算法）：说的是同一把钥，照常签。</summary>
    [TestMethod]
    public async Task PK_OK回显钥的类型名时照常签()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var inner = InMemorySshSigner.FromRsa(rsa);
        ExpensiveSigner signer = new(inner);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [inner.PublicKey.Blob.ToArray()],
                ServerSignatureAlgorithms = [SshAlgorithmNames.RsaSha256],   // 我们请求的是 rsa-sha2-256
                PublicKeyOkEchoAlgorithm = SshAlgorithmNames.SshRsa,         // 回显的却是钥的类型名
            });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreEqual(1, signer.SignCalls);
        Assert.AreSequenceEqual([SshAlgorithmNames.RsaSha256], run.Observation.PublicKeySignatureAlgorithms, "签名算法照我们选的");
    }

    [TestMethod]
    public async Task 服务端不认这把公钥时如实记录而不是说密码错()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer, "~/.ssh/id_ed25519")],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [],
            });

        SshAuthenticationException error = run.Failed;
        Assert.Contains(
            a => a.CredentialLabel == "~/.ssh/id_ed25519"
                                    && a.Outcome == SshAuthOutcome.Failure, error.Attempts,
            $"标签要原样进记录，好让用户知道是哪一把钥：{Environment.NewLine}{error.DescribeAttempts()}");
    }

    [TestMethod]
    public async Task 凭据取不到材料时记成跳过并继续试下一条()
    {
        using var good = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [
                new PublicKeyCredential(new BrokenSigner(good.PublicKey), "坏掉的私钥文件"),
                new PublicKeyCredential(good, "好的私钥"),
            ],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [good.PublicKey.Blob.ToArray()],
            });

        // 一条凭据坏了不该打断整条链 —— 后面还有能用的。
        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.Contains(
            a => a.CredentialLabel == "坏掉的私钥文件"
                                            && a.Outcome == SshAuthOutcome.SkippedNoMaterial, run.Succeeded.Attempts,
            "「私钥读不出来」与「服务端不认这把钥」是两件事，记录里必须分得开");
    }

    [TestMethod]
    public async Task Agent里的钥签名之前先向agent声明会话()
    {
        // 〔spec/04 §4.1、spec/07 §7.4〕不声明的话 agent 执行不了 ssh-add -h 给钥加的目的地约束。
        TestAgent agent = new();
        using var rejected = InMemorySshSigner.GenerateEd25519();
        using var accepted = InMemorySshSigner.GenerateEd25519();
        agent.Add(rejected, "服务端不认的");
        agent.Add(accepted, "id_ed25519");

        (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task serving = agent.ServeAsync(theirs, cts.Token);
        await using (var client = SshAgentClient.FromStream(ours))
        {
            AuthRun run = await RunAsync(
                await client.GetCredentialsAsync(cts.Token),
                new TestAuthPolicy
                {
                    RequiredMethods = [SshProtocolNames.AuthPublicKey],
                    AcceptedPublicKeys = [accepted.PublicKey.Blob.ToArray()],
                });

            Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        }

        TestSessionDeclaration declaration = Assert.ContainsSingle(agent.Declarations, "探测没过的钥不签，声明只在第一次签名之前发一次");
        Assert.IsFalse(declaration.IsForwarding, "认证用的连接 is_forwarding 为假");
        Assert.IsTrue(declaration.SignatureVerified, "签名必须是首次交换里服务端对会话标识的那一个");

        await cts.CancelAsync();
        await serving;
    }

    [TestMethod]
    public async Task Agent不支持会话声明时照常签名认证()
    {
        TestAgent agent = new() { DeclarationReply = TestDeclarationReply.Reject };
        using var key = InMemorySshSigner.GenerateEd25519();
        agent.Add(key, "id_ed25519");

        (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task serving = agent.ServeAsync(theirs, cts.Token);
        await using (var client = SshAgentClient.FromStream(ours))
        {
            AuthRun run = await RunAsync(
                await client.GetCredentialsAsync(cts.Token),
                new TestAuthPolicy
                {
                    RequiredMethods = [SshProtocolNames.AuthPublicKey],
                    AcceptedPublicKeys = [key.PublicKey.Blob.ToArray()],
                });

            Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method, "声明被拒不该让认证失败");
        }

        Assert.AreEqual(1, agent.SignRequests);
        await cts.CancelAsync();
        await serving;
    }

    [TestMethod]
    public async Task Agent拒签时记成跳过并继续试下一条()
    {
        using var good = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [
                new PublicKeyCredential(new RefusingAgentSigner(good.PublicKey), "agent: id_ed25519"),
                new PublicKeyCredential(good, "~/.ssh/id_ed25519"),
            ],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [good.PublicKey.Blob.ToArray()],
            });

        // SshAgentException 是 SshException —— 曾经它会直接打断整条凭据链。
        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.Contains(
            a => a.CredentialLabel == "agent: id_ed25519" && a.Outcome == SshAuthOutcome.SkippedNoMaterial,
            run.Succeeded.Attempts);
        Assert.AreEqual(1, run.Observation.PublicKeyProbeCount, "拒签发生在探测通过之后");
    }

    [TestMethod]
    public async Task 请求在途时回调抛出的异常照实抛出而不是当成跳过()
    {
        // 横幅回调在「密码请求已发出、SUCCESS 还没读」时抛异常。当成「跳过这条凭据」的话，
        // 客户端会报「所有方法都失败」—— 而服务端其实已经认证通过了。
        // 认证器把调用方的异常裹一层往外送（不让建连路上按类型归类的 catch 认出它），ConnectAsync 的出口再原样还原
        // （ConnectionTests.横幅回调自己抛的IO错原样交还）。
        SshCallbackFaultException fault = await Assert.ThrowsExactlyAsync<SshCallbackFaultException>(
            () => RunAsync(
                [new PasswordCredential("hunter2")],
                new TestAuthPolicy { AcceptPassword = "hunter2", BannersBeforeSuccess = ["维护通知"] },
                authenticatorFactory: static (transport, user, sessionId) => new SshAuthenticator(transport, user, sessionId)
                {
                    BannerHandler = static (text, _) => text == "维护通知"
                        ? throw new InvalidOperationException("界面已经关了")
                        : ValueTask.CompletedTask,
                }));

        InvalidOperationException error = Assert.IsInstanceOfType<InvalidOperationException>(fault.InnerException);
        Assert.AreEqual("界面已经关了", error.Message);
    }

    [TestMethod]
    public async Task 服务端宣告的server_sig_algs决定RSA用哪种签名算法()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var signer = InMemorySshSigner.FromRsa(rsa);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                // 服务端只肯认 SHA-256 那一种。
                ServerSignatureAlgorithms = [SshAlgorithmNames.RsaSha256],
            });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreSequenceEqual(
            [SshAlgorithmNames.RsaSha256], run.Observation.PublicKeySignatureAlgorithms, "我们自己更偏好 SHA-512，但服务端说只认 SHA-256 —— 就得听它的（RFC 8308）");
        Assert.Contains(
SshAlgorithmNames.RsaSha256, [.. run.Succeeded.ServerSignatureAlgorithms]);
    }

    /// <summary>
    /// 〔RFC 8308 §2.4、spec/04 §7.1〕EXT_INFO 只有两个合法位置；认证中途（后面跟的不是 SUCCESS）收到就是协议错误。
    /// 曾经任何位置都照收，后到的整体覆盖 server-sig-algs —— 认证中途就能改掉 RSA 签名算法的选择。
    /// </summary>
    [TestMethod]
    public async Task 认证中途的EXT_INFO是协议错误()
    {
        await Assert.ThrowsExactlyAsync<SshProtocolException>(() => RunAsync(
            [new PasswordCredential("wrong"), new PasswordCredential("hunter2")],
            new TestAuthPolicy
            {
                AcceptPassword = "hunter2",
                ExtInfoBeforeFirstFailure = [SshAlgorithmNames.RsaSha256],
            }));
    }

    /// <summary>紧挨着 USERAUTH_SUCCESS 的那个 EXT_INFO 是合法的第二个位置。</summary>
    [TestMethod]
    public async Task 紧挨着SUCCESS的EXT_INFO照收()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2")],
            new TestAuthPolicy
            {
                AcceptPassword = "hunter2",
                ServerSignatureAlgorithms = [SshAlgorithmNames.RsaSha512],
                ExtInfoBeforeSuccess = [SshAlgorithmNames.RsaSha256],
            });

        Assert.AreEqual(SshProtocolNames.AuthPassword, run.Succeeded.Method);
        Assert.AreSequenceEqual([SshAlgorithmNames.RsaSha256], [.. run.Succeeded.ServerSignatureAlgorithms]);
    }

    [TestMethod]
    public async Task 没有server_sig_algs时用我们自己的第一偏好()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var signer = InMemorySshSigner.FromRsa(rsa);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                ServerSignatureAlgorithms = null,
            });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreSequenceEqual(
            [SshAlgorithmNames.RsaSha512], run.Observation.PublicKeySignatureAlgorithms);
    }

    [TestMethod]
    public async Task 默认不会降级到SHA1的ssh_rsa()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var signer = InMemorySshSigner.FromRsa(rsa);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                // 服务端（谎）称只认 SHA-1。
                ServerSignatureAlgorithms = [SshAlgorithmNames.SshRsa],
            });

        // 无条件跟着服务端降级，就把降级攻击的收益还回去了。
        Assert.DoesNotContain(
SshAlgorithmNames.SshRsa, run.Observation.PublicKeySignatureAlgorithms, "默认不接受 SHA-1 的 ssh-rsa —— 需要它的人得显式打开 AllowSha1RsaSignatures");
    }

    [TestMethod]
    public async Task 显式打开之后才会使用SHA1的ssh_rsa()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var signer = InMemorySshSigner.FromRsa(rsa);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                ServerSignatureAlgorithms = [SshAlgorithmNames.SshRsa],
            },
            authenticatorFactory: static (t, u, s) => new SshAuthenticator(t, u, s)
            {
                AllowSha1RsaSignatures = true,
            });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreSequenceEqual(
            [SshAlgorithmNames.SshRsa], run.Observation.PublicKeySignatureAlgorithms, "开关打开之后才肯用它 —— 为的是还能连上停在 OpenSSH 7.x 的老机器");

        // 没有降级、直接挑中 SHA-1：尝试记录里同样写明（规格 04 §4.4）。
        SshAuthAttempt attempt = run.Succeeded.Attempts.Single(a => a.Method == SshProtocolNames.AuthPublicKey);
        Assert.AreEqual(SshAlgorithmNames.SshRsa, attempt.SignatureAlgorithm);
        Assert.Contains("SHA-1", attempt.Detail ?? "");
    }

    /// <summary>
    /// 不发 server-sig-algs、只认 ssh-rsa 的老服务器：先试 SHA-2，被拒后降级重试一次 SHA-1。
    /// 曾经不重试 —— 「允许 SHA-1」只在 server-sig-algs 列了 ssh-rsa 时起作用，
    /// 而最需要这个开关的老服务器恰恰不发 server-sig-algs。
    /// </summary>
    [TestMethod]
    public async Task 没有server_sig_algs时SHA2被拒就降级重试一次SHA1()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var signer = InMemorySshSigner.FromRsa(rsa);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                AcceptedPublicKeyAlgorithms = [SshAlgorithmNames.SshRsa],
            },
            authenticatorFactory: static (t, u, s) => new SshAuthenticator(t, u, s)
            {
                AllowSha1RsaSignatures = true,
            });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreSequenceEqual(
            [SshAlgorithmNames.RsaSha512, SshAlgorithmNames.SshRsa], run.Observation.PublicKeySignatureAlgorithms);

        SshAuthAttempt attempt = run.Succeeded.Attempts.Single(a => a.Method == SshProtocolNames.AuthPublicKey);
        Assert.AreEqual(SshAlgorithmNames.SshRsa, attempt.SignatureAlgorithm, "记的是重试那一次的算法");
        Assert.Contains("降级", attempt.Detail ?? "");
    }

    /// <summary>每一步 publickey 都记下用的签名算法；不是 SHA-1 时不附说明。</summary>
    [TestMethod]
    public async Task 尝试记录里写着用的签名算法()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [new PasswordCredential("wrong"), new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                AcceptPassword = "hunter2",
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
            });

        SshAuthAttempt publicKey = run.Succeeded.Attempts.Single(a => a.Method == SshProtocolNames.AuthPublicKey);
        Assert.AreEqual(SshAlgorithmNames.SshEd25519, publicKey.SignatureAlgorithm);
        Assert.IsNull(publicKey.Detail);
        Assert.Contains($"（{SshAlgorithmNames.SshEd25519}）", publicKey.ToString());
        Assert.IsNull(run.Succeeded.Attempts.Single(a => a.Method == SshProtocolNames.AuthPassword).SignatureAlgorithm);
    }

    [TestMethod]
    public async Task 不允许SHA1时不降级()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var signer = InMemorySshSigner.FromRsa(rsa);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                AcceptedPublicKeyAlgorithms = [SshAlgorithmNames.SshRsa],
            });

        Assert.IsNotNull(run.Failed);
        Assert.AreSequenceEqual([SshAlgorithmNames.RsaSha512], run.Observation.PublicKeySignatureAlgorithms,
            "默认不降级：无条件降级会把降级攻击的收益还回去");
    }

    // ------------------------------------------------------------ 键盘交互

    [TestMethod]
    public async Task 键盘交互单轮问答成功()
    {
        List<SshKeyboardChallenge> seen = [];

        AuthRun run = await RunAsync(
            [
                new KeyboardInteractiveCredential((challenge, _) =>
                {
                    seen.Add(challenge);
                    return ValueTask.FromResult<IReadOnlyList<string>>(["123456"]);
                }),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshProtocolNames.AuthKeyboardInteractive],
                RequiredMethods = [SshProtocolNames.AuthKeyboardInteractive],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Name = "两步验证",
                        Instruction = "请输入手机上的动态码。",
                        Prompts = [("Verification code: ", false)],
                        ExpectedAnswers = ["123456"],
                    },
                ],
            });

        Assert.AreEqual(SshProtocolNames.AuthKeyboardInteractive, run.Succeeded.Method);
        Assert.HasCount(1, seen);
        Assert.AreEqual("请输入手机上的动态码。", seen[0].Instruction);
        Assert.HasCount(1, seen[0].Prompts);
        Assert.IsFalse(seen[0].Prompts[0].Echo, "不回显的提示必须以密码方式采集");
        Assert.IsFalse(seen[0].IsInformationalOnly);
    }

    /// <summary>〔spec/04 §6.4〕一轮里每个字符串（名字、说明、提示）上限 4 KiB，超了是协议错误。曾经名字与说明放到了 64 KiB。</summary>
    [TestMethod]
    public async Task 键盘交互的说明超过4KiB是协议错误()
    {
        await Assert.ThrowsExactlyAsync<SshProtocolException>(() => RunAsync(
            [new KeyboardInteractiveCredential((_, _) => ValueTask.FromResult<IReadOnlyList<string>>(["123456"]))],
            new TestAuthPolicy
            {
                OfferedMethods = [SshProtocolNames.AuthKeyboardInteractive],
                RequiredMethods = [SshProtocolNames.AuthKeyboardInteractive],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Instruction = new string('x', 4 * 1024 + 1),
                        Prompts = [("Verification code: ", false)],
                        ExpectedAnswers = ["123456"],
                    },
                ],
            }));
    }

    [TestMethod]
    public async Task 键盘交互支持多轮()
    {
        List<SshKeyboardChallenge> seen = [];

        AuthRun run = await RunAsync(
            [
                new KeyboardInteractiveCredential((challenge, _) =>
                {
                    seen.Add(challenge);
                    return ValueTask.FromResult<IReadOnlyList<string>>(
                        seen.Count == 1 ? ["hunter2"] : ["123456"]);
                }),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshProtocolNames.AuthKeyboardInteractive],
                RequiredMethods = [SshProtocolNames.AuthKeyboardInteractive],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Prompts = [("Password: ", false)],
                        ExpectedAnswers = ["hunter2"],
                    },
                    new TestKeyboardRound
                    {
                        Prompts = [("Verification code: ", false)],
                        ExpectedAnswers = ["123456"],
                    },
                ],
            });

        Assert.AreEqual(SshProtocolNames.AuthKeyboardInteractive, run.Succeeded.Method);
        Assert.HasCount(2, seen, "服务端可以来回问任意多轮");
    }

    [TestMethod]
    public async Task 纯展示轮仍然通知回调但不要求输入()
    {
        List<SshKeyboardChallenge> seen = [];

        AuthRun run = await RunAsync(
            [
                new KeyboardInteractiveCredential((challenge, _) =>
                {
                    seen.Add(challenge);
                    return ValueTask.FromResult<IReadOnlyList<string>>(
                        challenge.IsInformationalOnly ? [] : ["123456"]);
                }),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshProtocolNames.AuthKeyboardInteractive],
                RequiredMethods = [SshProtocolNames.AuthKeyboardInteractive],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Instruction = "请按下硬件令牌上的按钮。",
                        Prompts = [],
                        ExpectedAnswers = [],
                    },
                    new TestKeyboardRound
                    {
                        Prompts = [("Verification code: ", false)],
                        ExpectedAnswers = ["123456"],
                    },
                ],
            });

        Assert.AreEqual(SshProtocolNames.AuthKeyboardInteractive, run.Succeeded.Method);
        Assert.HasCount(2, seen);

        // 这一轮没有提示，但 Instruction 必须送到 —— 否则用户对着没反应的界面干等，
        // 而服务端正等他去按硬件令牌。
        Assert.IsTrue(seen[0].IsInformationalOnly);
        Assert.AreEqual("请按下硬件令牌上的按钮。", seen[0].Instruction);
        Assert.AreSequenceEqual([], [.. run.Observation.KeyboardAnswers[0]]);
    }

    [TestMethod]
    public async Task 密码凭据在服务端只开键盘交互时自动改走那条路()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2")],
            new TestAuthPolicy
            {
                // 服务端根本不接受 password。
                OfferedMethods = [SshProtocolNames.AuthKeyboardInteractive],
                RequiredMethods = [SshProtocolNames.AuthKeyboardInteractive],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Prompts = [("Password: ", false)],
                        ExpectedAnswers = ["hunter2"],
                    },
                ],
            });

        // 大量服务器只开 keyboard-interactive，而它唯一的提示就是「Password:」。
        // 不做这一步，用户填了密码却连不上，也说不出为什么。
        Assert.AreEqual(SshProtocolNames.AuthKeyboardInteractive, run.Succeeded.Method);
        Assert.Contains(
            a => a.CredentialLabel.Contains("keyboard-interactive", StringComparison.Ordinal), run.Succeeded.Attempts,
            "记录里要看得出走的是桥接那条路");
    }

    /// <summary>
    /// PAM 两步验证：「Password:」一轮、「Verification code:」再一轮，两轮都是单条不回显。
    /// 兼答只答第一轮；第二轮回空串 —— 曾经把密码也填了进去，pam_radius、Duo 一类模块会把它转发到第三方。
    /// </summary>
    [TestMethod]
    public async Task 兼答在同一次键盘交互里只答一次密码()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2")],
            new TestAuthPolicy
            {
                OfferedMethods = [SshProtocolNames.AuthKeyboardInteractive],
                RequiredMethods = [SshProtocolNames.AuthKeyboardInteractive],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Prompts = [("Password: ", false)],
                        ExpectedAnswers = ["hunter2"],
                    },
                    new TestKeyboardRound
                    {
                        Prompts = [("Verification code: ", false)],
                        ExpectedAnswers = ["123456"],
                    },
                ],
            });

        Assert.IsNotNull(run.Failed, "没配动态码，这一次本来就该失败");
        Assert.HasCount(2, run.Observation.KeyboardAnswers);
        Assert.AreSequenceEqual(["hunter2"], [.. run.Observation.KeyboardAnswers[0]]);
        Assert.AreSequenceEqual([""], [.. run.Observation.KeyboardAnswers[1]], "密码被填进了动态码那一轮");
    }

    [TestMethod]
    public async Task 关掉开关之后密码不再自动填进键盘交互()
    {
        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2") { CanAnswerKeyboardInteractive = false }],
            new TestAuthPolicy
            {
                OfferedMethods = [SshProtocolNames.AuthKeyboardInteractive],
                RequiredMethods = [SshProtocolNames.AuthKeyboardInteractive],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Prompts = [("Password: ", false)],
                        ExpectedAnswers = ["hunter2"],
                    },
                ],
            });

        // 真正的 2FA 场景要关掉它：第一条提示可能就是动态码，
        // 自动填密码只会白白消耗一次尝试。
        SshAuthenticationException error = run.Failed;
        Assert.AreEqual(SshFailureReason.TwoFactorRequired, error.Reason);
        Assert.DoesNotContain(SshProtocolNames.AuthKeyboardInteractive, run.Observation.RequestedMethods,
            "关掉之后连试都不该试");
    }

    // ------------------------------------------------------------ 部分成功（2FA）

    [TestMethod]
    public async Task 公钥加动态码的两步认证能走通()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();
        List<SshKeyboardChallenge> seen = [];

        AuthRun run = await RunAsync(
            [
                new PublicKeyCredential(signer, "~/.ssh/id_ed25519"),
                new KeyboardInteractiveCredential((challenge, _) =>
                {
                    seen.Add(challenge);
                    return ValueTask.FromResult<IReadOnlyList<string>>(["123456"]);
                }),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshProtocolNames.AuthPublicKey, SshProtocolNames.AuthKeyboardInteractive],
                // **两个都要过。**第一个过了之后服务端回
                // FAILURE + partial_success = true —— 这就是 SSH 里 2FA 的全部表达方式。
                RequiredMethods = [SshProtocolNames.AuthPublicKey, SshProtocolNames.AuthKeyboardInteractive],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                KeyboardRounds =
                [
                    new TestKeyboardRound
                    {
                        Instruction = "请输入动态码。",
                        Prompts = [("Verification code: ", false)],
                        ExpectedAnswers = ["123456"],
                    },
                ],
            });

        Assert.AreEqual(SshProtocolNames.AuthKeyboardInteractive, run.Succeeded.Method);
        Assert.IsTrue(run.ServerSucceeded);

        // 把 partial_success 当失败处理的库会卡在这里：公钥明明过了，
        // 却被记成失败、跳过后续方法，最后报「认证失败」。
        Assert.AreSequenceEqual(
            [SshProtocolNames.AuthPublicKey, SshProtocolNames.AuthKeyboardInteractive], run.Observation.PassedMethods);

        Assert.Contains(
            a => a.Outcome == SshAuthOutcome.PartialSuccess, run.Succeeded.Attempts,
            $"公钥那一步要记成部分成功而不是失败：{Environment.NewLine}" +
            string.Join(Environment.NewLine, run.Succeeded.Attempts));
        Assert.HasCount(1, seen);
    }

    [TestMethod]
    public async Task 部分成功之后回头再试当时因方法不被接受而跳过的凭据()
    {
        // AuthenticationMethods publickey,password：服务端一开始只报 publickey，
        // 排在前面的口令凭据于是被跳过；公钥那一步过了之后服务端才开放 password ——
        // 不回头再扫一遍的话，认证以「凭据试完了」失败。
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [
                new PasswordCredential("hunter2") { CanAnswerKeyboardInteractive = false },
                new PublicKeyCredential(signer),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshProtocolNames.AuthPublicKey],
                RequiredMethods = [SshProtocolNames.AuthPublicKey, SshProtocolNames.AuthPassword],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
                AcceptPassword = "hunter2",
            });

        Assert.AreEqual(SshProtocolNames.AuthPassword, run.Succeeded.Method);
        Assert.AreSequenceEqual(
            [SshProtocolNames.AuthPublicKey, SshProtocolNames.AuthPassword], run.Observation.PassedMethods);
    }

    [TestMethod]
    public async Task 第四把钥才对时也能登上()
    {
        // 每把钥是不同的凭据，不是对同一个秘密的重试 —— 不能套「同一方法失败三次就不再试」。
        // agent 里有五把钥、对的是第四把，是很常见的情形。
        InMemorySshSigner[] keys = [.. Enumerable.Range(0, 5).Select(_ => InMemorySshSigner.GenerateEd25519())];
        try
        {
            AuthRun run = await RunAsync(
                [.. keys.Select(static k => new PublicKeyCredential(k))],
                new TestAuthPolicy
                {
                    RequiredMethods = [SshProtocolNames.AuthPublicKey],
                    AcceptedPublicKeys = [keys[3].PublicKey.Blob.ToArray()],
                });

            Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
            Assert.AreEqual(4, run.Observation.PublicKeySignedCount, "前三把各试一次，第四把成功，第五把不必试");
        }
        finally
        {
            foreach (InMemorySshSigner key in keys)
            {
                key.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task 第一步过了但第二步没配凭据时说得出卡在哪()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],   // 只配了公钥，没配动态码
            new TestAuthPolicy
            {
                OfferedMethods = [SshProtocolNames.AuthPublicKey, SshProtocolNames.AuthKeyboardInteractive],
                RequiredMethods = [SshProtocolNames.AuthPublicKey, SshProtocolNames.AuthKeyboardInteractive],
                AcceptedPublicKeys = [signer.PublicKey.Blob.ToArray()],
            });

        SshAuthenticationException error = run.Failed;

        // 「第一步过了，卡在第二步」与「一步都没过」对用户是完全不同的信息。
        Assert.IsTrue(error.PartialSuccessAchieved, "至少有一步是通过了的");
        Assert.AreEqual(SshFailureReason.TwoFactorRequired, error.Reason);
        Assert.Contains("键盘交互", error.Message,
            "界面要能说成「这台机器需要动态码」，而不是「用户名或密码不正确」");
    }

    // ------------------------------------------------------------ 方法调度

    [TestMethod]
    public async Task 服务端不接受的方法被跳过并记录原因()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [
                new PublicKeyCredential(signer, "~/.ssh/id_ed25519"),
                new PasswordCredential("hunter2"),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshProtocolNames.AuthPassword],   // 不接受公钥
                RequiredMethods = [SshProtocolNames.AuthPassword],
                AcceptPassword = "hunter2",
            });

        Assert.AreEqual(SshProtocolNames.AuthPassword, run.Succeeded.Method);

        // 公钥那条连发都不该发 —— 服务端已经说了它不接受。
        Assert.DoesNotContain(SshProtocolNames.AuthPublicKey, run.Observation.RequestedMethods);
        Assert.Contains(
            a => a.CredentialLabel == "~/.ssh/id_ed25519"
                                            && a.Outcome == SshAuthOutcome.SkippedNotOffered, run.Succeeded.Attempts);
    }

    [TestMethod]
    public async Task 按使用者给出的顺序依次尝试()
    {
        using var first = InMemorySshSigner.GenerateEd25519();
        using var second = InMemorySshSigner.GenerateEd25519();

        AuthRun run = await RunAsync(
            [
                new PublicKeyCredential(first, "第一把"),
                new PublicKeyCredential(second, "第二把"),
            ],
            new TestAuthPolicy
            {
                OfferedMethods = [SshProtocolNames.AuthPublicKey],
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [second.PublicKey.Blob.ToArray()],   // 只认第二把
            });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);

        string[] labels = [.. run.Succeeded.Attempts.Select(a => a.CredentialLabel)];
        int firstIndex = Array.IndexOf(labels, "第一把");
        int secondIndex = Array.IndexOf(labels, "第二把");
        Assert.IsTrue(firstIndex >= 0 && secondIndex > firstIndex,
            $"顺序由使用者决定，不能由库自作主张地重排：{string.Join(" → ", labels)}");
    }

    [TestMethod]
    public async Task 服务端配了无认证时none直接成功()
    {
        AuthRun run = await RunAsync(
            [],
            new TestAuthPolicy
            {
                OfferedMethods = [],
                RequiredMethods = [SshProtocolNames.AuthNone],
            });

        // none 通常失败，但**不能假设它一定失败**。
        Assert.AreEqual(SshProtocolNames.AuthNone, run.Succeeded.Method);
        Assert.IsTrue(run.ServerSucceeded);
    }

    [TestMethod]
    public async Task 一条凭据都没配时异常里仍有服务端可用方法()
    {
        AuthRun run = await RunAsync(
            [],
            new TestAuthPolicy
            {
                OfferedMethods = [SshProtocolNames.AuthPassword, SshProtocolNames.AuthPublicKey],
                AcceptPassword = "hunter2",
            });

        SshAuthenticationException error = run.Failed;
        Assert.AreSequenceEqual(
            [SshProtocolNames.AuthPassword, SshProtocolNames.AuthPublicKey], [.. error.ServerOffered], SequenceOrder.InAnyOrder, "none 探测拿回来的方法列表要留在异常里，这是用户唯一能看到的线索");
    }

    // ------------------------------------------------------------ 横幅

    [TestMethod]
    public async Task 横幅被送到回调并留在结果里()
    {
        List<string> received = [];

        AuthRun run = await RunAsync(
            [new PasswordCredential("hunter2")],
            new TestAuthPolicy
            {
                AcceptPassword = "hunter2",
                Banners = ["未经授权的访问将被记录。", "第二条横幅。"],
            },
            authenticatorFactory: (t, u, s) => new SshAuthenticator(t, u, s)
            {
                BannerHandler = (text, _) =>
                {
                    received.Add(text);
                    return ValueTask.CompletedTask;
                },
            });

        Assert.AreEqual(SshProtocolNames.AuthPassword, run.Succeeded.Method);
        Assert.AreSequenceEqual(["未经授权的访问将被记录。", "第二条横幅。"], received);
        Assert.AreSequenceEqual(received, [.. run.Succeeded.Banner]);
    }

    /// <summary>
    /// 服务端拒绝时不回 FAILURE，而是先发横幅说明原因、再断开：报的是认证期间对端断开（带着原因码），
    /// 横幅在断开之前照样交给回调 —— 使用方只有靠这段横幅才知道为什么被拒。
    /// </summary>
    [TestMethod]
    public async Task 横幅说明后断开时报对端断开且横幅先送到回调()
    {
        List<string> received = [];

        SshConnectionClosedException error = await Assert.ThrowsExactlyAsync<SshConnectionClosedException>(() => RunAsync(
            [],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPassword],
                RejectWithBannerAndDisconnect = "策略不允许以 joe 登录。",
            },
            authenticatorFactory: (t, u, s) => new SshAuthenticator(t, u, s)
            {
                BannerHandler = (text, _) =>
                {
                    received.Add(text);
                    return ValueTask.CompletedTask;
                },
            }));

        Assert.AreEqual(SshFailureReason.Disconnected, error.Reason);
        Assert.AreEqual(SshPhase.Authenticating, error.Phase);
        Assert.AreEqual(SshDisconnectReason.NoMoreAuthMethodsAvailable, error.DisconnectReason);
        Assert.AreSequenceEqual(["策略不允许以 joe 登录。"], received);
    }

    /// <summary>
    /// 证书被拒：按本机时钟已经过期这件事写进尝试记录（规格 04 §4.5）—— 不在本地拦，照样发出去由服务端判断。
    /// 登录用户在 principals 里时不附 principals 的说法。
    /// </summary>
    [TestMethod]
    public async Task 证书被拒时写明按本机时钟已过期()
    {
        SshCertificateSigner signer = await LoadCertificateSignerAsync("cert-expired");

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)], new TestAuthPolicy { RequiredMethods = [SshProtocolNames.AuthPublicKey] });

        Assert.AreEqual(1, run.Observation.PublicKeySignedCount, "过期的证书照样发出去：判断是服务端的事");
        string detail = run.Failed.Attempts.Single(a => a.Method == SshProtocolNames.AuthPublicKey).Detail ?? "";
        Assert.Contains("按本机时钟已于 2020-01-02", detail);
        Assert.Contains("expired@velashell", detail);
        Assert.DoesNotContain("不含登录用户", detail);
    }

    /// <summary>证书被拒、登录用户不在 principals 里：写明证书签给了谁。agent 里的证书身份（签名器只交得出 blob）一样。</summary>
    [TestMethod]
    [DataRow(false, DisplayName = "证书签名器")]
    [DataRow(true, DisplayName = "agent 那样只交得出 blob 的签名器")]
    public async Task 证书被拒时写明登录用户不在principals里(bool opaque)
    {
        SshCertificateSigner certificateSigner = await LoadCertificateSignerAsync("cert-ed25519");
        ISshSigner signer = opaque ? new ExpensiveSigner(certificateSigner) : certificateSigner;

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy { RequiredMethods = [SshProtocolNames.AuthPublicKey] },
            userName: "alice");

        string detail = run.Failed.Attempts.Single(a => a.Method == SshProtocolNames.AuthPublicKey).Detail ?? "";
        Assert.Contains("证书签给的用户是 joe、deploy，不含登录用户 alice", detail);
    }

    // ------------------------------------------------------------ 测试替身

    /// <summary>假装签名很贵的签名器 —— 用来验证两段式。</summary>
    private sealed class ExpensiveSigner(ISshSigner inner) : ISshSigner
    {
        public SshPublicKey PublicKey => inner.PublicKey;

        public IReadOnlyList<string> SignatureAlgorithms => inner.SignatureAlgorithms;

        public bool IsLocalAndCheap => false;

        public int SignCalls { get; private set; }

        public ValueTask<byte[]> SignAsync(
            ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default)
        {
            SignCalls++;
            return inner.SignAsync(data, algorithm, cancellationToken);
        }
    }

    // ------------------------------------------------------------ 证书

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", name);

    private static async Task<SshCertificateSigner> LoadCertificateSignerAsync(string name)
    {
        ISshSigner inner = await SshPrivateKeyFile.LoadAsync(FixturePath(name));
        OpenSshCertificate cert = await OpenSshCertificate.LoadAsync(FixturePath(name + "-cert.pub"));
        return SshCertificateSigner.Create(cert, inner);
    }

    /// <summary>
    /// 证书认证跑完整条 publickey：出示整张证书，签名由被签发的私钥出。
    /// </summary>
    /// <remarks>
    /// 服务端是**独立**重算被签名数据并自己验签的，所以这条用例真的能证明
    /// 「请求里那个算法名带后缀、签名 blob 里不带」这处不对称被处理对了 ——
    /// 处理错的症状是一句「签名验证失败」，从客户端这边什么也看不出来。
    /// </remarks>
    [TestMethod]
    [DataRow("cert-ed25519", "ssh-ed25519-cert-v01@openssh.com")]
    [DataRow("cert-rsa", "rsa-sha2-512-cert-v01@openssh.com")]
    [DataRow("cert-ecdsa", "ecdsa-sha2-nistp256-cert-v01@openssh.com")]
    public async Task 证书认证成功且服务端验得过签名(string name, string expectedAlgorithm)
    {
        SshCertificateSigner signer = await LoadCertificateSignerAsync(name);

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                // 服务端认的是**整张证书**的字节，与真服务端出示什么就比什么一致。
                AcceptedPublicKeys = [signer.Certificate.Blob.ToArray()],
            });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.IsTrue(run.Observation.AllSignaturesValid, "服务端必须能验证证书认证的签名");
        Assert.AreEqual(1, run.Observation.PublicKeySignedCount);

        // 请求里那个「公钥算法名」字段必须带证书后缀。
        Assert.Contains(expectedAlgorithm, run.Observation.PublicKeySignatureAlgorithms);
    }

    [TestMethod]
    public async Task RSA证书默认也不用SHA1的签名算法()
    {
        // 禁用 SHA-1 的过滤曾经只比 ssh-rsa，RSA 证书那个 SHA-1 算法叫 ssh-rsa-cert-v01@openssh.com，
        // 于是拿证书登录时，服务端一说「只认它」，SHA-1 照样被挑出来用。
        SshCertificateSigner signer = await LoadCertificateSignerAsync("cert-rsa");

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.Certificate.Blob.ToArray()],
                ServerSignatureAlgorithms = ["ssh-rsa-cert-v01@openssh.com"],
            });

        Assert.DoesNotContain(
"ssh-rsa-cert-v01@openssh.com", run.Observation.PublicKeySignatureAlgorithms, "默认不用 SHA-1 —— 证书也一样");
    }

    /// <summary>
    /// 〔AU-E6〕server-sig-algs 列的是签名算法（不带证书后缀）：只认 rsa-sha2-256 的服务端，RSA 证书就用 rsa-sha2-256 那一种。
    /// 曾经按带后缀的名字原样比、永远比不中，证书一律用第一偏好（512）。
    /// </summary>
    [TestMethod]
    public async Task 服务端只认rsa_sha2_256时RSA证书用它()
    {
        SshCertificateSigner signer = await LoadCertificateSignerAsync("cert-rsa");

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.Certificate.Blob.ToArray()],
                ServerSignatureAlgorithms = [SshAlgorithmNames.RsaSha256],
            });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreSequenceEqual(["rsa-sha2-256-cert-v01@openssh.com"], run.Observation.PublicKeySignatureAlgorithms);
        Assert.IsTrue(run.Observation.AllSignaturesValid);
    }

    /// <summary>〔AU-E6〕允许 SHA-1 时，只认 ssh-rsa 的服务端收到的 RSA 证书签名就是 SHA-1 那一种（曾经对证书不起作用）。</summary>
    [TestMethod]
    public async Task 允许SHA1且服务端只认ssh_rsa时RSA证书用SHA1()
    {
        SshCertificateSigner signer = await LoadCertificateSignerAsync("cert-rsa");

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.Certificate.Blob.ToArray()],
                ServerSignatureAlgorithms = [SshAlgorithmNames.SshRsa],
            },
            authenticatorFactory: (t, u, s) => new SshAuthenticator(t, u, s) { AllowSha1RsaSignatures = true });

        Assert.AreEqual(SshProtocolNames.AuthPublicKey, run.Succeeded.Method);
        Assert.AreSequenceEqual(["ssh-rsa-cert-v01@openssh.com"], run.Observation.PublicKeySignatureAlgorithms);
        Assert.IsTrue(run.Observation.AllSignaturesValid);
    }

    /// <summary>证书走的仍然是 publickey，没有第三种认证方法。</summary>
    [TestMethod]
    public async Task 证书认证用的仍是publickey方法()
    {
        SshCertificateSigner signer = await LoadCertificateSignerAsync("cert-ed25519");

        AuthRun run = await RunAsync(
            [new PublicKeyCredential(signer)],
            new TestAuthPolicy
            {
                RequiredMethods = [SshProtocolNames.AuthPublicKey],
                AcceptedPublicKeys = [signer.Certificate.Blob.ToArray()],
            });

        Assert.AreSequenceEqual(
            [SshProtocolNames.AuthNone, SshProtocolNames.AuthPublicKey], run.Observation.RequestedMethods);
    }

    /// <summary>取不到私钥材料的签名器 —— 模拟「私钥文件读不出来」。</summary>
    private sealed class BrokenSigner(SshPublicKey publicKey) : ISshSigner
    {
        public SshPublicKey PublicKey => publicKey;

        public IReadOnlyList<string> SignatureAlgorithms => [SshAlgorithmNames.SshEd25519];

        public bool IsLocalAndCheap => true;

        public ValueTask<byte[]> SignAsync(
            ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default) =>
            throw new IOException("私钥文件读不出来：拒绝访问。");
    }

    /// <summary>像 agent 那样先探测、签名时却被拒（用户在 ssh-add -c 的确认框里点了拒绝）。</summary>
    private sealed class RefusingAgentSigner(SshPublicKey publicKey) : ISshSigner
    {
        public SshPublicKey PublicKey => publicKey;

        public IReadOnlyList<string> SignatureAlgorithms => [SshAlgorithmNames.SshEd25519];

        public bool IsLocalAndCheap => false;

        public ValueTask<byte[]> SignAsync(
            ReadOnlyMemory<byte> data, string algorithm, CancellationToken cancellationToken = default) =>
            throw new SshAgentException(SshFailureReason.AgentRefused, "agent 拒绝签名。");
    }
}
