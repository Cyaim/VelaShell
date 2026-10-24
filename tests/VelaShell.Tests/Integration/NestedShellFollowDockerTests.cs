using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Terminal.Rendering;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.Integration;

/// <summary>
/// 嵌套 shell 的目录跟随(#637),按宿主真正的那条链路走一遍:真 sshd、真 PTY、真终端控件、真桥,
/// 用户只是在终端里敲字 —— 没有任何一步是测试替它做的。
/// </summary>
/// <remarks>
/// <para>
/// 探针怎么认前台、补注那一行在各 shell 里跑不跑得通,<c>ShellIntegrationDockerTests</c> 已经分开验过;
/// 这里钉的是把它们串起来的那一层:回车经桥报出 <c>LineSubmitted</c> → 等终端安静 → 发现没有上报 → 问前台 →
/// 带着输入代次写出补注 → 注入窗口把回显吞掉 → 新 shell 的上报经仿真器回到标签上。
/// </para>
/// <para>
/// 靶子是 <c>docker-compose.test.yml</c> 的 <c>ssh-shells</c>(端口 2223);起不来就报 Inconclusive。
/// </para>
/// </remarks>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 SSH 操作不接受测试取消令牌,协作取消无法中断它们。")]
[TestClass]
[TestCategory("DockerIntegration")]
public sealed class NestedShellFollowDockerTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 2223;
    private const string Password = "velapass";

    private static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(NestedShellFollowDockerTests).Assembly);

    [TestInitialize]
    public void RequireContainer()
    {
        try
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(3)))
            {
                Assert.Inconclusive($"多 shell 测试服务器 {Host}:{Port} 不可达。");
            }
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            Assert.Inconclusive($"多 shell 测试服务器 {Host}:{Port} 不可达:{ex.Message}");
        }
        // su 要 busybox-suid、sudo 要 sudoers 那一行,都是 #637 才加进 Dockerfile 的:镜像旧了就跳过而不是红。
        FixtureImage.RequireCurrent("ssh-shells", "ssh-shells", Host, Port, "vela-bash", Password);
    }

    /// <summary>
    /// issue 的原样复现:登录 shell 装着钩子 → 敲 <c>su - vela-zsh</c>、输密码 → 不做任何别的事,
    /// 新 shell 的目录就报了上来;在里面 <c>cd</c> 照样跟,<c>exit</c> 回来登录 shell 也照样跟。
    /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public Task SuIntoAnotherUser_KeepsFollowingWithoutAnyUserAction() => Session.RunOnUiAsync(async () =>
    {
        await using VelaSshClientWrapper client = new(
            ct => SshConnection.ConnectAsync(new SshConnectionOptions("vela-bash", Host, Port)
            {
                Credentials = [new PasswordCredential(Password)],
                HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            }, ct),
            TimeSpan.FromSeconds(10));
        await client.ConnectAsync(CancellationToken.None);
        IShellStreamWrapper shell = await client.CreateShellStreamAsync(
            "xterm-256color", 120, 40, 0, 0, 4096, cancellationToken: CancellationToken.None);

        using var control = new VelaTerminalControl();
        await using var tab = new TerminalTabViewModel(control, shell)
        {
            ConnectionStatus = SessionStatus.Connected,
            RemoteShellKind = RemoteShellKind.Bash
        };
        var reported = new List<string>();
        tab.WorkingDirectoryChanged += path =>
        {
            lock (reported)
            {
                reported.Add(path);
            }
        };
        tab.Start();

        // 与宿主握手同序:先挂补注,再注登录 shell 的钩子(见 MainWindowViewModel.ArmNestedShellFollow)。
        await WaitQuietAsync(tab);
        tab.EnableNestedShellFollow(ct => ForegroundShellProbe.DetectAsync(client, ct), () => true);
        tab.SendShellIntegration(ShellIntegrationScript.For(RemoteShellKind.Bash), ShellIntegrationScript.FunctionName);
        await WaitForAsync(() => Reported(reported, "/home/vela-bash"), "登录 shell 的首报", control);

        Type(control, "su - vela-zsh\r");
        await WaitForAsync(() => control.GetBufferLine(control.CursorRow).Contains("Password", StringComparison.Ordinal),
            "su 的密码提示", control);
        Type(control, Password + "\r");

        await WaitForAsync(() => Reported(reported, "/home/vela-zsh"), "新 shell 自动补注后的首报", control);
        Type(control, "cd /tmp\r");
        await WaitForAsync(() => Reported(reported, "/tmp"), "新 shell 里 cd 之后的上报", control);
        Assert.DoesNotContain(ShellIntegrationScript.FunctionName, ScreenText(control), "补注的那一行露在了屏幕上");

        Type(control, "exit\r");
        await WaitQuietAsync(tab);
        Type(control, "cd /etc\r");
        await WaitForAsync(() => Reported(reported, "/etc"), "exit 回到登录 shell 之后的上报", control);
    });

    private static void Type(VelaTerminalControl control, string text) => control.WriteInput(Encoding.UTF8.GetBytes(text));

    private static bool Reported(List<string> reported, string path)
    {
        lock (reported)
        {
            return reported.Contains(path);
        }
    }

    private static async Task WaitQuietAsync(TerminalTabViewModel tab)
    {
        for (int i = 0; i < 100 && DateTime.UtcNow - (tab.Bridge?.LastOutputUtc ?? DateTime.UtcNow) < TimeSpan.FromMilliseconds(500); i++)
        {
            await Task.Delay(50);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, VelaTerminalControl control)
    {
        for (int i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(50);
        }
        Assert.IsTrue(condition(), $"等不到「{what}」。\n--- 屏幕 ---\n{ScreenText(control)}");
    }

    private static string ScreenText(VelaTerminalControl control)
    {
        var sb = new StringBuilder();
        for (int row = 0; row <= control.CursorRow; row++)
        {
            sb.Append(control.GetBufferLine(row).TrimEnd()).Append('\n');
        }
        return sb.ToString();
    }
}
