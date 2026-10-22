using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace VelaShell.Infrastructure.Diagnostics;

/// <summary>
/// 诊断包落盘前的脱敏:主机、用户名、本机路径、IP、邮箱与形如口令 / 令牌 / 私钥的片段一律换成占位符。
/// </summary>
/// <remarks>
/// <para>
/// 同一个值在整个包里换成同一个占位符(<c>&lt;host-1&gt;</c>、<c>&lt;ip-2&gt;</c>):看日志的人仍能对上
/// 「这一行和那一行说的是同一台机器」,却不知道是哪台。映射表不进包。
/// </para>
/// <para>
/// 字面值匹配按词边界:<c>db1</c> 不会把 <c>db10</c> 或 <c>mydb1</c> 拆开。太短的值(两个字符以内)与
/// <c>root</c>、<c>ubuntu</c> 这类人人都在用的登录名不换 —— 它们认不出是谁,换掉只会把日志弄得没法读。
/// </para>
/// <para>
/// 口令一类的规则与会话日志的遮盖(<c>SessionOutputMasker</c>)是同一个意图:名字里<b>带着</b> password / token / secret …
/// 的 <c>名字=值</c>、<c>名字: 值</c>(含引号与 JSON 的写法)只换值;<c>Bearer</c> / <c>Basic</c> 后面的凭据;PEM 私钥块的正文;
/// 几种自带前缀的令牌。那个类在应用工程里,这里引用不到,规则在这里另写一份。
/// </para>
/// <para>
/// 实例不是线程安全的:一个包一个实例,顺序处理各个文件。
/// </para>
/// </remarks>
public sealed partial class DiagnosticRedactor
{
    /// <summary>认不出是谁的常见登录名,不换。</summary>
    private static readonly HashSet<string> CommonUserNames =
    [
with(StringComparer.OrdinalIgnoreCase),         "root", "admin", "administrator", "ubuntu", "debian", "centos", "ec2-user", "pi", "user", "git", "test",
    ];

    /// <summary>本机回环地址不算可识别信息。</summary>
    private static readonly HashSet<string> LoopbackHosts =
    [
with(StringComparer.OrdinalIgnoreCase),         "localhost", "127.0.0.1", "::1", "[::1]", "0.0.0.0",
    ];

    private readonly Regex? _literals;
    private readonly Dictionary<string, string> _literalTokens = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, string> _ipTokens = [with(StringComparer.Ordinal)];
    private readonly (string Path, StringComparison Comparison)[] _homes;
    private readonly Dictionary<string, int> _counts = [with(StringComparer.Ordinal)];

