namespace VelaShell.Services.Update;

/// <summary>
/// 下载速率计:喂给它"到目前为止一共下了多少字节",它给出最近一段时间窗内的实测速率。
/// </summary>
/// <remarks>
/// 为什么要自己算而不用整段平均值:更新包动辄上百 MB,前段快后段慢时全程均值会
/// 明显偏离当前网速,用户看到的数字既不解释"现在慢"也不解释"刚才快"。滑动窗口把基线
/// 始终钉在"最近一次收到字节的时刻",窗口走完就以那个时刻重新累计。长时间收不到字节时
/// 窗口起点不动、分子为 0,速率自然落到 0 —— 这正是"下载已经停住"要显示的数。
/// 线程安全:分段并发下载时多个段共用一个实例。
/// </remarks>
internal sealed class DownloadRateMeter
{
    /// <summary>速率统计窗口。3 秒:短到能跟上网络的忽快忽慢,长到不被单次 TCP 窗口抖动带偏。</summary>
    internal const long WindowMilliseconds = 3000;

    private readonly Lock _gate = new();
    private readonly Func<long> _clock;
    private long _windowStartAt;
    private long _windowStartBytes;
    private long _lastBytes;
    private long _lastDataAt;
    private double _lastRate;

    /// <summary>创建速率计。</summary>
    /// <param name="clock">
    /// 取当前时刻(毫秒)的委托;不传则用 <see cref="Environment.TickCount64" />。
    /// 测试注入假时钟即可在瞬间内跑完整个时间窗。
    /// </param>
    public DownloadRateMeter(Func<long>? clock = null) => _clock = clock ?? (static () => Environment.TickCount64);

    /// <summary>喂入累计已下载字节数,返回当前速率(字节/秒)。</summary>
    /// <param name="bytesDownloaded">到目前为止累计下载的字节数,含断点续传时此前已落盘的部分。</param>
    /// <returns>最近一个窗口内的实测速率(字节/秒);窗口尚未走满时给出的是窗口内目前的折算值。</returns>
    public double Sample(long bytesDownloaded)
    {
        lock (_gate)
        {
            long now = _clock();
            if (_windowStartAt == 0)
            {
                // 第一次喂入只立基线,还没有"一段时间"可言,速率无从谈起。
                _windowStartAt = now;
                _windowStartBytes = bytesDownloaded;
                _lastBytes = bytesDownloaded;
                _lastDataAt = now;
                return 0;
            }
            if (bytesDownloaded > _lastBytes)
            {
                _lastBytes = bytesDownloaded;
                _lastDataAt = now;
            }
            if (now - _windowStartAt >= WindowMilliseconds)
            {
                // 窗口走完:从"最后一次收到字节"那里重新开窗。不这样滚动的话,
                // 窗口里会一直含着一段很久以前的慢速,速率降不下来也升不上去。
                _windowStartAt = _lastDataAt;
                _windowStartBytes = _lastBytes;
            }
            long elapsed = now - _windowStartAt;
            if (elapsed <= 0)
            {
                // 刚滚过窗口、还没有新数据,沿用上一个值,别闪一个 0 出去。
                return _lastRate;
            }
            long gained = _lastBytes - _windowStartBytes;
            if (gained > 0)
            {
                _lastRate = gained * 1000.0 / elapsed;
            }
            else if (now - _lastDataAt >= WindowMilliseconds)
            {
                // 整个窗口一个字节都没到:是真的停住了,速率归零。
                _lastRate = 0;
            }
            return _lastRate;
        }
    }
}