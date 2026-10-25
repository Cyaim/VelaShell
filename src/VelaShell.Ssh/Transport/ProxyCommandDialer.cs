// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  ProxyCommand 的行为与 %h %p %r %n %% 替换(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §6

using System.Diagnostics;
using System.Globalization;
using System.Text;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Transport;

/// <summary>经一个外部程序拨号（<c>ProxyCommand</c>）：它的标准输入输出就是到目标的字节流。</summary>
/// <param name="CommandTemplate">
/// 命令行模板，交给系统 shell 解释（类 Unix 用 <c>/bin/sh -c</c>，Windows 用 <c>cmd.exe /c</c>）。
/// 支持 <c>%h</c> 目标主机、<c>%p</c> 端口、<c>%r</c> 用户名、<c>%n</c> 原始主机名、<c>%%</c> 百分号。
/// </param>
/// <remarks>
/// <para>
/// 〔限制，如实说明〕Windows 上子进程的标准输入输出是匿名管道，而匿名管道不支持重叠 IO ——
/// 对它们的异步读写由运行时在线程池线程上以阻塞方式完成。这是平台限制，不是本库的选择；
/// 类 Unix 上没有这个问题。要纯异步的链路，用 <see cref="Socks5Dialer"/> /
/// <see cref="HttpConnectDialer"/> / <see cref="SshJumpDialer"/>（velashell-docs/zh/ssh/spec/09 §6）。
/// </para>
/// <para>
/// 程序的 stderr 会被收集起来：握手前程序就退出时，它说的最后几行会进异常消息 ——
/// 很多代理程序只在 stderr 上说明失败原因。
/// </para>
/// </remarks>
internal sealed record ProxyCommandDialer(string CommandTemplate) : ISshTransportDialer, ISshDialKindSource
{
    /// <summary><c>%r</c> 替换成什么（目标的登录用户名）。</summary>
    public string? UserName { get; init; }

    /// <summary><c>%n</c> 替换成什么（用户原本输入的主机名，未经 <c>HostName</c> 改写）；缺省用目标主机。</summary>
    public string? OriginalHost { get; init; }

    /// <inheritdoc />
    public SshDialKind Kind => SshDialKind.ProxyCommand;

    /// <summary>把模板里的 <c>%</c> 记号换成实际值。</summary>
    internal string Expand(SshEndPoint target)
    {
        StringBuilder result = new(CommandTemplate.Length + 32);
        for (int i = 0; i < CommandTemplate.Length; i++)
        {
            char c = CommandTemplate[i];
            if (c != '%' || i + 1 >= CommandTemplate.Length)
            {
                result.Append(c);
                continue;
            }

            char token = CommandTemplate[++i];
            switch (token)
            {
                case 'h': result.Append(Checked(target.Host, "主机名", allowAt: false)); break;
                case 'p': result.Append(target.Port.ToString(CultureInfo.InvariantCulture)); break;
                case 'r': result.Append(Checked(UserName ?? "", "用户名", allowAt: true)); break;
                case 'n': result.Append(Checked(OriginalHost ?? target.Host, "主机名", allowAt: false)); break;
                case '%': result.Append('%'); break;
                default:
                    throw new SshConnectException(
                        SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                        $"ProxyCommand 里有不认识的记号 %{token}（支持 %h %p %r %n %%）。");
            }
        }

        return result.ToString();
    }

