using System.Globalization;

namespace VelaShell.Core.Net;

/// <summary>
/// 主机文本与 <c>host:port</c> 之间的换算;IPv6 字面量的方括号都在这里处理(#611)。
/// </summary>
/// <remarks>
/// <para>
/// 主机名与 IPv4 里都没有冒号,所以「含冒号」就当 IPv6 字面量 —— 与 SSH 库 <c>SshEndPoint</c> 同一个判断。
/// 不用 <c>IPAddress.TryParse</c>:它在 Windows 上认不出按网卡名写的 zone id(<c>fe80::1%en0</c>),
/// 还会把 <c>[::1]:2222</c> 里的端口默默丢掉。
/// </para>
/// <para>
/// 配置里存的主机一律是<b>不带方括号</b>的裸地址。方括号只是拼 <c>host:port</c> 时为了分清端口才加的写法:
/// 带进配置的话,经跳板的 <c>direct-tcpip</c> 会把它原样发给服务端,主机密钥也会与裸地址记成两台。
/// </para>
/// </remarks>
public static class HostAddress
{
    /// <summary>
    /// 去掉两端空白,以及包住整个地址的方括号(<c>[2001:db8::1]</c> → <c>2001:db8::1</c>)。
    /// 别的原样返回 —— 主机栏在串口协议下装的是设备名(<c>COM3</c>、<c>/dev/ttyUSB0</c>),不能乱动。
    /// </summary>
    public static string Normalize(string? host)
    {
        string value = (host ?? string.Empty).Trim();
        return value.Length > 2 && value[0] == '[' && value[^1] == ']' && IsBracketable(value[1..^1])
            ? value[1..^1]
            : value;
    }

    /// <summary>
    /// 拆 <c>host[:port]</c>:<c>host</c>、<c>host:port</c>、<c>[v6]</c>、<c>[v6]:port</c>。
    /// 不带方括号又有多个冒号的,整段是 IPv6 地址、不拆端口 —— <c>2001:db8::1:22</c> 本来就读不出哪段是端口;
    /// IPv6 要带端口就得加方括号,与 OpenSSH 的写法一致。
    /// </summary>
    /// <param name="text">要拆的文本;两端空白不算。</param>
    /// <param name="host">主机,不带方括号。</param>
    /// <param name="port">写了端口就是那个端口,没写为 <see langword="null" />。</param>
    /// <returns>
    /// 形态不对时为 <see langword="false" />:空串、方括号不闭合或是空的、方括号后面不是 <c>:端口</c>、
    /// 端口不是 1–65535 的数字。
    /// </returns>
    public static bool TrySplit(string? text, out string host, out int? port)
    {
        host = string.Empty;
        port = null;
        string value = (text ?? string.Empty).Trim();
        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || !IsBracketable(value[1..close]))
            {
                return false;
            }
            string rest = value[(close + 1)..];
            int bracketedPort = 0;
            if (rest.Length > 0 && (rest[0] != ':' || !TryParsePort(rest[1..], out bracketedPort)))
            {
                return false;
            }
            host = value[1..close];
            port = rest.Length > 0 ? bracketedPort : null;
            return true;
        }

        int colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || colon != value.LastIndexOf(':'))
        {
            host = value;
            return value.Length > 0;
        }
        if (colon == 0 || !TryParsePort(value[(colon + 1)..], out int parsed))
        {
            return false;
        }
        host = value[..colon];
        port = parsed;
        return true;
    }

    /// <summary>拼进 <c>host:port</c> 或 URL 时的主机写法:IPv6 加方括号,已经带着的不再加。</summary>
    public static string Bracket(string host) =>
        host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;

    /// <summary><c>host:port</c>;IPv6 写成 <c>[2001:db8::1]:22</c>,否则读不出哪段是端口。</summary>
    public static string FormatEndPoint(string host, int port) =>
        string.Create(CultureInfo.InvariantCulture, $"{Bracket(host)}:{port}");

    /// <summary><c>user@host:port</c>(IPv6 加方括号);用户名空着时不带 <c>user@</c>。</summary>
    public static string FormatTarget(string? user, string host, int port) =>
        string.IsNullOrEmpty(user) ? FormatEndPoint(host, port) : $"{user}@{FormatEndPoint(host, port)}";

    /// <summary>方括号里能装的东西:非空,不再有方括号,不带空白。</summary>
    private static bool IsBracketable(string inner) =>
        inner.Length > 0 && inner.AsSpan().IndexOfAny('[', ']') < 0 && !inner.Any(char.IsWhiteSpace);

    private static bool TryParsePort(string text, out int port)
    {
        port = 0;
        return text.Length is > 0 and <= 5
               && text.All(char.IsAsciiDigit)
               && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
               && port is >= 1 and <= 65535;
    }
}
