namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// 能施加背压的进度接收方:生产者 await <see cref="ReportAsync" /> 之后才产下一条。
/// <para>
/// 隔离插件的执行输出要逐条过 RPC。SSH 读泵一段输出就同步 <see cref="IProgress{T}.Report" /> 上千行时,
/// 有界队列只能溢出、把执行判失败;改成等它,慢接收方经 SSH 窗口把远端也慢下来,不丢输出也不中断。
/// 进程内插件的接收方是同步的,不实现本接口,照旧逐条 <see cref="IProgress{T}.Report" />。
/// </para>
/// </summary>
internal interface IFlowControlledProgress<in T> : IProgress<T>
{
    /// <summary>
    /// 入队一条:有空位就地入队(不看取消 —— 已经发生的事实不能因同时到来的取消而丢),
    /// 满了等到有空位,等待可取消。投递已失败或已收尾时抛出。
    /// </summary>
    ValueTask ReportAsync(T value, CancellationToken cancellationToken);
}