    /// <summary>按已保存连接里的主机与用户名,以及本机的用户名、机器名与主目录建一个脱敏器。</summary>
    /// <param name="hosts">连接配置里的主机(含跳板机);IP 与域名都行。</param>
    /// <param name="userNames">连接配置与共享凭据里的登录名。</param>
    /// <param name="homeDirectory">本机用户主目录;换成 <c>~</c>。</param>
    /// <param name="localUserName">本机登录名。</param>
    /// <param name="machineName">本机机器名。</param>
    public DiagnosticRedactor(IEnumerable<string> hosts, IEnumerable<string> userNames,
        string? homeDirectory, string? localUserName, string? machineName)
    {
        var literals = new List<string>();
        AddLiterals(hosts.Select(TrimBrackets).Where(host => !LoopbackHosts.Contains(host)), "host", literals);
        AddLiterals(userNames.Where(name => !CommonUserNames.Contains(name)), "user", literals);
        if (!string.IsNullOrWhiteSpace(machineName) && Usable(machineName)) Add(machineName.Trim(), "<machine>", literals);
        if (!string.IsNullOrWhiteSpace(localUserName) && Usable(localUserName) && !CommonUserNames.Contains(localUserName))
            Add(localUserName.Trim(), "<local-user>", literals);
        if (literals.Count > 0)
        {
            // 长的先试:同时存着 db1 与 db1.example.com 时,整个域名换成一个占位符
            string alternation = string.Join('|', literals.OrderByDescending(value => value.Length).Select(Regex.Escape));
            _literals = new Regex($@"(?<![\p{{L}}\p{{N}}_.\-])(?:{alternation})(?![\p{{L}}\p{{N}}_\-])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        StringComparison pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _homes = string.IsNullOrWhiteSpace(homeDirectory) || homeDirectory.TrimEnd('/', '\\').Length < 2
            ? []
            : [.. new[] { homeDirectory.TrimEnd('/', '\\'), homeDirectory.TrimEnd('/', '\\').Replace('\\', '/') }
                .Distinct(StringComparer.Ordinal)
                .Select(path => (path, pathComparison))];

        void AddLiterals(IEnumerable<string> values, string kind, List<string> into)
        {
            int index = 0;
            foreach (string value in values.Select(value => value.Trim()).Where(Usable).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_literalTokens.ContainsKey(value)) continue;
                Add(value, string.Create(CultureInfo.InvariantCulture, $"<{kind}-{++index}>"), into);
            }
        }

        void Add(string value, string token, List<string> into)
        {
            if (!_literalTokens.TryAdd(value, token)) return;
            into.Add(value);
        }
    }

    /// <summary>各类被换掉的次数(类别 → 次数);只有计数,不含原值。</summary>
    public IReadOnlyDictionary<string, int> Counts => _counts;

    /// <summary>脱敏一段文本。</summary>
    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 主目录先换:路径里带着本机登录名,先换登录名就对不上整段路径了
        foreach ((string path, StringComparison comparison) in _homes)
        {
            for (int at = text.IndexOf(path, comparison); at >= 0; at = text.IndexOf(path, at + path.Length, comparison))
                Count("path");
            text = text.Replace(path, "~", comparison);
        }

        text = PrivateKeyBlock().Replace(text, match =>
        {
            // 私钥正文整段换掉,首尾两行与换行留着:看日志的人知道这里有过一把私钥
            string body = match.Groups["body"].Value;
            if (string.IsNullOrWhiteSpace(body)) return match.Value;
            Count("secret");
            string head = body[..(body.Length - body.TrimStart().Length)];
            string tail = body[body.TrimEnd().Length..];
            return match.Groups["begin"].Value + head + "<redacted>" + tail + match.Groups["end"].Value;
        });
        // Bearer / Basic 先换:「token: Bearer abc」若先按名字=值换,值只吃到 Bearer 这个词,后面的 abc 就漏了
        text = BearerToken().Replace(text, _ =>
        {
            Count("secret");
            return "Bearer <redacted>";
        });
        text = BasicCredential().Replace(text, match =>
        {
            if (!IsBasicCredential(match.Groups["credential"].Value)) return match.Value;
            Count("secret");
            return match.Groups["scheme"].Value + " <redacted>";
        });
        text = SecretAssignment().Replace(text, match =>
        {
            Count("secret");
            // 值带引号的,引号留着:JSON 还是 JSON(字符串里套的 JSON 是 \"…\",照样留着转义)
            string value = match.Groups["value"].Value;
            string masked = value.StartsWith("\\\"", StringComparison.Ordinal) ? "\\\"<redacted>\\\""
                : value[0] is '"' or '\'' ? $"{value[0]}<redacted>{value[0]}" : "<redacted>";
            return match.Groups["key"].Value + match.Groups["sep"].Value + masked;
        });
        text = PrefixedToken().Replace(text, _ =>
        {
            Count("secret");
            return "<redacted>";
        });

        if (_literals is not null)
        {
            text = _literals.Replace(text, match =>
            {
                string token = LiteralToken(match.Value);
                Count(token.StartsWith("<host", StringComparison.Ordinal) ? "host" : "user");
                return token;
            });
        }

        text = Email().Replace(text, _ =>
        {
            Count("email");
            return "<email>";
        });
        text = IPv4().Replace(text, match =>
        {
            string ip = match.Value;
            return ip.StartsWith("127.", StringComparison.Ordinal) || ip == "0.0.0.0" ? ip : IpToken(ip);
        });
        return IPv6Candidate().Replace(text, match =>
        {
            // 候选只看字形,是不是地址交给 IPAddress 判:10:15:30 这样的时刻、MAC 地址解不成 IPv6,原样留着。
            // 回环 ::1 与未指定地址 :: 同 IPv4 的 127.* 与 0.0.0.0 一样不换;同一个地址换种写法也是同一个占位符
            if (!IPAddress.TryParse(match.Value, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetworkV6
                || IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any))
            {
                return match.Value;
            }
            return IpToken(address.ToString());
        });
    }

    private void Count(string kind, int times = 1)
    {
        if (times <= 0) return;
        _counts[kind] = _counts.GetValueOrDefault(kind) + times;
    }

    /// <summary>同一个地址换成同一个 <c>&lt;ip-N&gt;</c>。</summary>
    private string IpToken(string ip)
    {
        Count("ip");
        if (!_ipTokens.TryGetValue(ip, out string? token))
        {
            token = string.Create(CultureInfo.InvariantCulture, $"<ip-{_ipTokens.Count + 1}>");
            _ipTokens[ip] = token;
        }
        return token;
    }

