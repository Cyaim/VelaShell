// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md §1、§2.2、§6.3

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Ssh.Tests.Session;

/// <summary>配置的非法值在设值时就抛、成员不可变 —— 而不是等连上了才以一种难以理解的方式出事。</summary>
[TestClass]
[TestCategory("Session")]
public sealed class OptionValidationTests
{
    /// <summary>曾经 <c>MaxQueuedReplyBytes = 0</c> 会让第一条应答就把连接判死。</summary>
    [TestMethod]
    public void 连接限额的非法值在设值时就抛()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshConnectionLimits { MaxChannels = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshConnectionLimits { SessionWindowBudgetBytes = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshConnectionLimits { MaxQueuedReplyBytes = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshConnectionLimits { MaxQueuedReplyBytes = -1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshConnectionLimits { ChannelIdReuseDelay = TimeSpan.FromTicks(-1) });

        SshConnectionLimits edge = new() { MaxChannels = 1, SessionWindowBudgetBytes = 1, MaxQueuedReplyBytes = 1, ChannelIdReuseDelay = TimeSpan.Zero };
        Assert.AreEqual(1, edge.MaxChannels);
        Assert.AreEqual(TimeSpan.Zero, edge.ChannelIdReuseDelay);
    }

    /// <summary>曾经 0 或负数照样转成 uint 宣告给对端 —— 一个对端无法遵守的值。</summary>
    [TestMethod]
    public void 通道宣告的max_packet不为正时设值就抛()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshChannelOptions { ReceiveMaxPacketBytes = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshChannelOptions { ReceiveMaxPacketBytes = -1 });
        Assert.AreEqual(1, new SshChannelOptions { ReceiveMaxPacketBytes = 1 }.ReceiveMaxPacketBytes);
    }

    /// <summary>
    /// 环境变量设值时复制一份：之后改传进来的字典不影响选项；静态默认值不可写。
    /// 曾经默认值是经 <c>Default</c> 共享的可写 <c>Dictionary</c>，一处改动改掉全局默认。
    /// </summary>
    [TestMethod]
    public void 环境变量选项不可变且不跟着传入的字典变()
    {
        var shared = (IDictionary<string, string>)SshCommandOptions.Default.Environment;
        Assert.IsTrue(shared.IsReadOnly);
        Assert.ThrowsExactly<NotSupportedException>(() => shared["LANG"] = "C");
        Assert.IsEmpty(SshShellOptions.Default.Environment);

        Dictionary<string, string> source = new() { ["B"] = "2", ["A"] = "1" };
        SshShellOptions options = new() { Environment = source };
        source["C"] = "3";
        source["A"] = "changed";

        Assert.AreSequenceEqual(
            [new KeyValuePair<string, string>("B", "2"), new KeyValuePair<string, string>("A", "1")], [.. options.Environment], "复制时保留原来的顺序，之后的改动不影响");
        Assert.ThrowsExactly<ArgumentNullException>(() => new SshCommandOptions { Environment = null! });
    }

    /// <summary>
    /// 算法清单设值时抄一份只读的：调用方之后改自己的 List 不影响已经交出去的清单（连接把它存进重协商的上下文）；
    /// 默认清单下转型也改不了。
    /// </summary>
    [TestMethod]
    public void 算法清单设值后调用方再改也不影响()
    {
        List<string> kex = ["curve25519-sha256"];
        SshAlgorithmSet set = SshAlgorithmSet.Default with { KeyExchange = kex };
        kex.Add("diffie-hellman-group1-sha1");

        Assert.AreSequenceEqual(["curve25519-sha256"], [.. set.KeyExchange]);
        Assert.IsNotInstanceOfType<string[]>(SshAlgorithmSet.Default.EncryptionClientToServer, "下转型就能改默认清单");
        Assert.IsNotInstanceOfType<List<string>>(set.KeyExchange);
        Assert.ThrowsExactly<ArgumentNullException>(() => SshAlgorithmSet.Default with { HostKey = null! });
    }

    /// <summary>清单设值时会抄一份，相等比较因此按内容（含顺序）而不是按引用。</summary>
    [TestMethod]
    public void 算法清单按内容比较相等()
    {
        SshAlgorithmSet copy = SshAlgorithmSet.Default with { KeyExchange = [.. SshAlgorithmSet.Default.KeyExchange] };

        Assert.AreEqual(SshAlgorithmSet.Default, copy);
        Assert.AreEqual(SshAlgorithmSet.Default.GetHashCode(), copy.GetHashCode());
        Assert.AreNotEqual(SshAlgorithmSet.Default, copy with { KeyExchange = [.. copy.KeyExchange.Reverse()] }, "顺序就是偏好");
    }

    /// <summary>agent 转发的「只给这几把钥」设值后，调用方再往自己的 List 里加钥不影响。</summary>
    [TestMethod]
    public void 只转发的钥设值后调用方再改也不影响()
    {
        using var first = InMemorySshSigner.GenerateEd25519();
        using var second = InMemorySshSigner.GenerateEd25519();
        List<SshPublicKey> keys = [first.PublicKey];
        AgentForwardOptions options = new() { AllowedKeys = keys };
        keys.Add(second.PublicKey);

        Assert.HasCount(1, options.AllowedKeys);
        Assert.IsNull(new AgentForwardOptions { AllowedKeys = null }.AllowedKeys, "null 仍然表示不限");
    }

    /// <summary>SFTP 属性的扩展字段设值后，调用方再改自己的 List 不影响。</summary>
    [TestMethod]
    public void SFTP扩展属性设值后调用方再改也不影响()
    {
        List<SftpExtendedField> extended = [new("a@example.com", new byte[] { 1 })];
        SftpFileAttributes attributes = new() { Flags = SftpAttributeFields.Extended, Extended = extended };
        extended.Add(new("b@example.com", new byte[] { 2 }));

        Assert.HasCount(1, attributes.Extended);
        Assert.IsEmpty(default(SftpFileAttributes).Extended);
    }

    /// <summary>
    /// 互相矛盾的参数当场就抛，而不是静默忽略其中一个：
    /// 给了询问回调又不问（回调永远不会被调用）；agent 的端点与连接器都给（连哪一个说不清）。
    /// </summary>
    [TestMethod]
    public void 互相矛盾的参数当场就抛()
    {
        static ValueTask<bool> Ask(SshHostKeyContext context, CancellationToken token) => ValueTask.FromResult(true);

        Assert.ThrowsExactly<ArgumentException>(() => new KnownHostsPolicy(askUnknownHost: Ask) { UnknownHost = UnknownHostBehavior.Reject });
        Assert.ThrowsExactly<ArgumentException>(() => KnownHostsPolicy.WithoutFile(Ask, UnknownHostBehavior.AcceptAndPersist));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new KnownHostsPolicy { UnknownHost = (UnknownHostBehavior)42 });
        Assert.AreEqual(UnknownHostBehavior.Ask, new KnownHostsPolicy(askUnknownHost: Ask) { UnknownHost = UnknownHostBehavior.Ask }.UnknownHost);

        static ValueTask<SshAgentClient> Connector(CancellationToken token) => throw new InvalidOperationException();
        Assert.ThrowsExactly<ArgumentException>(() => new AgentForwardOptions { AgentEndpoint = "/tmp/agent.sock", LocalConnector = Connector });
        Assert.ThrowsExactly<ArgumentException>(() => new AgentForwardOptions { LocalConnector = Connector, AgentEndpoint = "/tmp/agent.sock" });
        Assert.IsNull((new AgentForwardOptions { LocalConnector = Connector } with { AgentEndpoint = null }).AgentEndpoint);
    }

