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
/// 主机与用户名分两路认。<b>已知来源</b>(已存连接、最近连接、共享凭据,见构造函数)什么写法都认,单段的
/// <c>db-prod-3</c> 也换;字面值匹配按词边界:<c>db1</c> 不会把 <c>db10</c> 或 <c>mydb1</c> 拆开。太短的值(两个字符以内)与
/// <c>root</c>、<c>ubuntu</c> 这类人人都在用的登录名不换 —— 它们认不出是谁,换掉只会把日志弄得没法读。
/// <b>通用规则</b>兜已知来源之外的(连接改过之前的旧主机、异常消息里的主机、别人的家目录),只认长相不会认错的几种:
/// VelaShell 自己的断开日志行 <c>[SSH] 用户@主机:端口 closed</c>(格式是自己定的,两段都换);IPv4 / IPv6;
/// 小写书写、以常见顶级域或内网后缀结尾的域名(判据见 <see cref="IsGenericHostName" />);
/// <c>C:\Users\名</c>、<c>/home/名</c>、<c>/Users/名</c> 里的那一段名字。单段主机名、其余位置的用户名与文件名认不出来,
/// 隐私说明里照实写了。
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

    /// <summary>家目录路径里那一段不是哪个人的名字:Windows 的公共 / 默认配置目录、macOS 的共享目录。</summary>
    private static readonly HashSet<string> SharedProfileNames =
    [
with(StringComparer.OrdinalIgnoreCase),         "Public", "Default", "Default User", "All Users", "Shared", "Guest",
    ];

    /// <summary>
    /// 通用域名规则认的顶级域与内网后缀(小写)。只收常见的那些,并刻意<b>不收</b>与文件扩展名撞车的
    /// (<c>.sh</c>、<c>.md</c>、<c>.py</c>、<c>.rs</c>、<c>.pl</c>、<c>.pm</c>、<c>.so</c>、<c>.cc</c>、<c>.ps</c>、<c>.mo</c>、<c>.tf</c>、
    /// <c>.am</c>、<c>.ac</c>、<c>.mk</c>、<c>.pro</c>,以及 gTLD 里的 <c>.zip</c>、<c>.mov</c>)—— 传输记录里满是 <c>deploy.sh</c>、
    /// <c>backup.zip</c>,换成 <c>&lt;host-N&gt;</c> 只会让日志没法读。
    /// </summary>
    private static readonly HashSet<string> HostSuffixes =
    [
with(StringComparer.Ordinal),
        // 通用顶级域
        "com", "net", "org", "edu", "gov", "mil", "int", "info", "biz", "name", "io", "ai", "dev", "app", "cloud", "tech",
        "online", "site", "xyz", "top", "vip", "club", "shop", "asia",
        // 常见的国家与地区顶级域
        "cn", "hk", "mo", "tw", "jp", "kr", "sg", "my", "th", "vn", "id", "ph", "in", "au", "nz", "uk", "ie", "de", "fr", "nl",
        "be", "lu", "ch", "at", "it", "es", "pt", "se", "no", "dk", "fi", "is", "cz", "sk", "hu", "ro", "bg", "gr", "tr", "ru",
        "ua", "kz", "il", "ae", "sa", "za", "br", "ar", "cl", "co", "mx", "us", "ca", "eu", "me", "tv", "ws",
        // 内网与保留后缀
        "local", "lan", "internal", "intranet", "corp", "home", "localdomain", "arpa", "test", "example",
    ];

    /// <summary>
    /// VelaShell 自己联系的公共服务(更新与头像、资讯源、插件市场、IP 库下载页),连同它们的子域。
    /// 它们不说明用户是谁,遮掉反而看不出是哪一路请求失败了。
    /// </summary>
    private static readonly string[] PublicServiceDomains = ["github.com", "githubusercontent.com", "easilynet.top", "db-ip.com"];

    /// <summary>倒写域名的标识符(<c>com.velashell.app</c> 这类包标识、D-Bus 名)以这几段开头,不是主机。</summary>
    private static readonly HashSet<string> ReverseDomainHeads = [with(StringComparer.Ordinal), "com", "org", "net", "io"];

    private readonly Regex? _literals;
    private readonly Dictionary<string, string> _literalTokens = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, string> _ipTokens = [with(StringComparer.Ordinal)];
    private readonly (string Path, StringComparison Comparison)[] _homes;
    private readonly Dictionary<string, int> _counts = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _notHosts = [with(StringComparer.OrdinalIgnoreCase)];
    private int _hostTokens;
    private int _userTokens;

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
            foreach (string value in values.Select(value => value.Trim()).Where(Usable).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_literalTokens.ContainsKey(value)) continue;
                Add(value, NextToken(kind), into);
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

    /// <summary>
    /// 登记长得像域名、却不是主机的名字(插件 id,如 <c>velashell.ai</c>):通用域名规则不换它们。
    /// 诊断包按插件清单与收进包的插件日志登记,调用方不用管。
    /// </summary>
    /// <param name="names">这些名字。</param>
    public void KeepNames(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (!string.IsNullOrWhiteSpace(name)) _notHosts.Add(name.Trim());
        }
    }

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

        // 断开日志行的格式是自己定的(SshDisconnectLog):「@」前是用户、「:端口 closed」前是主机,不靠猜。
        // 先于字面值换:已知的值照样拿到自己那个占位符,不认识的(连接改过之前的旧主机、单段的主机名)也换掉
        text = DisconnectLine().Replace(text, match =>
        {
            Group user = match.Groups["user"];
            Group host = match.Groups["host"];
            string maskedUser = user.Success ? MaskName(user.Value, "user") + "@" : string.Empty;
            return match.Groups["head"].Value + maskedUser + MaskDisconnectHost(host.Value) + match.Groups["tail"].Value;
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
        // 别人的家目录(远端路径、本机的其它账户):只换名字那一段,目录结构留着
        text = WindowsUserPath().Replace(text, MaskPathUser);
        text = UnixUserPath().Replace(text, MaskPathUser);
        // 邮箱之后:邮箱的域名部分已随整个地址换成 <email>,这里不会把它拆成「名字@<host-N>」
        text = GenericHostName().Replace(text, match =>
            IsGenericHostName(match.Value, match.Groups["tld"].Value) ? MaskName(match.Value, "host") : match.Value);
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

    /// <summary>下一个 <c>&lt;host-N&gt;</c> / <c>&lt;user-N&gt;</c>:已知来源与通用规则共用一套编号。</summary>
    private string NextToken(string kind) => string.Create(CultureInfo.InvariantCulture,
        $"<{kind}-{(kind == "host" ? ++_hostTokens : ++_userTokens)}>");

    /// <summary>
    /// 通用规则认出的一个主机或用户名换成占位符:已知来源里有它就用它那个,没有就新编一个并记下(同一个值在整个包里同一个占位符)。
    /// 已经是占位符的、人人都在用的登录名、本机回环不换。不按长度挑:这几处的上下文已经说明了它是名字。
    /// </summary>
    private string MaskName(string value, string kind)
    {
        if (value.Length == 0 || value[0] == '<' && value[^1] == '>') return value;
        if (kind == "user" ? CommonUserNames.Contains(value) : LoopbackHosts.Contains(value)) return value;
        if (!_literalTokens.TryGetValue(value, out string? token))
        {
            token = NextToken(kind);
            _literalTokens[value] = token;
        }
        Count(token.StartsWith("<host", StringComparison.Ordinal) ? "host" : "user");
        return token;
    }

    /// <summary>断开日志行里的主机:IP 留给后面的 IP 规则(占位符是 <c>&lt;ip-N&gt;</c>),其余一律换;IPv6 的方括号留着。</summary>
    private string MaskDisconnectHost(string host)
    {
        string inner = TrimBrackets(host);
        if (!_literalTokens.ContainsKey(inner) && IPAddress.TryParse(inner, out _)) return host;
        string masked = MaskName(inner, "host");
        return host.StartsWith('[') ? $"[{masked}]" : masked;
    }

    /// <summary>家目录路径里那一段名字;公共 / 默认配置目录与 <c>%USERNAME%</c>、<c>$USER</c> 这类占位写法不换。</summary>
    private string MaskPathUser(Match match)
    {
        string name = match.Groups["name"].Value;
        if (SharedProfileNames.Contains(name) || name[0] is '%' or '$' or '.' or '~') return match.Value;
        return match.Groups["prefix"].Value + MaskName(name, "user");
    }

    /// <summary>
    /// 通用域名规则的判据(候选已由 <see cref="GenericHostName" /> 按字形挑出:全小写、至少两段、末段全是字母):
    /// 末段是 <see cref="HostSuffixes" /> 里的顶级域或内网后缀;不是插件 id 这类已知不是主机的名字;
    /// 不是 VelaShell 自己联系的公共服务;不是 <c>com.</c> / <c>org.</c> 开头、三段以上的倒写标识符。
    /// </summary>
    /// <remarks>
    /// 只认小写:<c>System.Net</c>、<c>Avalonia.Base</c>、<c>README.md</c> 这些大小写混写的类型名与文件名一个都不碰,
    /// 代价是大写书写的主机名(<c>DB1.CORP.EXAMPLE.COM</c>)认不出 —— 已存连接里的照样按已知来源换。
    /// </remarks>
    private bool IsGenericHostName(string value, string tld)
    {
        if (!HostSuffixes.Contains(tld) || _notHosts.Contains(value)) return false;
        foreach (string domain in PublicServiceDomains)
        {
            if (value == domain || value.EndsWith("." + domain, StringComparison.Ordinal)) return false;
        }
        int firstDot = value.IndexOf('.');
        bool reverseDomain = ReverseDomainHeads.Contains(value[..firstDot]) && value.IndexOf('.', firstDot + 1) > 0;
        return !reverseDomain;
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
    /// 栈帧里的源文件位置(<c>in /src/Ssh/Auth/PasswordAuthenticator.cs:line 87</c>)也不是名字=值:文件名里带着关键字,
    /// 冒号后面跟的是行号,同样不换 —— 只认 <c>.cs:line 数字</c> 这一种写法。
    /// </remarks>
    [GeneratedRegex("""(?<key>(?<![A-Za-z0-9_.\-])(?!PublicKeyToken(?![A-Za-z0-9_.\-]))(?=[A-Za-z0-9_.\-]*?(?i:password|passwd|pwd|passphrase|secret|token|api[_\-]?key|access[_\-]?key|private[_\-]?key|credential))(?>[A-Za-z0-9_.\-]+)(?!(?<=\.cs):line [0-9])(?:\\?["'])?)(?<sep>[ \t]*[:=][ \t]*)(?!\\?["']?<redacted>)(?<value>"(?:[^"\\\r\n]|\\[^\r\n])+"|'[^'\r\n]+'|\\"(?:[^"\\\r\n]|\\[^"\\\r\n]|\\\\(?:[^"\\\r\n]|\\[^\r\n]))+\\"|[^\s,;"']+)""",
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
    /// IPv4:四段 0–255 的十进制。句末的句号不挡(<c>closed by 10.0.0.5.</c>)。
    /// 四段的版本号不是 IP,判据是<b>上下文</b>,不是数字本身 —— 光秃秃的 <c>1.2.3.4</c> 分不出是哪一种,宁可当 IP 遮掉:
    /// 前面紧挨着字母、数字、下划线或句点的(<c>v1.2.3.4</c>、<c>VelaShell_1.7.3.0_x64</c>、<c>1.1.2.3.4</c> 的后四段)、
    /// 后面紧跟字母、数字、下划线或「.数字」的(<c>1.2.3.4.5</c>、<c>1.2.3.4rc</c>),
    /// 前面是 <c>Version=</c> / <c>version:</c> / <c>Version </c>(崩溃记录里的程序集名)或 <c>VelaShell </c>(启动横幅与崩溃记录开头,
    /// 见 <see cref="DiagnosticLog" />)的,都不算。
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_.]|(?i:version)[ \t]{0,3}[=:]?[ \t]{0,3}|VelaShell )(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![A-Za-z0-9_]|\.\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex IPv4();

    /// <summary>
    /// <c>SshDisconnectLog</c> 写的那一行:<c>[SSH] 用户@主机:端口 closed …</c>(没有用户时只有 <c>主机:端口</c>,IPv6 带方括号)。
    /// 用户名里可以有 <c>@</c>(<c>alice@corp</c> 式的域账户),所以取最后一个 <c>@</c> 之前的整段。新旧两种写法
    /// (<c>closed after 时长</c> / <c>closed (connected since 时刻)</c>)都认 —— 日志目录里留着升级之前 7 天的日志。
    /// </summary>
    [GeneratedRegex(@"(?<head>\[SSH\] )(?:(?<user>\S+)@)?(?<host>\[[^\]\s]+\]|[^\s\[\]@]+?)(?<tail>:[0-9]{1,5} closed\b)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DisconnectLine();

    /// <summary>
    /// Windows 的用户目录 <c>X:\Users\名字</c>(斜杠方向、<c>Users</c> 的大小写都不论)。名字里可以有空格(<c>John Smith</c>),
    /// 但只有后面紧跟着下一级分隔符时才把空格算进去;否则只取到第一个空白为止。带空格的那一支限长 64,每个起点最多看几十个字符。
    /// </summary>
    [GeneratedRegex("""(?<prefix>(?<![A-Za-z0-9])[A-Za-z]:[\\/](?i:users)[\\/])(?<name>[^\\/:*?"<>|\r\n\t]{0,63}?[^\\/:*?"<>|\s](?=[\\/])|[^\\/:*?"<>|\s'`,;()\[\]{}]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex WindowsUserPath();

    /// <summary>
    /// Linux 的 <c>/home/名字</c>(含 Fedora Atomic 的 <c>/var/home/名字</c>)与 macOS 的 <c>/Users/名字</c>。
    /// 前面紧挨着字母、数字的不算(<c>src/home/index</c>、<c>https://example.com/home/about</c> 是网址或相对路径,不是家目录)。
    /// </summary>
    [GeneratedRegex("""(?<prefix>(?<![A-Za-z0-9_.\-])(?:/var)?/(?:home|Users)/)(?<name>[^/\\\s:*?"<>|'`,;()\[\]{}]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnixUserPath();

    /// <summary>
    /// 域名形式的主机名候选:全小写、至少两段、每段 1–63 个字母数字与短横(不以短横开头或结尾)、末段 2–24 个字母;
    /// 两头不挨着字母、数字、下划线、短横与句点(句末的句号不挡)。只是候选,是不是主机由 <see cref="IsGenericHostName" /> 判。
    /// 候选只能从一串字符的开头起(前面挨着字母数字的位置都被挡掉),一串里的回退最多按段数退,耗时随长度线性。
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_\-.])(?:[a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?\.)+(?<tld>[a-z]{2,24})(?![A-Za-z0-9_\-]|\.[A-Za-z0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex GenericHostName();

    /// <summary>
    /// 形似 IPv6 的一段:至少两个冒号,两头不挨着字母、数字与冒号(句末的句号不挡)。是不是真地址由 <see cref="IPAddress" /> 判;
    /// 量词都有上限,每个起点最多看几十个字符。内嵌 IPv4 的写法(<c>::ffff:203.0.113.7</c>)在这之前已被 IPv4 规则换掉了那一半。
    /// </summary>
    [GeneratedRegex(@"(?<![0-9A-Za-z:.])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![0-9A-Za-z:]|\.[0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex IPv6Candidate();
}
