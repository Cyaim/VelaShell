using System.Text;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Sudo.Kernel.Tests;

[TestClass]
public sealed class SudoKernelTests
{
    [TestMethod]
    public void LatestControlledCommandUsesExplicitRootAndFailClosedPreparation()
    {
        string wrapped = SudoCommandExecutor.BuildScript("printf '%s' '漢🙂'; id | cat > /root/file", "READY", "AUTH", "START");
        Assert.Contains("stty raw -echo || exit 125", wrapped);
        Assert.Contains("modes=\" $(stty -a) \" || exit 125", wrapped);
        Assert.Contains("exec sudo -S -u root -p 'AUTH' -- /bin/sh -c ", wrapped);
        Assert.Contains("</dev/null", wrapped);
        Assert.Contains("exit 125; exec /bin/sh -c", wrapped);
        Assert.DoesNotContain("sudo -v", wrapped);
        Assert.AreEqual("'a'\"'\"'b'", SudoCommandExecutor.Quote("a'b"));
    }

    [TestMethod]
    public void Utf8SecretIsClearedAndControlCharactersAreNotAccepted()
    {
        SudoPassword secret = new("密碼é🙂".AsSpan());
        ReadOnlyMemory<byte> bytes = secret.Utf8;
        Assert.AreEqual("密碼é🙂", Encoding.UTF8.GetString(bytes.Span));
        secret.Dispose();
        secret.Dispose();
        Assert.IsLessThan(0, bytes.Span.IndexOfAnyExcept((byte)0));
        foreach (char control in new[] { '\0', '\r', '\n', '\b', '\u001b', '\u007f' })
        {
            Assert.ThrowsExactly<ArgumentException>(() => new SudoPassword(("secret" + control).AsSpan()));
        }
    }

    [TestMethod]
    public void UnknownIsSafeDefaultOutcome()
    {
        Assert.AreEqual(SudoExecutionOutcome.Unknown, Enum.GetValues<SudoExecutionOutcome>()[0]);
    }

    private const string LoginShellPrefix = "exec /bin/sh -c 'eval \"$(printf %b \"";
    private const string LoginShellSuffix = "\")\"'";

    /// <summary>
    /// 交给登录 shell 的那层只剩字母数字、少数标点与 <c>\0ooo</c>:fish 单引号里的 <c>\\</c> / <c>\'</c>、
    /// csh 的 <c>!</c> 与换行、外层的单引号本身,一个都不会出现,还原后正是 /bin/sh 要执行的脚本。
    /// </summary>
    [TestMethod]
    public void LoginShellLayerContainsOnlyCharactersEveryShellReadsLiterally()
    {
        string command = "printf '%s' 'a\\\\b' \"it's\" 'bang!' \"$(id)\" `id`\n漢🙂‮\techo \\'";
        string outer = SudoCommandExecutor.BuildCommand(command, "\u001eR\u001f", "\u001eA\u001f", "\u001eS\u001f");
        Assert.StartsWith(LoginShellPrefix, outer);
        Assert.EndsWith(LoginShellSuffix, outer);
        string payload = outer[LoginShellPrefix.Length..^LoginShellSuffix.Length];
        Assert.MatchesRegex(@"^(?:[A-Za-z0-9 \-_./,:=+@]|\\0[0-7]{3})*$", payload);
        Assert.AreEqual(SudoCommandExecutor.BuildScript(command, "\u001eR\u001f", "\u001eA\u001f", "\u001eS\u001f"), DecodePrintf(payload));
    }

    /// <summary>还原外层编码后,sudo argv 里的 root 脚本、root 脚本里的用户命令逐级按 POSIX 解析,得到的仍是批准的那条命令。</summary>
    [TestMethod]
    [DataRow("id -u")]
    [DataRow("printf '%s\\n' 'a\\\\b'")]
    [DataRow("echo \\'; touch /tmp/x '\"'\"'")]
    [DataRow("cat <<'EOF' > /etc/x.conf\nline 1\n$HOME `id`\nEOF")]
    [DataRow("echo 'bang!' \"it's\" 漢🙂 ‮txt.exe")]
    [DataRow("exec /bin/sh -c 'id' -- /bin/sh -c x")]
    public void RootCommandSurvivesBothQuotingLayersUnchanged(string command)
    {
        string outer = SudoCommandExecutor.BuildCommand(command, "\u001eR\u001f", "\u001eA\u001f", "\u001eS\u001f");
        string script = DecodePrintf(outer[LoginShellPrefix.Length..^LoginShellSuffix.Length]);
        string root = WordAfter(script, " -- /bin/sh -c ");
        Assert.AreEqual(command, WordAfter(root, "exec /bin/sh -c "));
        Assert.EndsWith(" </dev/null", root);
    }

    [TestMethod]
    public async Task NulInCommandIsRejectedBeforeAnyPromptOrChannel()
    {
        RecordingPrompt prompt = new();
        SudoExecutionResult result = await SudoCommandExecutor.ExecuteAsync(null!, "id\0; rm -rf /", new("plugin", "joe@host"),
            prompt, _ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.AreEqual(SudoExecutionOutcome.NotSupported, result.Outcome);
        Assert.AreEqual(0, prompt.Calls);
    }

    private sealed class RecordingPrompt : ISudoPrompt
    {
        internal int Calls;
        public Task<bool> ConfirmAsync(SudoExecutionContext context, string command, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(true);
        }
        public Task<SudoPassword?> RequestPasswordAsync(SudoExecutionContext context, string command, int attempt, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<SudoPassword?>(null);
        }
    }

    private static string DecodePrintf(string payload)
    {
        List<byte> bytes = [];
        for (int i = 0; i < payload.Length; i++)
        {
            if (payload[i] == '\\')
            {
                bytes.Add(Convert.ToByte(payload.Substring(i + 2, 3), 8));
                i += 4;
            }
            else
            {
                bytes.Add((byte)payload[i]);
            }
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>取 <paramref name="marker" /> 之后的一个 POSIX shell 词(单引号、双引号与反斜杠按 sh 规则去掉)。</summary>
    private static string WordAfter(string script, string marker)
    {
        int start = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, $"找不到 {marker}");
        StringBuilder word = new();
        for (int i = start + marker.Length; i < script.Length && !char.IsWhiteSpace(script[i]); i++)
        {
            switch (script[i])
            {
                case '\'':
                    int close = script.IndexOf('\'', i + 1);
                    Assert.IsGreaterThan(i, close, "单引号未闭合");
                    word.Append(script, i + 1, close - i - 1);
                    i = close;
                    break;
                case '"':
                    for (i++; script[i] != '"'; i++)
                    {
                        if (script[i] == '\\' && script[i + 1] is '$' or '`' or '"' or '\\')
                        {
                            i++;
                        }
                        word.Append(script[i]);
                    }
                    break;
                case '\\':
                    word.Append(script[++i]);
                    break;
                default:
                    word.Append(script[i]);
                    break;
            }
        }
        return word.ToString();
    }
}
