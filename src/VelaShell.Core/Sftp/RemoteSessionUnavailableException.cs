namespace VelaShell.Core.Sftp;

/// <summary>
/// 操作所指的远程会话已经不在了(断开、关闭,或从来没登记过)。
/// </summary>
/// <remarks>
/// <para>
/// 单独一类,是为了让传输失败重试分得清「这条连接没了」与「服务端说不行」:前者在原会话上
/// 再试多少次都一样(会话 id 不会复活,重连得到的是一个新 id),但它也不是这个文件本身的问题 ——
/// 换一条连接就能接着传;后者(权限、路径不存在、磁盘满)换连接也没用。
/// </para>
/// <para>
/// 派生自 <see cref="InvalidOperationException" />:它原先就是以这个类型抛出的,
/// 按基类型接的调用方不受影响。
/// </para>
/// </remarks>
/// <param name="message">说明(写给日志看的,界面不直接显示会话 id)。</param>
public sealed class RemoteSessionUnavailableException(string message) : InvalidOperationException(message);