    /// <summary>代入命令行之前确认它不含 shell 元字符。</summary>
    /// <remarks>
    /// <para>
    /// 命令交给 <c>/bin/sh -c</c> 或 <c>cmd.exe /c</c> 解释，而主机名、用户名常常不是写配置的人给的 ——
    /// 一条 <c>ssh://</c> 链接、一个导入的会话、快速连接框里粘进来的一串。
    /// 配置里写着 <c>ProxyCommand nc %h %p</c> 的话，主机名 <c>x;touch /tmp/pwn</c>（或 Windows 上的
    /// <c>x&amp;calc</c>、<c>%VAR%</c>）就是一条被执行的命令（CVE-2023-51385 那一类）。
    /// </para>
    /// <para>
    /// 不做转义而是直接拒绝：两种 shell 的引用规则不一样，<c>cmd</c> 的尤其难以写对；
    /// 而合法的主机名与用户名本来就只用得到这几种字符。
    /// </para>
    /// <para>
    /// ⚠️ <b>开头的 <c>-</c> 也拒绝。</b>字符全都合法，值本身照样能变成别的东西：模板里的 <c>nc</c> / <c>ncat</c> / <c>socat</c>
    /// 会把 <c>-e/bin/sh</c>、<c>-oProxyCommand=…</c> 这样的值当成<b>选项</b>解析 —— 那是参数注入（与 Git 的 CVE-2017-1000117 同一类）。
    /// 合法的主机名与用户名不以 <c>-</c> 开头（RFC 1123 的主机名以字母或数字开头）。
    /// </para>
    /// </remarks>
    /// <summary>能不能原样代入交给 shell 的命令行：不以 <c>-</c> 开头，只由字母、数字与 <c>. - _</c> 组成（主机名还可以有 IPv6 的冒号，用户名还可以有 <c>@</c>）。</summary>
    /// <remarks><c>Match exec</c> 展开记号时用的也是这一套（见 <see cref="Config.SshConfigFile"/>）。</remarks>
    internal static bool IsShellSafe(string value, bool allowAt) =>
        !value.StartsWith('-')
        && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' || (c == ':' && !allowAt) || (c == '@' && allowAt));

    private static string Checked(string value, string what, bool allowAt)
    {
        if (value.StartsWith('-'))
        {
            throw new SshConnectException(
                SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                $"{what}以「-」开头，不能代入 ProxyCommand：命令里的 nc / ssh 之类会把它当成选项解析。");
        }

        foreach (char c in value)
        {
            bool safe = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' || (c == ':' && !allowAt) || (c == '@' && allowAt);
            if (!safe)
            {
                throw new SshConnectException(
                    SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                    $"{what}里有不能交给 shell 的字符 U+{(int)c:X4}，不能代入 ProxyCommand。" +
                    "合法的主机名与用户名只由字母、数字与 . - _ 组成（主机名还可以有 IPv6 的冒号）。");
            }
        }
        return value;
    }

    /// <inheritdoc />
    public ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        string command = Expand(target.EndPoint);
        long startedAt = Environment.TickCount64;

        ProcessStartInfo start = new()
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            // /s + 外层引号：cmd 去掉最外层的一对引号，其余原样 —— 这是让命令里的引号
            // 保持原意的唯一写法；ArgumentList 的转义规则是给 C 运行时的，不是给 cmd 的。
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.Arguments = $"/d /s /c \"{command}\"";
        }
        else
        {
            start.FileName = "/bin/sh";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
        }

        Process process;
        try
        {
            process = Process.Start(start)
                ?? throw new InvalidOperationException("Process.Start 返回了 null。");
        }
        catch (Exception ex) when (ex is not SshException)
        {
            // 起不来的是本机的 shell（ComSpec 指向的程序不在、没有执行权限）：配置问题，重试不会好。
            string message = $"启动 ProxyCommand 失败（{command}）：{ex.Message}";
            throw new SshConnectException(SshFailureReason.InvalidConfiguration, SshPhase.Dialing, message, ex)
            {
                Hops = [DialHops.Hop(Kind, target.EndPoint, succeeded: false, startedAt, message)],
            };
        }

        return ValueTask.FromResult<Stream>(new ProcessDuplexStream(process, command, target.EndPoint, startedAt));
    }
}

/// <summary>一个子进程的标准输出（读）与标准输入（写）拼成的双向流。</summary>
internal sealed class ProcessDuplexStream : Stream
{
    /// <summary>stderr 最多留多少 —— 只为了在失败时说清原因。</summary>
    private const int MaxStderrChars = 4096;

    /// <summary>关掉 stdin 之后给程序多久自己退出。</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly string _command;
    private readonly SshEndPoint _target;
    private readonly long _startedAt;
    private readonly Stream _output;
    private readonly Stream _input;
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrPump;
    private int _disposed;

    /// <summary>程序的 stdout 上出现过数据（只在读的那一侧写）。</summary>
    private bool _anyOutput;

