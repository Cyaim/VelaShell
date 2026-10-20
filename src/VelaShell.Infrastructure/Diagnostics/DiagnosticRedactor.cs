using System.Globalization;
using System.Text.RegularExpressions;

namespace VelaShell.Infrastructure.Diagnostics;

/// <summary>
/// 诊断包落盘前的脱敏:主机、用户名、本机路径、IP、邮箱与形如口令 / 令牌的片段一律换成占位符。
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
/// 实例不是线程安全的:一个包一个实例,顺序处理各个文件。
/// </para>
/// </remarks>
public sealed partial class DiagnosticRedactor
{
    /// <summary>认不出是谁的常见登录名,不换。</summary>
    private static readonly HashSet<string> CommonUserNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "root", "admin", "administrator", "ubuntu", "debian", "centos", "ec2-user", "pi", "user", "git", "test",
    };

    /// <summary>本机回环地址不算可识别信息。</summary>
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "::1", "[::1]", "0.0.0.0",
    };

    private readonly Regex? _literals;
    private readonly Dictionary<string, string> _literalTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _ipTokens = new(StringComparer.Ordinal);
    private readonly (string Path, StringComparison Comparison)[] _homes;
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

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

        text = SecretAssignment().Replace(text, match =>
        {
            Count("secret");
            return match.Groups["key"].Value + match.Groups["sep"].Value + "<redacted>";
        });
        text = BearerToken().Replace(text, _ =>
        {
            Count("secret");
            return "Bearer <redacted>";
        });

        if (_literals is not null)
        {
            text = _literals.Replace(text, match =>
            {
                string token = _literalTokens[match.Value];
                Count(token.StartsWith("<host", StringComparison.Ordinal) ? "host" : "user");
                return token;
            });
        }

        text = Email().Replace(text, _ =>
        {
            Count("email");
            return "<email>";
        });
        return IPv4().Replace(text, match =>
        {
            string ip = match.Value;
            if (ip.StartsWith("127.", StringComparison.Ordinal) || ip == "0.0.0.0") return ip;
            Count("ip");
            if (!_ipTokens.TryGetValue(ip, out string? token))
            {
                token = string.Create(CultureInfo.InvariantCulture, $"<ip-{_ipTokens.Count + 1}>");
                _ipTokens[ip] = token;
            }
            return token;
        });
    }

    private void Count(string kind, int times = 1)
    {
        if (times <= 0) return;
        _counts[kind] = _counts.GetValueOrDefault(kind) + times;
    }

    /// <summary>两个字符以内的值不换:<c>a</c>、<c>db</c> 这种会把日志里的普通单词一起换掉。</summary>
    private static bool Usable(string value) => value.Trim().Length >= 3;

    private static string TrimBrackets(string host) => host.Trim().TrimStart('[').TrimEnd(']');

    /// <summary><c>password=…</c>、<c>token: …</c> 这类赋值,只换值。</summary>
    [GeneratedRegex(@"(?<key>\b(?:password|passwd|pwd|passphrase|token|secret|api[_-]?key|access[_-]?key)\b)(?<sep>\s*[=:]\s*)(?!<redacted>)[^\s,;""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"\bBearer\s+(?!<redacted>)[A-Za-z0-9._~+/\-]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d.])",
        RegexOptions.CultureInvariant)]
    private static partial Regex IPv4();
}
