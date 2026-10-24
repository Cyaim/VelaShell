namespace VelaShell.Core.Tunnels;

/// <summary>
/// 本机这头要监听的 Unix 套接字路径上已经有文件。由创建转发前的预检抛出(与 <see cref="TunnelPortInUseException" /> 同一个用意):
/// 多半是上一次没收拾干净,也可能是别的程序的套接字 —— 不替用户删,说清楚让他自己判断。
/// </summary>
/// <param name="message">面向用户的说明(已本地化)。</param>
/// <param name="path">那个已存在的路径。</param>
public sealed class TunnelSocketPathInUseException(string message, string path) : Exception(message)
{
    /// <summary>已存在的套接字路径。</summary>
    public string Path { get; } = path;
}
