namespace VelaShell.Services.Update;

/// <summary>
/// 下载状态的显示侧快照器:把 <see cref="UpdateDownloadProgress" /> 的字节流攒成
/// 「已下载 / 总量 / 当前速率 / 已停滞多久」。
/// </summary>
/// <remarks>
/// 单独一个类的原因在于下载线程<b>只在收到新字节时才汇报</b>。网络一慢一卡就没有任何回调,
/// 界面只能停在最后一个速率值上,用户分不清"慢"和"已经不动了"。所以这里额外提供一个
/// 由界面心跳驱动的 <see cref="Tick" />:它不产生新数据,只让速率归零、把停滞时长顶出来。
/// </remarks>
/// <remarks>创建进度快照器。</remarks>
/// <param name="stallThreshold">
/// 判定停滞所需的时长;不传则用 <see cref="StallThreshold" />。测试用短阈值,
/// 免得为了验证停滞真的去等十秒。
/// </param>
/// <param name="speedFreshness">
/// 速率保鲜期;不传则用 <see cref="SpeedFreshness" />。
/// </param>
public sealed class UpdateDownloadTracker(TimeSpan? stallThreshold = null, TimeSpan? speedFreshness = null)
{
    /// <summary>连续这么久没有新字节就算停滞,界面改用停滞文案。</summary>
    public static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 默认的速率保鲜期:多久拿不到新的速率值就把速率清零。速率是"一段时间窗内的实测值",
    /// 窗口过去后再拿它当当前速率显示就是假数据了。
    /// </summary>
    public static readonly TimeSpan SpeedFreshness = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _stallThreshold = stallThreshold ?? StallThreshold;
    private readonly TimeSpan _speedFreshness = speedFreshness ?? SpeedFreshness;
    private DateTime _lastAdvancedAt = DateTime.UtcNow;
    private DateTime _lastSpeedAt = DateTime.UtcNow;

    /// <summary>已下载字节数。</summary>
    public long BytesDownloaded { get; private set; }

    /// <summary>整包字节数;未知时为 0。</summary>
    public long TotalBytes { get; private set; }

    /// <summary>已完成百分比(0–100);总量未知时为 0。</summary>
    public int Percent => TotalBytes > 0
        ? (int)Math.Clamp(BytesDownloaded * 100 / TotalBytes, 0, 100)
        : 0;

    /// <summary>当前速率(字节/秒);长时间没有新速率时为 0。</summary>
    public double BytesPerSecond { get; private set; }

    /// <summary>距离上一次收到新字节过去了多久。</summary>
    public TimeSpan StalledFor => DateTime.UtcNow - _lastAdvancedAt;

    /// <summary>是否已停滞到需要提醒用户的程度(见 <see cref="StallThreshold" />)。</summary>
    public bool IsStalled => StalledFor >= _stallThreshold;

    /// <summary>接收一次下载汇报。字节数只增不减,速率只认正数。</summary>
    /// <param name="progress">下载源给出的进度快照。</param>
    public void Report(UpdateDownloadProgress progress)
    {
        if (progress.BytesDownloaded > BytesDownloaded)
        {
            BytesDownloaded = progress.BytesDownloaded;
            _lastAdvancedAt = DateTime.UtcNow;
        }
        if (progress.TotalBytes > 0)
        {
            TotalBytes = progress.TotalBytes;
        }
        if (progress.BytesPerSecond > 0)
        {
            BytesPerSecond = progress.BytesPerSecond;
            _lastSpeedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 心跳:在两次汇报之间刷新派生值。界面定时调用,让速率能归零、停滞时长能增长。
    /// </summary>
    public void Tick()
    {
        if (DateTime.UtcNow - _lastSpeedAt >= _speedFreshness)
        {
            BytesPerSecond = 0;
        }
    }
}
