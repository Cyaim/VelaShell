using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;

namespace VelaShell.Sudo.Kernel.Tests;

/// <summary>
/// 受控 sudo 在<b>真实 sudo + PAM</b> 上的端到端行为。
/// </summary>
/// <remarks>
/// 靶子是 <c>docker-compose.test.yml</c> 的 <c>ssh-sudo</c>(端口 2230,见 <c>tests/fixtures/ssh-sudo/Dockerfile</c>):
/// <c>docker compose -f docker-compose.test.yml up -d --build ssh-sudo</c>。每条用例先 <c>docker exec</c> 进去
/// 重设 sudoer 的口令、登录 shell 与 sudoers 策略,所以只能指向一次性容器;
/// 要换一台,用 <c>VELASHELL_SUDO_CONTAINER</c> / <c>VELASHELL_SUDO_PORT</c> 指过去。靶机不可达时记为跳过。
/// </remarks>
[TestClass]
[DoNotParallelize]
[TestCategory("SudoInterop")]
[TestCategory("DockerIntegration")]
public sealed class SudoInteropTests
{
    public TestContext TestContext { get; set; } = null!;
    private static string Container => Environment.GetEnvironmentVariable("VELASHELL_SUDO_CONTAINER") ?? "velashell-test-sudo";
    private static int Port => int.TryParse(Environment.GetEnvironmentVariable("VELASHELL_SUDO_PORT"), out int port) ? port : 2230;
    private const string Password = "密碼é🙂42";

    private sealed class Prompt(bool approve = true, params string?[] answers) : ISudoPrompt
    {
        internal int Approvals;
        internal int Requests;
        public Task<bool> ConfirmAsync(SudoExecutionContext context, string command, CancellationToken cancellationToken)
        {
            Approvals++;
            return Task.FromResult(approve);
        }
        public Task<SudoPassword?> RequestPasswordAsync(SudoExecutionContext context, string command, int attempt, CancellationToken cancellationToken)
        {
            Requests++;
            string? answer = answers.Length >= attempt ? answers[attempt - 1] : null;
            return Task.FromResult(answer is null ? null : new SudoPassword(answer.AsSpan()));
        }
    }

    private sealed class Tap : IPacketTap
    {
        internal readonly List<string> Payloads = [];
        public void OnPacket(in PacketTapRecord record)
        {
            lock (Payloads) { if (!record.Payload.IsEmpty) { Payloads.Add(Encoding.UTF8.GetString(record.Payload)); } }
        }
    }

