namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// <see cref="VelaSftpClientWrapper.OpenAsync" /> 交出去的远端文件流:读写、定位、冲刷与关闭原样转给库的
/// <c>SftpFileStream</c>,只在出错时把库的异常经 <see cref="SshInterop.Translate" /> 翻成 Core 的中立异常。
/// </summary>
/// <remarks>
/// <para>
/// 包装器的其它方法都在 <c>GuardedAsync</c> 里翻译,唯独这条流交出去之后,调用方在自己的循环里读写 ——
/// 续传下载(<c>SftpService</c> 自己搬字节)、双栏中转(源端的流喂给目标端)、续传核实比对尾部、
/// 插件的流式读取都是这样。断线时冒出来的是库自己的类型,<c>TransferRetryPolicy</c> 只认中立类型,
/// 于是一个续传过一次的下载再断就按永久失败标红,而不是重试或挂成「已暂停」。
/// </para>
/// <para>
/// 只翻库的类型(<see cref="SshInterop.Translate" /> 认不出的原样抛):<see cref="NotSupportedException" />
/// (只读流上写)、<see cref="ObjectDisposedException" />(流已关)、调用方自己的取消都保持原样。
/// 库内部的超时(调用方没取消却收到取消)与其它方法一样翻成 <c>VelaSshOperationTimeoutException</c>。
/// </para>
/// <para>
/// 库的流只支持异步,同步的读写重载照旧转过去由它报不支持 —— 这里不把异步包成同步。
/// </para>
/// </remarks>
/// <param name="inner">库打开的远端文件流(归本对象所有,随它关闭)。</param>
internal sealed class TranslatingSftpStream(Stream inner) : Stream
{
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    /// <remarks>续传核实要来回定位,<c>SftpService</c> 要求它为真(见 <c>ISftpClientWrapper.OpenAsync</c> 的契约)。</remarks>
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc />
    public override long Length => Guard(() => _inner.Length);

    /// <inheritdoc />
    public override long Position
    {
        get => Guard(() => _inner.Position);
        set => Guard(() => _inner.Position = value);
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 必须覆写:<see cref="Stream" /> 的默认实现走 <c>BeginRead</c> → 同步的 <see cref="Read(byte[], int, int)" />,
    /// 库的流在那条路上直接报不支持。
    /// </remarks>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    /// <remarks>理由同 <see cref="ReadAsync(byte[], int, int, CancellationToken)" />。</remarks>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SshInterop.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    /// <inheritdoc />
    public override void Flush() => Guard(_inner.Flush);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => Guard(() => _inner.Seek(offset, origin));

    /// <inheritdoc />
    public override void SetLength(long value) => Guard(() => _inner.SetLength(value));

    /// <inheritdoc />
    /// <remarks>
    /// 写入流的关闭会等在途的 WRITE 落地,中途断开时库在这里报「中断 + 已确认的字节数」—— 同样翻过去,
    /// 上层才拿得到 <c>VelaSftpTransferInterruptedException</c> 与它的续传点。
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (SshInterop.Translate(ex) is { } translated)
        {
            throw translated;
        }
        finally
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>请用 <c>await using</c>:同步关闭要等网络往返,库的流对这条路另有说明。</remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Guard(_inner.Dispose);
        }
        base.Dispose(disposing);
    }

    private static T Guard<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception ex) when (SshInterop.Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    private static void Guard(Action operation)
    {
        try
        {
            operation();
        }
        catch (Exception ex) when (SshInterop.Translate(ex) is { } translated)
        {
            throw translated;
        }
    }
}
