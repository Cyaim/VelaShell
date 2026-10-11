using System.Buffers;
using System.IO.Pipelines;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 将 <see cref="SshShell" /> 适配到 <see cref="IShellStreamWrapper" />。
/// </summary>
/// <remarks>用一条已经打开的交互式 shell 构造。</remarks>
/// <param name="shell">已经打开的 shell。</param>
/// <param name="notices">打开时附带的提示(转发开没开成),见 <see cref="IShellStreamWrapper.Notices" />。</param>
public sealed class ShellStreamWrapper(SshShell shell, IReadOnlyList<ShellStreamNotice>? notices = null) : IShellStreamWrapper
{
    private readonly SshShell _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    private volatile bool _disposed;
    private volatile bool _channelClosed;
    private bool _readEof;

    /// <inheritdoc />
    public IReadOnlyList<ShellStreamNotice> Notices { get; } = notices ?? [];

    /// <summary>读端发出 EOF 前始终保持可读(读返回 0 表示 EOF)。</summary>
    public bool CanRead => !_disposed && !_readEof;

    /// <summary>
    /// 读端为什么走到了头。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><b>远端 shell 退出</b>:对端发 <c>SSH_MSG_CHANNEL_CLOSE</c>,
    ///     表现为管道**干净地**读完(<c>IsCompleted</c> 且没抛)。</item>
    ///   <item><b>连接中断</b>:读会抛 <see cref="SshException" />。</item>
    /// </list>
    /// 于是「读完了且没抛」就是且仅是远端自己退了,这正是 #383 要认出来的那个信号 ——
    /// 自动重连只该管连接中断,而不该在用户敲了 <c>exit</c> 之后又把他连回去。
    /// </remarks>
    public ShellCloseReason CloseReason { get; private set; } = ShellCloseReason.Unknown;

    /// <summary>shell 存活且通道未断时可写入。</summary>
    public bool CanWrite => !_disposed && !_channelClosed;

    /// <summary>当前是否有数据可读而不阻塞(对管道式 IO 无意义)。</summary>
    public bool DataAvailable => false;

    /// <summary>不被调用。</summary>
    public string? Expect(string regex, TimeSpan timeout) =>
        throw new NotSupportedException("Expect is not supported. Use ReadAsync instead.");

    /// <summary>不被调用。</summary>
    public void WriteLine(string line) =>
        throw new NotSupportedException("WriteLine is not supported. Use WriteAsync instead.");

    /// <summary>
    /// 从远端 shell 读原始字节。EOF 时返回 0 并把 <see cref="CloseReason" /> 记成真实原因。
    /// </summary>
    /// <remarks>
    /// stdout 与 stderr 已经由伪终端在远端合并成一条流 —— 有 pty 时本来就没有第二条。
    /// </remarks>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_disposed || _readEof)
        {
            return 0;
        }

        PipeReader reader = _shell.StandardOutput;

        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> available = result.Buffer;

                if (!available.IsEmpty)
                {
                    int copied = (int)Math.Min(count, available.Length);
                    available.Slice(0, copied).CopyTo(buffer.AsSpan(offset, copied));

                    // 没拷走的留在管道里 —— AdvanceTo 之后 available 指向的内存随时可能被回收。
                    reader.AdvanceTo(available.GetPosition(copied));
                    return copied;
                }

                reader.AdvanceTo(available.Start, available.End);

                if (result.IsCompleted)
                {
                    // 读完且没抛:看库记下的关闭原因,不靠管道的结束方式反推(W7)。
                    EndRead(FromChannel(_shell.Channel.CloseReason));
                    return 0;
                }

                if (result.IsCanceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        // 通道/连接在读之中断掉:不是用户让它结束的,自动重连该管这一类。
        catch (SshException) { EndRead(ShellCloseReason.ConnectionLost); return 0; }
        catch (IOException) { EndRead(ShellCloseReason.ConnectionLost); return 0; }

        // 我们自己在拆:关标签、点断开、释放。远端没给出任何结论。
        catch (ObjectDisposedException) { EndRead(ShellCloseReason.LocalTeardown); return 0; }
        catch (InvalidOperationException) { EndRead(ShellCloseReason.LocalTeardown); return 0; }
        catch (OperationCanceledException) { EndRead(ShellCloseReason.LocalTeardown); return 0; }
    }

