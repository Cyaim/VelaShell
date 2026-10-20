using System.Net;
using System.Net.Sockets;
using System.Text;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
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
        _workflow.TestConnectionAsync(profile, Arg.Any<CancellationToken>()).Returns(new ConnectionTestResult(true));

        DiagnosticReport report = await Service().DiagnoseAsync(profile);

        Assert.AreEqual(DiagnosticStepStatus.Success, report.Steps[3].Status);
        await _workflow.Received(1).TestConnectionAsync(profile, Arg.Any<CancellationToken>());
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
        await _workflow.DidNotReceive().TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Tailscale_AuthorizationFailure_SuggestsTailnetPolicyNotPasswords()
    {
        await using SshBannerListener server = new();
        SessionProfile profile = Tailscale(server.Port, "ops");
        string reason = $"{Strings.Get("SshErr_TailscaleRejected")}\nops@127.0.0.1:{server.Port}";
        _workflow.TestConnectionAsync(profile, Arg.Any<CancellationToken>()).Returns(new ConnectionTestResult(false, reason));

        DiagnosticReport report = await Service().DiagnoseAsync(profile);

        Assert.AreEqual(DiagnosticStepStatus.Failed, report.Steps[3].Status);
        Assert.AreEqual(Strings.Get("DiagSvc_AuthFailed"), report.IssueTitle);
        Assert.AreSequenceEqual([Strings.Get("DiagSvc_SuggestTailscale")], [.. report.Suggestions]);
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
