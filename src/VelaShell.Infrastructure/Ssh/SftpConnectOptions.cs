using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 按会话装配开 SFTP 通道时交给库的参数 —— 眼下只有一项:文件名按什么编码解读。
/// </summary>
/// <remarks>
/// <para>
/// 编码取会话的字符集(<see cref="ConnectionInfo.FileNameEncoding" />,与终端同一个口径)。连 GBK 服务器的人终端早就配成了 GBK,
/// 而 SFTP 一直按 UTF-8 解,同一台机器上终端里 <c>ls</c> 出来的中文名在文件面板里是一排替换字符。
/// </para>
/// <para>
/// <b>UTF-8、空、认不出的名字一律不传</b>(返回 <see langword="null" />,库用默认参数):库的默认是 UTF-8 且解不开的字节无损往返,
/// 那是 GBK 名字在 UTF-8 会话里还打得开、删得掉的原因;传一个显式的编码进去就换成了那种编码的规则。
/// 认不出的名字(手改坏的配置)退回默认,而不是让开 SFTP 抛异常 —— 与终端那边 <c>ResolveEncoding</c> 退回 UTF-8 同理。
/// </para>
/// </remarks>
internal static class SftpConnectOptions
{
    /// <summary>这个会话开 SFTP 时用的参数;null = 库的默认参数。</summary>
    /// <param name="info">会话的连接信息;null 表示拿不到(按默认处理)。</param>
    public static SftpOptions? For(ConnectionInfo? info) =>
        ResolveFileNameEncoding(info?.FileNameEncoding) is { } encoding
            ? SftpOptions.Default with { FileNameEncoding = encoding }
            : null;

    /// <summary>
    /// 字符集名 → 交给库的文件名编码;UTF-8、空白或认不出时为 <see langword="null" />(库按 UTF-8 无损往返)。
    /// </summary>
    /// <remarks>
    /// GBK / Big5 一族要 <c>Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)</c> 之后才取得到(<c>Program.Main</c> 已注册);
    /// 没注册时取不到,同样退回默认。UTF-8 按代码页判,<c>utf8</c>、<c>UTF-8</c> 之类的别名都算。
    /// </remarks>
    /// <param name="name">字符集名(如 <c>GBK</c>)。</param>
    /// <remarks>规则本身在 Core 的 <see cref="RemoteFileNameEncoding" />:远端搜索解 exec 输出用的是同一条。</remarks>
    internal static Encoding? ResolveFileNameEncoding(string? name) => RemoteFileNameEncoding.Resolve(name);
}