    private static async Task ConfigureAsync(string policy, string? extra = null)
    {
        ProcessStartInfo start = new("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { "exec", "-i", Container, "sh", "-s" }) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start)!;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        // 登录 shell 每次先复位成 bash:换 shell 的用例中途失败,也不能把后面的用例带进 fish/tcsh。
        await process.StandardInput.WriteAsync("set -eu\nusermod -s /bin/bash sudoer\nprintf '%s\\n' 'sudoer:" + Password + "' | chpasswd\nprintf '%s\\n' "
            + SudoCommandExecutor.Quote("Defaults:sudoer timestamp_timeout=0\n" + policy) + " > /etc/sudoers.d/test-user\nchmod 440 /etc/sudoers.d/test-user\nvisudo -cf /etc/sudoers.d/test-user\n" + extra);
        process.StandardInput.Close();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        try
        {
            await Task.WhenAll(errors, output).WaitAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("隔离容器配置超过30秒。");
        }
        string error = await errors;
        Assert.AreEqual(0, process.ExitCode, error);
    }

    private static async Task<(SudoExecutionResult Result, string Output, Tap Tap)> ExecuteAsync(Prompt prompt, string command,
        Action<SudoExecutionEvent, SshConnection>? observe = null, CancellationToken cancellationToken = default)
    {
        Tap tap = new();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        await using SshConnection connection = await SshConnection.ConnectAsync(new SshConnectionOptions("sudoer", "127.0.0.1", Port)
        {
            Credentials = [new PasswordCredential(Password)],
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            PacketTap = tap,
            AllowPacketTapPayload = true,
        }, deadline.Token);
        StringBuilder output = new();
        SudoExecutionResult result = await SudoCommandExecutor.ExecuteAsync(connection, command,
            new("real.plugin", $"sudoer@127.0.0.1:{Port}"), prompt, entry =>
            {
                if (entry.Text is { } text) { output.Append(text); }
                observe?.Invoke(entry, connection);
                return ValueTask.CompletedTask;
            }, deadline.Token);
        lock (tap.Payloads)
        {
            string payloads = string.Concat(tap.Payloads);
            Assert.DoesNotContain(Password, payloads);
            Assert.DoesNotContain("VELA_AUTH_", payloads);
            Assert.DoesNotContain("VELA_READY_", payloads);
            Assert.DoesNotContain("VELA_START_", payloads);
        }
        Assert.DoesNotContain("\u001eVELA_AUTH_", output.ToString());
        Assert.DoesNotContain(Password, output.ToString());
        return (result, output.ToString(), tap);
    }

    private static void RequireEnvironment()
    {
        try
        {
            using TcpClient tcp = new();
            if (!tcp.ConnectAsync("127.0.0.1", Port).Wait(TimeSpan.FromSeconds(3)))
            {
                Assert.Inconclusive($"[SKIP] ssh-sudo 靶机 127.0.0.1:{Port} 不可达。" +
                                    "运行 'docker compose -f docker-compose.test.yml up -d --build ssh-sudo'。");
            }
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            Assert.Inconclusive($"[SKIP] ssh-sudo 靶机 127.0.0.1:{Port} 不可达:{ex.Message}");
        }
    }

    /// <summary>
    /// 登录 shell 不是 POSIX 家族时照样只执行批准过的那条命令:fish 的单引号里 <c>\\</c> 仍是转义,
    /// csh 的单引号里不许换行、挡不住 <c>!</c> —— 任何一处被登录 shell 改写,输出就对不上。
    /// </summary>
    [TestMethod]
    [DataRow("/usr/bin/fish")]
    [DataRow("/usr/bin/tcsh")]
    [DataRow("/bin/bash")]
    public async Task NonPosixLoginShellRunsExactlyTheApprovedCommand(string loginShell)
    {
        RequireEnvironment();
        await ConfigureAsync("sudoer ALL=(ALL) NOPASSWD: ALL", $"usermod -s {loginShell} sudoer\n");
        const string command = """printf '%s|' 'a\\b' "it's" 'bang!' "$(id -u)" """ + "\nprintf '漢🙂'";
        Prompt prompt = new(true);
        var (result, output, _) = await ExecuteAsync(prompt, command);
        TestContext.WriteLine($"{loginShell}: outcome={result.Outcome}, exit={result.ExitCode}, output={output}");
        Assert.AreEqual(SudoExecutionOutcome.Exited, result.Outcome);
        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("""a\\b|it's|bang!|0|漢🙂""", output);
    }

    [TestMethod]
    public async Task RealSudoUtf8PasswordRequireTtyAndRootCommand()
    {
        RequireEnvironment();
        await ConfigureAsync("Defaults:sudoer requiretty\nsudoer ALL=(ALL) ALL");
        Prompt prompt = new(true, Password);
        var (result, output, _) = await ExecuteAsync(prompt, "id -u; printf '漢🙂'; printf 'err' >&2; exit 7");
        TestContext.WriteLine($"Outcome={result.Outcome}, exit={result.ExitCode}, password prompts={prompt.Requests}, output={output}");
        Assert.AreEqual(SudoExecutionOutcome.Exited, result.Outcome);
        Assert.AreEqual(7, result.ExitCode);
        Assert.Contains("0", output);
        Assert.Contains("漢🙂", output);
        Assert.Contains("err", output);
        Assert.AreEqual(1, prompt.Approvals);
        Assert.AreEqual(1, prompt.Requests);
    }

    [TestMethod]
    public async Task NoPasswordStillRequiresApprovalAndDoesNotAskPassword()
    {
        RequireEnvironment();
        await ConfigureAsync("sudoer ALL=(ALL) NOPASSWD: ALL");
        Prompt prompt = new(true);
        var (result, output, _) = await ExecuteAsync(prompt, "id -u; test ! -t 0");
        Assert.AreEqual(SudoExecutionOutcome.Exited, result.Outcome);
        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("0", output);
        Assert.AreEqual(1, prompt.Approvals);
        Assert.AreEqual(0, prompt.Requests);
        Prompt denied = new(false);
        var rejected = await ExecuteAsync(denied, "exit 99");
        Assert.AreEqual(SudoExecutionOutcome.AuthenticationCancelled, rejected.Result.Outcome);
        Assert.AreEqual(0, denied.Requests);
    }

    [TestMethod]
    public async Task WrongPasswordRetriesButNeverMoreThanThree()
    {
        RequireEnvironment();
        await ConfigureAsync("Defaults:sudoer passwd_tries=3\nsudoer ALL=(ALL) ALL");
        Prompt prompt = new(true, "incorrect", Password);
        var result = await ExecuteAsync(prompt, "id -u");
        Assert.AreEqual(SudoExecutionOutcome.Exited, result.Result.Outcome);
        Assert.AreEqual(2, prompt.Requests);
        Prompt wrong = new(true, "incorrect", "incorrect", "incorrect", Password);
        var failed = await ExecuteAsync(wrong, "id -u");
        Assert.AreNotEqual(SudoExecutionOutcome.Exited, failed.Result.Outcome);
        Assert.AreEqual(3, wrong.Requests);
    }

    [TestMethod]
    public async Task RestrictedSudoersDoesNotBypassRootShellPolicy()
    {
        RequireEnvironment();
        await ConfigureAsync("sudoer ALL=(root) NOPASSWD: /usr/bin/id");
        Prompt prompt = new(true, Password);
        bool running = false;
        var result = await ExecuteAsync(prompt, "id -u", (entry, _) => running |= entry.State == SudoExecutionState.Running);
        TestContext.WriteLine($"Restricted policy result: outcome={result.Result.Outcome}, exit={result.Result.ExitCode}, prompts={prompt.Requests}, output={result.Output}");
        Assert.AreNotEqual(SudoExecutionOutcome.Exited, result.Result.Outcome,
            $"Restricted policy outcome={result.Result.Outcome}, exit={result.Result.ExitCode}, prompts={prompt.Requests}, output={result.Output}");
        Assert.IsFalse(running, "sudoers 拒绝诊断回显的 argv 不能被当成启动标记");
        Assert.AreEqual(string.Empty, result.Output, "认证阶段的拒绝诊断不能进入插件输出");
        Assert.DoesNotContain("0\n", result.Output);
    }

    [TestMethod]
    public async Task StartedCommandCannotReopenAuthenticationAndStdinIsClosed()
    {
        RequireEnvironment();
        await ConfigureAsync("sudoer ALL=(ALL) ALL");
        Prompt prompt = new(true, Password, "SHOULD_NOT_SEND");
        var result = await ExecuteAsync(prompt,
            "printf '[sudo] password: VELA_AUTH_fake'; read value && exit 99; printf 'stdin-closed'");
        Assert.AreEqual(SudoExecutionOutcome.Exited, result.Result.Outcome);
        Assert.AreEqual(0, result.Result.ExitCode);
        Assert.Contains("stdin-closed", result.Output);
        Assert.AreEqual(1, prompt.Requests);
    }

    [TestMethod]
    public async Task DisconnectAfterStartIsUnknownAndNeverReplayed()
    {
        RequireEnvironment();
        await ConfigureAsync("sudoer ALL=(ALL) NOPASSWD: ALL");
        Task? disconnect = null;
        Prompt prompt = new(true);
        var result = await ExecuteAsync(prompt, "printf running; sleep 10; printf SHOULD_NOT_COMPLETE", (entry, connection) =>
        {
            if (entry.State == SudoExecutionState.Running)
            {
                disconnect = connection.DisposeAsync().AsTask();
            }
        });
        if (disconnect is not null) { await disconnect; }
        Assert.AreEqual(SudoExecutionOutcome.Unknown, result.Result.Outcome);
        Assert.AreEqual(1, prompt.Approvals);
        Assert.DoesNotContain("SHOULD_NOT_COMPLETE", result.Output);
    }

    [TestMethod]
    public async Task LongForegroundCommandStreamsToCompletionBeforeNextCommand()
    {
        RequireEnvironment();
        await ConfigureAsync("sudoer ALL=(ALL) NOPASSWD: ALL");
        Stopwatch elapsed = Stopwatch.StartNew();
        var first = await ExecuteAsync(new Prompt(true), "printf begin; sleep 2; printf done; printf stderr >&2; exit 9");
        Assert.AreEqual(SudoExecutionOutcome.Exited, first.Result.Outcome);
        Assert.AreEqual(9, first.Result.ExitCode);
        Assert.Contains("begindone", first.Output);
        Assert.Contains("stderr", first.Output);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(2), elapsed.Elapsed);
        var next = await ExecuteAsync(new Prompt(true), "printf after-completion");
        Assert.AreEqual(0, next.Result.ExitCode);
        Assert.Contains("after-completion", next.Output);
    }

