namespace VelaShell.Services.Update;

/// <summary>
/// 下载速率计:喂给它"到目前为止一共下了多少字节",它给出最近一段时间窗内的实测速率。
/// </summary>
/// <remarks>
/// 为什么要自己算而不用整段平均值:更新包动辄上百 MB,前段快后段慢时全程均值会
/// 明显偏离当前网速,用户看到的数字既不解释"现在慢"也不解释"刚才快"。这里是真正的滑动窗口:
/// 保留最近一个窗口内的样本,速率 = 窗口内最老样本到现在的字节增量 / 时长,每喂一次窗口就往前滑一点,
/// 不会像"窗口走完就从零重新累计"那样在每次重开窗口后跳一下。停了超过一整个窗口再恢复时,
/// 窗口里只剩刚到的这一个样本,报 0 而不是停滞前的旧速率,下一次喂入就是新鲜的值。
/// 线程安全:分段并发下载时多个段共用一个实例。
/// </remarks>
/// <remarks>创建速率计。</remarks>
/// <param name="clock">
/// 取当前时刻(毫秒)的委托;不传则用 <see cref="Environment.TickCount64" />。
/// 测试注入假时钟即可在瞬间内跑完整个时间窗。
/// </param>
internal sealed class DownloadRateMeter(Func<long>? clock = null)
{
    /// <summary>速率统计窗口。3 秒:短到能跟上网络的忽快忽慢,长到不被单次 TCP 窗口抖动带偏。</summary>
    internal const long WindowMilliseconds = 3000;

    private readonly Lock _gate = new();
    private readonly Func<long> _clock = clock ?? (static () => Environment.TickCount64);

    /// <summary>窗口内的(时刻, 累计字节)样本,按时间先后排列;最老的那个就是速率的基线。</summary>
    private readonly Queue<(long At, long Bytes)> _samples = new();

    private long _lastBytes;

    /// <summary>喂入累计已下载字节数,返回当前速率(字节/秒)。</summary>
    /// <param name="bytesDownloaded">到目前为止累计下载的字节数,含断点续传时此前已落盘的部分。</param>
    /// <returns>最近一个窗口内的实测速率(字节/秒);窗口尚未走满时给出的是窗口内目前的折算值。</returns>
    public double Sample(long bytesDownloaded)
    {
        lock (_gate)
        {
            long now = _clock();
            // 分段并发时各段的汇报可能交错到达,累计字节只认见过的最大值。
            _lastBytes = Math.Max(_lastBytes, bytesDownloaded);
            while (_samples.Count > 0 && now - _samples.Peek().At > WindowMilliseconds)
            {
                _samples.Dequeue();
            }
            _samples.Enqueue((now, _lastBytes));
            (long baseAt, long baseBytes) = _samples.Peek();
            long elapsed = now - baseAt;
            // 窗口里只有刚喂的这一个样本(第一次喂入,或停了整整一窗之后刚恢复):
            // 还没有"一段时间"可言,速率无从谈起。
            return elapsed > 0 ? (_lastBytes - baseBytes) * 1000.0 / elapsed : 0;
        }
    }
}
