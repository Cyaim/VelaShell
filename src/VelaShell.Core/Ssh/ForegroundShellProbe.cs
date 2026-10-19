namespace VelaShell.Core.Ssh;

/// <summary>
/// 探针结论:本连接终端上<b>此刻站在提示符前</b>的那个交互式 shell。
/// </summary>
/// <param name="LoginShellPid">sshd 为这条连接起的登录 shell(连接时注入过目录上报钩子的那一个)。</param>
/// <param name="Pid">此刻占着前台、等用户输入的交互式 shell。</param>
/// <param name="Kind">按它的 <c>argv[0]</c> 认出的种类,决定补注哪一段脚本。</param>
public readonly record struct ForegroundShell(int LoginShellPid, int Pid, RemoteShellKind Kind)
{
    /// <summary>前台就是登录 shell 自己 —— 连接时已经注入过,无事可做。</summary>
    public bool IsLoginShell => Pid == LoginShellPid;
}

/// <summary>
/// 嵌套 shell 探针:回答「终端前台现在是不是一个<b>还没装钩子</b>的交互式 shell」(#637)。
/// </summary>
/// <remarks>
/// <para>
/// <b>问题。</b>目录上报钩子只在连接时注入一次,装进的是 sshd 起的那个登录 shell。
/// 用户随后敲 <c>su - node</c>、<c>sudo -i</c>、<c>sudo su -</c>,或者干脆敲一句 <c>zsh</c>,
/// 前台换成了一个<b>全新的</b> shell 进程 —— 函数、<c>PROMPT_COMMAND</c> 都不会被它继承
/// (<c>su -</c> 连环境变量都清空,只留 <c>TERM</c>),于是「跟随终端目录」静默失效;
/// <c>exit</c> 回到登录 shell 后又恢复。环境变量这条路走不通,只能在新 shell 的提示符前<b>再注一次</b>。
/// </para>
/// <para>
/// <b>再注一次的前提是确定无疑。</b>往 PTY 里打字,打进去的是前台<b>此刻在读的那个程序</b>:
/// 打进 <c>su</c> 的密码提示就成了口令,打进 <c>read</c> 等回答的脚本就成了回答,打进 vim 就成了一串编辑命令。
/// 屏幕上的提示符长什么样靠不住(<c>postgres=# </c> 也以 <c># </c> 结尾),所以不看屏幕,
/// 改走<b>独立的 exec 通道</b>去问内核:这个终端的前台进程组是谁、它是不是一个交互式 shell。
/// 终端里一个字符都看不见,与 <see cref="RemoteShellProbe" /> 同一个路数。
/// </para>
/// <para>
/// <b>怎么认出"本连接的终端"。</b>exec 通道与 shell 通道同属一条 SSH 连接,两者的父进程是同一个
/// sshd 会话进程(OpenSSH 的 <c>sshd-session</c>,Dropbear 同理)—— 探针沿自己的祖先往上找,
/// 第一个<b>恰好有一个带终端的子进程</b>的祖先,那个子进程就是登录 shell。不是恰好一个就放弃。
/// 不靠 shell 自报 PID:那条上报谁都能在终端里伪造(<c>cat</c> 一个文件就够了),而进程树伪造不了。
/// </para>
/// <para>
/// <b>怎么认出"前台是交互式 shell"。</b>交互式 shell 开着作业控制:等输入时它自己就是终端的前台进程组组长;
/// 一旦跑起外部命令,前台就交给那条命令的进程组。所以「前台组长是 shell」= 它正停在提示符前。
/// 再用命令行把脚本排除掉:<c>bash deploy.sh</c> 在 <c>read</c> 时同样是前台组长,
/// 但它带着脚本路径这个操作数;<c>su -c …</c>、<c>bash -c …</c> 带着 <c>-c</c>(见 <see cref="InteractiveShellKind" />)。
/// </para>
/// <para>
/// <b>sudo 会另开一个终端。</b>sudo 1.9.14 起默认 <c>use_pty</c>(Debian 12 起 sudoers 里也写着),
/// <c>sudo -i</c> 起的 shell 在一个<b>新的</b> pty 上,原终端的前台是 sudo 自己,由它把击键转过去。
/// 于是前台组长不是 shell 时,再看它的后代里有没有落在<b>另一个</b>终端上的进程,
/// 有且只有一个这样的终端,就沿那个终端的前台接着认(<c>script</c>、<c>su --pty</c> 同理)。
/// 往那边打字照样是写本连接的 PTY,由中转进程转进去。
/// </para>
/// <para>
/// 认不出来的一律当作没有:tmux / screen 的 shell 挂在守护进程下、不是本连接的后代;
/// <c>ssh 别的机器</c>、<c>docker exec</c> 的前台是客户端进程 —— 那些 shell 的目录也不在 SFTP 看得到的这台机器上,
/// 本来就不该跟。macOS / BSD 没有 <c>/proc</c>,退回 <c>ps</c>;两样都不行(BusyBox 的 ps 没有 <c>tpgid</c>,
/// 但 BusyBox 系统有 <c>/proc</c>)就探不出来,行为与修复之前一样。
/// </para>
/// </remarks>
public static class ForegroundShellProbe
{
    /// <summary>
    /// 探针命令:输出祖先链(<c>A 进程 父进程</c>)与所有带终端的进程
    /// (<c>P 进程 父进程 进程组 终端 终端前台组 [命令行]</c>,命令行只给前台组长)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>整段包进 <c>sh -c '…'</c>。</b>exec 通道里的命令由用户的登录 shell 解析,而登录 shell 可能是 fish ——
    /// 单引号里的东西 fish 原样交出去(它只认 <c>\'</c> 与 <c>\\</c> 两种转义,本段两样都不含)。
    /// 所以脚本里<b>不许出现单引号</b>,引号一律用双引号。
    /// </para>
    /// <para>
    /// Linux 走 <c>/proc</c>:只用 <c>read</c> 这个内建读 stat,几千个进程也不 fork;
    /// 只有前台组长才去 <c>tr</c> 一次命令行(每个终端至多一个)。<c>stat</c> 里的进程名可能含空格与括号,
    /// 先剥到<b>最后一个</b> <c>)</c> 再按空白切:之后依次是状态、父进程、进程组、会话、终端号、终端前台组。
    /// 终端号为 0 = 没有控制终端,直接跳过 —— 探针自己、守护进程都在这一档,输出因此只有寥寥几行。
    /// </para>
    /// <para>
    /// 没有 <c>/proc</c>(macOS、BSD)退回 <c>ps</c>,同样的列、同样的格式;终端列的「没有」各家写法不同
    /// (<c>?</c> / <c>??</c> / <c>-</c>),一并跳过。
    /// </para>
    /// </remarks>
    public const string Command =
        """
        sh -c '
        if [ -r /proc/self/stat ]; then
          p=$$; i=0
          while [ "$i" -lt 6 ] && read -r s < /proc/$p/stat; do
            s=${s##*)}; set -- $s
            printf "A %s %s\n" "$p" "$2"
            [ "$2" -gt 1 ] || break
            p=$2; i=$((i+1))
          done
          for f in /proc/[0-9]*/stat; do
            read -r s < $f || continue
            p=${f%/stat}; p=${p#/proc/}
            s=${s##*)}; set -- $s
            [ "$5" = 0 ] && continue
            a=
            [ "$p" = "$3" ] && [ "$3" = "$6" ] && a=$(tr "\000" " " < /proc/$p/cmdline)
            printf "P %s %s %s %s %s %s\n" "$p" "$2" "$3" "$5" "$6" "$a"
          done
        else
          p=$$; i=0
          while [ "$i" -lt 6 ]; do
            q=$(ps -o ppid= -p "$p") || break
            set -- $q; [ -n "$1" ] || break
            printf "A %s %s\n" "$p" "$1"
            [ "$1" -gt 1 ] || break
            p=$1; i=$((i+1))
          done
          ps -A -ww -o pid= -o ppid= -o pgid= -o tt= -o tpgid= -o args= | while read -r a b c d e f; do
            case $d in "?"|"??"|"-") continue;; esac
            printf "P %s %s %s %s %s %s\n" "$a" "$b" "$c" "$d" "$e" "$f"
          done
        fi'
        """;

