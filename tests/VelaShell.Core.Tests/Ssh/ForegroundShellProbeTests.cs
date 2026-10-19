using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 嵌套 shell 探针的判定(#637:root 登录后 <c>su - node</c>,「跟随终端目录」就停了)。
/// </summary>
/// <remarks>
/// 样例是探针在 Debian 13(sshd + sudo 1.9 默认 <c>use_pty</c>)上的<b>真实输出</b>,
/// 只把同机另一条连接的进程留了几行作干扰。判定错一次的代价是往不该打字的地方打字,
/// 所以"认不出来"的样例与"认得出来"的一样多。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public class ForegroundShellProbeTests
{
    /// <summary>
    /// 探针输出的公共前缀:探针 sh(36)← sshd 会话进程(30)← sshd 特权进程(23)← 监听进程(1)。
    /// </summary>
    private const string Ancestors =
        """
        A 36 30
        A 30 23
        A 23 1

        """;

    /// <summary>登录 shell 自己在前台:它连接时就注入过了,什么都不该做。</summary>
    [TestMethod]
    public void Resolve_LoginShellAtPrompt_IsLoginShell()
    {
        ForegroundShell? shell = ForegroundShellProbe.Resolve(Ancestors + "P 31 30 31 34816 31 -bash \n");

        Assert.AreEqual(new ForegroundShell(31, 31, RemoteShellKind.Bash), shell);
        Assert.IsTrue(shell!.Value.IsLoginShell);
    }

    /// <summary>issue 里的那一幕:<c>su - node</c> 之后,前台是 node 的登录 shell(su 自己退到后面等着)。</summary>
    [TestMethod]
    public void Resolve_AfterSuDash_FindsNestedLoginShell()
    {
        const string output = Ancestors +
            """
            P 31 30 31 34816 41
            P 39 31 39 34816 41
            P 41 39 41 34816 41 -bash
            """;

        Assert.AreEqual(new ForegroundShell(31, 41, RemoteShellKind.Bash), ForegroundShellProbe.Resolve(output));
    }

    /// <summary>
    /// <c>sudo -i</c>:sudo 默认 <c>use_pty</c>,root 的 shell 在一个新的 pty(34817)上,
    /// 原终端的前台是 sudo 自己 —— 要沿着它的后代跳到那个终端上去认。
    /// </summary>
    [TestMethod]
    public void Resolve_SudoWithUsePty_FollowsRelayTerminal()
    {
        const string output = Ancestors +
            """
            P 31 30 31 34816 50
            P 39 31 39 34816 50
            P 41 39 41 34816 50
            P 50 41 50 34816 50 sudo -i
            P 52 50 52 34817 53
            P 53 52 53 34817 53 -bash
            """;

        Assert.AreEqual(new ForegroundShell(31, 53, RemoteShellKind.Bash), ForegroundShellProbe.Resolve(output));
    }

    /// <summary>
    /// fish 登录用户 <c>sudo su -</c> 后又敲了 <c>zsh</c>:中转终端上又套了一层。同机另一条连接
    /// (31 / 81,root 在跑 <c>sleep</c>)也在输出里 —— 它挂在别的 sshd 进程下,绝不能被认成我们的。
    /// </summary>
    [TestMethod]
    public void Resolve_SudoSuThenZsh_IgnoresOtherConnections()
    {
        const string output =
            """
            A 120 104
            A 104 98
            A 98 1
            P 105 104 105 34818 130
            P 130 105 130 34818 130 sudo su -
            P 132 130 132 34819 148
            P 133 132 133 34819 148
            P 135 133 135 34819 148
            P 148 135 148 34819 148 zsh
            P 31 30 31 34816 81
            P 81 31 81 34816 81 sleep 30
            """;

        Assert.AreEqual(new ForegroundShell(105, 148, RemoteShellKind.Zsh), ForegroundShellProbe.Resolve(output));
    }

    /// <summary>前台是一条外部命令(登录 shell 在跑 <c>sleep</c>):不是提示符,不认。</summary>
    [TestMethod]
    public void Resolve_CommandInForeground_IsNull() =>
        Assert.IsNull(ForegroundShellProbe.Resolve(Ancestors +
            """
            P 31 30 31 34816 81
            P 81 31 81 34816 81 sleep 30
            """));

    /// <summary>
    /// <c>su - node</c> 正在问密码:前台组长是 su。往这里打字就成了口令 —— 这是最不能认错的一种。
    /// </summary>
    [TestMethod]
    public void Resolve_SuAskingForPassword_IsNull() =>
        Assert.IsNull(ForegroundShellProbe.Resolve(Ancestors +
            """
            P 31 30 31 34816 39
            P 39 31 39 34816 39 su - node
            """));

    /// <summary>
    /// <c>ssh 另一台机器</c>:前台是 ssh 客户端。那边的 shell 目录在另一台机器上,SFTP 看不到,本来就不该跟。
    /// </summary>
    [TestMethod]
    public void Resolve_NestedSshClient_IsNull() =>
        Assert.IsNull(ForegroundShellProbe.Resolve(Ancestors +
            """
            P 31 30 31 34816 74
            P 74 31 74 34816 74 ssh -o StrictHostKeyChecking=no root@localhost
            """));

    /// <summary><c>bash deploy.sh</c> 卡在 <c>read</c> 上:前台组长是 bash,但它在跑脚本 —— 打字进去就成了它的回答。</summary>
    [TestMethod]
    public void Resolve_ScriptWaitingOnRead_IsNull() =>
        Assert.IsNull(ForegroundShellProbe.Resolve(Ancestors +
            """
            P 31 30 31 34816 90
            P 90 31 90 34816 90 bash ./deploy.sh
            """));

    /// <summary>
    /// <c>script</c> 里再 <c>sudo -i</c>:两层中转。最里层的 shell 也是 script 的后代,
    /// 但它和 script 之间隔着一层终端,只能一层一层往里认 —— 一次数出两个终端就会被判成认不出来。
    /// </summary>
    [TestMethod]
    public void Resolve_TwoRelayLevels_FollowsEachTerminalInTurn()
    {
        const string output = Ancestors +
            """
            P 31 30 31 34816 60
            P 60 31 60 34816 60 script -q /dev/null
            P 61 60 61 34820 70
            P 62 61 62 34820 70
            P 70 62 70 34820 70 sudo -i
            P 72 70 72 34821 73
            P 73 72 73 34821 73 -bash
            """;

        Assert.AreEqual(new ForegroundShell(31, 73, RemoteShellKind.Bash), ForegroundShellProbe.Resolve(output));
    }

    /// <summary>前台程序底下挂着两个终端(screen 开了两个窗口):不知道用户看的是哪个,放弃。</summary>
    [TestMethod]
    public void Resolve_RelayToSeveralTerminals_IsNull() =>
        Assert.IsNull(ForegroundShellProbe.Resolve(Ancestors +
            """
            P 31 30 31 34816 60
            P 60 31 60 34816 60 screen
            P 61 60 61 34820 61 bash
            P 62 60 62 34821 62 bash
            """));

    /// <summary>
    /// 同一个 sshd 进程底下有两个带终端的子进程(一条连接开了两个 shell 通道):分不清哪个是我们的,放弃。
    /// </summary>
    [TestMethod]
    public void Resolve_SeveralTerminalsOnOneConnection_IsNull() =>
        Assert.IsNull(ForegroundShellProbe.Resolve(Ancestors +
            """
            P 31 30 31 34816 31 -bash
            P 32 30 32 34822 32 -bash
            """));

    /// <summary>
    /// 找不到登录 shell(连接上只有 exec 通道)时一路往上找,到 1 号进程必须停下:
    /// init 底下挂着 getty,数到一个也绝不是我们的终端。
    /// </summary>
    [TestMethod]
    public void Resolve_NeverTakesChildrenOfInit() =>
        Assert.IsNull(ForegroundShellProbe.Resolve(Ancestors + "P 700 1 700 1024 700 -bash \n"));

    /// <summary>macOS 走 <c>ps</c> 那一支:终端列是名字(<c>s000</c>)而不是设备号,判定一样。</summary>
    [TestMethod]
    public void Resolve_PsFallbackOutput_WorksTheSame()
    {
        const string output =
            """
            A 5100 5090
            A 5090 5080
            A 5080 1
            P 5091 5090 5091 s000 5120 -zsh
            P 5110 5091 5110 s000 5120 su - joe
            P 5120 5110 5120 s000 5120 -zsh
            """;

        Assert.AreEqual(new ForegroundShell(5091, 5120, RemoteShellKind.Zsh), ForegroundShellProbe.Resolve(output));
    }

    /// <summary>空输出、乱码、Windows 上 cmd 的报错:一律当作没有。</summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("'sh' is not recognized as an internal or external command\r\n")]
    [DataRow("A x y\nP a b c d e\n")]
    public void Resolve_Garbage_IsNull(string? output) => Assert.IsNull(ForegroundShellProbe.Resolve(output));

    [TestMethod]
    [DataRow("-bash", RemoteShellKind.Bash)]
    [DataRow("bash", RemoteShellKind.Bash)]
    [DataRow("/bin/bash", RemoteShellKind.Bash)]
    [DataRow("bash -l", RemoteShellKind.Bash)]
    [DataRow("bash --login -i", RemoteShellKind.Bash)]
    [DataRow("bash --rcfile /tmp/nix-shell-rc", RemoteShellKind.Bash)]
    [DataRow("bash -o vi", RemoteShellKind.Bash)]
    [DataRow("-zsh", RemoteShellKind.Zsh)]
    [DataRow("zsh", RemoteShellKind.Zsh)]
    [DataRow("-fish", RemoteShellKind.Fish)]
    [DataRow("fish --login", RemoteShellKind.Fish)]
    [DataRow("fish -C set_greeting", RemoteShellKind.Fish)]
    [DataRow("-sh", RemoteShellKind.PosixSh)]
    [DataRow("/bin/sh", RemoteShellKind.PosixSh)]
    [DataRow("dash", RemoteShellKind.PosixSh)]
    [DataRow("-ash", RemoteShellKind.PosixSh)]
    [DataRow("mksh", RemoteShellKind.PosixSh)]
    [DataRow("ksh93", RemoteShellKind.PosixSh)]
    [DataRow("bash --", RemoteShellKind.Bash)]
    public void InteractiveShellKind_RecognisesInteractiveShells(string args, RemoteShellKind expected) =>
        Assert.AreEqual(expected, ForegroundShellProbe.InteractiveShellKind(args));

    /// <summary>在跑命令、在跑脚本、或者根本不是我们会注的 shell:一律不认。</summary>
    [TestMethod]
    [DataRow("")]
    [DataRow("bash -c sleep 30")]
    [DataRow("-bash -c id")]
    [DataRow("bash -lc make")]
    [DataRow("sh -ic true")]
    [DataRow("fish --command=ls")]
    [DataRow("fish --command ls")]
    [DataRow("bash ./deploy.sh")]
    [DataRow("bash -- ./deploy.sh")]
    [DataRow("sh /etc/init.d/x restart")]
    [DataRow("zsh -s arg")]
    [DataRow("python3")]
    [DataRow("su - node")]
    [DataRow("sudo -i")]
    [DataRow("tcsh")]
    [DataRow("-csh")]
    [DataRow("nu")]
    [DataRow("bashtop")]
    public void InteractiveShellKind_RejectsEverythingElse(string args) =>
        Assert.IsNull(ForegroundShellProbe.InteractiveShellKind(args));

    /// <summary>
    /// exec 通道里的命令由登录 shell 解析,而登录 shell 可能是 fish:脚本整段包在 <c>sh -c '…'</c> 里,
    /// 里面一个单引号都不许有(会提前合上),也不许有 fish 单引号里唯二认的转义 <c>\\</c> 与 <c>\'</c>。
    /// </summary>
    [TestMethod]
    public void Command_IsSafeInsideFishSingleQuotes()
    {
        string command = ForegroundShellProbe.Command;
        Assert.StartsWith("sh -c '", command);
        Assert.EndsWith("'", command);
        string body = command["sh -c '".Length..^1];
        Assert.DoesNotContain("'", body);
        Assert.DoesNotContain(@"\\", body);
    }

    /// <summary>探针跑不起来(exec 被禁、超时、连接断了)= 没有结论,绝不抛到调用方。</summary>
    [TestMethod]
    public async Task DetectAsync_WhenExecFails_ReturnsNull()
    {
        var client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("channel open failed"));

        Assert.IsNull(await ForegroundShellProbe.DetectAsync(client));
    }

    [TestMethod]
    public async Task DetectAsync_ParsesCommandOutput()
    {
        var client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(ForegroundShellProbe.Command, Arg.Any<CancellationToken>())
            .Returns(new RemoteCommandResult(Ancestors + "P 31 30 31 34816 41 \nP 41 31 41 34816 41 -bash \n", "", 0));

        Assert.AreEqual(new ForegroundShell(31, 41, RemoteShellKind.Bash), await ForegroundShellProbe.DetectAsync(client));
    }
}
