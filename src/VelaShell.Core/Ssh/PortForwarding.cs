using VelaShell.Core.Models;

namespace VelaShell.Core.Ssh;

/// <summary>端口转发类型(对应 SSH 的 direct-tcpip / forwarded-tcpip / SOCKS 动态转发)。</summary>
public enum PortForwardKind
{
    /// <summary>本地转发:监听在本机 Bound 端,流量送往远端 Target。</summary>
    Local,

    /// <summary>远程转发:监听在服务器 Bound 端,流量送回本机侧 Target。</summary>
    Remote,

    /// <summary>动态转发(SOCKS 代理):仅使用 Bound 端,无固定 Target。</summary>
    Dynamic,

    /// <summary>远程动态转发:服务器 Bound 端当 SOCKS 代理,本机按 <see cref="PortForwardRequest.PermitRemoteOpen" /> 替远端去连;无固定 Target。</summary>
    RemoteDynamic
}

/// <summary>
/// 库中立的端口转发请求:Bound = 监听端(Local 在本机、Remote 在服务器),
/// Target = 目的端;Dynamic 转发不需要 Target。
/// </summary>
/// <param name="Kind">端口转发类型(本地/远程/动态)。</param>
/// <param name="BoundHost">监听端主机地址。</param>
/// <param name="BoundPort">监听端端口。</param>
/// <param name="TargetHost">目的端主机地址;动态转发时为空。</param>
/// <param name="TargetPort">目的端端口;动态转发时为空。</param>
public sealed record PortForwardRequest(
    PortForwardKind Kind,
    string BoundHost,
    uint BoundPort,
    string? TargetHost = null,
    uint? TargetPort = null)
{
    /// <summary>限速:每个方向每秒最多搬多少应用字节,这条转发的全部连接共用;<see langword="null" /> 不限。</summary>
    public long? MaxBytesPerSecond { get; init; }

    /// <summary>远程动态转发的放行名单(<c>主机:端口</c>,或单独一条 <c>any</c>);别的类型不用。</summary>
    public IReadOnlyList<string>? PermitRemoteOpen { get; init; }

    /// <summary>监听端改用 Unix 域套接字(Local 在本机、Remote 在服务器);设了就不用 BoundHost / BoundPort。</summary>
    public string? BoundSocketPath { get; init; }

    /// <summary>目的端改用 Unix 域套接字(Local 在服务器、Remote 在本机);设了就不用 TargetHost / TargetPort。</summary>
    public string? TargetSocketPath { get; init; }
}

/// <summary>
/// 一条运行中的端口转发的句柄,由 <see cref="ISshClientWrapper.StartPortForwardAsync" /> 返回。
/// 创建即启动;<see cref="IAsyncDisposable.DisposeAsync" /> 停止监听并从客户端摘除(幂等,底层已随
/// 连接失效时静默成功)。转发通道内的错误(如目标拒绝连接)不会停掉监听端口,
/// 经 <see cref="ChannelError" /> 上报供界面展示。
/// </summary>
public interface IPortForwardHandle : IAsyncDisposable
{
    /// <summary>转发是否处于已启动(监听中)状态。</summary>
    bool IsStarted { get; }

    /// <summary>
    /// 经该转发累计传输的字节数(上行 + 下行)。计数由宿主自己的中继累加,
    /// 因此只统计真正流过转发的数据,不含 SSH 协议开销与 SOCKS 握手字节。
    /// </summary>
    long BytesTransferred { get; }

    /// <summary>该转发自建立以来累计接受的连接数。</summary>
    int TotalConnections { get; }

    /// <summary>该转发当前仍在传输的连接数。</summary>
    int ActiveConnections { get; }

    /// <summary>此刻本机送进隧道的速率(应用字节 / 秒,最近三个整秒的平均)。</summary>
    double SentBytesPerSecond { get; }

    /// <summary>此刻从隧道收回本机的速率(应用字节 / 秒,最近三个整秒的平均)。</summary>
    double ReceivedBytesPerSecond { get; }

    /// <summary>正在搬的连接(快照,按序号排;每次读取都是新的一份)。</summary>
    IReadOnlyList<TunnelConnectionInfo> Connections { get; }

    /// <summary>转发通道错误(每个经过的连接失败时触发,监听端口本身仍在)。</summary>
    event Action<Exception>? ChannelError;
}
