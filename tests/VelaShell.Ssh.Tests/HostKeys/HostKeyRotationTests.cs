// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md §6.4

using System.Diagnostics;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.HostKeys;

/// <summary>
/// 主机密钥轮换（UpdateHostKeys）：证实了的新钥补记、证明有一把是假的就一把都不记、没打开或者当前的钥没记着就不做。
/// 签的是什么另由对真 sshd 的互通用例裁决（测试桩与实现出自同一份理解）。
/// </summary>
[TestClass]
[TestCategory("HostKeys")]
public sealed class HostKeyRotationTests
{
    public TestContext TestContext { get; set; } = null!;

    private sealed class Rig(string knownHosts) : IAsyncDisposable
    {
        public string KnownHosts => knownHosts;

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            File.Delete(knownHosts);
        }
    }

    /// <summary>服务端已经发过宣告，等客户端把它走完（证明、写 known_hosts）的期限。</summary>
    private static readonly TimeSpan UpdateBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 断言「不会有更新」时等的期限。缺席没有信号可等，只能等出来 —— 等不到就是「确实没有」；
    /// 这个方向赌输了是断言变弱（漏判），不是 CI 上红，所以不必给到 <see cref="UpdateBudget" /> 那么长。
    /// </summary>
    private static readonly TimeSpan NoUpdateBudget = TimeSpan.FromSeconds(2);

    private async Task<SshHostKeyUpdate?> ConnectAsync(
        IHostKeyPolicy policy, TestHostKey primary, TestHostKey[] announced, bool corruptProof = false, bool expectUpdate = true)
    {
        // 宣告在认证之后才由服务端发出：等服务端打的那个点，而不是「睡 100 × 10 毫秒再看」——
        // 固定次数的轮询等于赌调度，机器一忙就赌输（run 37638519269 上连着两次红的是这个类的两条用例）。
        TaskCompletionSource announcement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        List<Task> servers = [];
        SshConnectionOptions options = new("joe@rotate.example:22")
        {
            Dialer = InMemoryTransport.CreateDialer((stream, _, _) =>
            {
                servers.Add(Task.Run(async () =>
                {
                    try
                    {
                        await using TestSshServer server = new(stream, new TestSshServerOptions { HostKey = primary });
                        TestSshServerHandshake handshake = await server.HandshakeAsync(lifetime.Token);
                        TestAuthServer auth = new(server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "hunter2" });
                        await auth.RunAsync(lifetime.Token);
                        TestChannelServer channels = new(server.Transport, new TestChannelScript
                        {
                            AnnouncedHostKeys = announced,
                            HostKeySessionId = handshake.ExchangeHash,
                            CorruptHostKeyProof = corruptProof,
                            HostKeysAnnouncementSent = announcement,
                        });
                        await channels.RunAsync(lifetime.Token);
                    }
                    catch (Exception)
                    {
                        // 拆场。宣告没发出来也让等的那一方放行：判据落在断言上，比等满 30 秒好懂。
                        announcement.TrySetResult();
                    }
                }, CancellationToken.None));
                return ValueTask.CompletedTask;
            }),
            HostKeyPolicy = policy,
            Credentials = [new PasswordCredential("hunter2")],
        };

        SshHostKeyUpdate? update;
        await using (SshConnection connection = await SshConnection.ConnectAsync(options, TestContext.CancellationToken))
        {
            await announcement.Task.WaitAsync(lifetime.Token);
            TimeSpan budget = expectUpdate ? UpdateBudget : NoUpdateBudget;
            var waited = Stopwatch.StartNew();
            while (connection.LastHostKeyUpdate is null && waited.Elapsed < budget)
            {
                await Task.Delay(10, TestContext.CancellationToken);
            }
            await connection.HostKeyRotation;
            update = connection.LastHostKeyUpdate;
        }
        await lifetime.CancelAsync();
        await Task.WhenAll(servers);
        return update;
    }

    private static Rig NewKnownHosts() => new(Path.Combine(Path.GetTempPath(), $"vela-kh-{Guid.NewGuid():N}"));

    /// <summary>头一次连接按 TOFU 记下当前那把，轮换补记另外两把（RSA 用 SHA-2 签）；再连一次没有新钥。</summary>
    [TestMethod]
    public async Task 证实了的新钥补记进known_hosts()
    {
        await using Rig rig = NewKnownHosts();
        using var ed25519 = TestHostKey.Create(SshAlgorithmNames.SshEd25519);
        using var ecdsa = TestHostKey.Create(SshAlgorithmNames.EcdsaSha2Nistp256);
        using var rsa = TestHostKey.Create(SshAlgorithmNames.RsaSha512);
        KnownHostsPolicy policy = new(rig.KnownHosts) { UnknownHost = UnknownHostBehavior.AcceptAndPersist, AllowHostKeyUpdates = true };

        SshHostKeyUpdate? first = await ConnectAsync(policy, ed25519, [ed25519, ecdsa, rsa]);
        Assert.IsNotNull(first);
        Assert.IsNull(first.Skipped, first.Skipped);
        Assert.AreSequenceEqual([ecdsa.KeyType, rsa.KeyType], [.. first.Added.Select(k => k.KeyType)]);
        Assert.HasCount(3, await File.ReadAllLinesAsync(rig.KnownHosts, TestContext.CancellationToken));

        SshHostKeyUpdate? second = await ConnectAsync(policy, ed25519, [ed25519, ecdsa, rsa]);
        Assert.IsNotNull(second);
        Assert.IsEmpty(second.Added);
    }

    /// <summary>证明里有一把签不过：一把都不记（同一个应答里有假的，其余的也不可信）。</summary>
    [TestMethod]
    public async Task 证明有一把是假的就一把都不记()
    {
        await using Rig rig = NewKnownHosts();
        using var ed25519 = TestHostKey.Create(SshAlgorithmNames.SshEd25519);
        using var ecdsa = TestHostKey.Create(SshAlgorithmNames.EcdsaSha2Nistp256);
        using var rsa = TestHostKey.Create(SshAlgorithmNames.RsaSha512);
        KnownHostsPolicy policy = new(rig.KnownHosts) { UnknownHost = UnknownHostBehavior.AcceptAndPersist, AllowHostKeyUpdates = true };

        SshHostKeyUpdate? update = await ConnectAsync(policy, ed25519, [ed25519, ecdsa, rsa], corruptProof: true);

        Assert.IsNotNull(update);
        Assert.IsEmpty(update.Added);
        StringAssert.Contains(update.Skipped, "验不过");
        Assert.HasCount(1, await File.ReadAllLinesAsync(rig.KnownHosts, TestContext.CancellationToken), "只有 TOFU 记下的那一把");
    }

    /// <summary>
    /// 〔Q4〕服务端不再出示的旧钥从 known_hosts 删掉（有新钥时证明过了才删；没有新钥时直接删）；证明是假的就不补也不删。
    /// </summary>
    [TestMethod]
    public async Task 服务端不再出示的旧钥从known_hosts删掉()
    {
        await using Rig rig = NewKnownHosts();
        using var ed25519 = TestHostKey.Create(SshAlgorithmNames.SshEd25519);
        using var ecdsa = TestHostKey.Create(SshAlgorithmNames.EcdsaSha2Nistp256);
        using var rsa = TestHostKey.Create(SshAlgorithmNames.RsaSha512);
        SshPublicKey rsaKey = SshPublicKey.Decode(rsa.PublicKeyBlob), ecdsaKey = SshPublicKey.Decode(ecdsa.PublicKeyBlob);
        KnownHostsPolicy policy = new(rig.KnownHosts) { UnknownHost = UnknownHostBehavior.AcceptAndPersist, AllowHostKeyUpdates = true };

        await ConnectAsync(policy, ed25519, [ed25519, rsa]);
        Assert.HasCount(2, await File.ReadAllLinesAsync(rig.KnownHosts, TestContext.CancellationToken));

        // 换了一把 ECDSA、证明却是假的：这次宣告整个不可信 —— 不补，也不删 RSA。
        SshHostKeyUpdate? forged = await ConnectAsync(policy, ed25519, [ed25519, ecdsa], corruptProof: true);
        Assert.IsNotNull(forged);
        Assert.IsEmpty(forged.RemovedFingerprints);
        Assert.HasCount(2, await File.ReadAllLinesAsync(rig.KnownHosts, TestContext.CancellationToken));

        SshHostKeyUpdate? rotated = await ConnectAsync(policy, ed25519, [ed25519, ecdsa]);
        Assert.IsNotNull(rotated);
        Assert.IsNull(rotated.Skipped, rotated.Skipped);
        Assert.AreSequenceEqual([ecdsa.KeyType], [.. rotated.Added.Select(k => k.KeyType)]);
        Assert.AreSequenceEqual([rsaKey.Sha256Fingerprint], [.. rotated.RemovedFingerprints]);
        string[] lines = await File.ReadAllLinesAsync(rig.KnownHosts, TestContext.CancellationToken);
        Assert.HasCount(2, lines);
        Assert.IsFalse(lines.Any(l => l.Contains(Convert.ToBase64String(rsaKey.Blob.Span), StringComparison.Ordinal)));

        // 没有新钥、只少了一把：直接删。
        SshHostKeyUpdate? shrunk = await ConnectAsync(policy, ed25519, [ed25519]);
        Assert.IsNotNull(shrunk);
        Assert.IsEmpty(shrunk.Added);
        Assert.AreSequenceEqual([ecdsaKey.Sha256Fingerprint], [.. shrunk.RemovedFingerprints]);
        Assert.HasCount(1, await File.ReadAllLinesAsync(rig.KnownHosts, TestContext.CancellationToken));
    }

    /// <summary>〔Q4〕宣告超过一次看的上限（16 把）：后面没看的里也许就有记着的那把 —— 不删。</summary>
    [TestMethod]
    public async Task 宣告不完整时不删旧钥()
    {
        await using Rig rig = NewKnownHosts();
        using var ed25519 = TestHostKey.Create(SshAlgorithmNames.SshEd25519);
        using var rsa = TestHostKey.Create(SshAlgorithmNames.RsaSha512);
        TestHostKey[] many = [.. Enumerable.Range(0, 16).Select(_ => TestHostKey.Create(SshAlgorithmNames.SshEd25519))];
        try
        {
            KnownHostsPolicy policy = new(rig.KnownHosts) { UnknownHost = UnknownHostBehavior.AcceptAndPersist, AllowHostKeyUpdates = true };
            await ConnectAsync(policy, ed25519, [ed25519, rsa]);

            SshHostKeyUpdate? update = await ConnectAsync(policy, ed25519, [ed25519, .. many]);
            Assert.IsNotNull(update);
            Assert.IsNull(update.Skipped, update.Skipped);
            Assert.IsEmpty(update.RemovedFingerprints);
            string rsaBlob = Convert.ToBase64String(rsa.PublicKeyBlob);
            Assert.Contains(l => l.Contains(rsaBlob, StringComparison.Ordinal),
                await File.ReadAllLinesAsync(rig.KnownHosts, TestContext.CancellationToken));
        }
        finally
        {
            foreach (TestHostKey key in many)
            {
                key.Dispose();
            }
        }
    }

    /// <summary>没打开 AllowHostKeyUpdates：宣告来了也不理；当前的钥没有记在 known_hosts 里（不用文件的策略）：不做。</summary>
    [TestMethod]
    public async Task 没打开或者当前的钥没记着就不做()
    {
        await using Rig rig = NewKnownHosts();
        using var ed25519 = TestHostKey.Create(SshAlgorithmNames.SshEd25519);
        using var ecdsa = TestHostKey.Create(SshAlgorithmNames.EcdsaSha2Nistp256);

        KnownHostsPolicy off = new(rig.KnownHosts) { UnknownHost = UnknownHostBehavior.AcceptAndPersist };
        Assert.IsNull(await ConnectAsync(off, ed25519, [ed25519, ecdsa], expectUpdate: false), "没打开就一直为 null");
        Assert.HasCount(1, await File.ReadAllLinesAsync(rig.KnownHosts, TestContext.CancellationToken));

        SshHostKeyUpdate? update = await ConnectAsync(
            new RotatingWithoutRecord(), ed25519, [ed25519, ecdsa]);
        Assert.IsNotNull(update);
        Assert.IsEmpty(update.Added);
        StringAssert.Contains(update.Skipped, "没有作为普通钥记在 known_hosts 里");
    }

    /// <summary>接受一切、却什么都没记着的轮换策略：当前那把不在「记着的」里，轮换不该做。</summary>
    private sealed class RotatingWithoutRecord : IHostKeyPolicy, IHostKeyRotationPolicy
    {
        public bool AllowHostKeyUpdates => true;

        public ValueTask<SshHostKeyVerdict> EvaluateAsync(SshHostKeyContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SshHostKeyVerdict.Accept);

        public ValueTask<IReadOnlyList<string>> GetKnownHostKeyFingerprintsAsync(string host, int port, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask RecordHostKeysAsync(string host, int port, IReadOnlyList<SshPublicKey> keys, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("不该记");
    }
}