    /// <summary>
    /// 探针超时。它跑在用户敲完回车、终端安静下来之后,晚到的结论没有意义(用户多半已经在敲下一条了)。
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>沿前台往下认中转终端(sudo 套 sudo、script 套 sudo)的层数上限。</summary>
    private const int MaxRelayDepth = 4;

    /// <summary>沿父进程往上找中转关系时最多走几步(sudo → 监视进程 → su → shell 就要三步)。</summary>
    private const int MaxAncestorHops = 6;

    /// <summary>
    /// 跑一次探针。任何失败 —— exec 被禁、通道开不出来、超时、连接已断、输出认不出来 —— 一律返回 null,
    /// 调用方据此什么都不做。
    /// </summary>
    public static async Task<ForegroundShell?> DetectAsync(ISshClientWrapper client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            RemoteCommandResult result = await client.RunCommandDetailedAsync(Command, timeout.Token).ConfigureAwait(false);
            return Resolve(result.StandardOutput);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] Foreground shell probe failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把探针输出翻成结论:先认登录 shell,再沿它所在终端的前台往下找交互式 shell。认不出来返回 null。
    /// </summary>
    public static ForegroundShell? Resolve(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }
        List<int> ancestors = [];
        Dictionary<int, ProcessRow> rows = [];
        foreach (string line in output.Split('\n'))
        {
            string[] f = line.TrimEnd('\r').Split(' ', 7);
            if (f is ["A", _, var parent] && int.TryParse(parent, out int ppid))
            {
                ancestors.Add(ppid);
            }
            else if (f.Length >= 6 && f[0] == "P"
                     && int.TryParse(f[1], out int pid)
                     && int.TryParse(f[2], out int parentPid)
                     && int.TryParse(f[3], out int pgrp)
                     && int.TryParse(f[5], out int tpgid))
            {
                rows[pid] = new(pid, parentPid, pgrp, f[4], tpgid, f.Length > 6 ? f[6].Trim() : string.Empty);
            }
        }
        if (FindLoginShell(ancestors, rows) is not { } login)
        {
            return null;
        }
        return FindForegroundShell(login.Tty, login.Tpgid, rows, 0) is { } shell
            ? new ForegroundShell(login.Pid, shell.Pid, shell.Kind)
            : null;
    }

    /// <summary>
    /// 登录 shell = 离探针最近的、<b>恰好</b>有一个带终端子进程的那个祖先的那个子进程。
    /// </summary>
    /// <remarks>
    /// 从近往远找,第一个有带终端子进程的祖先就是答案(sshd 会话进程):有一个就是它,有好几个就放弃 ——
    /// 再往上就是 sshd 的监听进程与 init,init 底下挂着 getty,数到一个也绝不是我们的。所以 1 号进程不算。
    /// </remarks>
    private static ProcessRow? FindLoginShell(List<int> ancestors, Dictionary<int, ProcessRow> rows)
    {
        foreach (int ancestor in ancestors)
        {
            if (ancestor <= 1)
            {
                return null;
            }
            ProcessRow[] children = [.. rows.Values.Where(r => r.Ppid == ancestor)];
            if (children.Length == 1)
            {
                return children[0];
            }
            if (children.Length > 1)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// 终端 <paramref name="tty" /> 的前台进程组是 <paramref name="foreground" />:组长是交互式 shell 就是它;
    /// 否则看这个组的后代有没有落在另一个终端上(sudo 的 <c>use_pty</c>),有且仅有一个就沿那个终端接着认。
    /// </summary>
    private static (int Pid, RemoteShellKind Kind)? FindForegroundShell(
        string tty, int foreground, Dictionary<int, ProcessRow> rows, int depth)
    {
        if (depth > MaxRelayDepth || foreground <= 0)
        {
            return null;
        }
        if (rows.TryGetValue(foreground, out ProcessRow? leader) && leader.Tty == tty
            && InteractiveShellKind(leader.Args) is { } kind)
        {
            return (leader.Pid, kind);
        }
        HashSet<int> group = [.. rows.Values.Where(r => r.Pgrp == foreground && r.Tty == tty).Select(r => r.Pid)];
        if (group.Count == 0)
        {
            return null;
        }
        ProcessRow[] relayed = [.. rows.Values.Where(r => r.Tty != tty && DescendsFrom(r, group, rows))];
        string[] ttys = [.. relayed.Select(r => r.Tty).Distinct(StringComparer.Ordinal)];
        return ttys.Length == 1
            ? FindForegroundShell(ttys[0], relayed[0].Tpgid, rows, depth + 1)
            : null;
    }

    /// <summary>
    /// 沿父进程往上能不能走到 <paramref name="group" /> 里的某一个,且途中只经过与 <paramref name="row" /> <b>同一个终端</b>的进程。
    /// </summary>
    /// <remarks>
    /// 途经别的终端就不算:<c>script</c> 里再 <c>sudo -i</c> 时,最里层的 shell 也是 script 的后代,
    /// 但它和 script 之间隔着中间那一层终端 —— 只认直接接出来的这一层,里面的交给下一轮递归。
    /// 否则一次数出两个终端,整条链就被判成认不出来。
    /// </remarks>
    private static bool DescendsFrom(ProcessRow row, HashSet<int> group, Dictionary<int, ProcessRow> rows)
    {
        int parent = row.Ppid;
        for (int hop = 0; hop < MaxAncestorHops; hop++)
        {
            if (group.Contains(parent))
            {
                return true;
            }
            if (!rows.TryGetValue(parent, out ProcessRow? next) || next.Tty != row.Tty)
            {
                return false;
            }
            parent = next.Ppid;
        }
        return false;
    }

    /// <summary>
    /// 这条命令行是不是一个<b>交互式</b> shell,是的话是哪一种;不是(或者不认识)返回 null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 种类按 <c>argv[0]</c> 的文件名认:登录 shell 带前导连字符(<c>-bash</c>),<c>sudo -s</c> 给的是全路径
    /// (<c>/bin/bash</c>)。<c>sh</c> 可能是 dash、BusyBox ash,也可能是 posix 模式的 bash ——
    /// 一律按 <see cref="RemoteShellKind.PosixSh" /> 注入:那一段只碰 <c>PS1</c>,三者都认。
    /// csh / tcsh / nushell 之类不认,宁可不跟也不往里打 sh 语法。
    /// </para>
    /// <para>
    /// 交互与否看参数:有 <c>-c</c>(含 <c>-lc</c> 这种合写、fish 的 <c>--command</c>)就是在跑一条命令;
    /// 有不以 <c>-</c> / <c>+</c> 开头的操作数就是在跑脚本(<c>bash deploy.sh</c> 卡在 <c>read</c> 时
    /// 同样是前台组长,往里打字就成了它的回答)。<c>-o</c>、<c>--rcfile</c> 这类带值的选项把值一并跳过
    /// (<c>nix-shell</c> 起的就是 <c>bash --rcfile …</c>)。命令行里的空格已被换成参数分隔,
    /// 带空格的参数会被切碎 —— 碎出来的操作数只会让判定偏向"不是",方向是安全的。
    /// </para>
    /// </remarks>
    public static RemoteShellKind? InteractiveShellKind(string? args)
    {
        string[] argv = (args ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (argv.Length == 0)
        {
            return null;
        }
        string name = argv[0].TrimStart('-');
        name = name[(name.LastIndexOf('/') + 1)..];
        RemoteShellKind? kind = name switch
        {
            "bash" => RemoteShellKind.Bash,
            "zsh" => RemoteShellKind.Zsh,
            "fish" => RemoteShellKind.Fish,
            "sh" or "dash" or "ash" or "ksh" or "ksh93" or "mksh" or "lksh" or "oksh" or "pdksh" => RemoteShellKind.PosixSh,
            _ => null
        };
        if (kind is null)
        {
            return null;
        }
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (TakesValue(kind.Value, arg))
            {
                i++;
                continue;
            }
            if (arg is "-" or "--")
            {
                // 选项到此为止:后面再有东西就是脚本或位置参数。
                return i == argv.Length - 1 ? kind : null;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (arg.StartsWith("--command", StringComparison.Ordinal))
                {
                    return null;
                }
                continue;
            }
            if (arg[0] is '-' or '+')
            {
                if (arg[0] == '-' && arg.Contains('c', StringComparison.Ordinal))
                {
                    return null;
                }
                continue;
            }
            return null;
        }
        return kind;
    }

    /// <summary>
    /// 这个选项是不是把<b>下一个</b>参数当作自己的值。按 shell 区分:
    /// <c>-C</c> 在 fish 是初始命令(带值),在 bash 却是 noclobber(不带值)—— 混用就会把 <c>bash -C deploy.sh</c>
    /// 里的脚本名当成选项值吃掉,误判成交互式。
    /// </summary>
    private static bool TakesValue(RemoteShellKind kind, string arg) =>
        arg is "-o" or "+o"
        || (kind == RemoteShellKind.Bash && arg is "-O" or "+O" or "--rcfile" or "--init-file")
        || (kind == RemoteShellKind.Fish && arg is "-C" or "--init-command");

    /// <summary>探针输出里的一行进程。<paramref name="Args" /> 只有前台组长才有。</summary>
    private sealed record ProcessRow(int Pid, int Ppid, int Pgrp, string Tty, int Tpgid, string Args);
}
