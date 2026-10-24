namespace VelaShell.Core.Tunnels;

/// <summary>
/// 本机监听没起来(地址不是这台电脑的、端口被系统禁止、套接字所在目录不存在或不可写……)。
/// 消息已本地化,说清在哪儿(配置里的地址与端口、或套接字路径)与为什么(套接字错误码);原始异常挂在 <see cref="Exception.InnerException" /> 上。
/// </summary>
/// <param name="message">面向用户的说明(已本地化)。</param>
/// <param name="inner">底层异常。</param>
public sealed class TunnelBindFailedException(string message, Exception inner) : Exception(message, inner);
