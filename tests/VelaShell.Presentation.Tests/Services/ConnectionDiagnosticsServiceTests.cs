using System.Net;
using System.Net.Sockets;
using System.Text;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;

namespace VelaShell.Presentation.Tests.Services;

/// <summary>连接诊断的第 4 步(用户认证)对 Tailscale SSH 配置的判断:它没有密码 / 密钥可存,只要远端用户名。</summary>
[TestClass]
public sealed class ConnectionDiagnosticsServiceTests
{
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IConnectionWorkflowService _workflow = Substitute.For<IConnectionWorkflowService>();

    /// <summary>曾经落到「未保存凭据」:跳过认证测试,还建议去保存密码 / 密钥 —— Tailscale 根本没有这些。</summary>
    [TestMethod]
    public async Task Tailscale_WithUsername_RunsTheAuthenticationTest()
    {
        await using SshBannerListener server = new();
        SessionProfile profile = Tailscale(server.Port, "ops");
        _workflow.TestConnectionAsync(profile, Arg.Any<SshPacketTrace>(), Arg.Any<CancellationToken>()).Returns(new ConnectionTestResult(true));

        DiagnosticReport report = await Service().DiagnoseAsync(profile);

        Assert.AreEqual(DiagnosticStepStatus.Success, report.Steps[3].Status);
        await _workflow.Received(1).TestConnectionAsync(profile, Arg.Any<SshPacketTrace>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(Strings.Get("DiagSvc_SuggestSaveCreds"), report.Suggestions);
    }

    [TestMethod]
    public async Task Tailscale_WithoutUsername_AsksForTheUsernameInsteadOfSavedCredentials()
    {
        await using SshBannerListener server = new();
        SessionProfile profile = Tailscale(server.Port, " ");

        DiagnosticReport report = await Service().DiagnoseAsync(profile);

        Assert.AreEqual(DiagnosticStepStatus.Warning, report.Steps[3].Status);
        Assert.AreEqual(Strings.Get("DiagSvc_TailscaleNoUsernameSkip"), report.Steps[3].Detail);
        Assert.AreSequenceEqual([Strings.Get("DiagSvc_SuggestTailscaleUsername")], [.. report.Suggestions]);
        await _workflow.DidNotReceive().TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<SshPacketTrace>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Tailscale_AuthorizationFailure_SuggestsTailnetPolicyNotPasswords()
    {
        await using SshBannerListener server = new();
        SessionProfile profile = Tailscale(server.Port, "ops");
        string reason = $"{Strings.Get("SshErr_TailscaleRejected")}\nops@127.0.0.1:{server.Port}";
        _workflow.TestConnectionAsync(profile, Arg.Any<SshPacketTrace>(), Arg.Any<CancellationToken>()).Returns(new ConnectionTestResult(false, reason));

        DiagnosticReport report = await Service().DiagnoseAsync(profile);

        Assert.AreEqual(DiagnosticStepStatus.Failed, report.Steps[3].Status);
        Assert.AreEqual(Strings.Get("DiagSvc_AuthFailed"), report.IssueTitle);
        Assert.AreSequenceEqual([Strings.Get("DiagSvc_SuggestTailscale")], [.. report.Suggestions]);
    }

    /// <summary>
    /// 认证那一步挂上协议轨迹:诊断交给测试连接一份轨迹,连接期间记下的报文原样进报告;
    /// 不走不挂轨迹的那个重载 —— 平常的「测试连接」不挂旁路,诊断才挂。
    /// </summary>
    [TestMethod]
    public async Task AuthStep_RecordsAProtocolTraceIntoTheReport()
    {
        await using SshBannerListener server = new();
        SessionProfile profile = Tailscale(server.Port, "ops");
        _workflow.TestConnectionAsync(profile, Arg.Any<SshPacketTrace>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                SshPacketTrace trace = call.Arg<SshPacketTrace>();
                trace.Record("ops@127.0.0.1:22", SshPacketDirection.Sent, 20, 1200, 0, null);
                trace.Record("ops@127.0.0.1:22", SshPacketDirection.Received, 20, 900, 0, null);
                return new ConnectionTestResult(true);
            });

        DiagnosticReport report = await Service().DiagnoseAsync(profile);

        Assert.HasCount(2, report.PacketTrace);
        Assert.AreEqual((SshPacketDirection.Sent, (byte)20, (int?)1200), (report.PacketTrace[0].Direction, report.PacketTrace[0].MessageNumber, report.PacketTrace[0].Length));
        await _workflow.DidNotReceive().TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    /// <summary>没走到认证那一步(没有凭据):报告里没有轨迹,也没去连。</summary>
    [TestMethod]
    public async Task NoAuthStep_LeavesTheTraceEmpty()
    {
        await using SshBannerListener server = new();
        DiagnosticReport report = await Service().DiagnoseAsync(Tailscale(server.Port, " "));

        Assert.IsEmpty(report.PacketTrace);
        Assert.AreEqual(0, report.PacketTraceDropped);
    }

    /// <summary>
    /// 认证那一步(完整连接)失败时,报告带上原始异常:协商就发生在这一步,
    /// 诊断窗口据它给出「放开这几个算法并重新诊断」,健康巡检据它分出要人确认的那一类。
    /// </summary>
    [TestMethod]
    public async Task AuthenticationStepFailure_CarriesTheOriginalException()
    {
        await using SshBannerListener server = new();
        SessionProfile profile = Tailscale(server.Port, "ops");
        var error = new InvalidOperationException("算法谈不成");
        _workflow.TestConnectionAsync(profile, Arg.Any<SshPacketTrace>(), Arg.Any<CancellationToken>())
                 .Returns(new ConnectionTestResult(false, error.Message, error));

        DiagnosticReport report = await Service().DiagnoseAsync(profile);

        Assert.AreEqual(DiagnosticStepStatus.Failed, report.Steps[3].Status);
        Assert.AreSame(error, report.Failure);
    }

    /// <summary>巡检那一路:认证这一步走「没人看着」的测试,不走会弹框的那一个。</summary>
    [TestMethod]
    public async Task Unattended_UsesTheUnattendedTest()
    {
        await using SshBannerListener server = new();
        SessionProfile profile = Tailscale(server.Port, "ops");
        _workflow.TestConnectionUnattendedAsync(profile, Arg.Any<CancellationToken>()).Returns(new ConnectionTestResult(true));

        DiagnosticReport report = await Service().DiagnoseUnattendedAsync(profile, verifyLogin: true);

        Assert.AreEqual(DiagnosticStepStatus.Success, report.Steps[3].Status);
        await _workflow.Received(1).TestConnectionUnattendedAsync(profile, Arg.Any<CancellationToken>());
        await _workflow.DidNotReceive().TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    /// <summary>关掉「验证登录」:只查到 SSH 握手,不往服务器上登录;认证那一步记成跳过、不算问题。</summary>
    [TestMethod]
    public async Task Unattended_WithoutLogin_StopsAtTheHandshake()
    {
        await using SshBannerListener server = new();
        SessionProfile profile = Tailscale(server.Port, "ops");

        DiagnosticReport report = await Service().DiagnoseUnattendedAsync(profile, verifyLogin: false);

        Assert.IsTrue(report.Success);
        Assert.AreEqual(DiagnosticStepStatus.Success, report.Steps[2].Status);
        Assert.AreEqual(DiagnosticStepStatus.Skipped, report.Steps[3].Status);
        Assert.AreEqual(Strings.Get("DiagSvc_LoginNotVerified"), report.Steps[3].Detail);
        await _workflow.DidNotReceive().TestConnectionUnattendedAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
        await _workflow.DidNotReceive().TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 存着的密码在这台设备上解不开:认证那一步不拿密文去试(白白一次失败认证),标成「要人补」并说明原因 ——
    /// 巡检里归到「需要凭据」(第二轮第 20 条)。
    /// </summary>
    [TestMethod]
    public async Task UnreadableStoredPassword_SkipsTheLoginAttempt_WithAReason()
    {
        await using SshBannerListener server = new();
        var profile = new SessionProfile
        {
            Name = "web", Host = "127.0.0.1", Port = server.Port, Username = "root",
            AuthMethod = AuthMethod.Password, Password = "enc1:AAAAAAAAAAAAAAAAAAAAAAAA"
        };

        DiagnosticReport report = await Service().DiagnoseAsync(profile);

        Assert.AreEqual(DiagnosticStepStatus.Warning, report.Steps[3].Status);
        Assert.AreEqual(Strings.Get("Cred_Failure_StoredSecretUnreadable"), report.Steps[3].Detail);
        await _workflow.DidNotReceive().TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<SshPacketTrace>(), Arg.Any<CancellationToken>());
        await _workflow.DidNotReceive().TestConnectionUnattendedAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    private ConnectionDiagnosticsService Service() => new(_sessions, _workflow);

    private static SessionProfile Tailscale(int port, string username) => new()
    {
        Name = "node",
        Host = "127.0.0.1",
        Port = port,
        Username = username,
        AuthMethod = AuthMethod.Tailscale
    };

    /// <summary>只发一行 SSH 识别串的本机监听:让前三步(DNS / TCP / 握手)通过,诊断才会走到认证那一步。</summary>
    private sealed class SshBannerListener : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public SshBannerListener()
        {
            _listener.Start();
            _serving = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("SSH-2.0-DiagTest\r\n"), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
            {
                // 测试结束。
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _serving;
            _stop.Dispose();
        }
    }
}
