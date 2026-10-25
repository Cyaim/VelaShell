using System.Text;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 远端文件名按什么编码解读:取会话的字符集(<c>ConnectionInfo.FileNameEncoding</c>,与终端同一个口径)。
/// SFTP 文件名(Infrastructure 的 <c>SftpConnectOptions</c>)与远端搜索的 exec 输出共用这一条规则,
/// 同一台机器上两处看到的名字才是同一个。
/// </summary>
/// <remarks>
/// <b>UTF-8、空白、认不出的名字一律给 <see langword="null" /></b>,调用方照各自的默认走:SFTP 交给库的 UTF-8 无损往返,
/// 远端搜索照旧按 UTF-8 解。认不出的名字(手改坏的配置)退回默认,而不是抛异常 —— 与终端那边 <c>ResolveEncoding</c> 退回 UTF-8 同理。
/// GBK / Big5 一族要 <c>Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)</c> 之后才取得到(<c>Program.Main</c> 已注册);
/// 没注册时取不到,同样退回默认。UTF-8 按代码页判,<c>utf8</c>、<c>UTF-8</c> 之类的别名都算。
/// </remarks>
public static class RemoteFileNameEncoding
{
    /// <summary>字符集名 → 文件名编码;UTF-8、空白或认不出时为 <see langword="null" />。</summary>
    /// <param name="name">字符集名(如 <c>GBK</c>)。</param>
    public static Encoding? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(name.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
        return encoding.CodePage == Encoding.UTF8.CodePage ? null : encoding;
    }
}
