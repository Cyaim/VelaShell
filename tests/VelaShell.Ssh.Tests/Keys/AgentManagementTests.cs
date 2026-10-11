// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §7.3、§7.5;draft-miller-ssh-agent
//
// 删钥、清空、锁、解锁与「证书 + 私钥」一起加。报文的形状另由对真 OpenSSH agent 的互通用例裁决 ——
// 这里的 TestAgent 与实现出自对规格的同一份理解。

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class AgentManagementTests
{
    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly Task _serving;

        public Rig()
        {
            (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();
            _serving = Task.Run(() => Agent.ServeAsync(theirs, _cts.Token));
            Client = SshAgentClient.FromStream(ours, "(测试 agent)");
        }

        public TestAgent Agent { get; } = new();

        public SshAgentClient Client { get; }

        public CancellationToken Token => _cts.Token;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _cts.CancelAsync();
            await _serving;
            _cts.Dispose();
        }
    }

    /// <summary>删一把：那一把没了、别的还在；再删一次回 false（agent 里没有它了）。</summary>
    [TestMethod]
    public async Task 删钥只删那一把_删不存在的回false()
    {
        await using Rig rig = new();
        using var first = InMemorySshSigner.GenerateEd25519();
        using var second = InMemorySshSigner.GenerateEd25519();
        await rig.Client.AddIdentityAsync(first, "first", cancellationToken: rig.Token);
        await rig.Client.AddIdentityAsync(second, "second", cancellationToken: rig.Token);

        Assert.IsTrue(await rig.Client.RemoveIdentityAsync(first.PublicKey, rig.Token));
        Assert.IsFalse(await rig.Client.RemoveIdentityAsync(first.PublicKey, rig.Token));

        IReadOnlyList<SshAgentIdentity> left = await rig.Client.ListIdentitiesAsync(rig.Token);
        Assert.HasCount(1, left);
        Assert.AreEqual(second.PublicKey.Sha256Fingerprint, left[0].PublicKey.Sha256Fingerprint);
    }

    /// <summary>清空：一把不剩。</summary>
    [TestMethod]
    public async Task 清空之后一把不剩()
    {
        await using Rig rig = new();
        using var key = InMemorySshSigner.GenerateEd25519();
        await rig.Client.AddIdentityAsync(key, "k", cancellationToken: rig.Token);

        await rig.Client.RemoveAllIdentitiesAsync(rig.Token);

        Assert.IsEmpty(await rig.Client.ListIdentitiesAsync(rig.Token));
    }

    /// <summary>
    /// 锁：锁着时清空被拒（报 AgentRefused，消息点出「锁定」）；口令不对解不开；再锁一次回 false；
    /// 用对的口令解开之后照常。
    /// </summary>
    [TestMethod]
    public async Task 锁着时拒绝操作_口令对了才解得开()
    {
        await using Rig rig = new();

        Assert.IsTrue(await rig.Client.LockAsync("离开座位", rig.Token));
        Assert.IsFalse(await rig.Client.LockAsync("离开座位", rig.Token), "已经锁着");

        SshAgentException refused = await Assert.ThrowsAsync<SshAgentException>(
            async () => await rig.Client.RemoveAllIdentitiesAsync(rig.Token));
        Assert.AreEqual(Diagnostics.SshFailureReason.AgentRefused, refused.Reason);
        Assert.Contains("锁定", refused.Message);

        Assert.IsFalse(await rig.Client.UnlockAsync("猜一个", rig.Token));
        Assert.IsTrue(await rig.Client.UnlockAsync("离开座位", rig.Token));
        Assert.IsNull(rig.Agent.LockPassphrase);
        await rig.Client.RemoveAllIdentitiesAsync(rig.Token);
    }

    /// <summary>加证书：给的不是证书、证的不是这把私钥，都当场报参数错 —— 不发给 agent。</summary>
    [TestMethod]
    public async Task 加证书时证书与私钥对不上当场报错()
    {
        await using Rig rig = new();
        using var key = InMemorySshSigner.GenerateEd25519();
        var otherCertificate = SshPublicKey.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", "cert-ed25519-cert.pub")));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await rig.Client.AddIdentityAsync(key, key.PublicKey, "不是证书", cancellationToken: rig.Token));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await rig.Client.AddIdentityAsync(key, otherCertificate, "证的是别的钥", cancellationToken: rig.Token));
        Assert.AreEqual(0, rig.Agent.AddRequests);
    }

    /// <summary>
    /// 默认端点里的 Pageant：OpenSSH agent 的管道不在、当前用户的 Pageant 在就用它；两个都在仍用 OpenSSH 的；
    /// 别的用户的 Pageant 不认；<c>SSH_AUTH_SOCK</c> 指明了管道时照旧以它为准。
    /// </summary>
    [TestMethod]
    public void 默认端点在OpenSSH_agent不在时找当前用户的Pageant()
    {
        const string openSsh = @"\\.\pipe\openssh-ssh-agent";
        const string pageant = @"\\.\pipe\pageant.joe.0123abcd";

        Assert.AreEqual(pageant, SshAgentClient.DefaultEndpointFor(true, null, () => [@"\\.\pipe\other", pageant], "joe"));
        Assert.AreEqual(pageant, SshAgentClient.DefaultEndpointFor(true, null, () => [pageant], "JOE"), "用户名不分大小写");
        Assert.AreEqual(openSsh, SshAgentClient.DefaultEndpointFor(true, null, () => [openSsh, pageant], "joe"));
        Assert.AreEqual(openSsh, SshAgentClient.DefaultEndpointFor(true, null, () => [@"\\.\pipe\pageant.alice.99"], "joe"));
        Assert.AreEqual(openSsh, SshAgentClient.DefaultEndpointFor(true, null, () => [], "joe"));
        Assert.AreEqual(@"\\.\pipe\agent-1password", SshAgentClient.DefaultEndpointFor(true, @"\\.\pipe\agent-1password", () => [pageant], "joe"));
        Assert.AreEqual("/run/agent.sock", SshAgentClient.DefaultEndpointFor(false, "/run/agent.sock", () => [pageant], "joe"));
    }

    /// <summary>〔AGENTS 4.3〕交来的流按 <c>ownsStream</c> 决定释放不释放；不给标签时端点名是中性的占位，不是一句界面语言。</summary>
    [TestMethod]
    public async Task FromStream按ownsStream决定释放不释放流()
    {
        (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();
        await using (theirs)
        {
            var borrowed = SshAgentClient.FromStream(ours, ownsStream: false);
            Assert.AreEqual("(stream)", borrowed.Endpoint);
            await borrowed.DisposeAsync();
            Assert.IsTrue(ours.CanRead, "没交出所有权，流不该被释放");

            await SshAgentClient.FromStream(ours).DisposeAsync();
            Assert.IsFalse(ours.CanRead, "默认交进来就归客户端");
        }
    }
}
