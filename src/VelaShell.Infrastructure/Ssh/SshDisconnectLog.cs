using System.Diagnostics;
using System.Globalization;
using System.Text;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Session;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 连接结束时把原因写进诊断日志(<c>~/.velashell/logs</c>)。
/// </summary>
/// <remarks>
/// <para>
/// 用户报「用着用着就断了」时,日志里原先只有重连的痕迹,没有断开的原因 —— <c>plan.md</c> §197 那次
/// 只能另搭单文件程序复现,才看到是哪一端、为什么关的。库在 <see cref="SshConnection.Completion" /> 上交出了原因
/// (保活超时、对端关闭、收到 <c>DISCONNECT</c>、协议错误……),这里每条连接挂一次,结束时写一行。
/// 本端释放(关标签页、断开、重连前拆旧连接)同样记一行:看日志的人才分得清是掉线还是用户自己关的。
/// </para>
/// <para>
/// 记的是<b>连接起始时刻</b>(墙上时钟,带时区偏移),不是连了多久。原先用 <c>Stopwatch</c> 量时长,
/// 机器睡眠的那段各平台口径不同(Windows 的计数器一般把睡眠算进去,macOS 与 Linux 的单调时钟不算),
/// 同样是「合盖一夜、早上发现断了」,记下的时长差出一整夜。起始时刻与日志行自己的时间戳一减,就是墙上时钟的时长,
/// 哪个平台都一样;中间睡没睡,对一下系统日志即可。
/// </para>
/// </remarks>
internal static class SshDisconnectLog
{
    /// <summary>落盘文本的长度上限;对端描述是不可信文本,异常消息里也可能带着它。</summary>
    private const int TextLimit = 256;

    /// <summary>挂在一条刚建好的连接上;连接结束时写一行。</summary>
    /// <param name="connection">刚建好的连接(含跳板链上的每一跳)。</param>
    /// <param name="target">这条连接连的是谁,<c>用户@主机:端口</c>。</param>
    internal static void Watch(SshConnection connection, string target)
        => _ = Watch(connection.Completion, target, TimeProvider.System, static line => Trace.WriteLine(line));

    /// <summary>同上,时钟与写日志的地方由调用方给(测试用)。起始时刻在挂上的这一刻取,不等结束。</summary>
    internal static Task Watch(Task<SshException> completion, string target, TimeProvider clock, Action<string> write)
        => LogWhenClosedAsync(completion, target, clock.GetLocalNow(), write);

    private static async Task LogWhenClosedAsync(Task<SshException> completion, string target, DateTimeOffset connectedAt,
        Action<string> write)
    {
        // Completion 只以结果完成、从不以异常完成,这里不会冒出未观察的任务异常
        SshException reason = await completion.ConfigureAwait(false);
        write(Describe(target, reason, connectedAt));
    }

    /// <summary>一行日志:谁、什么时候连上的、为什么结束。</summary>
    internal static string Describe(string target, SshException reason, DateTimeOffset connectedAt)
    {
        var line = new StringBuilder();
        // 日志行用英文:它是给看日志的人的,不随界面语言变;库给的原因消息原样附在后面。
        // 时刻与日志行前缀同样写本地时间,另带偏移:跨了夏令时或改过时区,也减得出真实的时长
        line.Append(CultureInfo.InvariantCulture,
            $"[SSH] {target} closed (connected since {connectedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}): ");
        if (reason.Reason == SshFailureReason.Aborted)
        {
            line.Append("closed locally");
            return line.ToString();
        }

        line.Append(CultureInfo.InvariantCulture, $"{reason.Reason}({reason.Phase})");
        if (reason is SshConnectionClosedException { DisconnectReason: { } code } closed)
        {
            line.Append(CultureInfo.InvariantCulture, $" peer DISCONNECT {code}({(uint)code})");
            if (!string.IsNullOrWhiteSpace(closed.PeerDescription))
            {
                line.Append(" \"").Append(Clean(closed.PeerDescription)).Append('"');
            }
        }
        line.Append(" - ").Append(Clean(reason.Message));
        if (reason.InnerException is { } inner)
        {
            line.Append(" <- ").Append(inner.GetType().Name).Append(": ").Append(Clean(inner.Message));
        }
        return line.ToString();
    }

    /// <summary>压成一行、去掉控制字符并截断:日志一条一行,对端给的文本不能伪造出第二行。</summary>
    /// <remarks>
    /// 各种换行(含 U+2028 / U+2029,不少查看器把它们当换行)先换成空格;其余控制字符与双向控制符交给库的
    /// <see cref="PeerText.Sanitize" /> 换成 <c>?</c> —— 库自己往异常消息里拼对端原话也是这一套。
    /// </remarks>
    private static string Clean(string text) => PeerText.Sanitize(text.ReplaceLineEndings(" "), TextLimit).Trim();
}