    public ProcessDuplexStream(Process process, string command, SshEndPoint target, long startedAt)
    {
        _process = process;
        _command = command;
        _target = target;
        _startedAt = startedAt;
        _output = process.StandardOutput.BaseStream;
        _input = process.StandardInput.BaseStream;
        _stderrPump = PumpStderrAsync();
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _anyOutput = true;
        }
        else if (!buffer.IsEmpty)
        {
            await ThrowIfFailedAsync().ConfigureAwait(false);
        }
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await _input.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // 管道断了多半是程序退出了 —— 把它说的话带出来，比一句「管道已结束」有用。
            await ThrowIfFailedAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => _input.FlushAsync(cancellationToken);

    public override void Flush() => _input.Flush();

    public override int Read(byte[] buffer, int offset, int count) => _output.Read(buffer, offset, count);

    public override void Write(byte[] buffer, int offset, int count)
    {
        _input.Write(buffer, offset, count);
        _input.Flush();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>程序已经退出：没输出过任何数据就退出是拨号失败，输出过之后以非零退出码结束是连接断了。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/09 §6〕程序在连上之前就退出（<c>nc</c> 连不上目标、代理拒绝、命令本身不存在），
    /// 判 <see cref="SshFailureReason.ProxyRefused"/>，消息带退出码与 stderr 末尾，并带上这一跳。
    /// 曾经一律抛 <see cref="IOException"/>：建连路上它被归成「对端断开」，消息还被截到 256 个字符 ——
    /// 代理程序往往只在 stderr 上说明原因，截掉的正是那一段。
    /// </remarks>
    private async ValueTask ThrowIfFailedAsync()
    {
        // stdout 读到结尾时进程多半正在退出，但操作系统未必已经把它标成「已退出」——
        // 稍等一下，不然这里会把一次失败当成正常结束放过去，stderr 就丢了。
        try
        {
            using CancellationTokenSource wait = new(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;   // 还没退出：stdout 关了但进程还在 —— 让上层按「对端关闭」处理
        }

        try
        {
            await _stderrPump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 读不全就用已经读到的。
        }

        if (_process.ExitCode == 0 && _anyOutput)
        {
            return;
        }

        string stderr;
        lock (_stderr)
        {
            // stderr 里常常转述着对端的话（nc、connect-proxy 打出来的应答），进消息之前同样先清一遍。
            stderr = PeerText.Sanitize(_stderr.ToString().Trim(), MaxStderrChars);
        }
        string said = stderr.Length == 0 ? "。" : $"：{stderr}";

        if (!_anyOutput)
        {
            string message = $"ProxyCommand（{_command}）在连上 {_target} 之前就以退出码 {_process.ExitCode} 结束{said}";
            throw new SshConnectException(SshFailureReason.ProxyRefused, SshPhase.Dialing, message)
            {
                Hops = [DialHops.Hop(SshDialKind.ProxyCommand, _target, succeeded: false, _startedAt, message)],
                FailureKind = SshConnectFailureKind.ProxyCommandExited,
                Via = _command,
                Target = _target.ToString(),
                ProxyReplyCode = _process.ExitCode,
                ProxyReply = stderr.Length == 0 ? null : stderr,
            };
        }

        throw new IOException($"ProxyCommand（{_command}）以退出码 {_process.ExitCode} 结束{said}");
    }

    private async Task PumpStderrAsync()
    {
        char[] buffer = new char[512];
        try
        {
            while (true)
            {
                int read = await _process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                lock (_stderr)
                {
                    _stderr.Append(buffer, 0, read);
                    if (_stderr.Length > MaxStderrChars)
                    {
                        _stderr.Remove(0, _stderr.Length - MaxStderrChars);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 进程没了。
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await ReleaseAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>先关 stdin 给程序一个体面退出的机会；等不到就结束整个进程树。</summary>
    private async ValueTask ReleaseAsync()
    {
        try
        {
            await _input.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 管道已经断了。
        }

        try
        {
            using CancellationTokenSource grace = new(ExitGrace);
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 恰好在这时退出了。
            }
        }
        catch (Exception)
        {
            // 同上。
        }

        try
        {
            await _output.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 同上。
        }

        _process.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = ReleaseInBackgroundAsync();
        }
        base.Dispose(disposing);
    }

    private async Task ReleaseInBackgroundAsync()
    {
        try
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 没有人能接这个异常了。
        }
    }
}
