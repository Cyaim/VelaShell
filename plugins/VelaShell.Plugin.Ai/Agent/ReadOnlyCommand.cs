namespace VelaShell.Plugin.Ai.Agent;

/// <summary>
/// 判断一条 shell 命令是否"一望即知没有副作用",供
/// <see cref="Configuration.ApprovalMode.ReadOnlyAuto" /> 决定要不要免审批。
/// </summary>
/// <remarks>
/// <b>这是个安全判断,所以刻意写得胆小</b>:只有<i>确定</i>无害才返回 true,
/// 但凡有一点看不准就返回 false 去问用户。放过一条该问的命令,代价可能是删掉生产数据;
/// 多问一次的代价只是多点一下鼠标。两者不对称,所以规则宁可过严。
///
/// 具体地:命令名必须在白名单里,而且整条命令不许出现任何能改变状态的构造 ——
/// 重定向、管道、命令分隔符、命令替换。名单里那些"带上某些参数就会改东西"的命令
/// (<c>curl -o</c>、<c>ip link set</c>、<c>date -s</c>、<c>dmesg -c</c> 之类)另按参数逐个判断,
/// 见 <see cref="ArgumentChecks" />。
/// <para>
/// 只按首个词放行是不够的(#652 复审):<c>env sh -c …</c> 能跑任意命令,
/// <c>curl -o ~/.ssh/authorized_keys …</c>、<c>wget -O ~/.bashrc …</c> 能写任意文件,
/// <c>curl -T /etc/shadow …</c> 能把文件传出去,<c>sudo</c> 后面跟什么都抬高了权限 ——
/// 这些都不弹确认就是审批被绕过,所以 <c>env</c> 移出名单、<c>sudo</c> 一律照问、
/// <c>curl</c> / <c>wget</c> 只认白名单里的参数。
/// </para>
/// </remarks>
public static class ReadOnlyCommand
{
    /// <summary>公认没有副作用的命令名。没列进来的一律当作可能有副作用。</summary>
    /// <remarks>
    /// 不在名单里的:<c>env</c>(<c>env sh -c …</c> 什么都能跑)、<c>sudo</c>(提权,后面跟什么都得问),
    /// 以及 <c>nohup</c> / <c>timeout</c> / <c>nice</c> / <c>xargs</c> 这类"替别的命令开路"的包装。
    /// 行首的 <c>FOO=bar</c> 赋值同样不在名单里(<c>LD_PRELOAD=… ls</c>)。
    /// </remarks>
    private static readonly HashSet<string> Safe =
    [
        with(StringComparer.Ordinal),
        "ls", "ll", "dir", "cat", "bat", "head", "tail", "less", "more", "wc", "nl",
        "stat", "file", "readlink", "realpath", "basename", "dirname", "pwd",
        "df", "du", "free", "uptime", "uname", "hostname", "whoami", "id", "groups", "date",
        "ps", "top", "htop", "pgrep", "lsof", "netstat", "ss", "ip", "ifconfig", "route",
        "dmesg", "journalctl", "who", "w", "last", "lastlog",
        "grep", "egrep", "fgrep", "rg", "diff", "cmp", "md5sum", "sha256sum",
        "printenv", "echo", "which", "whereis", "type", "locale",
        "lscpu", "lsblk", "lsusb", "lspci", "mount", "vmstat", "iostat", "sar",
        "getent", "dig", "nslookup", "host", "ping", "traceroute", "curl", "wget"
    ];

    /// <summary>
    /// 出现其中任何一个片段就直接判定"看不准"。
    /// 前面几个是 shell 构造(能把只读命令接成写操作),后面几个是特定命令的危险参数。
    /// </summary>
    private static readonly string[] Suspicious =
    [
        ">", "<", "|", ";", "&", "$(", "`", "\n",
        " -delete", " -exec", " -execdir", " -ok",
        " -i ", " -i\t", " --in-place", " -o ", " --output"
    ];

