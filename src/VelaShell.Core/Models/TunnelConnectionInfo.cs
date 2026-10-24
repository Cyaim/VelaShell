namespace VelaShell.Core.Models;

/// <summary>
/// 隧道上一条正在搬的连接(快照):隧道面板的「当前连接」列表一行一条。
/// </summary>
/// <remarks>
/// 字节数一律从本机的视角说:<see cref="BytesSent" /> 是本机这头送进隧道的,<see cref="BytesReceived" /> 是从隧道那头收回来的
/// —— 不论连接是谁发起的(与 SSH 库 <c>PortForwarder</c> 同一个口径)。
/// </remarks>
/// <param name="Id">这条连接在本隧道内的序号。</param>
/// <param name="Source">
/// 来源:本地 / 动态转发是本机上连进来的那个客户端,远程转发是服务端报的发起方;
/// 给不出(Unix 套接字上连进来的、服务端没报)时为 <see langword="null" />。
/// </param>
/// <param name="Target">目标(主机:端口、套接字路径;动态转发是握手里给的那个)。</param>
/// <param name="StartedAtUtc">开始搬运的时刻(UTC)。</param>
/// <param name="BytesSent">到目前为止本机送进隧道的应用字节数。</param>
/// <param name="BytesReceived">到目前为止从隧道收回的应用字节数。</param>
public sealed record TunnelConnectionInfo(
    long Id,
    string? Source,
    string Target,
    DateTime StartedAtUtc,
    long BytesSent,
    long BytesReceived);
