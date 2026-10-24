using System.Text;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Sftp;

/// <summary>一次远端搜索要找什么。</summary>
/// <param name="Root">从哪个目录找起(远端绝对路径)。</param>
/// <param name="NamePattern">文件名模式(<c>find -name</c> 的通配符);空 = 任意名字,不含通配符时按「名字里含有」找。</param>
/// <param name="ContentText">文件内容里要有的文字(按字面、不按正则);空 = 只按名字找。</param>
/// <param name="IgnoreCase">名字与内容都不区分大小写。</param>
/// <param name="MaxResults">最多要多少条;到数就停,远端的 <c>find</c> 也随之停下。</param>
public sealed record RemoteSearchQuery(
    string Root,
    string? NamePattern,
    string? ContentText = null,
    bool IgnoreCase = true,
    int MaxResults = RemoteSearch.DefaultMaxResults)
{
    /// <summary>是不是按内容找(要跑 <c>grep</c>)。</summary>
    public bool IsContentSearch => !string.IsNullOrEmpty(ContentText);
}

/// <summary>搜到的一条。</summary>
/// <param name="Path">远端绝对路径。</param>
/// <param name="IsDirectory">是不是目录(指向目录的链接也算 —— 点了能进得去)。</param>
public sealed record RemoteSearchHit(string Path, bool IsDirectory);

/// <summary>
/// 远端搜索:把查询拼成<b>一条</b> exec 命令(<c>find</c>,按内容时再加 <c>grep</c>),解析它的输出。
/// 与连接无关的纯函数,单独拎出来才测得到引号与转义。
/// </summary>
/// <remarks>
/// <para>
/// <b>不留注入口。</b>脚本本体是写死的常量(只按「按名字 / 按内容」「忽略大小写」「从 / 起找」挑几段常量拼),
/// 用户填的目录、模式、文字一个字节都不进脚本,全部作为 <c>sh -c '脚本' 名字 参数…</c> 的位置参数传进去,
/// 脚本里只以 <c>"$1"</c> 这样的形式引用 —— 位置参数在 sh 里不会被二次展开。命令行上的每个参数都按登录 shell
/// 的单引号规则转义(见 <see cref="QuoteFor" />):POSIX shell 里 <c>'</c> 写成 <c>'\''</c>;fish 的单引号里
/// <c>\</c> 与 <c>'</c> 是转义符,要写成 <c>\\</c> 与 <c>\'</c>。
/// </para>
/// <para>
/// <b>BusyBox 兼容。</b>只用 BusyBox 也有的那一组:<c>find -mindepth/-name/-iname/-type/-path/-prune/-print</c>、
/// <c>-exec … {} +</c>(POSIX.1-2008)、<c>grep -l -s -i -F -e</c>、<c>head -n</c>、<c>[ -d ]</c>、<c>printf</c>。
/// 不用 GNU 才有的 <c>-printf</c>、<c>--include</c>、<c>-print0</c> + <c>xargs -0</c>。目录还是文件由 shell 的
/// <c>[ -d ]</c> 判(内建,不起进程),每条输出成 <c>d\t路径</c> 或 <c>f\t路径</c>。
/// </para>
/// <para>
/// <b>有上限、能停。</b>条数:<c>head -n 上限+1</c> 收满就退出,<c>find</c> 下一次写管道即被 SIGPIPE 收掉,
/// 不会在远端接着把整棵树走完;多出来的那一条只用来判断「结果不完整」。时间与取消由调用方的令牌管:
/// 取消时 SSH 库先给远端发 <c>TERM</c>,脚本的 <c>trap</c> 再把整个进程组(<c>find</c>、<c>grep</c>、<c>head</c>)一起收掉 ——
/// 只收 sh 的话,一个还没匹配到东西的 <c>find</c> 会在远端一直走下去。管道放在后台再 <c>wait</c>,
/// 是因为 sh 在前台命令跑完之前不执行 trap。
/// </para>
/// </remarks>
public static class RemoteSearch
{
    /// <summary>默认最多多少条。</summary>
    public const int DefaultMaxResults = 500;

    /// <summary>远端没有 <c>find</c> 时脚本打印的标记。</summary>
    public const string NoFindMarker = "VELA-SEARCH-NO-FIND";

    /// <summary>按内容找、远端没有 <c>grep</c> 时脚本打印的标记。</summary>
    public const string NoGrepMarker = "VELA-SEARCH-NO-GREP";

    /// <summary><c>sh -c</c> 的 <c>$0</c>(出现在远端的报错与进程列表里)。</summary>
    private const string ScriptName = "vela-search";

    // 脚本片段里不能出现单引号(整段要被单引号包着交给 sh -c)。$1 = 根目录、$2 = 名字模式、$3 = 条数上限、$4 = 内容文字。
    private const string Prologue =
        "trap \"trap - TERM HUP INT; kill 0\" TERM HUP INT; " +
        "command -v find >/dev/null 2>&1 || { echo " + NoFindMarker + "; exit 0; }; ";

    private const string GrepCheck =
        "command -v grep >/dev/null 2>&1 || { echo " + NoGrepMarker + "; exit 0; }; ";

    /// <summary>从 <c>/</c> 起找时跳过的伪文件系统:<c>/proc</c> 里的东西走不完,<c>/sys</c> 的链接成环。</summary>
    private const string PruneVirtual = "\\( -path /proc -o -path /sys -o -path /dev \\) -prune -o ";

    private const string Classify =
        " | head -n \"$3\" | while IFS= read -r p; do " +
        "if [ -d \"$p\" ]; then printf \"d\\t%s\\n\" \"$p\"; else printf \"f\\t%s\\n\" \"$p\"; fi; done & wait";