    /// <summary>
    /// 名单里那些带上某些参数就会改状态(或替别的东西开路)的命令:参数(不含命令名)交给对应的判定,
    /// 返回 true 才算只读。没登记的命令只看上面的通用规则。
    /// </summary>
    private static readonly Dictionary<string, Func<string[], bool>> ArgumentChecks = new(StringComparer.Ordinal)
    {
        ["curl"] = IsReadOnlyCurl,
        ["wget"] = IsReadOnlyWget,
        ["ip"] = IsReadOnlyIp,
        // route add / del / flush;BSD 的 change / delete
        ["route"] = args => Plain(args) && !args.Any(a => a is "add" or "del" or "delete" or "change" or "flush" or "replace"),
        // 只认"看":不带参数、看一块网卡,或几个列表开关;ifconfig eth0 down / mtu / -arp 都要问
        ["ifconfig"] = args => Plain(args) && args.Count(a => !a.StartsWith('-')) <= 1
            && args.Where(a => a.StartsWith('-')).All(a => a is "-a" or "-s" or "-v" or "-l" or "-L" or "-u" or "-d"),
        // 带参数的 mount 就是挂载;只认光秃秃的 mount 与 mount -l
        ["mount"] = args => args.All(a => a == "-l"),
        ["hostname"] = IsReadOnlyHostname,
        ["date"] = IsReadOnlyDate,
        // -c / -C 清空环形缓冲,-n / -D / -E 改控制台级别
        ["dmesg"] = args => Plain(args) && !args.Any(a => HasShortFlag(a, "cCnDE")
            || a is "--clear" or "--read-clear" or "--console-off" or "--console-on"
            || a.StartsWith("--console-level", StringComparison.Ordinal)),
        // 清理、轮转、落盘、改目录 —— 以及 --cursor-file 会把游标写进文件
        ["journalctl"] = args => Plain(args) && !args.Any(a => a.StartsWith("--vacuum", StringComparison.Ordinal)
            || a.StartsWith("--cursor-file", StringComparison.Ordinal)
            || a is "--rotate" or "--flush" or "--sync" or "--relinquish-var" or "--smart-relinquish-var"
                or "--setup-keys" or "--update-catalog"),
        // -K 杀连接,-D 把原始信息写进文件
        ["ss"] = args => Plain(args) && !args.Any(a => HasShortFlag(a, "KD")
            || a == "--kill" || a.StartsWith("--diag", StringComparison.Ordinal)),
        // -o 把采样存进文件
        ["sar"] = args => Plain(args) && !args.Any(a => HasShortFlag(a, "o")),
        // --pre 对每个文件跑一个外部程序(rg --pre sh … 等于执行文件内容)
        ["rg"] = args => Plain(args) && !args.Any(a => a == "--pre" || a.StartsWith("--pre=", StringComparison.Ordinal)),
        // -C 编译魔数文件(写 .mgc)
        ["file"] = args => Plain(args) && !args.Any(a => HasShortFlag(a, "C") || a == "--compile"),
        // -C 清空、-S 改写登录记录
        ["lastlog"] = args => Plain(args) && !args.Any(a => HasShortFlag(a, "CS") || a is "--clear" or "--set"),
        // bat cache 写 / 删缓存;--pager 配 --paging=always 会去执行那条分页命令
        ["bat"] = args => Plain(args) && !(args.Length > 0 && args[0] == "cache")
            && !args.Any(a => a.StartsWith("--pager", StringComparison.Ordinal)
                || a.StartsWith("--paging", StringComparison.Ordinal) || a == "--generate-config-file"),
        // +命令 是启动后先执行的命令(含 !shell),-o / -O 把输入写进日志文件
        ["less"] = IsReadOnlyPager,
        ["more"] = IsReadOnlyPager
    };

