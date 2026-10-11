// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §3.5、§4.2;RFC 4419

using System.Runtime.CompilerServices;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Crypto.Kex;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Crypto;

/// <summary>
/// 群交换（<c>diffie-hellman-group-exchange-sha256</c>）：只开它的服务端连得上；服务端给的群不合格时不连 ——
/// 太小（logjam）、不是素数、生成元越界，各报各的原因。线上格式与交换哈希另由对真 OpenSSH 的互通用例核对。
/// </summary>
[TestClass]
[TestCategory("Kex")]
public sealed class GroupExchangeTests
{
    public TestContext TestContext { get; set; } = null!;

    private static readonly SshAlgorithmSet GroupExchangeOnly = SshAlgorithmSet.Default with
    {
        KeyExchange = [SshAlgorithmNames.DiffieHellmanGroupExchangeSha256],
    };

    private async Task<(SshConnection Connection, List<Task> Servers, CancellationTokenSource Lifetime)> ConnectAsync(
        TestSshServerOptions serverOptions)
    {
        CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(60));
        List<Task> servers = [];
        SshConnectionOptions options = new("joe@gex.example:22")
        {
            Dialer = InMemoryTransport.CreateDialer((server, _, _) =>
            {
                servers.Add(Task.Run(() => ServeAsync(server, serverOptions, lifetime.Token), CancellationToken.None));
                return ValueTask.CompletedTask;
            }),
            Algorithms = GroupExchangeOnly,
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        try
        {
            return (await SshConnection.ConnectAsync(options, TestContext.CancellationToken), servers, lifetime);
        }
        catch
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(servers);
            lifetime.Dispose();
            throw;
        }
    }

    private static async Task ServeAsync(Stream stream, TestSshServerOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await using TestSshServer server = new(stream, options with { Algorithms = GroupExchangeOnly });
            TestSshServerHandshake handshake = await server.HandshakeAsync(cancellationToken);
            TestAuthServer auth = new(server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "hunter2" });
            await auth.RunAsync(cancellationToken);
            TestChannelServer channels = new(server.Transport, new TestChannelScript { StandardOutput = Encoding.UTF8.GetBytes("gex"), ExitCode = 0 });
            await channels.RunAsync(cancellationToken);
        }
        catch (Exception)
        {
            // 客户端拒了这个群、走了，或用例拆场。
        }
    }

    private async Task<SshConnectException> ConnectExpectingFailureAsync(byte[] prime, byte[] generator)
    {
        SshConnectException failure = await Assert.ThrowsAsync<SshConnectException>(
            async () => await ConnectAsync(new TestSshServerOptions { GexGroup = (prime, generator) }));
        Assert.AreEqual(SshPhase.KeyExchange, failure.Phase);
        return failure;
    }

    /// <summary>只开群交换的服务端：请求是 2048 / 3072 / 8192，谈成之后命令照常跑。</summary>
    [TestMethod]
    public async Task 只开群交换的服务端连得上()
    {
        StrongBox<(uint, uint, uint)> request = new();
        (SshConnection connection, List<Task> servers, CancellationTokenSource lifetime) =
            await ConnectAsync(new TestSshServerOptions { ObservedGexRequest = request });
        try
        {
            Assert.AreEqual(SshAlgorithmNames.DiffieHellmanGroupExchangeSha256, connection.Algorithms.KeyExchange);
            Assert.AreEqual((2048u, 3072u, 8192u), request.Value);
            SshCommandResult result = await connection.RunAsync("echo", cancellationToken: TestContext.CancellationToken);
            Assert.AreEqual("gex", result.StandardOutput);
        }
        finally
        {
            await connection.DisposeAsync();
            await lifetime.CancelAsync();
            await Task.WhenAll(servers);
            lifetime.Dispose();
        }
    }

    /// <summary>1536 位的群：logjam 之后不接受（NegotiationFailed，spec/03 §9）。</summary>
    [TestMethod]
    public async Task 群小于2048位不接()
    {
        SshConnectException failure = await ConnectExpectingFailureAsync(
            DHStandardGroups.rfc3526_1536.P.ToByteArrayUnsigned(), DHStandardGroups.rfc3526_1536.G.ToByteArrayUnsigned());

        Assert.AreEqual(SshFailureReason.NegotiationFailed, failure.Reason);
        Assert.Contains("1536", failure.Message);
    }

    /// <summary>
    /// 两个大素数之积（2560 位、奇数、没有小因子）：便宜的检查全过，只有 Miller-Rabin 认得出它是合数（ProtocolError）。
    /// </summary>
    [TestMethod]
    public async Task P不是素数不接()
    {
        byte[] composite = DHStandardGroups.rfc3526_1536.P.Multiply(DHStandardGroups.rfc2409_1024.P).ToByteArrayUnsigned();

        SshConnectException failure = await ConnectExpectingFailureAsync(composite, [2]);

        Assert.IsInstanceOfType<SshKeyExchangeException>(failure);
        Assert.AreEqual(SshFailureReason.ProtocolError, failure.Reason);
        Assert.Contains("不是素数", failure.Message);
    }

    /// <summary>生成元为 1：<c>g^x</c> 恒为 1，共享密钥由对端说了算（ProtocolError）。</summary>
    [TestMethod]
    public async Task 生成元越界不接()
    {
        SshConnectException failure = await ConnectExpectingFailureAsync(
            DHStandardGroups.rfc3526_3072.P.ToByteArrayUnsigned(), [1]);

        Assert.AreEqual(SshFailureReason.ProtocolError, failure.Reason);
        Assert.Contains("生成元", failure.Message);
    }

    /// <summary>重协商同样走群交换：多一轮之后新密钥照样对得上，连接继续可用。</summary>
    [TestMethod]
    public async Task 群交换的重协商之后连接照常可用()
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(
            new TestChannelScript { StandardOutput = Encoding.UTF8.GetBytes("ok\n"), ExitCode = 0 },
            algorithms: GroupExchangeOnly);

        Assert.AreEqual(SshAlgorithmNames.DiffieHellmanGroupExchangeSha256, host.Connection.Algorithms.KeyExchange);
        await host.Channels.RequestRekeyAsync().WaitAsync(host.Token);

        SshCommandResult after = await host.Connection.RunAsync("ok", cancellationToken: host.Token);
        Assert.AreEqual("ok\n", after.StandardOutput);
        Assert.AreEqual(1, host.Connection.RekeyCount);
    }

    /// <summary>交换哈希：GEX 的五项插在 <c>K_S</c> 之后、<c>e</c> 之前，按 uint32 ×3 ‖ mpint ‖ mpint（spec/03 §4.2）。</summary>
    [TestMethod]
    public void 交换哈希的GEX输入插在主机公钥之后()
    {
        byte[] p = [0x80, 0x01];   // 最高位为 1：mpint 要补 0x00
        byte[] g = [0x02];
        static byte[] Compute(SshGroupExchangeHashInput? gex) => SshExchangeHash.Compute(System.Security.Cryptography.HashAlgorithmName.SHA256, new SshExchangeHashInput
        {
            ClientVersion = "C"u8,
            ServerVersion = "S"u8,
            ClientKexInit = [20],
            ServerKexInit = [20],
            HostKeyBlob = [0xAA],
            ClientPublicValue = [0x05],
            ServerPublicValue = [0x06],
            SharedSecret = [0x07],
            PublicValueEncoding = SshKexValueEncoding.Mpint,
            SharedSecretEncoding = SshKexValueEncoding.Mpint,
            GroupExchange = gex,
        });

        byte[] expectedInput =
        [
            0, 0, 0, 1, (byte)'C', 0, 0, 0, 1, (byte)'S', 0, 0, 0, 1, 20, 0, 0, 0, 1, 20, 0, 0, 0, 1, 0xAA,
            0, 0, 0x08, 0x00, 0, 0, 0x0C, 0x00, 0, 0, 0x20, 0x00,   // min ‖ n ‖ max
            0, 0, 0, 3, 0x00, 0x80, 0x01,                          // mpint p
            0, 0, 0, 1, 0x02,                                      // mpint g
            0, 0, 0, 1, 0x05, 0, 0, 0, 1, 0x06, 0, 0, 0, 1, 0x07,
        ];
        Assert.AreSequenceEqual(
            System.Security.Cryptography.SHA256.HashData(expectedInput), Compute(new SshGroupExchangeHashInput(2048, 3072, 8192, p, g)));
        Assert.AreNotSequenceEqual(Compute(null), Compute(new SshGroupExchangeHashInput(2048, 3072, 8192, p, g)));
    }
}