    /// <summary>实际交给 <c>find -name</c> 的模式:空为 <c>*</c>;不含 <c>*</c> <c>?</c> <c>[</c> 时两头补 <c>*</c>(按「名字里含有」找)。</summary>
    /// <param name="pattern">用户填的模式。</param>
    public static string EffectiveNamePattern(string? pattern)
    {
        string trimmed = pattern?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "*";
        }
        return trimmed.IndexOfAny(['*', '?', '[']) >= 0 ? trimmed : "*" + trimmed + "*";
    }

    /// <summary>根目录归一:去掉尾部的 <c>/</c>(根目录本身除外),连续的 <c>/</c> 并成一个。</summary>
    /// <param name="root">远端目录。</param>
    public static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string collapsed = CollapseSlashes(root.Trim());
        string trimmed = collapsed.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>
    /// 拼出这次搜索的命令行。
    /// </summary>
    /// <param name="query">查询;<see cref="RemoteSearchQuery.Root" /> 必须是绝对路径。</param>
    /// <param name="shell">远端登录 shell 的种类(决定命令行上参数怎么加引号)。</param>
    /// <exception cref="ArgumentException">根目录不是绝对路径,或条数上限不是正数。</exception>
    /// <exception cref="NotSupportedException">登录 shell 不是 POSIX shell 也不是 fish。</exception>
    public static string BuildCommand(RemoteSearchQuery query, RemoteShellKind shell)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (shell is not (RemoteShellKind.Bash or RemoteShellKind.Zsh or RemoteShellKind.PosixSh or RemoteShellKind.Fish))
        {
            throw new NotSupportedException($"Remote search needs a POSIX shell or fish, not {shell}.");
        }
        string root = NormalizeRoot(query.Root);
        if (root[0] != '/')
        {
            throw new ArgumentException("The search root must be an absolute remote path.", nameof(query));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.MaxResults);

        var script = new StringBuilder(Prologue);
        if (query.IsContentSearch)
        {
            script.Append(GrepCheck);
        }
        // -mindepth 1:根目录自己不算结果(名字恰好匹配时 find 会列出它),而且得在远端就排除 —— 否则它占掉一个名额,
        // 「上限 + 1」那一条就判断不出截断了。GNU、BSD、BusyBox 的 find 都有它(POSIX 没写)。
        script.Append("{ find \"$1\" -mindepth 1 ");
        if (root == "/")
        {
            script.Append(PruneVirtual);
        }
        string name = query.IgnoreCase ? "-iname" : "-name";
        if (query.IsContentSearch)
        {
            // -l 只报文件名、-s 不报读不了的文件、-F 按字面、-e 让以 - 开头的文字也是文字;-exec … + 一批批喂,不必每个文件起一次。
            script.Append("-type f ").Append(name).Append(" \"$2\" -exec grep -l -s ")
                  .Append(query.IgnoreCase ? "-i " : string.Empty)
                  .Append("-F -e \"$4\" {} + 2>/dev/null; }");
        }
        else
        {
            script.Append(name).Append(" \"$2\" -print 2>/dev/null; }");
        }
        script.Append(Classify);

        var command = new StringBuilder("sh -c ");
        command.Append(QuoteFor(shell, script.ToString())).Append(' ').Append(ScriptName)
               .Append(' ').Append(QuoteFor(shell, root))
               .Append(' ').Append(QuoteFor(shell, EffectiveNamePattern(query.NamePattern)))
               .Append(' ').Append(QuoteFor(shell, (query.MaxResults + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (query.IsContentSearch)
        {
            command.Append(' ').Append(QuoteFor(shell, query.ContentText!));
        }
        return command.ToString();
    }

    /// <summary>按登录 shell 的单引号规则把一个参数原样引起来。</summary>
    /// <param name="shell">登录 shell 的种类。</param>
    /// <param name="value">参数原文。</param>
    public static string QuoteFor(RemoteShellKind shell, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // fish 的单引号里只有 \\ 与 \' 两个转义:先翻倍反斜杠,再转义引号。
        return shell == RemoteShellKind.Fish
            ? "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'"
            : RemoteSha256.Quote(value);
    }

    /// <summary>这一行是不是「远端缺工具」的标记。</summary>
    /// <param name="line">一行输出。</param>
    public static bool IsToolMissingMarker(string line) =>
        line.Trim() is NoFindMarker or NoGrepMarker;

    /// <summary>
    /// 解析一行输出。认得出的是 <c>d\t路径</c> / <c>f\t路径</c>,路径必须是绝对路径;根目录本身不算结果
    /// (它的名字恰好也匹配时 <c>find</c> 会把它列出来)。
    /// </summary>
    /// <param name="line">一行输出(行尾的 <c>\r</c> 已去或未去皆可)。</param>
    /// <param name="root">这次搜索的根目录。</param>
    /// <param name="hit">认出来的那一条。</param>
    public static bool TryParseHit(string line, string root, out RemoteSearchHit hit)
    {
        hit = null!;
        string trimmed = line.TrimEnd('\r');
        if (trimmed.Length < 3 || trimmed[1] != '\t' || trimmed[0] is not ('d' or 'f') || trimmed[2] != '/')
        {
            return false;
        }
        string path = CollapseSlashes(trimmed[2..]);
        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }
        if (string.Equals(path, NormalizeRoot(root), StringComparison.Ordinal))
        {
            return false;
        }
        hit = new(path, trimmed[0] == 'd');
        return true;
    }

    private static string CollapseSlashes(string path)
    {
        if (!path.Contains("//", StringComparison.Ordinal))
        {
            return path;
        }
        var builder = new StringBuilder(path.Length);
        foreach (char c in path)
        {
            if (c != '/' || builder.Length == 0 || builder[^1] != '/')
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }
}
