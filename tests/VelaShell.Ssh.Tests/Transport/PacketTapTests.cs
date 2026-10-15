// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/08-failures.md §9

using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Transport;

/// <summary>
/// 报文旁路的三条硬规则：默认不启用；载荷默认不给；认证报文的载荷永远不给（没有开关）。
/// 外加：旁路自己出错不弄坏连接；通道消息带着通道号。
/// </summary>
[TestClass]
[TestCategory("Diagnostics")]
public sealed class PacketTapTests
{
    public TestContext TestContext { get; set; } = null!;

    private sealed record Seen(PacketDirection Direction, byte Number, int Length, uint Sequence, uint? Channel, int PayloadLength);

    private sealed class RecordingTap(bool throwEachTime = false) : IPacketTap
    {
        public List<Seen> Records { get; } = [];

        public void OnPacket(in PacketTapRecord record)
        {
            lock (Records)
            {
                Records.Add(new Seen(record.Direction, record.MessageNumber, record.Length, record.SequenceNumber, record.ChannelNumber, record.Payload.Length));
            }
            if (throwEachTime)
            {
                throw new InvalidOperationException("旁路自己的错");
            }
        }
    }

    private async Task<SshCommandResult> ConnectAndRunAsync(IPacketTap tap, bool includePayload)
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        List<Task> servers = [];
        SshConnectionOptions options = new("joe@tap.example:22")
        {
            Dialer = InMemoryTransport.CreateDialer((server, _, _) =>
            {
                servers.Add(Task.Run(() => ServeAsync(server, lifetime.Token), CancellationToken.None));
                return ValueTask.CompletedTask;
            }),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            PacketTap = tap,
            AllowPacketTapPayload = includePayload,
        };

        SshCommandResult result;
        await using (SshConnection connection = await SshConnection.ConnectAsync(options, TestContext.CancellationToken))
        {
            result = await connection.RunAsync("echo", cancellationToken: TestContext.CancellationToken);
        }
        await lifetime.CancelAsync();
        await Task.WhenAll(servers);
        return result;
    }

    private static async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            await using TestSshServer server = new(stream);
            TestSshServerHandshake handshake = await server.HandshakeAsync(cancellationToken);
            TestAuthServer auth = new(server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "hunter2" });
            await auth.RunAsync(cancellationToken);
            TestChannelServer channels = new(server.Transport, new TestChannelScript { StandardOutput = Encoding.UTF8.GetBytes("tapped"), ExitCode = 0 });
            await channels.RunAsync(cancellationToken);
        }
        catch (Exception)
        {
            // 客户端走了、用例拆场。
        }
    }

    /// <summary>默认：两个方向都看得到，元信息齐全，载荷一律为空；通道消息带着通道号。</summary>
    [TestMethod]
    public async Task 默认只给元信息_载荷为空()
    {
        RecordingTap tap = new();

        SshCommandResult result = await ConnectAndRunAsync(tap, includePayload: false);

        Assert.AreEqual("tapped", result.StandardOutput);
        Seen[] records = [.. tap.Records];
        Assert.IsTrue(records.All(r => r.PayloadLength == 0), "没打开时一个字节的载荷都不给");
        Seen firstOut = records.First(r => r.Direction == PacketDirection.Outbound);
        Assert.AreEqual((byte)SshMessageNumber.KexInit, firstOut.Number);
        Assert.AreEqual(0u, firstOut.Sequence);
        Assert.Contains(r => r.Direction == PacketDirection.Inbound && r.Number == (byte)SshMessageNumber.KexInit, records);
        Assert.Contains(r => r.Number == (byte)SshMessageNumber.ChannelData && r.Channel is not null, records);
        Assert.IsTrue(records.All(r => r.Length > 0));
    }

    /// <summary>打开载荷：别的报文给，认证报文（50–79，含口令）照样不给。</summary>
    [TestMethod]
    public async Task 打开载荷之后认证报文的载荷照样不给()
    {
        RecordingTap tap = new();

        await ConnectAndRunAsync(tap, includePayload: true);

        Seen[] records = [.. tap.Records];
        Seen[] auth = [.. records.Where(r => r.Number is >= 50 and <= 79)];
        Assert.IsNotEmpty(auth);
        Assert.IsTrue(auth.All(r => r.PayloadLength == 0), "认证报文的载荷（口令就在里面）无论如何都不给");
        Assert.Contains(r => r.Number == (byte)SshMessageNumber.KexInit && r.PayloadLength == r.Length, records);
    }

    [TestMethod]
    public async Task SensitiveLeasesSuppressBothDirectionsOverlapAndRestoreOnlyOriginalSettings()
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        List<Task> servers = [];
        RecordingTap tap = new();
        await using SshConnection connection = await SshConnection.ConnectAsync(new SshConnectionOptions("joe@tap.example:22")
        {
            Dialer = InMemoryTransport.CreateDialer((server, _, _) =>
            {
                servers.Add(ServeAsync(server, lifetime.Token));
                return ValueTask.CompletedTask;
            }),
            Credentials = [new PasswordCredential("hunter2")],
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            PacketTap = tap,
            AllowPacketTapPayload = true,
        }, TestContext.CancellationToken);
        IAsyncDisposable first = connection.SuppressSensitivePayload();
        IAsyncDisposable second = connection.SuppressSensitivePayload();
        lock (tap.Records) { tap.Records.Clear(); }
        await first.DisposeAsync();
        await first.DisposeAsync();
        await connection.RunAsync("sensitive", cancellationToken: TestContext.CancellationToken);
        Seen[] protectedRecords;
        lock (tap.Records) { protectedRecords = [.. tap.Records]; tap.Records.Clear(); }
        Assert.IsNotEmpty(protectedRecords);
        Assert.IsTrue(protectedRecords.All(record => record.PayloadLength == 0));
        Assert.Contains(record => record.Direction == PacketDirection.Inbound, protectedRecords);
        Assert.Contains(record => record.Direction == PacketDirection.Outbound, protectedRecords);
        Assert.IsTrue(protectedRecords.All(record => record.Length > 0));
        await second.DisposeAsync();
        await connection.RunAsync("normal", cancellationToken: TestContext.CancellationToken);
        lock (tap.Records) { Assert.Contains(record => record.PayloadLength > 0, tap.Records); }
        await connection.DisposeAsync();
        Assert.ThrowsExactly<ObjectDisposedException>(() => connection.SuppressSensitivePayload());
        await second.DisposeAsync();
        await lifetime.CancelAsync();
        await Task.WhenAll(servers);
    }

    /// <summary>旁路每次都抛：连接照常建起来、命令照常跑完。</summary>
    [TestMethod]
    public async Task 旁路出错不弄坏连接()
    {
        RecordingTap tap = new(throwEachTime: true);

        SshCommandResult result = await ConnectAndRunAsync(tap, includePayload: false);

        Assert.AreEqual("tapped", result.StandardOutput);
        Assert.IsNotEmpty(tap.Records);
    }
}