    /// <summary>
    /// 字面值的占位符。正则的忽略大小写比字典宽 —— 开尔文符号 K(U+212A)在正则里等于 k,<c>OrdinalIgnoreCase</c> 却不认:
    /// 字典查不到时按正则的口径逐个再对一遍;万一还对不上也照样遮,只是不编号。
    /// </summary>
    private string LiteralToken(string value)
    {
        if (_literalTokens.TryGetValue(value, out string? token)) return token;
        foreach ((string literal, string candidate) in _literalTokens)
        {
            if (Regex.IsMatch(value, $@"\A{Regex.Escape(literal)}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return candidate;
        }
        return "<redacted>";
    }

    /// <summary><c>Basic</c> 的凭据是 base64 的「用户名:口令」(RFC 7617):解得开、解出来带冒号才算,Basic authentication 这样的普通单词不动。</summary>
    private static bool IsBasicCredential(string value)
    {
        byte[] decoded = new byte[value.Length];
        return Convert.TryFromBase64String(value, decoded, out int written) && decoded.AsSpan(0, written).Contains((byte)':');
    }

    /// <summary>两个字符以内的值不换:<c>a</c>、<c>db</c> 这种会把日志里的普通单词一起换掉。</summary>
    private static bool Usable(string value) => value.Trim().Length >= 3;

    private static string TrimBrackets(string host) => host.Trim().TrimStart('[').TrimEnd(']');

    /// <summary>
    /// <c>名字=值</c> / <c>名字: 值</c>:名字里带着口令一类的词(<c>DB_PASSWORD</c>、<c>access_token</c>、<c>client_secret</c>),
    /// 名字与值都可以带引号(JSON、YAML),只换值。值为空的(<c>Password: </c> 提示)不动。
    /// </summary>
    /// <remarks>
    /// 名字的边界只认 ASCII 的字母、数字与 <c>_ . -</c>:<c>\b</c> 把 <c>_</c> 也算在词里,<c>DB_PASSWORD</c> 原先就是这么漏的。
    /// 先用前瞻确认这一段名字里有关键字,再用原子组把整段一次吃下、不回吐:一长串像名字的字符只扫一遍,不会回溯成平方级。
    /// <c>PublicKeyToken</c> 是程序集强名称里公开的那一段,崩溃记录里到处都是,不换。
    /// </remarks>
    [GeneratedRegex("""(?<key>(?<![A-Za-z0-9_.\-])(?!PublicKeyToken(?![A-Za-z0-9_.\-]))(?=[A-Za-z0-9_.\-]*?(?i:password|passwd|pwd|passphrase|secret|token|api[_\-]?key|access[_\-]?key|private[_\-]?key|credential))(?>[A-Za-z0-9_.\-]+)(?:\\?["'])?)(?<sep>[ \t]*[:=][ \t]*)(?!\\?["']?<redacted>)(?<value>"(?:[^"\\\r\n]|\\[^\r\n])+"|'[^'\r\n]+'|\\"(?:[^"\\\r\n]|\\[^"\\\r\n]|\\\\(?:[^"\\\r\n]|\\[^\r\n]))+\\"|[^\s,;"']+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"\bBearer\s+(?!<redacted>)[A-Za-z0-9._~+/\-]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    /// <summary><c>Basic</c> 后面像 base64 的一段;是不是凭据由 <see cref="IsBasicCredential" /> 判。</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<scheme>(?i:basic))[ \t]+(?<credential>[A-Za-z0-9+/]{8,}={0,2})", RegexOptions.CultureInvariant)]
    private static partial Regex BasicCredential();

    /// <summary>PEM 私钥块;没有结尾那一行(日志被截断)时遮到文本末尾。</summary>
    [GeneratedRegex(@"(?<begin>-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----)(?<body>[\s\S]*?)(?<end>-----END [A-Z0-9 ]*PRIVATE KEY-----|\z)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlock();

    /// <summary>自带前缀、不看上下文就认得出的令牌:AWS 访问密钥 ID、GitHub、Slack,以及 OpenAI 式的 <c>sk-…</c>。</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:(?:AKIA|ASIA)[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|xox[abprs]-[A-Za-z0-9\-]{10,}|sk-[A-Za-z0-9_\-]{20,})(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedToken();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    /// <summary>
    /// IPv4。句末的句号不挡(<c>closed by 10.0.0.5.</c>)。四段的版本号不是 IP:<c>Version=12.1.4.0</c>(崩溃记录里的程序集名)、
    /// <c>v1.2.3.4</c>,以及启动横幅与崩溃记录开头的 <c>VelaShell 0.0.1.0</c>(见 <see cref="DiagnosticLog" />)。
    /// </summary>
    [GeneratedRegex(@"(?<![\d.]|[vV]|(?i:version)[ \t]{0,3}[=:]?[ \t]{0,3}|VelaShell )(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?!\d|\.\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex IPv4();

    /// <summary>
    /// 形似 IPv6 的一段:至少两个冒号,两头不挨着字母、数字与冒号(句末的句号不挡)。是不是真地址由 <see cref="IPAddress" /> 判;
    /// 量词都有上限,每个起点最多看几十个字符。内嵌 IPv4 的写法(<c>::ffff:203.0.113.7</c>)在这之前已被 IPv4 规则换掉了那一半。
    /// </summary>
    [GeneratedRegex(@"(?<![0-9A-Za-z:.])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![0-9A-Za-z:]|\.[0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex IPv6Candidate();
}
