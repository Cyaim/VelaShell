// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §7.3.2
//
// 报文的嵌套方式由规格里那个对过 ssh-add 字节的例子钉住（12 / 74 / 98 / 102 / 147）；
// agent 怎么执行它只有真 agent 裁决得了，见互操作用例。

using System.Buffers;
using System.Net.Sockets;
using System.Security.Cryptography;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class AgentDestinationConstraintTests
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

    private static SshPublicKey NewKey()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();
        return signer.PublicKey;
    }

    private static SshPublicKey Fixture(string name) =>
        SshPublicKey.Decode(Convert.FromBase64String(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", name)).Split(' ')[1]));

    /// <summary>照规格的字段表手工拼一段主机描述：string 用户名 ‖ string 主机名 ‖ string 保留 ‖ 若干组（string blob ‖ boolean CA）。</summary>
    private static byte[] HostDescription(string user, string name, params (SshPublicKey Key, bool IsCa)[] keys)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String(user);
        writer.WriteUtf8String(name);
        writer.WriteString([]);
        foreach ((SshPublicKey key, bool isCa) in keys)
        {
            writer.WriteString(key.Blob.Span);
            writer.WriteBoolean(isCa);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Strings(params byte[][] parts)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        foreach (byte[] part in parts)
        {
            writer.WriteString(part);
        }
        return buffer.WrittenSpan.ToArray();
    }

    // ------------------------------------------------------------ 报文

    /// <summary>
    /// 规格里那个对过 <c>ssh-add -h host-c</c> 字节的例子：起点 12 字节、终点 74、一跳 98、跳列表 102、整条约束 147。
    /// </summary>
    [TestMethod]
    public async Task 一跳的目的地约束按四层嵌套写进25()
    {
        await using Rig rig = new();
        using var key = InMemorySshSigner.GenerateEd25519();
        SshPublicKey hostKey = NewKey();

        await rig.Client.AddIdentityAsync(key, "k", new SshAgentKeyConstraints
        {
            AllowedHops = [new SshAgentHop(new SshAgentHopHost("host-c", [hostKey]))],
        }, rig.Token);

        Assert.AreEqual((byte)25, rig.Agent.LastAddMessageType);
        Assert.AreSequenceEqual(new byte[] { 255 }, [.. rig.Agent.LastConstraints]);
        Assert.HasCount(147, rig.Agent.LastConstraintBytes);

        byte[] start = HostDescription("", "");
        byte[] end = HostDescription("", "host-c", (hostKey, false));
        byte[] hop = Strings(start, end, []);
        Assert.HasCount(12, start);
        Assert.HasCount(74, end);
        Assert.HasCount(98, hop);

        (string name, byte[] payload) = rig.Agent.LastConstraintExtensions.Single();
        Assert.AreEqual("restrict-destination-v00@openssh.com", name);
        Assert.HasCount(102, payload);
        Assert.AreSequenceEqual(Strings(hop), payload);
    }

    /// <summary>带用户名、带起点、一台主机两把钥、带 CA：主机钥在前（false）、CA 在后（true），起点的用户名为空，跳与跳首尾相接。</summary>
    [TestMethod]
    public async Task 用户名起点多把钥与CA按顺序写()
    {
        await using Rig rig = new();
        using var key = InMemorySshSigner.GenerateEd25519();
        SshPublicKey bastionKey1 = NewKey(), bastionKey2 = NewKey(), dbKey = NewKey(), dbCa = NewKey();
        SshAgentHopHost bastion = new("bastion.example.com", [bastionKey1, bastionKey2]);
        SshAgentHopHost db = new("db.internal", [dbKey], [dbCa]);

        await rig.Client.AddIdentityAsync(key, "k", new SshAgentKeyConstraints
        {
            AllowedHops = [new SshAgentHop(bastion), new SshAgentHop(db, userName: "deploy", via: bastion)],
        }, rig.Token);

        byte[] first = Strings(
            HostDescription("", ""),
            HostDescription("", "bastion.example.com", (bastionKey1, false), (bastionKey2, false)),
            []);
        byte[] second = Strings(
            HostDescription("", "bastion.example.com", (bastionKey1, false), (bastionKey2, false)),
            HostDescription("deploy", "db.internal", (dbKey, false), (dbCa, true)),
            []);
        Assert.AreSequenceEqual(Strings(first, second), rig.Agent.LastConstraintExtensions.Single().Payload);
    }

    /// <summary>
    /// ⚠️ 只给了目的地约束也发 <c>25</c>：约束接在 <c>17</c> 后面会被 agent 静默丢掉 —— 使用者以为钥只能登 bastion，其实哪儿都能登。
    /// 三种约束按有效期、逐次确认、目的地的次序排。
    /// </summary>
    [TestMethod]
    public async Task 有目的地约束一定发25且约束按次序排()
    {
        await using Rig rig = new();
        using var key = InMemorySshSigner.GenerateEd25519();
        SshAgentHop[] hops = [new SshAgentHop(new SshAgentHopHost("h", [NewKey()]))];

        await rig.Client.AddIdentityAsync(key, "k", new SshAgentKeyConstraints { AllowedHops = hops }, rig.Token);
        Assert.AreEqual((byte)25, rig.Agent.LastAddMessageType, "只有目的地约束也得发 25");

        await rig.Client.AddIdentityAsync(key, "k", new SshAgentKeyConstraints
        {
            Lifetime = TimeSpan.FromMinutes(1),
            IsConfirmationRequired = true,
            AllowedHops = hops,
        }, rig.Token);
        Assert.AreSequenceEqual(new byte[] { 1, 2, 255 }, [.. rig.Agent.LastConstraints]);
    }

    /// <summary>agent 回 FAILURE 或 EXTENSION_FAILURE：都是 <c>AgentRefused</c>，消息点出目的地约束；绝不去掉约束再加一次。</summary>
    [TestMethod]
    [DataRow((byte)5)]
    [DataRow((byte)28)]
    public async Task Agent拒绝目的地约束时抛AgentRefused且不重试(byte reply)
    {
        await using Rig rig = new();
        rig.Agent.RejectAdditions = true;
        rig.Agent.RejectionCode = reply;
        using var key = InMemorySshSigner.GenerateEd25519();

        SshAgentException error = await Assert.ThrowsExactlyAsync<SshAgentException>(async () =>
            await rig.Client.AddIdentityAsync(key, "k", new SshAgentKeyConstraints
            {
                AllowedHops = [new SshAgentHop(new SshAgentHopHost("h", [NewKey()]))],
            }, rig.Token));

        Assert.AreEqual(SshFailureReason.AgentRefused, error.Reason);
        Assert.Contains("目的地约束", error.Message);
        Assert.AreEqual(1, rig.Agent.AddRequests, "被拒就是被拒，不能退回去不带约束地加");
    }

    /// <summary>整条加钥报文超过 256 KiB：发送前就抛 <c>LimitExceeded</c>，一个字节都不发。</summary>
    [TestMethod]
    public async Task 约束太大时发送前就抛()
    {
        await using Rig rig = new();
        using var key = InMemorySshSigner.GenerateEd25519();
        SshPublicKey[] big = [.. Enumerable.Range(0, 32).Select(_ =>
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP521);
            using var signer = InMemorySshSigner.FromEcdsa(ecdsa);
            return signer.PublicKey;
        })];
        SshAgentHopHost host = new("h", big);
        SshAgentHop[] hops = [.. Enumerable.Range(0, 64).Select(_ => new SshAgentHop(host, via: host))];

        SshAgentException error = await Assert.ThrowsExactlyAsync<SshAgentException>(async () =>
            await rig.Client.AddIdentityAsync(key, "k", new SshAgentKeyConstraints { AllowedHops = hops }, rig.Token));

        Assert.AreEqual(SshFailureReason.LimitExceeded, error.Reason);
        Assert.AreEqual(0, rig.Agent.AddRequests);
    }

    // ------------------------------------------------------------ 校验

    [TestMethod]
    public void 主机的名字与钥在构造时就校验()
    {
        SshPublicKey key = NewKey();
        Assert.ThrowsExactly<ArgumentNullException>(() => new SshAgentHopHost(null!, [key]));
        Assert.ThrowsExactly<ArgumentNullException>(() => new SshAgentHopHost("h", null!));
        foreach (string bad in (string[])["", "a b", "a,b", "a\tb", "a\u0001b", new string('x', 256)])
        {
            Assert.ThrowsExactly<ArgumentException>(() => new SshAgentHopHost(bad, [key]), $"名字「{bad}」该被拒");
        }
        Assert.AreEqual(new string('x', 255), new SshAgentHopHost(new string('x', 255), [key]).Name);
        Assert.AreEqual("*.example.org", new SshAgentHopHost("*.example.org", [key]).Name, "principal 自己可以是通配模式，名字照抄它是放行的");

        Assert.ThrowsExactly<ArgumentException>(() => new SshAgentHopHost("h", [key, null!]));
        Assert.ThrowsExactly<ArgumentException>(() => new SshAgentHopHost("h", []), "没有钥的主机 agent 认不出来");
        Assert.ThrowsExactly<ArgumentException>(() => new SshAgentHopHost("h", [Fixture("cert-ed25519-cert.pub")]), "钉住证书过一阵子就对不上");
        Assert.ThrowsExactly<ArgumentException>(() => new SshAgentHopHost("h", [key], [Fixture("cert-ed25519-cert.pub")]));
        Assert.ThrowsExactly<ArgumentException>(() => new SshAgentHopHost("h", [.. Enumerable.Range(0, 33).Select(_ => NewKey())]));

        SshAgentHopHost deduplicated = new("h", [key, key, NewKey()], [key]);
        Assert.HasCount(2, deduplicated.HostKeys, "按 blob 去重，保留第一次出现的位置");
        Assert.AreEqual(key, deduplicated.HostKeys[0]);
        Assert.HasCount(1, deduplicated.CertificateAuthorities);
        Assert.HasCount(1, new SshAgentHopHost("h", [], [key]).CertificateAuthorities, "只有 CA 也认得出");
    }

    [TestMethod]
    public void 一跳与跳表在构造时就校验()
    {
        SshAgentHopHost host = new("h", [NewKey()]);
        Assert.ThrowsExactly<ArgumentNullException>(() => new SshAgentHop(null!));
        foreach (string bad in (string[])["", "a b", "a,b", "!a", "a\u0001", new string('u', 256)])
        {
            Assert.ThrowsExactly<ArgumentException>(() => new SshAgentHop(host, bad), $"用户名「{bad}」该被拒");
        }
        Assert.AreEqual("pro*", new SshAgentHop(host, "pro*").UserName);
        Assert.IsNull(new SshAgentHop(host).UserName);
        Assert.IsNull(new SshAgentHop(host).Via);

        Assert.ThrowsExactly<ArgumentException>(() => new SshAgentKeyConstraints { AllowedHops = [] }, "空表不当「不限」");
        Assert.ThrowsExactly<ArgumentException>(() => new SshAgentKeyConstraints { AllowedHops = [null!] });
        Assert.ThrowsExactly<ArgumentException>(
            () => new SshAgentKeyConstraints { AllowedHops = [.. Enumerable.Repeat(new SshAgentHop(host), 65)] });
        Assert.HasCount(64, new SshAgentKeyConstraints { AllowedHops = [.. Enumerable.Repeat(new SshAgentHop(host), 64)] }.AllowedHops!,
            "条目原样保留、不去重");
        Assert.IsNull(new SshAgentKeyConstraints().AllowedHops);
    }

    [TestMethod]
    public void 相等按内容比且跳表抄了一份()
    {
        SshPublicKey key = NewKey(), ca = NewKey();
        Assert.AreEqual(new SshAgentHopHost("h", [key], [ca]), new SshAgentHopHost("h", [key], [ca]));
        Assert.AreEqual(new SshAgentHopHost("h", [key], [ca]).GetHashCode(), new SshAgentHopHost("h", [key], [ca]).GetHashCode());
        Assert.AreNotEqual(new SshAgentHopHost("h", [key]), new SshAgentHopHost("H", [key]), "名字区分大小写");
        Assert.AreNotEqual(new SshAgentHopHost("h", [key, ca]), new SshAgentHopHost("h", [ca, key]), "顺序算在内");

        SshAgentHop hop = new(new SshAgentHopHost("h", [key]), "deploy", new SshAgentHopHost("b", [ca]));
        Assert.AreEqual(hop, new SshAgentHop(new SshAgentHopHost("h", [key]), "deploy", new SshAgentHopHost("b", [ca])));

        List<SshAgentHop> mine = [hop];
        SshAgentKeyConstraints constraints = new() { AllowedHops = mine };
        mine.Add(hop);
        Assert.HasCount(1, constraints.AllowedHops!, "设值时抄了一份，调用方之后改它手里那份不影响");
        Assert.AreEqual(constraints, new SshAgentKeyConstraints { AllowedHops = [hop] });
        Assert.AreNotEqual(constraints, new SshAgentKeyConstraints());
    }

    // ------------------------------------------------------------ 从 known_hosts 拼

    [TestMethod]
    public void 从known_hosts拼主机_规则与Lookup一致()
    {
        SshPublicKey plain = NewKey(), hashed = NewKey(), wildcard = NewKey(), negated = NewKey(),
            ca = NewKey(), revoked = NewKey(), other = NewKey(), otherPort = NewKey();
        static string Line(string pattern, SshPublicKey key) => $"{pattern} {key.KeyType} {Convert.ToBase64String(key.Blob.Span)}";

        string text = string.Join('\n',
            Line("host-c", plain),
            KnownHostsFile.FormatEntry("host-c", 22, hashed, hashHostName: true),
            Line("*.corp,host-?", wildcard),
            Line("host-*,!host-c", negated),
            "@cert-authority " + Line("host-c", ca),
            Line("host-c", revoked),
            Line("other", other),
            Line("[host-c]:2222", otherPort),
            Line("host-c", plain),
            "host-c ssh-ed25519 AAAA",
            "host-c " + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", "cert-ed25519-cert.pub")).Trim(),
            "@revoked " + Line("host-c", revoked));
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(text);

        SshAgentHopHost host = SshAgentHopHost.FromKnownHosts(entries, "Host-C")!;
        Assert.AreEqual("host-c", host.Name, "转小写、不带端口");
        Assert.AreSequenceEqual([plain, hashed, wildcard], [.. host.HostKeys],
            "取反对上的整行不算；吊销的剔掉（不论排在前后）；重复的、认不得的、证书跳过");
        Assert.AreSequenceEqual([ca], [.. host.CertificateAuthorities]);

        SshAgentHopHost onPort = SshAgentHopHost.FromKnownHosts(entries, "host-c", 2222)!;
        Assert.AreEqual("host-c", onPort.Name);
        Assert.AreSequenceEqual([otherPort], [.. onPort.HostKeys], "端口不是 22 时只认 [host]:port");

        Assert.IsNull(SshAgentHopHost.FromKnownHosts(entries, "nowhere"), "找不到就交回 null，由调用方叫使用者先连一次");
        Assert.IsNull(SshAgentHopHost.FromKnownHosts(KnownHostsFile.Parse("@revoked " + Line("solo", revoked) + "\n" + Line("solo", revoked)), "solo"),
            "只剩吊销的钥等于没有");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SshAgentHopHost.FromKnownHosts(entries, "host-c", 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SshAgentHopHost.FromKnownHosts(entries, "host-c", 65536));
        Assert.ThrowsExactly<ArgumentException>(() => SshAgentHopHost.FromKnownHosts(entries, "a b"));
        Assert.ThrowsExactly<ArgumentNullException>(() => SshAgentHopHost.FromKnownHosts(null!, "host-c"));
    }

    // ------------------------------------------------------------ 一条 agent 连接只替一个会话做认证

    private static async Task<SshSessionProof> ProofAsync()
    {
        using var hostKey = InMemorySshSigner.GenerateEd25519();
        byte[] sessionId = new byte[32];
        Random.Shared.NextBytes(sessionId);
        byte[] signature = await hostKey.SignAsync(sessionId, SshAlgorithmNames.SshEd25519);
        return new SshSessionProof(hostKey.PublicKey.Blob.ToArray(), sessionId, signature);
    }

    /// <summary>
    /// 自己连上的客户端：上一次被接受的认证声明属于别的会话时，先重开一条连接再声明（ProxyJump 的跳板与目标共用一份 agent 凭据）；
    /// 上一次没被接受、为转发声明、或者同一个会话再问，都不重开。
    /// </summary>
    [TestMethod]
    [DataRow(true, false, 2)]
    [DataRow(false, false, 1)]
    [DataRow(true, true, 1)]
    public async Task 为另一个会话做认证声明之前重开连接(bool isAccepted, bool isForwarding, int expectedConnections)
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这台机器不支持 Unix 套接字。");
        }

        TestAgent agent = new() { DeclarationReply = isAccepted ? TestDeclarationReply.Accept : TestDeclarationReply.Reject };
        SshAgentConnectionPurpose purpose = isForwarding ? SshAgentConnectionPurpose.Forwarding : SshAgentConnectionPurpose.Authentication;
        string path = Path.Combine(Path.GetTempPath(), $"vsd-{Guid.NewGuid().ToString("N")[..8]}.sock");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();

        List<Task> sessions = [];
        var accepting = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                Socket accepted = await listener.AcceptAsync(cts.Token);
                lock (sessions)
                {
                    sessions.Add(agent.ServeAsync(new NetworkStream(accepted, ownsSocket: true), cts.Token));
                }
            }
        });

        try
        {
            await using SshAgentClient client = await SshAgentClient.ConnectAsync(path, cts.Token);
            SshSessionProof jump = await ProofAsync(), target = await ProofAsync();

            await client.DeclareSessionAsync(jump, purpose, cts.Token);
            await client.DeclareSessionAsync(jump, purpose, cts.Token);   // 同一个会话：不重开、不再发
            await client.DeclareSessionAsync(target, purpose, cts.Token);

            Assert.AreEqual(expectedConnections, agent.Connections);
            TestSessionDeclaration[] declarations = [.. agent.Declarations];
            Assert.HasCount(2, declarations);
            Assert.AreEqual(expectedConnections, declarations[1].Connection, "后一个会话的声明落在重开的连接上");
            Assert.IsEmpty(await client.ListIdentitiesAsync(cts.Token), "之后照常能用");
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await accepting;
            }
            catch (OperationCanceledException)
            {
                // 收尾。
            }

            Task[] pending;
            lock (sessions)
            {
                pending = [.. sessions];
            }
            await Task.WhenAll(pending);
            File.Delete(path);
        }
    }

    /// <summary><c>FromStream</c> 交来的流重开不了：照旧在原连接上声明。</summary>
    [TestMethod]
    public async Task 交来的流不重开()
    {
        await using Rig rig = new();
        await rig.Client.DeclareSessionAsync(await ProofAsync(), SshAgentConnectionPurpose.Authentication, rig.Token);
        await rig.Client.DeclareSessionAsync(await ProofAsync(), SshAgentConnectionPurpose.Authentication, rig.Token);

        Assert.AreEqual(1, rig.Agent.Connections);
        Assert.HasCount(2, rig.Agent.Declarations);
    }
}
