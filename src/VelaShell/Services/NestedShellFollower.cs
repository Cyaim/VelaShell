using VelaShell.Core.Ssh;

namespace VelaShell.Services;

/// <summary>
/// <see cref="NestedShellFollower" /> 眼里的一个终端会话:它要读的几样状态与它唯一要做的动作。
/// </summary>
/// <remarks>抽成接口是为了让那条状态机脱离真连接、真桥来测(见 <c>NestedShellFollowerTests</c>)。</remarks>
internal interface INestedShellSession
{
    /// <summary>收到过的目录上报条数(<b>不去重</b>:同一目录报两次算两条)。</summary>
    long WorkingDirectoryReports { get; }

    /// <summary>用户产生的输入次数:键入、粘贴、快捷命令、同频道转发过来的输入。终端的协议自动应答不算。</summary>
    long UserInputs { get; }

    /// <summary>最后一次收到远端输出的时刻(UTC)。</summary>
    DateTime LastOutputUtc { get; }

    /// <summary>出站输入代次(见 <c>SshTerminalBridge.InputGeneration</c>),补注时据此确认这期间没人再写过东西。</summary>
    long InputGeneration { get; }

    /// <summary>前台程序开着备用屏幕(vim、less、top)—— 那肯定不是 shell 的提示符。</summary>
    bool IsAlternateScreen { get; }

    /// <summary>
    /// 往终端里补注 <paramref name="kind" /> 那一段目录上报钩子;<paramref name="expectedInputGeneration" />
    /// 之后有过任何输入就不发,返回 false。
    /// </summary>
    bool TryInjectShellIntegration(RemoteShellKind kind, long expectedInputGeneration);
}

/// <summary>
/// 给会话中途冒出来的嵌套 shell(<c>su - node</c>、<c>sudo -i</c>、敲一句 <c>zsh</c>)补注目录上报钩子(#637)。
/// </summary>
/// <remarks>
/// <para>
/// 钩子只在连接时注入登录 shell 一次;切到新 shell 之后它不再上报,「跟随终端目录」就停了。
/// 这个类在每次回车之后看一眼:<b>终端安静下来了、却没等到目录上报</b> —— 前台可能换人了,
/// 于是用 <see cref="ForegroundShellProbe" /> 走 exec 通道问一句前台是谁;
/// 是一个还没注过的交互式 shell,就照它的种类补注一次。
/// </para>
/// <para>
/// <b>每一道闸都是为了"绝不打进不该打的地方"。</b>
/// </para>
/// <list type="bullet">
/// <item>回车之后要等输出静默满 <see cref="DefaultIdleWindow" />:新 shell 的 rc、MOTD 还在往外吐时不动手。
/// 一直不静默(<c>tail -f</c>)就到 <see cref="DefaultMaxWait" /> 放弃 —— 跟注入窗口的兜底不同,这里放弃才是对的。</item>
/// <item>等待期间用户又敲了键就放弃:提示符那一行已经不是空的了。这一下包括输密码 —— 输完那一回车会再触发一轮。</item>
/// <item>备用屏幕开着(vim / less / top)不问;等到了目录上报也不问 —— 报了就说明前台是装过钩子的 shell。
/// 只有我们自己发的命令(认证后命令、快捷命令)例外,见 <see cref="OnLineSubmitted" />。</item>
/// <item>问完回来,终端又有了新输出、或者又来了新的一行,就作废这次结论 —— 前台可能又变了。</item>
/// <item>真正写出去的那一下走 <see cref="INestedShellSession.TryInjectShellIntegration" />:
/// 从静默那一刻起<b>任何</b>输入入过队都不发,检查与入队在同一把锁里。</item>
/// <item>每个 shell 进程只补一次:补注失败了(比如那是个我们认错的 shell)也不会每回车重来一遍。</item>
/// </list>
/// <para>
/// 代价是在没有上报的回车之后多一次 exec 往返:装了钩子的 bash / zsh / sh 每个提示符都上报,
/// 平时根本走不到探针;走到的是 REPL、长命令,以及 fish(它的钩子只在目录变化时报)。
/// 资源监视本就每秒一次 exec,这点量不算什么。
/// </para>
/// </remarks>
internal sealed class NestedShellFollower : IDisposable
{
    /// <summary>回车之后输出静默多久,才认为前台停在了提示符前。</summary>
    internal static readonly TimeSpan DefaultIdleWindow = TimeSpan.FromMilliseconds(400);

    /// <summary>等静默最多等这么久,一直刷屏就放弃这一轮。</summary>
    internal static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private readonly INestedShellSession _session;
    private readonly Func<CancellationToken, Task<ForegroundShell?>> _probe;
    private readonly Func<bool> _enabled;
    private readonly TimeSpan _idleWindow;
    private readonly TimeSpan _maxWait;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();

    /// <summary>补注过的 shell 进程。只在循环里读写,循环任何时刻只有一个在跑。</summary>
    private readonly HashSet<int> _injected = [];

    private Trigger? _pending;

    /// <summary>
    /// 循环在不在跑。与 <see cref="_pending" /> 在同一把锁里起落:循环决定退出与新的一行到来是同一个临界区,
    /// 不会出现"循环正要退、新来的那一行以为它还在跑"而被晾着的情形(看 <c>Task.IsCompleted</c> 就会)。
    /// </summary>
    private bool _running;

    private bool _disposed;