    /// <summary>库记下的通道关闭原因换成终端流的结束原因。</summary>
    /// <remarks>
    /// 会话没了是断线(自动重连该管),本端关的是拆除;对端关的、以及只收到 EOF 还没收到 CLOSE 的(<see cref="SshChannelCloseReason.Unknown" />),
    /// 是远端 shell 自己退了 —— 用户在远端敲了 <c>exit</c>,不该再把他连回去(#383)。
    /// </remarks>
    internal static ShellCloseReason FromChannel(SshChannelCloseReason reason) => reason switch
    {
        SshChannelCloseReason.SessionClosed => ShellCloseReason.ConnectionLost,
        SshChannelCloseReason.ClosedLocally => ShellCloseReason.LocalTeardown,
        _ => ShellCloseReason.RemoteExited,
    };

    /// <summary>标记读端到此为止,并记下第一次给出的原因(后续读一律短路,不再改写)。</summary>
    private void EndRead(ShellCloseReason reason)
    {
        _readEof = true;
        if (CloseReason == ShellCloseReason.Unknown)
        {
            CloseReason = reason;
        }
    }

    /// <summary>
    /// 向远端 shell 写入原始字节。通道已断 / 已释放时静默丢弃并把 <see cref="CanWrite" />
    /// 置为 false。
    /// </summary>
    /// <remarks>
    /// 与读端 EOF 返回 0 对称,也与另一个实现 <c>PluginTerminalShellStream.WriteAsync</c>
    /// 的约定一致:「会话已断这件事由读循环收到 EOF 去改标签状态,写端不必再抛一遍」。
    /// <para>
    /// **每次写完都要 Flush。** <see cref="PipeWriter" /> 的写入只进缓冲区,
    /// 不冲就等于没发 —— 症状是「敲了字远端没反应」,而且只在输入量小的时候出现
    /// (量大时缓冲区满了会自己冲,于是偶发)。
    /// </para>
    /// </remarks>
    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_disposed || _channelClosed)
        {
            return;
        }

        try
        {
            _shell.StandardInput.Write(buffer.AsSpan(offset, count));
            FlushResult result = await _shell.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (result.IsCompleted)
            {
                _channelClosed = true;
            }
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or IOException)
        {
            // 通道已断:后续写入一律短路,不再逐次去撞库内异常(断线时键盘输入仍在入队)。
            // 不再接 InvalidOperationException:库曾在关通道时替我们完成 StandardInput,再写就是它;
            // 现在通道关了之后写入返回 IsCompleted(上面那条分支)。
            _channelClosed = true;
        }
    }

    /// <summary>空操作(<see cref="WriteAsync" /> 每次都已经冲过了)。</summary>
    public void Flush()
    {
    }

    /// <summary>
    /// 发送 <c>window-change</c> 请求以调整远端终端尺寸,像素一并带上。
    /// </summary>
    /// <remarks>
    /// 打开通道时的 <c>pty-req</c> 像素仍是 0:那时控件还没布局,不知道像素;
    /// 挂流后宿主立刻按真实网格补发一次(<c>TerminalTabViewModel.SyncPtySize</c>),像素由这一次覆盖。
    /// </remarks>
    public void Resize(PtySize size)
    {
        if (_disposed || _channelClosed || size.Columns <= 0 || size.Rows <= 0)
        {
            return;
        }

        // 接口是同步的,而发请求是异步的。这里**不等**它完成:
        // 调整尺寸失败不影响会话本身,而在 UI 线程上阻塞等一个网络往返是不能接受的。
        _ = ResizeCoreAsync(ToTerminalSize(size));
    }

    /// <summary>换成库的尺寸类型。负的像素按「不知道」发 0 —— 库在构造时就会拒绝负数。</summary>
    internal static SshTerminalSize ToTerminalSize(PtySize size) =>
        new(size.Columns, size.Rows, Math.Max(0, size.PixelWidth), Math.Max(0, size.PixelHeight));

    private async Task ResizeCoreAsync(SshTerminalSize size)
    {
        try
        {
            await _shell.ResizeAsync(size).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
        {
            // 只有"通道确实没了"才短路后续调用。
            _channelClosed = true;
        }
        catch
        {
            // 其它原因(参数、瞬时状态)吞掉,不永久禁写。
        }
    }

    /// <inheritdoc />
    public bool SupportsBreak => !_disposed && !_channelClosed;

    /// <inheritdoc />
    /// <remarks>服务端没执行(回 FAILURE)、通道已经没了都是 <see langword="false" />:按了「发送 Break」不该弹异常。</remarks>
    public async Task<bool> SendBreakAsync(CancellationToken cancellationToken)
    {
        if (!SupportsBreak)
        {
            return false;
        }

        try
        {
            return await _shell.SendBreakAsync(length: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>释放底层 shell 通道。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            await _shell.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 远端可能已经自己退了;释放抛的是清理噪声,吞掉即可。
        }
        GC.SuppressFinalize(this);
    }
}