    /// <summary>这条命令能不能免审批直接跑。</summary>
    public static bool IsSafe(string? command)
    {
        string text = command?.Trim() ?? "";
        if (text.Length == 0)
        {
            return false;
        }
        foreach (string marker in Suspicious)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // shell 按空格与制表符切词,这里也一样 —— 只按空格切的话,"-T\t-c" 会被当成一个词放过
        string[] words = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        // 带路径的调用(./script、/usr/local/bin/x)一律不认:名字看不出它干什么。
        // sudo 不在名单里:提权之后跟什么都得问,不再"看后面那个词是不是白名单"
        string name = words[0];
        if (name.Contains('/') || !Safe.Contains(name))
        {
            return false;
        }
        return !ArgumentChecks.TryGetValue(name, out Func<string[], bool>? check) || check(words[1..]);
    }

    // ---- 按参数判断的那几个 ----

    /// <summary>
    /// 参数里有没有 shell 会再加工的东西:引号与反斜杠(<c>"-c"</c> 去掉引号就是 <c>-c</c>)、
    /// 变量(<c>${IFS}</c> 展开后再切词,能凭空切出 <c>-o</c>)、通配与花括号(<c>-?</c> 能匹配到叫 <c>-c</c> 的文件)。
    /// 按参数判断的命令只认字面量:看到这些就不再猜 shell 会把它变成什么。
    /// </summary>
    private static bool Plain(string[] args) => args.All(Plain);

    private static bool Plain(string word) => word.IndexOfAny(['\'', '"', '\\', '$', '*', '?', '[', ']', '{', '}']) < 0;

    /// <summary>单短横的选项(可合写,如 <c>-Tc</c>)里有没有 <paramref name="letters" /> 中的任一个字母。</summary>
    private static bool HasShortFlag(string word, string letters)
        => word.Length > 1 && word[0] == '-' && word[1] != '-' && word.AsSpan(1).IndexOfAny(letters) >= 0;

    private static bool IsReadOnlyPager(string[] args)
        => Plain(args) && !args.Any(a => a.StartsWith('+') || HasShortFlag(a, "oO")
            || a.StartsWith("--log-file", StringComparison.Ordinal) || a.StartsWith("--LOG-FILE", StringComparison.Ordinal));

    /// <summary>位置参数只认显式的 http(s) 地址:<c>gopher://</c>、<c>dict://</c>、<c>telnet://</c> 能往内网服务里写东西。</summary>
    /// <remarks>
    /// 地址里的 <c>?</c> <c>[</c> <c>{</c> 就算被 shell 当成通配展开,展开结果也还是以这个前缀开头,变不成选项;
    /// 引号、反斜杠与 <c>$</c> 不行 —— 它们能在展开后切出新的词。
    /// </remarks>
    private static bool IsHttpUrl(string word)
        => (word.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || word.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
           && word.IndexOfAny(['\'', '"', '\\', '$']) < 0;

    /// <summary>
    /// curl 只认"把一个 http(s) 地址 GET / HEAD 到标准输出"。
    /// </summary>
    /// <remarks>
    /// 写文件的(<c>-o</c> <c>-O</c> <c>--output</c> <c>-D</c> <c>-c</c> <c>--trace</c> <c>--libcurl</c> <c>-w %output{}</c>…)、
    /// 发数据的(<c>-d</c> <c>-F</c> <c>-T</c> <c>--upload-file</c> <c>--json</c>…)、换方法的(<c>-X</c> 非 GET / HEAD)、
    /// 读配置的(<c>-K</c>)选项太多,黑名单列不全 —— 所以反过来列白名单,认不出的选项一律照问。
    /// 自定义请求头(<c>-H</c>)也不认:<c>X-HTTP-Method-Override: DELETE</c> 能让 GET 变成删除。
    /// </remarks>
    private static bool IsReadOnlyCurl(string[] args)
    {
        const string flags = "sSLIivkf46gNqVh";
        const string valued = "mAerX";
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--")
            {
                return args[(i + 1)..].All(IsHttpUrl);
            }
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                switch (arg)
                {
                    case "--silent" or "--show-error" or "--location" or "--head" or "--include" or "--verbose"
                        or "--insecure" or "--fail" or "--fail-with-body" or "--compressed" or "--globoff"
                        or "--ipv4" or "--ipv6" or "--http1.0" or "--http1.1" or "--http2" or "--no-progress-meter"
                        or "--no-buffer" or "--no-keepalive" or "--disable" or "--version" or "--help":
                        continue;
                    case "--max-time" or "--connect-timeout" or "--user-agent" or "--referer" or "--range"
                        or "--retry" or "--retry-delay" or "--retry-max-time" or "--request":
                        if (++i >= args.Length || !CurlValue(arg == "--request", args[i])) return false;
                        continue;
                    default:
                        return false;
                }
            }
            if (arg.StartsWith('-') && arg.Length > 1)
            {
                // 短选项可以合写:-sSL;带值的那个吃掉余下的字符或下一个词(-m10 / -m 10)
                for (int c = 1; c < arg.Length; c++)
                {
                    if (flags.Contains(arg[c])) continue;
                    if (!valued.Contains(arg[c])) return false;
                    string value = c + 1 < arg.Length ? arg[(c + 1)..] : ++i < args.Length ? args[i] : "";
                    if (!CurlValue(arg[c] == 'X', value)) return false;
                    break;
                }
                continue;
            }
            if (!IsHttpUrl(arg)) return false;
        }
        return true;

