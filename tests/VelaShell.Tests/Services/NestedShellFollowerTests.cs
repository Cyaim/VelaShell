using VelaShell.Core.Ssh;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 嵌套 shell 补注的状态机(#637)。探针与终端都是替身:这里验的是"什么时候问、问完什么时候才动手",
/// 探针本身怎么认前台见 <c>ForegroundShellProbeTests</c>,真机链路见 <c>ShellIntegrationDockerTests</c>。
/// </summary>
/// <remarks>
/// 每一条"不该注"的用例背后都是一个具体的打错地方:用户正在敲半截命令、vim 开着、
/// 探针往返期间屏幕又动了……注错一次就是往别人的输入里塞了一行字。
/// </remarks>
[TestClass]
[TestCategory("NestedShellFollow")]
public sealed class NestedShellFollowerTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly ForegroundShell NestedBash = new(31, 41, RemoteShellKind.Bash);

    /// <summary>issue 里的那一幕:<c>su - node</c> 回车 → 终端安静、没有上报 → 问一次 → 照 node 的 shell 种类补注一次。</summary>
    [TestMethod]
    public async Task EnterWithoutReport_InjectsNestedShellOnce()
    {
        var session = new FakeSession();
        int probes = 0;
        using var follower = Create(session, _ =>
        {
            probes++;
            return Task.FromResult<ForegroundShell?>(NestedBash);
        });

        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);

        Assert.AreEqual(1, probes);
        CollectionAssert.AreEqual(new[] { (RemoteShellKind.Bash, 7L) }, session.Injections);

        // 同一个 shell 里接着回车:补过了就不再补,哪怕它一直不上报(比如那其实是个我们认错的 shell)。
        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);
        Assert.AreEqual(2, probes);
        Assert.HasCount(1, session.Injections);
    }

    /// <summary>
    /// 换了一个 shell(exit 之后又 su 进去,进程号不同)就再补一次;认种类按的是<b>这个</b> shell,不是登录 shell。
    /// </summary>
    [TestMethod]
    public async Task NewNestedShell_InjectsAgainWithItsOwnKind()
    {
        var session = new FakeSession();
        ForegroundShell next = NestedBash;
        using var follower = Create(session, _ => Task.FromResult<ForegroundShell?>(next));

        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);
        next = new(31, 77, RemoteShellKind.Zsh);
        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);

        CollectionAssert.AreEqual(
            new[] { RemoteShellKind.Bash, RemoteShellKind.Zsh },
            session.Injections.Select(i => i.Kind).ToArray());
    }

    /// <summary>回车之后等到了上报 = 前台是装过钩子的 shell:平时绝大多数回车都走这条,连 exec 都不该开。</summary>
    [TestMethod]
    public async Task ReportAfterEnter_DoesNotProbe()
    {
        var session = new FakeSession();
        int probes = 0;
        using var follower = Create(session, _ =>
        {
            probes++;
            return Task.FromResult<ForegroundShell?>(NestedBash);
        });

        follower.OnLineSubmitted();
        session.WorkingDirectoryReports++;
        await follower.Completion.WaitAsync(Wait);

        Assert.AreEqual(0, probes);
        Assert.IsEmpty(session.Injections);
    }

    /// <summary>
    /// 认证后命令 <c>sudo su -</c> 紧跟着钩子注入发出,钩子自己的首报会落在它后面 ——
    /// 我们自己发的那一行不能拿"随后有上报"当作没换人,必须照问。
    /// </summary>
    [TestMethod]
    public async Task ForcedLine_ProbesEvenAfterReport()
    {
        var session = new FakeSession();
        using var follower = Create(session, _ => Task.FromResult<ForegroundShell?>(NestedBash));

        follower.OnLineSubmitted(force: true);
        session.WorkingDirectoryReports++;
        await follower.Completion.WaitAsync(Wait);

        Assert.HasCount(1, session.Injections);
    }

    /// <summary>
    /// 强制标记在真正问过之前一直有效:我们发的那一行还没等到静默就被用户的回车顶掉了,
    /// 而钩子的首报落在了用户那一回车之后 —— 这一轮照样得问,不能被那条首报骗过去。
    /// </summary>
    [TestMethod]
    public async Task ForcedLine_SurvivesBeingSupersededBeforeProbing()
    {
        var session = new FakeSession { LastOutputUtc = DateTime.UtcNow.AddHours(1) };
        using var follower = Create(session, _ => Task.FromResult<ForegroundShell?>(NestedBash));

        follower.OnLineSubmitted(force: true);
        follower.OnLineSubmitted();
        session.WorkingDirectoryReports++;
        session.LastOutputUtc = DateTime.UtcNow.AddHours(-1);
        await follower.Completion.WaitAsync(Wait);

        Assert.HasCount(1, session.Injections);
    }

    /// <summary>前台是登录 shell 自己、或者探不出来:什么都不做。</summary>
    [TestMethod]
    public async Task LoginShellOrNoAnswer_DoesNothing()
    {
        var session = new FakeSession();
        ForegroundShell? answer = new ForegroundShell(31, 31, RemoteShellKind.Bash);
        using var follower = Create(session, _ => Task.FromResult(answer));

        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);
        answer = null;
        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);

        Assert.IsEmpty(session.Injections);
    }

    /// <summary>等静默期间用户又敲了键(新 shell 的提示符一出来就开始打字):提示符那一行不空了,放弃,问都不问。</summary>
    [TestMethod]
    public async Task TypingWhileWaiting_Aborts()
    {
        var session = new FakeSession { LastOutputUtc = DateTime.UtcNow.AddHours(1) };
        int probes = 0;
        using var follower = Create(session, _ =>
        {
            probes++;
            return Task.FromResult<ForegroundShell?>(NestedBash);
        });

        follower.OnLineSubmitted();
        session.UserInputs++;
        session.LastOutputUtc = DateTime.UtcNow.AddHours(-1);
        await follower.Completion.WaitAsync(Wait);

        Assert.AreEqual(0, probes);
    }

    /// <summary>一直在刷屏(<c>tail -f</c>):等不到静默,到点放弃 —— 这里放弃才是对的,不能像连接时那样到点硬注。</summary>
    [TestMethod]
    public async Task OutputNeverQuiet_GivesUp()
    {
        var session = new FakeSession { OutputAlwaysFresh = true };
        int probes = 0;
        using var follower = Create(session, _ =>
        {
            probes++;
            return Task.FromResult<ForegroundShell?>(NestedBash);
        }, maxWait: TimeSpan.FromMilliseconds(200));

        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);

        Assert.AreEqual(0, probes);
    }

    /// <summary>vim / less / top 开着备用屏幕:那肯定不是 shell 的提示符,不问。</summary>
    [TestMethod]
    public async Task AlternateScreen_DoesNotProbe()
    {
        var session = new FakeSession { IsAlternateScreen = true };
        int probes = 0;
        using var follower = Create(session, _ =>
        {
            probes++;
            return Task.FromResult<ForegroundShell?>(NestedBash);
        });

        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);

        Assert.AreEqual(0, probes);
    }

    /// <summary>会话中途在设置里关掉了「上报终端工作目录」:不再补注。</summary>
    [TestMethod]
    public async Task Disabled_DoesNotProbe()
    {
        var session = new FakeSession();
        int probes = 0;
        using var follower = Create(session, _ =>
        {
            probes++;
            return Task.FromResult<ForegroundShell?>(NestedBash);
        }, enabled: () => false);

        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);

        Assert.AreEqual(0, probes);
    }

    /// <summary>
    /// 探针往返期间屏幕又动了(<c>TMOUT</c> 把嵌套 shell 踢回了登录 shell 之类):结论作废,不注。
    /// </summary>
    [TestMethod]
    public async Task OutputDuringProbe_DiscardsAnswer()
    {
        var session = new FakeSession();
        using var follower = Create(session, _ =>
        {
            session.LastOutputUtc = DateTime.UtcNow;
            return Task.FromResult<ForegroundShell?>(NestedBash);
        });

        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);

        Assert.IsEmpty(session.Injections);
    }

    /// <summary>
    /// 探针往返期间有任何输入入了队(哪怕是终端的协议应答):写出去的那一下被拒,
    /// 而且<b>不</b>记成"补过了" —— 下一次回车还得再试。
    /// </summary>
    [TestMethod]
    public async Task InputDuringProbe_InjectionRefusedAndRetriedLater()
    {
        var session = new FakeSession();
        bool interfere = true;
        using var follower = Create(session, _ =>
        {
            if (interfere)
            {
                session.InputGeneration++;
            }
            return Task.FromResult<ForegroundShell?>(NestedBash);
        });

        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);
        Assert.IsEmpty(session.Injections);
        Assert.AreEqual(1, session.RefusedInjections);

        interfere = false;
        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);
        Assert.HasCount(1, session.Injections);
    }

    /// <summary>探针往返期间用户又交了一行:前台可能又换了,旧结论作废,按新的那一行重新来一轮。</summary>
    [TestMethod]
    public async Task NewLineDuringProbe_StartsOver()
    {
        var session = new FakeSession();
        NestedShellFollower? follower = null;
        int probes = 0;
        follower = Create(session, _ =>
        {
            if (++probes == 1)
            {
                follower!.OnLineSubmitted();
                return Task.FromResult<ForegroundShell?>(NestedBash);
            }
            return Task.FromResult<ForegroundShell?>(null);
        });
        using (follower)
        {
            follower.OnLineSubmitted();
            await follower.Completion.WaitAsync(Wait);
        }

        Assert.AreEqual(2, probes);
        Assert.IsEmpty(session.Injections);
    }

    /// <summary>断开 / 重连 / 关标签之后不再动手。</summary>
    [TestMethod]
    public async Task Disposed_IgnoresLines()
    {
        var session = new FakeSession();
        int probes = 0;
        var follower = Create(session, _ =>
        {
            probes++;
            return Task.FromResult<ForegroundShell?>(NestedBash);
        });
        follower.Dispose();

        follower.OnLineSubmitted();
        await follower.Completion.WaitAsync(Wait);

        Assert.AreEqual(0, probes);
    }

    private static NestedShellFollower Create(
        FakeSession session,
        Func<CancellationToken, Task<ForegroundShell?>> probe,
        Func<bool>? enabled = null,
        TimeSpan? maxWait = null) =>
        new(session, probe, enabled ?? (() => true), Idle, maxWait ?? Wait);

    /// <summary>终端会话替身:几样状态都能从用例里直接拨。</summary>
    private sealed class FakeSession : INestedShellSession
    {
        private DateTime _lastOutputUtc = DateTime.UtcNow.AddHours(-1);

        public List<(RemoteShellKind Kind, long Generation)> Injections { get; } = [];

        public int RefusedInjections { get; private set; }

        public bool OutputAlwaysFresh { get; init; }

        public long WorkingDirectoryReports { get; set; }

        public long UserInputs { get; set; }

        public DateTime LastOutputUtc
        {
            get => OutputAlwaysFresh ? DateTime.UtcNow : _lastOutputUtc;
            set => _lastOutputUtc = value;
        }

        public long InputGeneration { get; set; } = 7;

        public bool IsAlternateScreen { get; set; }

        public bool TryInjectShellIntegration(RemoteShellKind kind, long expectedInputGeneration)
        {
            if (expectedInputGeneration != InputGeneration)
            {
                RefusedInjections++;
                return false;
            }
            Injections.Add((kind, expectedInputGeneration));
            InputGeneration++;
            return true;
        }
    }
}