    /// <summary>枚举的零值是安全的那个（未知 / 拒绝）：<c>default</c> 出来的不该是一个具体的、偏宽松的取值。</summary>
    [TestMethod]
    public void 枚举的零值是安全的那个()
    {
        Assert.AreEqual(nameof(UnknownHostBehavior.Reject), Enum.GetName(typeof(UnknownHostBehavior), 0));
        Assert.AreEqual(nameof(ForwardErrorReason.Unknown), Enum.GetName(typeof(ForwardErrorReason), 0));
        Assert.AreEqual(nameof(Diagnostics.SshDisconnectReason.Unknown), Enum.GetName(typeof(Diagnostics.SshDisconnectReason), 0u));
        Assert.AreEqual(typeof(uint), Enum.GetUnderlyingType(typeof(Diagnostics.SshDisconnectReason)), "与线上的 uint32 一致");
        Assert.AreEqual(UnknownHostBehavior.Ask, new KnownHostsPolicy().UnknownHost, "策略的默认值仍然是问");
    }

    /// <summary>异常消息是纯文本，不带 Markdown 记号：到了界面上反引号会原样显示出来。</summary>
    [TestMethod]
    public void 异常消息里没有反引号()
    {
        SshCommandResult failed = new(new SshExitStatus(1, null, false, null), "", "boom");
        SshCommandFailedException error = Assert.ThrowsExactly<SshCommandFailedException>(() => failed.EnsureSuccess("ls -la"));

        Assert.DoesNotContain("`", error.Message);
        Assert.Contains("ls -la", error.Message);
    }

    /// <summary>终端模式只有一个入口：没有名字的操作码强转过来照样能设；结束标记与保留区当场就抛。</summary>
    [TestMethod]
    public void 终端模式的操作码()
    {
        SshTerminalModes modes = SshTerminalModes.Empty.With((SshTerminalModeOpcode)42, 7);
        Assert.AreSequenceEqual(new byte[] { 42, 0, 0, 0, 7, 0 }, modes.Encode());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SshTerminalModes.Empty.With(SshTerminalModeOpcode.EndOfOptions, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SshTerminalModes.Empty.With((SshTerminalModeOpcode)160, 1));
    }

    /// <summary>曾经超过约 24.8 天的间隔让保活循环里的 <c>(int)</c> 溢出，循环静默退出 —— 设了保活等于没设。</summary>
    [TestMethod]
    public void 保活间隔超过上限时构造就抛()
    {
        Assert.AreEqual(SshKeepAlivePolicy.MaxInterval, new SshKeepAlivePolicy(SshKeepAlivePolicy.MaxInterval).Interval);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SshKeepAlivePolicy(SshKeepAlivePolicy.MaxInterval + TimeSpan.FromTicks(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshKeepAlivePolicy(TimeSpan.FromDays(30)));
    }
}
