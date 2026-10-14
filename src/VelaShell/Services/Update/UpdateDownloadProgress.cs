namespace VelaShell.Services.Update;

/// <summary>
/// 更新包下载的一次进度快照。百分比只能回答"下了多少",答不了"下得多快"——
/// 慢网和卡死停在同一个数字上,所以字节数与速率必须一路带出界面。
/// </summary>
/// <param name="BytesDownloaded">已落到磁盘(含断点续传时此前那部分)的字节数。</param>
/// <param name="TotalBytes">整包字节数;未知时为 0,此时 <see cref="Percent" /> 恒为 0。</param>
/// <param name="BytesPerSecond">最近一个时间窗内实测的平均速率,单位字节/秒。</param>
public sealed record UpdateDownloadProgress(long BytesDownloaded, long TotalBytes, double BytesPerSecond)
{
    /// <summary>已完成百分比(0–100);总量未知时为 0。</summary>
    public int Percent => TotalBytes > 0
        ? (int)Math.Clamp(BytesDownloaded * 100 / TotalBytes, 0, 100)
        : 0;

    /// <summary>下载收尾时的快照:已下满整包,不再有速率可言。</summary>
    /// <param name="totalBytes">整包字节数;未知时传 0。</param>
    /// <returns>100% 的完成快照。</returns>
    public static UpdateDownloadProgress Completed(long totalBytes) => new(totalBytes, totalBytes, 0);
}