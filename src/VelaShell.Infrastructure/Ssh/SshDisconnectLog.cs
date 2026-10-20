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
/// 用户报「用着用着就断了」时,日志里原先只有重连的痕迹,没有断开的原因 —— <c>plan.md</c> §197 那次
/// 只能另搭单文件程序复现,才看到是哪一端、为什么关的。库在 <see cref="SshConnection.Completion" /> 上交出了原因
/// (保活超时、对端关闭、收到 <c>DISCONNECT</c>、协议错误……),这里每条连接挂一次,结束时写一行。
/// 本端释放(关标签页、断开、重连前拆旧连接)同样记一行:看日志的人才分得清是掉线还是用户自己关的。
/// </remarks>
internal static class SshDisconnectLog
{
    /// <summary>落盘文本的长度上限;对端描述是不可信文本,异常消息里也可能带着它。</summary>
    private const int TextLimit = 256;

    /// <summary>挂在一条刚建好的连接上;连接结束时写一行。</summary>
    /// <param name="connection">刚建好的连接(含跳板链上的每一跳)。</param>
    /// <param name="target">这条连接连的是谁,<c>用户@主机:端口</c>。</param>
    internal static void Watch(SshConnection connection, string target)
        => _ = LogWhenClosedAsync(connection.Completion, target, Stopwatch.GetTimestamp());

    private static async Task LogWhenClosedAsync(Task<SshException> completion, string target, long connectedAt)
    {
        // Completion 只以结果完成、从不以异常完成,这里不会冒出未观察的任务异常
        SshException reason = await completion.ConfigureAwait(false);
        Trace.WriteLine(Describe(target, reason, Stopwatch.GetElapsedTime(connectedAt)));
    }

    /// <summary>一行日志:谁、连了多久、为什么结束。</summary>
    internal static string Describe(string target, SshException reason, TimeSpan duration)
    {
        var line = new StringBuilder();
        // 日志行用英文:它是给看日志的人的,不随界面语言变;库给的原因消息原样附在后面
        line.Append(CultureInfo.InvariantCulture, $"[SSH] {target} closed after {FormatDuration(duration)}: ");
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

    private static string FormatDuration(TimeSpan duration)
        => duration.ToString(duration.TotalDays >= 1 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>压成一行、去掉控制字符并截断:日志一条一行,对端给的文本不能伪造出第二行。</summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, TextLimit + 1));
        foreach (char c in text)
        {
            if (builder.Length >= TextLimit)
            {
                builder.Append('…');
                break;
            }
            builder.Append(char.IsControl(c) ? ' ' : c);
        }
        return builder.ToString().Trim();
    }
}
