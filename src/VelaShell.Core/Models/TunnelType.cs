namespace VelaShell.Core.Models;

/// <summary>
/// 指定 SSH 端口转发隧道的类型。
/// </summary>
public enum TunnelType
{
    /// <summary>
    /// 本地转发:localhost:localPort → remoteHost:remotePort(经由 SSH 服务器)
    /// </summary>
    LocalForward,

    /// <summary>
    /// 远程转发:sshServer:remotePort → localhost:localPort
    /// </summary>
    RemoteForward,

    /// <summary>
    /// Dynamic forward: localhost:localPort 作为 SOCKS 代理,目标由客户端协议协商(ssh -D)。
    /// </summary>
    DynamicForward,

    /// <summary>
    /// 远程动态转发:sshServer:remotePort 作为 SOCKS 代理,远端程序经它让本机替自己去连
    /// (<c>ssh -R [bind:]port</c> 不给目标),只放行 <see cref="TunnelConfig.PermitRemoteOpen" /> 里的目标。
    /// 追加在末尾:持久化存的是枚举的数值,前三个的值不能动。
    /// </summary>
    RemoteDynamicForward
}
