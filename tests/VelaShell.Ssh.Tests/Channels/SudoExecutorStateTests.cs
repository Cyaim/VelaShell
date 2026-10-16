using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Channels;

/// <summary>通过真实内存 SSH 通道执行宿主认证状态机；不模拟 sudo/PAM 的真实终端实现。</summary>
[TestClass]
public sealed class SudoExecutorStateTests
{
    private sealed class Prompt : ISudoPrompt
    {
        internal int Requests;
        internal Func<CancellationToken, Task<SudoPassword?>> Answer = _ => Task.FromResult<SudoPassword?>(new("密碼é🙂".AsSpan()));
        internal TaskCompletionSource Asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ConfirmAsync(SudoExecutionContext context, string command, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<SudoPassword?> RequestPasswordAsync(SudoExecutionContext context, string command, int attempt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            Asked.TrySetResult();
            return Answer(cancellationToken);
        }
    }

    private static string Marker(string command, string kind)
    {
        const string prefix = "\u001eVELA_AUTH_";
        int start = command.IndexOf(prefix, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start);
        start += prefix.Length;
        int end = command.IndexOf('\u001f', start);
        Assert.IsGreaterThan(start, end);
        return "\u001eVELA_" + kind + "_" + command[start..end] + "\u001f";
    }

    private static async Task SendFragmentedAsync(PipeWriter output, string text, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        foreach (byte value in bytes)
        {
            await output.WriteAsync(new byte[] { value }, cancellationToken);
        }
    }

    private static async Task<string> ReadPasswordLineAsync(PipeReader input, CancellationToken cancellationToken)
    {
        StringBuilder result = new();
        while (true)
        {
            ReadResult read = await input.ReadAsync(cancellationToken);
            byte[] bytes = read.Buffer.ToArray();
            input.AdvanceTo(read.Buffer.End);
            result.Append(Encoding.UTF8.GetString(bytes));
            if (result.ToString().Contains('\n', StringComparison.Ordinal))
            {
                return result.ToString();
            }
            Assert.IsFalse(read.IsCompleted, "收到密码前输入不能结束");
        }
    }

    [TestMethod]
    public async Task FragmentedAuthenticationWritesExactlyOneUtf8PasswordAndThenDisablesAuthentication()
    {
        Prompt prompt = new();
        string? sentPassword = null;
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            ExecHandler = async (command, input, output, ct) =>
            {
                await SendFragmentedAsync(output, Marker(command, "READY") + Marker(command, "AUTH"), ct);
                sentPassword = await ReadPasswordLineAsync(input, ct);
                await SendFragmentedAsync(output, Marker(command, "START") + "漢🙂" + Marker(command, "AUTH") + "tail", ct);
            }
        });
        List<SudoExecutionEvent> events = [];
        SudoExecutionResult result = await SudoCommandExecutor.ExecuteAsync(host.Connection, "id", new("plugin", "joe@test"),
            prompt, events.Add, host.Token);
        Assert.AreEqual("密碼é🙂\n", sentPassword);
        Assert.AreEqual(1, prompt.Requests);
        Assert.Contains(entry => entry.State == SudoExecutionState.Running, events);
        string text = string.Concat(events.Select(entry => entry.Text));
        Assert.Contains("漢🙂", text);
        Assert.Contains("tail", text);
        Assert.DoesNotContain("VELA_READY_", text);
        Assert.DoesNotContain("密碼", text);
        Assert.AreEqual(SudoExecutionOutcome.Unknown, result.Outcome, "剧本没有伪造真实进程退出码");
    }

    [TestMethod]
    public async Task EofRevokesPendingPasswordUiAndNeverWritesItsLateAnswer()
    {
        Prompt prompt = new();
        prompt.Answer = async ct =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { prompt.Cancelled.TrySetResult(); throw; }
            return null;
        };
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            ExecHandler = async (command, _, output, ct) =>
            {
                await SendFragmentedAsync(output, Marker(command, "READY") + Marker(command, "AUTH"), ct);
                await prompt.Asked.Task.WaitAsync(ct);
                // 结束处理器使现有输出泵发送 EOF/CLOSE；不阻塞客户端读取泵。
            }
        });
        SudoExecutionResult result = await SudoCommandExecutor.ExecuteAsync(host.Connection, "id", new("plugin", "joe@test"),
            prompt, _ => { }, host.Token);
        await prompt.Cancelled.Task.WaitAsync(host.Token);
        Assert.AreEqual(1, prompt.Requests);
        Assert.IsEmpty(host.Channels.Observation.StandardInput);
        Assert.AreNotEqual(SudoExecutionOutcome.Exited, result.Outcome);
    }

    [TestMethod]
    public async Task PolicyDenialEchoingCommandNeverBecomesAStartedCommand()
    {
        Prompt prompt = new();
        prompt.Answer = async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        };
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            ExitCode = 1,
            ExecHandler = async (command, _, output, ct) =>
            {
                Assert.DoesNotContain(Marker(command, "START"), command,
                    "sudo argv 的诊断回显不能含有可被识别为已启动的原始标记");
                await SendFragmentedAsync(output, Marker(command, "READY") + "sudo: not allowed: " + command, ct);
            }
        });
        List<SudoExecutionEvent> events = [];
        SudoExecutionResult result = await SudoCommandExecutor.ExecuteAsync(host.Connection, "id", new("plugin", "joe@test"),
            prompt, events.Add, host.Token);
        Assert.AreEqual(SudoExecutionOutcome.AuthenticationFailed, result.Outcome);
        Assert.DoesNotContain(entry => entry.State == SudoExecutionState.Running, events);
        Assert.IsTrue(events.All(entry => entry.Text is null));
        Assert.IsEmpty(host.Channels.Observation.StandardInput);
    }

    [TestMethod]
    public async Task PasswordlessStartNeverRequestsPasswordEvenWhenOutputImitatesPrompt()
    {
        Prompt prompt = new();
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            ExecHandler = async (command, _, output, ct) =>
            {
                await SendFragmentedAsync(output, Marker(command, "READY") + Marker(command, "START") + Marker(command, "AUTH") + "password:", ct);
            }
        });
        List<SudoExecutionEvent> events = [];
        await SudoCommandExecutor.ExecuteAsync(host.Connection, "id", new("plugin", "joe@test"), prompt, events.Add, host.Token);
        Assert.AreEqual(0, prompt.Requests);
        Assert.Contains(entry => entry.State == SudoExecutionState.Running, events);
        Assert.IsEmpty(host.Channels.Observation.StandardInput);
    }
}
