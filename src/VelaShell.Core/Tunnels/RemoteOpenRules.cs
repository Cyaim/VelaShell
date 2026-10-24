using System.Globalization;

namespace VelaShell.Core.Tunnels;

/// <summary>
/// 远程动态转发的放行名单(<see cref="Models.TunnelConfig.PermitRemoteOpen" />)在界面上的写法与校验。
/// </summary>
/// <remarks>
/// <para>
/// 写法同 ssh_config(5) 的 <c>PermitRemoteOpen</c>,也同 SSH 库 <c>RemoteOpenPolicy</c> 认的那一套:每条 <c>主机:端口</c>,
/// 主机可带 <c>*</c> / <c>?</c> 通配(不分大小写),IPv6 字面量写在方括号里(<c>[::1]:22</c>),端口是 1–65535 或 <c>*</c>;
/// 单独一条 <c>any</c> 是全放。条目之间用空白或逗号隔开。
/// </para>
/// <para>
/// 宿主在这里先校验一遍,是为了在表单上当场指出哪一条写错了;真正放不放行由库按远端在握手里给的名字比
/// (不先解析:名单写 IP、远端给域名就对不上,宁可错拒)。
/// </para>
/// </remarks>
public static class RemoteOpenRules
{
    /// <summary>全放(OpenSSH 的 <c>PermitRemoteOpen any</c>):本机能到的地方远端都能到。</summary>
    public const string Any = "any";

    private static readonly char[] Separators = [' ', '\t', '\r', '\n', ',', '，'];

    /// <summary>把输入框里的文字拆成规则并逐条校验。</summary>
    /// <param name="text">输入框里的文字。</param>
    /// <param name="rules">拆出来的规则(去掉空白);<c>any</c> 统一成小写。</param>
    /// <param name="invalidRule">写错的第一条;一条也没写时为 <see langword="null" />。</param>
    /// <returns>至少一条、而且每条都写对了。</returns>
    public static bool TryParse(string? text, out IReadOnlyList<string> rules, out string? invalidRule)
    {
        string[] parts = (text ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        rules = [.. parts.Select(p => string.Equals(p, Any, StringComparison.OrdinalIgnoreCase) ? Any : p)];
        invalidRule = null;
        if (rules.Count == 0)
        {
            return false;
        }
        // any 只能单独写:和别的规则写在一起,要么是手误、要么以为它只是又一条 —— 两种都不该悄悄变成全放。
        if (rules.Contains(Any) && rules.Count > 1)
        {
            invalidRule = Any;
            return false;
        }
        invalidRule = rules.FirstOrDefault(rule => rule != Any && !IsValidRule(rule));
        return invalidRule is null;
    }

    /// <summary>一条 <c>主机:端口</c> 写得对不对。</summary>
    /// <param name="rule">一条规则。</param>
    public static bool IsValidRule(string rule)
    {
        string host;
        string port;
        if (rule.StartsWith('['))
        {
            int close = rule.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= rule.Length || rule[close + 1] != ':')
            {
                return false;
            }
            host = rule[1..close];
            port = rule[(close + 2)..];
        }
        else
        {
            int colon = rule.LastIndexOf(':');
            if (colon <= 0)
            {
                return false;
            }
            host = rule[..colon];
            port = rule[(colon + 1)..];
        }
        return host.Length > 0
               && (port == "*"
                   || (int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number is >= 1 and <= 65535));
    }

    /// <summary>名单写回输入框的样子:空格隔开。</summary>
    /// <param name="rules">规则;<see langword="null" /> 写成空串。</param>
    public static string Format(IReadOnlyList<string>? rules) => rules is null ? string.Empty : string.Join(' ', rules);
}