    /// <param name="session">要照看的终端会话。</param>
    /// <param name="probe">探针(走本连接的 exec 通道,见 <see cref="ForegroundShellProbe.DetectAsync" />)。</param>
    /// <param name="enabled">「上报终端工作目录」此刻是否开着 —— 设置里关掉之后不再补注。</param>
    /// <param name="idleWindow">测试用;默认 <see cref="DefaultIdleWindow" />。</param>
    /// <param name="maxWait">测试用;默认 <see cref="DefaultMaxWait" />。</param>
    public NestedShellFollower(
        INestedShellSession session,
        Func<CancellationToken, Task<ForegroundShell?>> probe,
        Func<bool> enabled,
        TimeSpan? idleWindow = null,
        TimeSpan? maxWait = null)
    {
        _session = session;
        _probe = probe;
        _enabled = enabled;
        _idleWindow = idleWindow ?? DefaultIdleWindow;
        _maxWait = maxWait ?? DefaultMaxWait;
    }

    /// <summary>当前这一轮检查(测试据此等它跑完)。</summary>
    internal Task Completion
    {
        get
        {
            lock (_gate)
            {
                return field;
            }
        }

        private set;
    } = Task.CompletedTask;

    /// <summary>
    /// 有一行交给了前台程序。
    /// </summary>
    /// <param name="force">
    /// 这一行是我们自己发的(认证后命令、启动命令):即使随后等到了目录上报也照样问一次。
    /// 那条上报多半是紧挨在它前面的钩子注入自己报的 —— <c>sudo su -</c> 作为认证后命令时,
    /// 钩子的首报与 <c>sudo su -</c> 几乎同时发出,"回车之后有上报"在这里证明不了前台还是登录 shell。
    /// 只在下一轮真的问过之前有效,被用户的回车顶掉也保留。
    /// </param>
    public void OnLineSubmitted(bool force = false)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _pending = new(
                DateTime.UtcNow,
                _session.WorkingDirectoryReports,
                _session.UserInputs,
                force || _pending?.Force == true);
            if (!_running)
            {
                _running = true;
                Completion = Task.Run(RunAsync);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _pending = null;
        }
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        CancellationToken token;
        try
        {
            token = _cts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        while (true)
        {
            Trigger? trigger;
            lock (_gate)
            {
                trigger = _pending;
                if (trigger is null || _disposed)
                {
                    _running = false;
                    return;
                }
            }
            try
            {
                await CheckAsync(trigger, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 即发即弃的循环:不接住就是一个无人观察的异常,下一次回车也不会再有人来看。
                System.Diagnostics.Trace.WriteLine($"[VelaShell] Nested shell follow failed: {ex.Message}");
            }
            lock (_gate)
            {
                if (ReferenceEquals(_pending, trigger))
                {
                    _pending = null;
                }
            }
        }
    }

    /// <summary>处理一轮:等静默 → 过闸 → 问前台 → 补注。中途被新的一行顶掉就直接返回,由外层接着处理新的那一行。</summary>
    private async Task CheckAsync(Trigger trigger, CancellationToken token)
    {
        DateTime? quietSince = await WaitForQuietAsync(trigger, token).ConfigureAwait(false);
        if (quietSince is null || !IsCurrent(trigger))
        {
            return;
        }
        if ((!trigger.Force && _session.WorkingDirectoryReports != trigger.Reports)
            || _session.IsAlternateScreen
            || !_enabled())
        {
            return;
        }
        long generation = _session.InputGeneration;
        ForegroundShell? found = await _probe(token).ConfigureAwait(false);
        if (found is not { IsLoginShell: false } shell || _injected.Contains(shell.Pid))
        {
            return;
        }

        // 问的这一个往返里屏幕又动了、或者又来了一行:结论可能已经过时(TMOUT 把嵌套 shell 踢回了登录 shell……)。
        if (!IsCurrent(trigger)
            || _session.UserInputs != trigger.UserInputs
            || _session.LastOutputUtc != quietSince.Value)
        {
            return;
        }
        if (_session.TryInjectShellIntegration(shell.Kind, generation))
        {
            _injected.Add(shell.Pid);
        }
    }

    /// <summary>
    /// 等到回车之后输出静默满一个窗口;返回那一刻的「最后输出时刻」。被顶掉、用户又敲了键、等太久都返回 null。
    /// </summary>
    private async Task<DateTime?> WaitForQuietAsync(Trigger trigger, CancellationToken token)
    {
        DateTime deadline = trigger.At + _maxWait;
        while (true)
        {
            if (!IsCurrent(trigger) || _session.UserInputs != trigger.UserInputs)
            {
                return null;
            }
            DateTime now = DateTime.UtcNow;
            DateTime lastOutput = _session.LastOutputUtc;
            if (now - trigger.At >= _idleWindow && now - lastOutput >= _idleWindow)
            {
                return lastOutput;
            }
            if (now >= deadline)
            {
                return null;
            }
            await Task.Delay(Poll, token).ConfigureAwait(false);
        }
    }

    private bool IsCurrent(Trigger trigger)
    {
        lock (_gate)
        {
            return ReferenceEquals(_pending, trigger) && !_disposed;
        }
    }

    /// <summary>一次「有一行交出去了」:何时、当时收到过几条上报、用户输入过几次、是不是我们自己发的。</summary>
    private sealed record Trigger(DateTime At, long Reports, long UserInputs, bool Force);
}
