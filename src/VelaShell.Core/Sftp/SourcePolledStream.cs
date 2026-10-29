using System.Buffers;
using System.IO.Pipelines;

namespace VelaShell.Core.Sftp;

/// <summary>A bounded pipe reader; the relay producer exclusively owns its writer.</summary>
public sealed class SourcePolledStream : Stream
{
    /// <summary>
    /// 管道容量:发送端最多能领先接收端这么多字节,超出就等接收方取走。
    /// </summary>
    /// <remarks>
    /// 不是"无限堆积"那种管道:源端必须等接收端取走才能继续读,于是
    /// (a) 接收端停下时源端立刻被停住,不会把整份文件读进内存;
    /// (b) 两端速度差被吸收,不像"读一块、写一块"那样互相拖累。
    /// </remarks>
    public const long MemoryBudget = 4L * 1024 * 1024;

    private readonly Pipe _pipe = new(new PipeOptions(
        pauseWriterThreshold: MemoryBudget,
        resumeWriterThreshold: MemoryBudget / 2,
        minimumSegmentSize: 64 * 1024,
        useSynchronizationContext: false));

    private readonly string _path;
    private readonly CancellationToken _cancellationToken;
    private bool _disposed;
    private long _delivered;

    /// <summary>创建一个空的、等待写端投递的读流。</summary>
    /// <param name="path">源路径(只用在线尾核对与错误信息里)。</param>
    /// <param name="cancellationToken">整个中转的取消令牌:它一取消,读端立刻结束。</param>
    public SourcePolledStream(string path, CancellationToken cancellationToken)
    {
        _path = path;
        _cancellationToken = cancellationToken;
    }

    /// <summary>已经交给调用方的字节数。</summary>
    public long DeliveredBytes => Interlocked.Read(ref _delivered);

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("The streamed source has no known length.");

    /// <inheritdoc />
    public override long Position
    {
        get => DeliveredBytes;
        set => throw new NotSupportedException("The streamed source is sequential.");
    }

    /// <summary>写端从这里写。<b>只有</b> <see cref="RemoteRelay.PumpSourceAsync" /> 的任务应当用它。</summary>
    internal PipeWriter Writer => _pipe.Writer;

    internal bool EndOfStreamObserved { get; private set; }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty) return ValueTask.FromResult(0);
        return ReadCoreAsync(buffer, cancellationToken);
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
        readCancellation.Token.ThrowIfCancellationRequested();
        if (EndOfStreamObserved) return 0;
        ReadResult result = await _pipe.Reader.ReadAsync(readCancellation.Token).ConfigureAwait(false);
        try
        {
            if (result.IsCanceled) throw new OperationCanceledException(_path, readCancellation.Token);
            int copied = (int)Math.Min(buffer.Length, result.Buffer.Length);
            result.Buffer.Slice(0, copied).CopyTo(buffer.Span);
            Interlocked.Add(ref _delivered, copied);
            if (copied == 0 && result.IsCompleted) EndOfStreamObserved = true;
            return copied;
        }
        finally
        {
            long consumed = Math.Min(buffer.Length, result.Buffer.Length);
            _pipe.Reader.AdvanceTo(result.Buffer.GetPosition(consumed));
        }
    }
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The streamed source is asynchronous only.");

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) =>
        throw new NotSupportedException("The streamed source is asynchronous only.");

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("The streamed source is sequential.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The streamed source is read-only.");

    /// <summary>释放读流。已交给调用方的字节不会因为释放而丢失,调用方只是在读到一半停了手 ——
    /// 那由 <see cref="RemoteRelay.PumpSourceAsync" /> 的取消处理走"未读完"的分支。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _pipe.Reader.Complete();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        return ValueTask.CompletedTask;
    }
}
