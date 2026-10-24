namespace VelaShell.Core.Models;

/// <summary>
/// 文件传输(上传/下载)任务的状态。
/// </summary>
public enum TransferStatus
{
    /// <summary>已加入队列,等待开始。</summary>
    Queued,

    /// <summary>正在传输中。</summary>
    InProgress,

    /// <summary>正在断点续传中。</summary>
    Resuming,

    /// <summary>已成功完成。</summary>
    Completed,

    /// <summary>传输失败。</summary>
    Failed,

    /// <summary>已被取消。</summary>
    Cancelled,

    /// <summary>
    /// 连接没了(或临时失败重试用完)停下,留在传输队列里等用户「继续」—— 换一条连接就能接着传,
    /// 不算这个文件失败。只出现在记进传输队列的批次里;其余传输照旧是 <see cref="Failed" />。
    /// 加在末尾:各成员的数值不变。
    /// </summary>
    Paused
}
