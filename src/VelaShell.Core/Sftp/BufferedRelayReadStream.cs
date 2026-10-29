using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace VelaShell.Core.Sftp;

// One producer and one reader. The slot is held until the reader returns the block,
// not merely until it leaves the queue. This stream never owns/closes the source.
internal sealed class BufferedRelayReadStream : Stream
{
    internal const int BlockSize = 256 * 1024;
    internal const int BlockCount = 16;
    internal const int MemoryBudget = BlockSize * BlockCount;

    private readonly Stream _source;
    private readonly long _expectedLength;
    private readonly string _path;
    private readonly ArrayPool<byte> _pool;
    private readonly Channel<Block> _blocks = Channel.CreateUnbounded<Block>(new UnboundedChannelOptions
    {
        SingleWriter = true,
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly SemaphoreSlim _slots = new(BlockCount, BlockCount);
    private readonly CancellationTokenSource _stop;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _producer;
    private ExceptionDispatchInfo? _failure;
    private Block? _current;
    private int _offset;
    private long _consumed;
    private bool _stopped;
    private readonly bool _cacheEntireFile;

    internal long ConsumedBytes => _consumed;

    internal bool IsEmpty { get; private set; }

    internal BufferedRelayReadStream(Stream source, long expectedLength, string path, bool cacheEntireFile,
        CancellationToken cancellationToken, ArrayPool<byte>? pool = null)
    {
        _cacheEntireFile = cacheEntireFile;
        _source = source;
        _expectedLength = expectedLength;
        _path = path;
        // A private fixed bucket has exact 256 KiB capacities and does not retain
        // remote contents in the process-wide shared pool after this relay dies.
        _pool = pool ?? ArrayPool<byte>.Create(BlockSize, BlockCount);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Task.Run also separates a source whose ReadAsync completes synchronously from
        // a synchronous uploader. Neither side executes the other's I/O loop inline.
        _producer = Task.Run(() => ProduceAsync(cacheEntireFile));
    }

    internal async Task WaitUntilReadyAsync()
    {
        await _ready.Task.ConfigureAwait(false);
        if (_cacheEntireFile || IsEmpty)
        {
            await _producer.ConfigureAwait(false);
        }
    }

    private async Task ProduceAsync(bool cacheEntireFile)
    {
        long produced = 0;
        byte[]? rented = null;
        bool slotHeld = false;
        // 「整份缓存」只在长度可信时成立。长度未知(-1:服务器不支持 SIZE)时按不可信处理:
        // 否则 limit 算成负数,一块都不读就直接报 EOF,一个非空的源会被当成空文件传过去,
        // 目标收到 0 字节。这类源改走有界预读,EOF 由真正读到的 0 决定。
        cacheEntireFile = cacheEntireFile && _expectedLength > 0;
        try
        {
            while (true)
            {
                await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
                slotHeld = true;
                rented = _pool.Rent(BlockSize);
                if (rented.Length != BlockSize)
                {
                    _pool.Return(rented, clearArray: true);
                    rented = null;
                    // A custom pool may round up. Do not admit an oversized array
                    // into the sixteen-slot budget.
                    throw new InvalidOperationException("Relay pool must provide exact-size blocks.");
                }
                int count = 0;
                int limit = cacheEntireFile ? (int)Math.Min(BlockSize, _expectedLength - produced) : BlockSize;
                bool eof = false;
                while (count < limit)
                {
                    int read = await _source.ReadAsync(rented.AsMemory(count, limit - count), _stop.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        eof = true;
                        break;
                    }
                    count += read;
                    produced += read;
                    if (_expectedLength > 0 && produced > _expectedLength)
                    {
                        throw LengthMismatch(produced);
                    }
                }
                if (cacheEntireFile && produced == _expectedLength)
                {
                    // Reuse the last block for the EOF probe: even at exactly 4 MiB
                    // there is no seventeenth rent or a second contiguous file copy.
                    // A non-EOF probe invalidates the cache, so overwriting is harmless.
                    int probeOffset = count == 0 ? 0 : count - 1;
                    byte saved = rented[probeOffset];
                    int extra = await _source.ReadAsync(rented.AsMemory(probeOffset, 1), _stop.Token).ConfigureAwait(false);
                    rented[probeOffset] = saved;
                    if (extra != 0)
                    {
                        throw LengthMismatch(produced + extra);
                    }
                    eof = true;
                }
                if (eof && _expectedLength > 0 && produced != _expectedLength)
                {
                    throw LengthMismatch(produced);
                }
                if (count > 0)
                {
                    _blocks.Writer.TryWrite(new Block(rented, count));
                    rented = null;
                    slotHeld = false;
                    if (!cacheEntireFile)
                    {
                        _ready.TrySetResult();
                    }
                }
                if (eof)
                {
                    IsEmpty = produced == 0;
                    _ready.TrySetResult();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _failure = ExceptionDispatchInfo.Capture(ex);
            _ready.TrySetException(ex);
        }
        finally
        {
            if (rented is not null)
            {
                _pool.Return(rented, clearArray: true);
            }
            if (slotHeld)
            {
                _slots.Release();
            }
            _blocks.Writer.TryComplete();
        }
    }

    private async ValueTask<bool> EnsureBlockAsync(CancellationToken cancellationToken)
    {
        if (_current is not null)
        {
            return true;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        while (await _blocks.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
        {
            if (_blocks.Reader.TryRead(out Block? block))
            {
                _current = block;
                _offset = 0;
                return true;
            }
        }
        _failure?.Throw();
        return false;
    }

    private int CopyCurrent(Span<byte> destination)
    {
        Block block = _current!;
        int count = Math.Min(destination.Length, block.Count - _offset);
        block.Buffer.AsSpan(_offset, count).CopyTo(destination);
        _offset += count;
        _consumed += count;
        if (_offset == block.Count)
        {
            _current = null;
            _pool.Return(block.Buffer, clearArray: true);
            _slots.Release();
        }
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }
        _stop.Token.ThrowIfCancellationRequested();
        return EnsureBlockAsync(default).AsTask().GetAwaiter().GetResult() ? CopyCurrent(buffer) : 0;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stop.Token.ThrowIfCancellationRequested();
        if (buffer.IsEmpty || !await EnsureBlockAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }
        return CopyCurrent(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    internal async Task VerifyCompleteAsync(CancellationToken cancellationToken)
    {
        _failure?.Throw();
        if (_expectedLength > 0 && _consumed != _expectedLength)
        {
            throw LengthMismatch(_consumed);
        }
        // Do not drain unread data on behalf of an uploader that returned early.
        // For unknown sizes, only a real EOF proves completion (stat zero does not).
        if (await EnsureBlockAsync(cancellationToken).ConfigureAwait(false))
        {
            throw LengthMismatch(_consumed);
        }
        await _producer.ConfigureAwait(false);
        _failure?.Throw();
    }

    internal async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }
        _stopped = true;
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            // Cancellation callbacks belong to the source and may themselves fail.
            // They must never bypass joining the producer or returning its blocks.
            try
            {
                await _producer.ConfigureAwait(false);
            }
            finally
            {
                if (_current is { } current)
                {
                    _current = null;
                    _pool.Return(current.Buffer, clearArray: true);
                    _slots.Release();
                }
                while (_blocks.Reader.TryRead(out Block? block))
                {
                    _pool.Return(block.Buffer, clearArray: true);
                    _slots.Release();
                }
                _slots.Dispose();
                _stop.Dispose();
            }
        }
    }

    private RemoteTransferLengthException LengthMismatch(long actual) => new(_path, _expectedLength, actual);
    private sealed record Block(byte[] Buffer, int Count);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _consumed; set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
