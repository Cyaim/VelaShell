namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>
/// 一轮在桥接总并发闸上的名额:只等传输时让出,下一次调模型之前取回<b>同一把</b>闸。
/// </summary>
/// <remarks>
/// 让出与取回之间可能被停止或撤权打断,所以"手里到底有没有名额"由这里精确记着:
/// 没拿到就不还,拿着才还,还过一次就不再还 —— 多还一次等于凭空多给一个名额,
/// 少还一次则永久吃掉一个,两种都会让总并发上限悄悄失守。
/// </remarks>
internal sealed class BridgeTurnSlot(SemaphoreSlim gate)
{
    private readonly Lock _sync = new();
    private bool _held;

    /// <summary>此刻是否持有名额。</summary>
    internal bool IsHeld
    {
        get { lock (_sync) return _held; }
    }

    /// <summary>拿名额(已经拿着就什么都不做)。取消时没拿到就是没拿到,不留半个名额。</summary>
    internal async Task AcquireAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_held) return;
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool duplicate;
        lock (_sync)
        {
            duplicate = _held;
            _held = true;
        }
        // 并发的两次取回只留一个名额,多出来的那个当场还回去。
        if (duplicate) gate.Release();
        // 许可与取消同时成立时 SemaphoreSlim 不保证先报取消(见 ConversationRouter.RunTurnAsync);
        // 此刻已记为持有,由调用方的收尾归还。
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>归还(让出与收尾都走这里)。手里没有就什么都不做。</summary>
    internal void Release()
    {
        lock (_sync)
        {
            if (!_held) return;
            _held = false;
        }
        gate.Release();
    }
}
