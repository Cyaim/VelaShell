using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 宿主的主机密钥策略把「这台主机记着哪种类型的钥」交给库:库据此把那种类型的算法排到最前,
/// 正常的服务端就谈成同一种,不会因为服务端新增了一把别的类型的钥而误报「指纹已变更」。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class VelaHostKeyPolicyTests
{
    [TestMethod]
    public async Task 交出这台主机记着的钥的类型_最近见过的在前()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.FindKnownHostKeysAsync("old.example", 22, Arg.Any<CancellationToken>())
            .Returns([
                new KnownHost { Host = "old.example", Port = 22, KeyType = "ssh-ed25519", Fingerprint = "SHA256:y" },
                new KnownHost { Host = "old.example", Port = 22, KeyType = "ssh-rsa", Fingerprint = "SHA256:x" },
            ]);

        VelaHostKeyPolicy preference = new(store, settings: null, prompt: null, alerts: null);

        Assert.AreSequenceEqual(["ssh-ed25519", "ssh-rsa"], (await preference.GetKnownKeyTypesAsync("old.example", 22)).ToArray());
    }

    /// <summary>
    /// 〔F1〕主机密钥轮换:交给库的是记着的指纹(换库之前存的裸 base64 也交,库按归一之后比),
    /// 服务端证明过的新钥按类型补记进信任库、并告诉用户一声。
    /// </summary>
    [TestMethod]
    public async Task 轮换_交出记着的指纹并补记证明过的新钥()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.FindKnownHostKeysAsync("rot.example", 22, Arg.Any<CancellationToken>())
            .Returns([new KnownHost { Host = "rot.example", Port = 22, KeyType = "ssh-rsa", Fingerprint = "bareBase64Fingerprint" }]);
        ISecurityAlertService alerts = Substitute.For<ISecurityAlertService>();

        VelaHostKeyPolicy policy = new(store, settings: null, prompt: null, alerts);
        Assert.IsTrue(policy.AllowHostKeyUpdates);
        Assert.AreSequenceEqual(["bareBase64Fingerprint"], (await policy.GetKnownHostKeyFingerprintsAsync("rot.example", 22)).ToArray());

        using var fresh = InMemorySshSigner.GenerateEd25519();
        await policy.RecordHostKeysAsync("rot.example", 22, [fresh.PublicKey]);

        await store.Received(1).TrustHostKeyAsync("rot.example", 22, "ssh-ed25519", fresh.PublicKey.Sha256Fingerprint, Arg.Any<CancellationToken>());
        await alerts.Received(1).RaiseAsync("hostkey-learned", Arg.Is<string>(m => m.Contains(fresh.PublicKey.Sha256Fingerprint)), Arg.Any<object?>());
    }

    /// <summary>
    /// 〔Q4〕服务端不再出示的旧钥按类型从信任库删掉、告诉用户一声;要忘掉的指纹之外的类型不动。
    /// </summary>
    [TestMethod]
    public async Task 轮换_删掉服务端不再出示的旧钥()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.FindKnownHostKeysAsync("rot.example", 22, Arg.Any<CancellationToken>())
            .Returns([
                new KnownHost { Host = "rot.example", Port = 22, KeyType = "ssh-rsa", Fingerprint = "staleRsa" },
                new KnownHost { Host = "rot.example", Port = 22, KeyType = "ssh-ed25519", Fingerprint = "SHA256:current" },
            ]);
        ISecurityAlertService alerts = Substitute.For<ISecurityAlertService>();
        VelaHostKeyPolicy policy = new(store, settings: null, prompt: null, alerts);

        IReadOnlyList<string> forgotten = await policy.ForgetHostKeysAsync("rot.example", 22, ["SHA256:staleRsa"]);

        Assert.AreSequenceEqual(["staleRsa"], forgotten.ToArray());
        await store.Received(1).RemoveKnownHostAsync("rot.example", 22, "ssh-rsa", Arg.Any<CancellationToken>());
        await store.DidNotReceive().RemoveKnownHostAsync("rot.example", 22, "ssh-ed25519", Arg.Any<CancellationToken>());
        await alerts.Received(1).RaiseAsync("hostkey-forgotten", Arg.Is<string>(m => m.Contains("staleRsa")), Arg.Any<object?>());
    }

    [TestMethod]
    public async Task 没记过的主机返回空()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.FindKnownHostKeysAsync("new.example", 22, Arg.Any<CancellationToken>()).Returns([]);

        VelaHostKeyPolicy preference = new(store, settings: null, prompt: null, alerts: null);

        Assert.IsEmpty(await preference.GetKnownKeyTypesAsync("new.example", 22));
    }

    [TestMethod]
    public async Task 信任库出错时返回空而不阻断建连()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.FindKnownHostKeysAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("数据库被锁"));

        VelaHostKeyPolicy preference = new(store, settings: null, prompt: null, alerts: null);

        Assert.IsEmpty(await preference.GetKnownKeyTypesAsync("any.example", 22));
    }

    /// <summary>
    /// 信任了却没能记下来：库照常连下去（失败记在连接上）。宿主这边要在本次运行里记住这把指纹、
    /// 别在下一次连接时又弹窗，并告诉用户一声 —— 不然重启之后又被问，他不会知道为什么。
    /// </summary>
    [TestMethod]
    public async Task 信任库写不进去时本次运行记住指纹并告警()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        IOException locked = new("数据库被锁");
        store.TrustHostKeyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(locked);
        ISecurityAlertService alerts = Substitute.For<ISecurityAlertService>();

        using var signer = InMemorySshSigner.GenerateEd25519();
        string host = $"persist-{Guid.NewGuid():N}.example";
        SshHostKeyContext context = new()
        {
            Host = host,
            Port = 22,
            Key = signer.PublicKey,
            NegotiatedAlgorithm = signer.PublicKey.KeyType,
        };

        VelaHostKeyPolicy policy = new(store, settings: null, prompt: null, alerts);

        IOException error = await Assert.ThrowsExactlyAsync<IOException>(async () => await policy.PersistAsync(context));

        Assert.AreSame(locked, error, "原样交给库，库把它记在连接的 HostKeyPersistFailure 上");
        Assert.IsTrue(HostTrustOnceCache.IsTrusted(host, 22, signer.PublicKey.Sha256Fingerprint));
        await alerts.Received(1).RaiseAsync(
            "hostkey-persist-failed", Arg.Is<string>(m => m.Contains("数据库被锁")), Arg.Any<object?>());
    }

    /// <summary>问用户时（这里是指纹变了），把这把钥的指纹图一并交给确认框（与 ssh-keygen -lv 同一张图）。</summary>
    [TestMethod]
    public async Task 问用户时交出指纹图()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.VerifyHostKeyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(HostKeyVerification.Changed);
        IHostKeyPrompt prompt = Substitute.For<IHostKeyPrompt>();
        prompt.DecideAsync(default!, default, default!, default!, default, default, default)
            .ReturnsForAnyArgs(HostKeyDecision.Reject);

        using var signer = InMemorySshSigner.GenerateEd25519();
        SshHostKeyContext context = new()
        {
            Host = $"art-{Guid.NewGuid():N}.example",
            Port = 22,
            Key = signer.PublicKey,
            NegotiatedAlgorithm = signer.PublicKey.KeyType,
        };

        await new VelaHostKeyPolicy(store, settings: null, prompt, alerts: null).EvaluateAsync(context);

        await prompt.Received(1).DecideAsync(
            context.Host, 22, Arg.Any<string>(), signer.PublicKey.Sha256Fingerprint,
            HostKeyVerification.Changed, Arg.Any<string?>(), signer.PublicKey.RandomArt);
    }

    /// <summary>
    /// 自动重连（没人在场）：本该弹窗问的一律拒绝，不弹 —— 指纹变了按「变了」拒，没见过按「不信任」拒；
    /// 照样记一笔安全告警（没人看到弹窗，审计里至少要留下这件事）。
    /// </summary>
    [TestMethod]
    [DataRow(HostKeyVerification.Unknown)]
    [DataRow(HostKeyVerification.Changed)]
    public async Task 自动重连时本该弹窗问的一律拒绝(HostKeyVerification verification)
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.VerifyHostKeyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(verification);
        AppSettings snapshot = new();
        snapshot.Security.ConfirmFirstFingerprint = true;   // 首次连接也要问 —— 不开它的话「没见过」本来就按 TOFU 记下，不问
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.CurrentSnapshot.Returns(snapshot);
        IHostKeyPrompt prompt = Substitute.For<IHostKeyPrompt>();
        ISecurityAlertService alerts = Substitute.For<ISecurityAlertService>();

        using var signer = InMemorySshSigner.GenerateEd25519();
        SshHostKeyContext context = new()
        {
            Host = $"auto-{Guid.NewGuid():N}.example",
            Port = 22,
            Key = signer.PublicKey,
            NegotiatedAlgorithm = signer.PublicKey.KeyType,
        };

        SshHostKeyVerdict verdict =
            await new VelaHostKeyPolicy(store, settings, prompt, alerts, automaticReconnect: true).EvaluateAsync(context);

        Assert.IsFalse(verdict.IsAccepted);
        Assert.AreEqual(
            verification == HostKeyVerification.Changed ? SshFailureReason.HostKeyChanged : SshFailureReason.HostKeyRejected,
            verdict.Reason);
        await prompt.DidNotReceiveWithAnyArgs().DecideAsync(default!, default, default!, default!, default, default, default);
        await alerts.Received(1).RaiseAsync("hostkey-rejected", Arg.Any<string>(), Arg.Any<object?>());
    }

    /// <summary>自动重连只拦「本该问」的那些：不问的默认照旧 —— 关着「首次连接确认」时，没见过的主机照常按 TOFU 记下。</summary>
    [TestMethod]
    public async Task 自动重连时不问的默认照旧()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.VerifyHostKeyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(HostKeyVerification.Unknown);
        IHostKeyPrompt prompt = Substitute.For<IHostKeyPrompt>();

        using var signer = InMemorySshSigner.GenerateEd25519();
        SshHostKeyContext context = new()
        {
            Host = $"tofu-{Guid.NewGuid():N}.example",
            Port = 22,
            Key = signer.PublicKey,
            NegotiatedAlgorithm = signer.PublicKey.KeyType,
        };

        SshHostKeyVerdict verdict =
            await new VelaHostKeyPolicy(store, settings: null, prompt, alerts: null, automaticReconnect: true).EvaluateAsync(context);

        Assert.AreEqual(SshHostKeyDecision.AcceptAndPersist, verdict.Decision);
    }

    /// <summary>
    /// 端到端的那一半在库里：记着 RSA 时，RSA 的算法排到 Ed25519 前面 —— 协商以客户端的顺序为准。
    /// </summary>
    [TestMethod]
    public void 记着的类型排到主机密钥算法清单最前()
    {
        SshAlgorithmSet preferred = SshAlgorithmSet.Default.PreferHostKeyTypes(["ssh-rsa"]);

        Assert.StartsWith("rsa-sha2-", preferred.HostKey[0]);
        Assert.IsGreaterThan(
            preferred.HostKey.ToList().FindIndex(a => a.StartsWith("rsa-sha2-", StringComparison.Ordinal)),
            preferred.HostKey.ToList().IndexOf("ssh-ed25519"));
    }
}
