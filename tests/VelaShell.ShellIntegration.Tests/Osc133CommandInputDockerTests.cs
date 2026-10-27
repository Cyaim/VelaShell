using System.Diagnostics.CodeAnalysis;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Semantics;
using Osc133Snippets = VelaShell.Core.Models.ShellIntegration;

namespace VelaShell.ShellIntegration.Tests;

/// <summary>
/// 「复制这条命令」「重跑」的端到端验证:把设置页给用户抄的 OSC 133 片段(<see cref="Osc133Snippets" />)
/// 装进真 bash / 真 zsh,在真 PTY 上敲命令,看终端从 B 切到 C 切出来的是不是用户敲的那一条。
/// </summary>
/// <remarks>
/// <para>
/// 替身流里 B / C 落在哪一列是我们自己写的,永远「刚好」。真 shell 会做替身想不到的事:
/// readline 的回显重绘、zsh 的 PROMPT_SP(上一条输出没换行时先补一个 <c>%</c> 再整行擦掉)、
/// RPROMPT、续行提示符(bash 的 <c>&gt; </c>、zsh 的 <c>for&gt; </c>)。
/// </para>
/// <para>靶子同 <see cref="ShellIntegrationDockerTests" />:<c>docker compose -f docker-compose.test.yml up -d ssh-shells</c>。</para>
/// </remarks>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 docker/SSH 操作不接受测试取消令牌,协作取消无法中断它们。")]
[TestClass]
[TestCategory("DockerIntegration")]
public sealed class Osc133CommandInputDockerTests
{
    private static IEnumerable<object[]> Shells =>
    [
        ["vela-bash", Osc133Snippets.Bash],
        ["vela-zsh", Osc133Snippets.Zsh]
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup() => ShellIntegrationHarness.RequireContainer();

    /// <summary>缓冲区里每条命令块切出来的命令(切不出来的跳过),自上而下。</summary>
    private static List<CommandInput> Commands(TerminalEmulator e)
    {
        var commands = new List<CommandInput>();
        for (int abs = CommandBlocks.NextPrompt(e.Screen, -1); abs >= 0; abs = CommandBlocks.NextPrompt(e.Screen, abs))
        {
            if (CommandBlocks.CommandInputOf(e.Screen, CommandBlocks.BlockAt(e.Screen, abs)!.Value) is { } input)
            {
                commands.Add(input);
            }
        }
        return commands;
    }

    /// <summary>缓冲区里的提示符条数。</summary>
    private static int PromptCount(TerminalEmulator e)
    {
        int count = 0;
        for (int abs = CommandBlocks.NextPrompt(e.Screen, -1); abs >= 0; abs = CommandBlocks.NextPrompt(e.Screen, abs))
        {
            count++;
        }
        return count;
    }

    /// <summary>敲一行并等到<b>下一条</b>提示符画完、光标停回空提示符上。</summary>
    /// <remarks>
    /// 只等「空闲」不够:回显还没回来的那一刻,光标还停在上一条空提示符上,判定照样是空闲 ——
    /// 用例会抢在命令执行之前往下走。所以先记下提示符条数,等它涨了再等空闲。
    /// </remarks>
    private static async Task TypeAndWaitForPromptAsync(ShellIntegrationHarness harness, string line)
    {
        int before = harness.Inspect(PromptCount);
        await harness.TypeAsync(line);
        harness.WaitFor(
            () => harness.Inspect(e => PromptCount(e) > before && CommandBlocks.IsIdleAtPrompt(e)),
            $"敲完 {line} 之后回到一条空的新提示符");
        harness.WaitForOutputIdle(idleMs: 200);
    }

    [TestMethod]
    [DynamicData(nameof(Shells))]
    [Timeout(90_000)]
    public async Task CommandsAreCutExactly_AcrossOutputNoOutputMultilineAndResize(string user, string snippet)
    {
        await using ShellIntegrationHarness harness = await ShellIntegrationHarness.ConnectAsync(user);
        harness.WaitForOutputIdle();

        // 片段经独立 exec 通道写成文件再 source:逐行敲进去的话,case…esac 会在交互式 shell 里走续行,
        // 敲进去的东西本身就成了一条多行命令,与要验的东西搅在一起。
        string path = $"/tmp/vela-osc133-{user}.sh";
        await harness.RunAsync($"printf '%s\\n' '{snippet.Replace("'", "'\\''")}' > {path}");
        await harness.TypeAsync(". " + path);
        harness.WaitForOutputIdle();
        harness.WaitFor(() => harness.Inspect(e => e.HasPromptMarks), "装上片段之后第一条带标记的提示符");
        harness.WaitFor(() => harness.Inspect(CommandBlocks.IsIdleAtPrompt), "光标停在空提示符的 B 上");

        await TypeAndWaitForPromptAsync(harness, "echo hello-vela");
        await TypeAndWaitForPromptAsync(harness, "cd /tmp");
        await TypeAndWaitForPromptAsync(harness, "printf no-newline");
        await harness.TypeAsync("for i in 1 2; do");
        await harness.TypeAsync("echo vela-$i");
        await TypeAndWaitForPromptAsync(harness, "done");

        string[] expected = ["echo hello-vela", "cd /tmp", "printf no-newline", "for i in 1 2; do\necho vela-$i\ndone"];
        List<CommandInput> commands = harness.Inspect(Commands);
        TestContext.WriteLine("--- 屏幕 ---\n" + harness.Screen);
        TestContext.WriteLine("--- 原始输出 ---\n" + ShellIntegrationHarness.Escape(harness.Raw));
        Assert.AreSequenceEqual(expected, [.. commands.Select(c => c.Text)], $"{user}:切出来的命令不对。");
        Assert.IsTrue(commands[^1].CanRerun, $"{user}:续行提示符末尾带了 B,多行命令应当能重跑。");

        // 像用户拖窗口那样改宽度:本地重排 + 远端收到 SIGWINCH 重画提示符。切出来的文本必须一字不差。
        foreach (int cols in new[] { 23, 61, 120 })
        {
            harness.Resize(cols, 40);
            harness.WaitForOutputIdle();
            Assert.AreSequenceEqual(
                expected, [.. harness.Inspect(Commands).Select(c => c.Text)], $"{user}:改到 {cols} 列后切出来的命令变了。\n{harness.Screen}");
        }
        harness.WaitFor(() => harness.Inspect(CommandBlocks.IsIdleAtPrompt), "改完尺寸仍停在空提示符上");
    }

    [TestMethod]
    [DynamicData(nameof(Shells))]
    [Timeout(90_000)]
    public async Task NotIdle_WhileTyping_OrWhileACommandRuns(string user, string snippet)
    {
        await using ShellIntegrationHarness harness = await ShellIntegrationHarness.ConnectAsync(user);
        harness.WaitForOutputIdle();
        string path = $"/tmp/vela-osc133-{user}.sh";
        await harness.RunAsync($"printf '%s\\n' '{snippet.Replace("'", "'\\''")}' > {path}");
        await harness.TypeAsync(". " + path);
        harness.WaitForOutputIdle();
        harness.WaitFor(() => harness.Inspect(CommandBlocks.IsIdleAtPrompt), "光标停在空提示符的 B 上");

        // 敲了半截、没回车。
        await harness.WriteRawAsync("echo hal");
        harness.WaitForOutputIdle();
        Assert.IsFalse(harness.Inspect(CommandBlocks.IsIdleAtPrompt), $"{user}:命令行上已经有半截。");

        // 清掉半截(Ctrl+U),再跑一条一时半会儿不结束的命令。
        await harness.WriteRawAsync("\u0015sleep 30\n");
        harness.WaitFor(() => harness.Inspect(e => e.IsCommandRunning), "sleep 开始执行(收到 C)");
        Assert.IsFalse(harness.Inspect(CommandBlocks.IsIdleAtPrompt), $"{user}:sleep 还在跑。");

        await harness.WriteRawAsync("\u0003");
        harness.WaitForOutputIdle();
        harness.WaitFor(() => harness.Inspect(CommandBlocks.IsIdleAtPrompt), "Ctrl+C 之后回到空提示符");
    }
}