    [TestMethod]
    public async Task ExplicitRootOverridesRunasDefault()
    {
        RequireEnvironment();
        await ConfigureAsync("Defaults:sudoer runas_default=nobody\nsudoer ALL=(ALL) NOPASSWD: ALL");
        var result = await ExecuteAsync(new Prompt(true), "id -u");
        Assert.AreEqual(SudoExecutionOutcome.Exited, result.Result.Outcome);
        Assert.AreEqual("0", result.Output.Trim());
    }

    [TestMethod]
    public async Task ExternalCancellationAfterStartThrowsWithoutPretendingCompletion()
    {
        RequireEnvironment();
        await ConfigureAsync("sudoer ALL=(ALL) NOPASSWD: ALL");
        using CancellationTokenSource cancel = new();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await ExecuteAsync(new Prompt(true), "sleep 10; printf SHOULD_NOT_COMPLETE", (entry, _) =>
            {
                if (entry.State == SudoExecutionState.Running) { cancel.Cancel(); }
            }, cancel.Token);
        });
    }

    [TestMethod]
    public async Task PasswordCancelledDoesNotRunCommand()
    {
        RequireEnvironment();
        await ConfigureAsync("sudoer ALL=(ALL) ALL");
        Prompt prompt = new(true, (string?)null);
        var result = await ExecuteAsync(prompt, "printf SHOULD_NOT_RUN");
        Assert.AreEqual(SudoExecutionOutcome.AuthenticationCancelled, result.Result.Outcome);
        Assert.DoesNotContain("SHOULD_NOT_RUN", result.Output);
    }
}
