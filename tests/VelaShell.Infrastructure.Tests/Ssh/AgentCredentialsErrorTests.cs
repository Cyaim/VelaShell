using System.Buffers.Binary;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Transport;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>agent 列不出钥时,提示与连不上 agent 那一条一样按原因码本地化,不塞库的原文。</summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class AgentCredentialsErrorTests
{
    [TestMethod]
    public async Task Agent列不出钥时提示按原因码本地化()
    {
        (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();
        var server = Task.Run(async () =>
        {
            // 读一条请求,回 SSH_AGENT_FAILURE(5):列身份时回这个,库报协议错误。
            byte[] header = new byte[4];
            await theirs.ReadExactlyAsync(header, TestContext.CancellationToken);
            await theirs.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt32BigEndian(header)], TestContext.CancellationToken);
            await theirs.WriteAsync(new byte[] { 0, 0, 0, 1, 5 }, TestContext.CancellationToken);
            await theirs.FlushAsync(TestContext.CancellationToken);
        }, TestContext.CancellationToken);
        await using var agent = SshAgentClient.FromStream(ours, "(假 agent)");

        VelaSshAuthenticationException ex = await Assert.ThrowsExactlyAsync<VelaSshAuthenticationException>(
            async () => await SshConnectionAssembler.AgentCredentialsAsync(agent, TestContext.CancellationToken));
        await server;

        var inner = (SshAgentException)ex.InnerException!;
        Assert.AreEqual(Strings.Format("SshErr_AgentUnavailable", SshInterop.Localize(inner)), ex.Message);
        Assert.DoesNotContain(inner.Message, ex.Message, "提示里不该塞库的原文。");
    }

    public TestContext TestContext { get; set; } = null!;
}