        static bool CurlValue(bool method, string value)
            => method ? value is "GET" or "HEAD" : value.Length > 0 && !value.StartsWith('-') && Plain(value);
    }

    /// <summary>
    /// wget 默认就是<b>把文件存到当前目录</b>(写操作),所以只认两种形状:
    /// 输出到标准输出(<c>-O -</c> / <c>-qO-</c> / <c>--output-document=-</c>)或只探不下(<c>--spider</c>)。
    /// </summary>
    /// <remarks>
    /// 同 curl,反过来列白名单:<c>-O 文件</c>、<c>-o</c> / <c>-a</c> 日志、<c>-P</c> / <c>-x</c> / <c>-r</c> 落盘、
    /// <c>--post-data</c> / <c>--post-file</c> / <c>--method</c> 发数据、<c>-e</c> 执行 wgetrc 指令、<c>-i</c> 读地址表,
    /// 一个都不认。
    /// </remarks>
    private static bool IsReadOnlyWget(string[] args)
    {
        const string flags = "qSv46";
        const string valued = "OTtU";
        bool toStdout = false, spider = false, info = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                int equals = arg.IndexOf('=');
                string option = equals < 0 ? arg : arg[..equals];
                switch (option)
                {
                    case "--quiet" or "--no-verbose" or "--verbose" or "--server-response" or "--no-check-certificate"
                        or "--inet4-only" or "--inet6-only" when equals < 0:
                        continue;
                    case "--spider" when equals < 0:
                        spider = true;
                        continue;
                    case "--version" or "--help" when equals < 0:
                        info = true;
                        continue;
                    // --output-document 的长写法其实先被通用规则里的 " --output" 挡下了;这里照样按值判断,
                    // 免得哪天放宽通用规则时把 --output-document=文件 漏过去
                    case "--output-document" or "--timeout" or "--tries" or "--max-redirect" or "--user-agent":
                        string value = equals >= 0 ? arg[(equals + 1)..] : ++i < args.Length ? args[i] : "";
                        if (option == "--output-document")
                        {
                            if (value != "-") return false;
                            toStdout = true;
                        }
                        else if (!WgetValue(value)) return false;
                        continue;
                    default:
                        return false;
                }
            }
            if (arg == "-nv")
            {
                continue;
            }
            if (arg.StartsWith('-') && arg.Length > 1)
            {
                for (int c = 1; c < arg.Length; c++)
                {
                    if (flags.Contains(arg[c])) continue;
                    if (!valued.Contains(arg[c])) return false;
                    string value = c + 1 < arg.Length ? arg[(c + 1)..] : ++i < args.Length ? args[i] : "";
                    if (arg[c] == 'O')
                    {
                        if (value != "-") return false;
                        toStdout = true;
                    }
                    else if (!WgetValue(value)) return false;
                    break;
                }
                continue;
            }
            if (!IsHttpUrl(arg)) return false;
        }
        return toStdout || spider || (info && args.All(a => a.StartsWith('-')));

        static bool WgetValue(string value) => value.Length > 0 && !value.StartsWith('-') && Plain(value);
    }

    /// <summary>
    /// <c>ip</c> 只认"看":对象后面不跟动词(默认就是 show),或者动词恰好是
    /// <c>show</c> / <c>list</c> / <c>ls</c> / <c>lst</c> / <c>sh</c> / <c>get</c>。
    /// </summary>
    /// <remarks>
    /// iproute2 认缩写(<c>ip a a</c> 就是 <c>ip addr add</c>,<c>ip r f</c> 是 flush),
    /// 还有 <c>ip netns exec … sh</c> 与批处理 <c>-b</c> / <c>-batch</c> —— 所以对象、动词与全局选项都只认列出来的写法,
    /// 其余一律照问。动词是 show / get 之后的过滤条件(<c>dev eth0</c>、<c>up</c>)改不了任何东西,不再细看。
    /// </remarks>
    private static bool IsReadOnlyIp(string[] args)
    {
        if (!Plain(args)) return false;
        int i = 0;
        while (i < args.Length && args[i].StartsWith('-'))
        {
            string option = args[i++];
            if (option is "-n" or "-netns" or "-f" or "-family")
            {
                if (i >= args.Length || args[i].StartsWith('-')) return false;
                i++;
                continue;
            }
            if (option is not ("-4" or "-6" or "-0" or "-s" or "-stats" or "-statistics" or "-d" or "-details"
                or "-h" or "-human" or "-human-readable" or "-iec" or "-j" or "-json" or "-p" or "-pretty"
                or "-c" or "-color" or "-br" or "-brief" or "-r" or "-resolve" or "-t" or "-timestamp"
                or "-ts" or "-tshort" or "-a" or "-all" or "-N" or "-numeric"))
            {
                return false;
            }
        }
        if (i >= args.Length) return true; // 光一个 ip(带选项也一样):打用法
        if (args[i++] is not ("a" or "addr" or "address" or "l" or "link" or "r" or "route" or "n" or "neigh"
            or "neighbor" or "neighbour" or "rule" or "maddr" or "maddress" or "mroute" or "ntable" or "netconf" or "netns"))
        {
            return false;
        }
        return i >= args.Length || args[i] is "show" or "list" or "ls" or "lst" or "sh" or "get";
    }

    /// <summary>hostname 带位置参数(或 <c>-F 文件</c>)就是改名;只认几个"显示"开关。</summary>
    private static bool IsReadOnlyHostname(string[] args)
        => args.All(a => a is "-s" or "--short" or "-f" or "--fqdn" or "--long" or "-d" or "--domain"
            or "-i" or "--ip-address" or "-I" or "--all-ip-addresses" or "-A" or "--all-fqdns"
            or "-a" or "--alias" or "-h" or "--help" or "-V" or "--version");

    /// <summary>
    /// date 带 <c>-s</c> / <c>--set</c> 或一个不以 <c>+</c> 开头的位置参数(<c>date 0101…</c>)就是改系统时间;
    /// 只认格式串、几个显示开关与 <c>-d</c> / <c>-r</c> 这类只读取值的参数。
    /// </summary>
    private static bool IsReadOnlyDate(string[] args)
    {
        if (!Plain(args)) return false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith('+')
                || arg.StartsWith("-I", StringComparison.Ordinal) || arg.StartsWith("--iso-8601", StringComparison.Ordinal)
                || arg.StartsWith("--rfc-3339=", StringComparison.Ordinal) || arg.StartsWith("--date=", StringComparison.Ordinal)
                || arg.StartsWith("--reference=", StringComparison.Ordinal)
                || arg is "-u" or "--utc" or "--universal" or "-R" or "--rfc-email" or "--rfc-2822" or "--debug"
                    or "-j" or "--help" or "--version")
            {
                continue;
            }
            if (arg is "-d" or "--date" or "-r" or "--reference")
            {
                if (++i >= args.Length || args[i].StartsWith('-')) return false;
                continue;
            }
            return false;
        }
        return true;
    }
}
