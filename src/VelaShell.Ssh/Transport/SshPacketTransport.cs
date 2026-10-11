// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §4.2  版本标识串交换(行式 IO)
//   RFC 4253 §6    二进制报文协议(帧式 IO)
//   RFC 4253 §7.3  SSH_MSG_NEWKEYS 之后切换密钥;两个方向**各自独立**切换
//   OpenSSH PROTOCOL 的 kex-strict-*-v00@openssh.com —— 切换后序号归零
//   行为规格:      velashell-docs/zh/ssh/spec/01-transport-framing.md §3、§4;velashell-docs/zh/ssh/spec/02-version-exchange.md

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Transport;

/// <summary>
/// 一条 SSH 传输：在字节流之上提供**行式**（版本交换）与**帧式**（二进制报文）两种读写。
/// </summary>
/// <remarks>
/// <para>
/// 它把 <see cref="Stream"/> 包成 <see cref="PipeReader"/> / <see cref="PipeWriter"/>，
/// 于是分帧是零拷贝的切片，而不是「先读进自己的缓冲再搬一次」。
/// 版本交换之后残留在缓冲里的字节自动归帧层 —— 这正是用 Pipelines 而不是
/// <c>Stream.ReadAsync</c> 的直接好处：不需要自己搬缓冲，也就不会搬错。
/// </para>
/// <para>
/// <b>收发是两条独立的路径</b>，各有各的密码套件与序号。
/// <c>SSH_MSG_NEWKEYS</c> 的两个方向互不等待（RFC 4253 §7.3）——
/// 把它们合并成一个「切换时刻」是一个常见错误，症状是快速链路上偶发的解密失败。
/// </para>
/// <para>
/// <b>不是线程安全的。</b>读由收包泵独占，写由发包泵独占，两者可以并发。
/// 释放可以与读写并发：哪一端还有调用在途，就由那次调用退出时收尾（<see cref="TransportSideLifetime"/>）。
/// </para>
/// </remarks>
internal sealed class SshPacketTransport : IAsyncDisposable
{
    /// <summary>版本标识串单行的最大字节数（含 CR LF）。RFC 4253 §4.2。</summary>
    public const int MaxIdentificationLineBytes = 255;

    private readonly Stream _stream;
    private readonly CountingStream _counting;
    private readonly bool _ownsStream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly ArrayBufferWriter<byte> _payloadBuffer = new(4096);

    private ISshCipherSuite _receiveSuite = new PlaintextCipherSuite();
    private ISshCipherSuite _sendSuite = new PlaintextCipherSuite();

    private ISshCompressor _receiveCompressor = NoCompression.Instance;
    private ISshCompressor _sendCompressor = NoCompression.Instance;

    /// <summary>压缩用的中转缓冲（压缩开着时才会用到）。</summary>
    /// <remarks>
    /// ⚠️ <b>两个方向必须各用一个，绝不能共用。</b>
    ///
    /// 收与发是**并发**的：接收循环在读，而 N 条通道的泵在写。共用一个
    /// <see cref="ArrayBufferWriter{T}"/> 的后果是：解压刚把载荷写进去、
    /// 还没等调用方用完，一个并发的 <see cref="WritePacket"/> 就
    /// <c>ResetWrittenCount()</c> 把它清了 —— 调用方拿到的是一段长度为 0
    /// 或者半截的载荷。
    ///
    /// 症状极具迷惑性：小载荷时撞不上（一发一收之间没有交错的机会），
    /// 数据量一上来就开始「收到 0 字节」或者「报文载荷为空，没有消息编号」，
    /// 而那两句话都指不到压缩这里。实测 1.9 KB 的载荷次次都过，
    /// 8 KB 的载荷次次都挂。
    /// </remarks>
    private readonly ArrayBufferWriter<byte> _sendCompressionBuffer = new(4096);

    /// <summary>解压用的中转缓冲。见 <see cref="_sendCompressionBuffer"/> 上的说明。</summary>
    private readonly ArrayBufferWriter<byte> _receiveCompressionBuffer = new(4096);
    private bool _disposed;

    /// <summary>
    /// 读端：<see cref="ReadPacketAsync"/>、<see cref="ReadRawLineAsync"/> 与换接收方向的套件 / 解压器。
    /// 收尾时释放接收方向的套件与解压器、完成 <see cref="_reader"/>（velashell-docs/zh/ssh/spec/01 §4「释放」）。
    /// </summary>
    private readonly TransportSideLifetime _readSide;

    /// <summary>写端：<see cref="WritePacket"/>、<see cref="FlushAsync"/>、<see cref="WriteLineAsync"/> 与换发送方向的套件 / 压缩器。</summary>
    private readonly TransportSideLifetime _writeSide;

    /// <summary>在给定的双工字节流上建立传输。</summary>
    /// <param name="stream">底层流，通常来自 <see cref="ISshTransportDialer"/>。</param>
    /// <param name="ownsStream">释放本对象时是否一并释放 <paramref name="stream"/>。</param>
    public SshPacketTransport(Stream stream, bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _ownsStream = ownsStream;

        // 套一层计数：**线上字节数与应用字节数不是一回事** ——
        // 前者含协议头、填充与 MAC，压缩打开之后两者还会差出好几倍。
        // 终端产品要在界面上显示「这条连接用了多少流量」，说的是前者。
        _counting = new CountingStream(stream);

        // leaveOpen: 由我们自己决定什么时候释放底层流。
        _reader = PipeReader.Create(_counting, new StreamPipeReaderOptions(
            bufferSize: 64 * 1024, minimumReadSize: 4096, leaveOpen: true));
        _writer = PipeWriter.Create(_counting, new StreamPipeWriterOptions(leaveOpen: true));

        _readSide = new TransportSideLifetime(this, CloseReadSide);
        _writeSide = new TransportSideLifetime(this, CloseWriteSide);
    }

    /// <summary>度量的 <c>host</c> 标签；<see langword="null"/>（单独用传输的测试）时不记度量。</summary>
    internal string? MetricsHost
    {
        get => _counting.MetricsHost;
        init
        {
            _counting.MetricsHost = value;
            _metricsHostTag = value is null ? null : Diagnostics.SshMetrics.HostTag(value);
        }
    }

    private readonly KeyValuePair<string, object?>? _metricsHostTag;

    /// <summary>线上收到的字节数（含协议头、填充与 MAC）。</summary>
    public long BytesReceived => _counting.BytesRead;

    /// <summary>线上发出的字节数（含协议头、填充与 MAC）。</summary>
    public long BytesSent => _counting.BytesWritten;

    /// <summary>收到的报文数。</summary>
    /// <remarks>
    /// 写在收包路径上、读在重协商的阈值监视与诊断里 —— 两个线程。64 位计数在 32 位平台上普通读写会撕裂，
    /// 读到半新半旧的值可能误触发或漏触发重协商；所以与字节数一样走 <see cref="Interlocked"/>（AGENTS 4.3）。
    /// </remarks>
    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

    /// <summary>发出的报文数（同 <see cref="PacketsReceived"/> 的说明）。</summary>
    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    private long _packetsReceived;
    private long _packetsSent;

    /// <summary>当前允许的最大 <c>packet_length</c>。认证成功后由会话放宽。</summary>
    public int MaxPacketLength { get; set; } = SshPacketFormat.PreAuthMaxPacketLength;

    /// <summary>
    /// 同一套密钥下，任一方向最多处理多少个报文。默认 2³²：再多一个，序号就回绕到这套密钥用过的值上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是<b>最后一道</b>保险，与重协商策略无关、关不掉。chacha20-poly1305 的 nonce 就是序号 ——
    /// 同一把钥下序号重复，密钥流与 Poly1305 的一次性密钥一起重用，报文可以被伪造；HMAC 套件则可以被重放
    /// （RFC 4344 §3.1：序号回绕之前必须换钥）。正常情况下会话在 2³¹ 处就主动重协商了，走不到这里。
    /// </para>
    /// <para>只有测试会把它调小。</para>
    /// </remarks>
    internal long MaxPacketsPerKey { get; init; } = 1L << 32;

    private long _sendPacketsUnderKey;
    private long _receivePacketsUnderKey;

    /// <summary>当前这套密钥下已经发了多少个报文。</summary>
    internal long SendPacketsUnderKey => Volatile.Read(ref _sendPacketsUnderKey);

    /// <summary>当前这套密钥下已经收了多少个报文。</summary>
    internal long ReceivePacketsUnderKey => Volatile.Read(ref _receivePacketsUnderKey);

    /// <summary>接收方向的当前序号（下一个要收的报文用它）。</summary>
    public uint ReceiveSequenceNumber { get; private set; }

    /// <summary>报文旁路（<see cref="Diagnostics.IPacketTap"/>）；<see langword="null"/> 时不调。</summary>
    internal Diagnostics.IPacketTap? PacketTap { get; set; }

    /// <summary>旁路拿不拿得到载荷（认证报文 50–79 无论如何都不给）。</summary>
    internal bool AllowPacketTapPayload { get; set; }

    /// <summary>把一个报文交给旁路。旁路抛的异常吞掉：旁路出错不该弄坏连接。</summary>
    private void Tap(Diagnostics.IPacketTap tap, Diagnostics.PacketDirection direction, ReadOnlySpan<byte> payload, uint sequence)
    {
        byte number = payload.IsEmpty ? (byte)0 : payload[0];

        // 通道消息（CHANNEL_OPEN_CONFIRMATION 91 … CHANNEL_FAILURE 100）紧跟着就是接收方通道号。
        uint? channel = number is >= 91 and <= 100 && payload.Length >= 5
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload[1..5])
            : null;

        // 〔velashell-docs/zh/ssh/spec/08 §9 第 3 条〕认证报文（50–79）的载荷永远不给，没有开关。
        bool includePayload = AllowPacketTapPayload && number is not (>= 50 and <= 79);
        try
        {
            tap.OnPacket(new Diagnostics.PacketTapRecord(
                direction, number, payload.Length, sequence, channel, includePayload ? payload : default));
        }
        catch (Exception)
        {
            // 旁路是使用者的代码：它出错不该让连接断开。
        }
    }

    /// <summary>发送方向的当前序号（下一个要发的报文用它）。</summary>
    public uint SendSequenceNumber { get; private set; }

    // ------------------------------------------------------------ 行式（握手）

    /// <summary>
    /// 读一行以 <c>\n</c> 结尾的文本（版本交换阶段用）。
    /// </summary>
    /// <returns>去掉行尾的那一行；对端干净关闭时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 〔互操作，velashell-docs/zh/ssh/spec/02 §5〕行尾按 <c>\n</c> 找，然后**顺带去掉前面的 <c>\r</c>**。
    /// RFC 要求 <c>\r\n</c>，但部分嵌入式 SSH 实现（含若干型号网络设备的管理口）只发 <c>\n</c>。
    /// 严格要求 <c>\r\n</c> 会让这些设备完全连不上，而接受它没有任何安全代价 ——
    /// 标识串不参与任何信任判定，且进交换哈希的是「去掉行尾之后」的内容，两种写法结果相同。
    /// </para>
    /// <para>
    /// 多读的字节留在 <see cref="PipeReader"/> 里，自动归后续的帧式读取。
    /// </para>
    /// </remarks>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        byte[]? line = await ReadRawLineAsync(cancellationToken).ConfigureAwait(false);

        // 标识串按 RFC 是 US-ASCII，但前导行（banner）可能是任意 UTF-8。
        // 用宽容的 UTF-8 解码：这里的文本只用于展示与判断开头，进交换哈希的是原始字节（见 ReadRawLineAsync）。
        return line is null ? null : Encoding.UTF8.GetString(line);
    }

    /// <summary>
    /// 同 <see cref="ReadLineAsync"/>，但交回<b>原始字节</b>（去掉行尾的 <c>\r\n</c> / <c>\n</c>）。
    /// </summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/02 §六〕服务端标识串进交换哈希（<c>V_S</c>）的必须是它发来的那串字节。
    /// 先解成 string 再编回去的话，注释段里一个不是合法 UTF-8 的字节（Latin-1 的 ©）会变成 <c>EF BF BD</c>，
    /// 两边算出的交换哈希不同 —— 验签失败、报「主机密钥被拒、可能有中间人」，一个兼容性问题被报成了安全事件。
    /// </remarks>
    internal async ValueTask<byte[]?> ReadRawLineAsync(CancellationToken cancellationToken = default)
    {
        _readSide.Enter();
        try
        {
            while (true)
            {
                ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfCanceledByDispose(result);
                ReadOnlySequence<byte> buffer = result.Buffer;

                SequencePosition? newline = buffer.PositionOf((byte)'\n');
                if (newline is not null)
                {
                    // 〔RFC 4253 §4.2〕255 字节**含行尾**。line 到 \n 为止（不含 \n），所以上限是 254 ——
                    // 曾经写成 > 255，含 CRLF 共 256 字节的行能过。
                    ReadOnlySequence<byte> line = buffer.Slice(0, newline.Value);
                    if (line.Length + 1 > MaxIdentificationLineBytes)
                    {
                        _reader.AdvanceTo(buffer.Start, buffer.End);
                        throw new SshFrameFormatException(
                            $"标识串行（含行尾）超过 {MaxIdentificationLineBytes} 字节（收到 {line.Length + 1}）。");
                    }

                    byte[] raw = StripCarriageReturn(line);
                    _reader.AdvanceTo(buffer.GetPosition(1, newline.Value));
                    return raw;
                }

                if (buffer.Length >= MaxIdentificationLineBytes)
                {
                    _reader.AdvanceTo(buffer.Start, buffer.End);
                    throw new SshFrameFormatException(
                        $"标识串行超过 {MaxIdentificationLineBytes} 字节仍未见换行。");
                }

                if (result.IsCompleted)
                {
                    _reader.AdvanceTo(buffer.Start, buffer.End);
                    return null;
                }

                _reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        finally
        {
            _readSide.Exit();
        }
    }

    /// <summary>挂起的读被取消了 —— 只有释放会这么做（<see cref="DisposeAsync"/>），这次读就以「已释放」退出。</summary>
    private void ThrowIfCanceledByDispose(in ReadResult result)
    {
        if (result.IsCanceled)
        {
            _reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            throw new ObjectDisposedException(nameof(SshPacketTransport), "传输已释放，挂起的读被取消。");
        }
    }

    /// <summary>写一行文本并附加 <c>\r\n</c>，随后刷出。</summary>
    /// <remarks>标识串必须是可打印 US-ASCII（RFC 4253 §4.2）。</remarks>
    public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        _writeSide.Enter();
        try
        {
            ArgumentNullException.ThrowIfNull(line);

            int byteCount = Encoding.ASCII.GetByteCount(line) + 2;
            if (byteCount > MaxIdentificationLineBytes)
            {
                throw new ArgumentException(
                    $"标识串含行尾不得超过 {MaxIdentificationLineBytes} 字节。", nameof(line));
            }

            Span<byte> span = _writer.GetSpan(byteCount)[..byteCount];
            int written = Encoding.ASCII.GetBytes(line, span);
            span[written] = (byte)'\r';
            span[written + 1] = (byte)'\n';
            _writer.Advance(byteCount);

            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeSide.Exit();
        }
    }

    /// <summary>一行的原始字节，顺带去掉行尾的 CR（见 <see cref="ReadLineAsync"/> 的互操作说明）。</summary>
    private static byte[] StripCarriageReturn(ReadOnlySequence<byte> line)
    {
        byte[] bytes = line.ToArray();
        return bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes[..^1] : bytes;
    }

    // ------------------------------------------------------------ 帧式（稳态）

    /// <summary>
    /// 读一个完整报文。
    /// </summary>
    /// <returns>
    /// 报文的载荷。<see cref="SshInboundPacket.IsEndOfStream"/> 为真表示对端干净关闭。
    /// </returns>
    /// <remarks>
    /// <b>返回的内存只在下一次 <see cref="ReadPacketAsync"/> 之前有效。</b>
    /// 要留着就自己复制 —— 这是 Pipelines 的一贯契约，也是零拷贝的代价。
    /// </remarks>
    public async ValueTask<SshInboundPacket> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        _readSide.Enter();
        try
        {
            while (true)
            {
                ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfCanceledByDispose(result);
                ReadOnlySequence<byte> buffer = result.Buffer;

                if (!buffer.IsEmpty)
                {
                    if (_receivePacketsUnderKey >= MaxPacketsPerKey)
                    {
                        _reader.AdvanceTo(buffer.Start, buffer.End);
                        throw SequenceExhausted("接收");
                    }

                    _payloadBuffer.ResetWrittenCount();
                    SshOpenStatus status;
                    try
                    {
                        status = _receiveSuite.TryOpen(
                            buffer, ReceiveSequenceNumber, MaxPacketLength, _payloadBuffer, out long consumed);

                        if (status == SshOpenStatus.Opened)
                        {
                            _reader.AdvanceTo(buffer.GetPosition(consumed));
                            // 序号在**成功取出一帧之后**才推进，与密码套件的约定一致。
                            ReceiveSequenceNumber = unchecked(ReceiveSequenceNumber + 1);
                            Interlocked.Increment(ref _packetsReceived);
                            Interlocked.Increment(ref _receivePacketsUnderKey);
                            if (_metricsHostTag is { } hostTag)
                            {
                                Diagnostics.SshMetrics.Packets.Add(1, hostTag, Diagnostics.SshMetrics.DirectionReceived);
                            }
                        }
                    }
                    catch
                    {
                        // 解析失败时也要把 reader 交还，否则 Dispose 会挂住。
                        _reader.AdvanceTo(buffer.Start, buffer.End);
                        throw;
                    }

                    if (status == SshOpenStatus.Opened)
                    {
                        // 解密在前，解压在后 —— 顺序反了什么都对不上（发送侧是先压后加密）。
                        //
                        // ⚠️ 解压放在上面那个 try **外面**：reader 在那里已经 AdvanceTo 过了。
                        //    曾经放在里面，解压一失败（压缩炸弹、坏的 zlib 流），catch 就再 AdvanceTo 一次，
                        //    真正的原因被「PipeReader 已经越过这个位置」的 InvalidOperationException 盖掉。
                        ReadOnlyMemory<byte> payload = _receiveCompressor.IsActive
                            ? DecompressPayload(_payloadBuffer.WrittenMemory)
                            : _payloadBuffer.WrittenMemory;

                        // 〔velashell-docs/zh/ssh/spec/01 §5〕载荷为空的报文在协议里不存在（每个报文至少有一个消息编号字节）：
                        // 在帧层就拒收，不让它带着一个「取不出消息编号」的载荷往上走。
                        if (payload.IsEmpty)
                        {
                            throw new SshFrameFormatException("报文载荷为空，没有消息编号。");
                        }
                        if (PacketTap is { } tap)
                        {
                            Tap(tap, Diagnostics.PacketDirection.Inbound, payload.Span, unchecked(ReceiveSequenceNumber - 1));
                        }
                        return new SshInboundPacket(payload);
                    }
                }

                if (result.IsCompleted)
                {
                    _reader.AdvanceTo(buffer.Start, buffer.End);
                    if (buffer.IsEmpty)
                    {
                        return SshInboundPacket.EndOfStream;   // 帧边界上的干净关闭
                    }
                    throw new SshFrameFormatException("对端在报文中途关闭了连接。") { PeerClosedMidPacket = true };
                }

                // 数据不足：consumed = start（什么都没消费）、examined = end（已经看到这里）。
                // 两个参数给错会让 PipeReader 要么丢数据，要么不再等新数据（表现为卡死）。
                _reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        finally
        {
            _readSide.Exit();
        }
    }

    /// <summary>
    /// 把一个载荷封装成报文写进发送缓冲，<b>不刷出</b>。
    /// </summary>
    /// <remarks>
    /// 不刷出是刻意的：调用方连写若干帧再 <see cref="FlushAsync"/> 一次，
    /// 把「每帧一次系统调用」压成「一轮一次」。SFTP 满管线时这是 64 → 1 的差别
    /// （velashell-docs/zh/ssh/spec/01 §4）。
    /// </remarks>
    public void WritePacket(ReadOnlySpan<byte> payload)
    {
        if (PacketTap is { } tap)
        {
            Tap(tap, Diagnostics.PacketDirection.Outbound, payload, SendSequenceNumber);
        }

        _writeSide.Enter();
        try
        {
            if (_sendPacketsUnderKey >= MaxPacketsPerKey)
            {
                throw SequenceExhausted("发送");
            }

            // **先压缩，再加密。**反过来的话压缩器面对的是密文 ——
            // 密文没有可压缩性，压出来只会更长，而且会泄漏明文的统计特征。
            if (_sendCompressor.IsActive)
            {
                _sendCompressionBuffer.ResetWrittenCount();
                _sendCompressor.Compress(payload, _sendCompressionBuffer);
                _sendSuite.Seal(_sendCompressionBuffer.WrittenSpan, SendSequenceNumber, _writer);
            }
            else
            {
                _sendSuite.Seal(payload, SendSequenceNumber, _writer);
            }

            SendSequenceNumber = unchecked(SendSequenceNumber + 1);
            Interlocked.Increment(ref _packetsSent);
            Interlocked.Increment(ref _sendPacketsUnderKey);
            if (_metricsHostTag is { } hostTag)
            {
                Diagnostics.SshMetrics.Packets.Add(1, hostTag, Diagnostics.SshMetrics.DirectionSent);
            }
        }
        finally
        {
            _writeSide.Exit();
        }
    }

    /// <summary>同一套密钥下的序号要用完了，重协商却没有完成：宁可断开，也不让序号回绕。</summary>
    private SshFrameFormatException SequenceExhausted(string direction) =>
        new($"{direction}方向在同一套密钥下已经处理了 {MaxPacketsPerKey} 个报文，重协商却没有完成 —— " +
            "再处理一个序号就会回绕（nonce 重用），为此断开连接。");

    /// <summary>把刚解密出来的载荷解压。</summary>
    private ReadOnlyMemory<byte> DecompressPayload(ReadOnlyMemory<byte> compressed)
    {
        _receiveCompressionBuffer.ResetWrittenCount();
        _receiveCompressor.Decompress(
            new ReadOnlySequence<byte>(compressed), _receiveCompressionBuffer, MaxPacketLength);
        return _receiveCompressionBuffer.WrittenMemory;
    }

    /// <summary>换掉发送方向的压缩器。</summary>
    /// <remarks>
    /// <c>zlib@openssh.com</c> 要等认证成功之后才调用它 ——
    /// 认证之前的报文里有密码与公钥，而压缩会让密文长度泄漏明文的可压缩性。
    /// </remarks>
    public void SetSendCompressor(ISshCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(compressor);
        _writeSide.Enter();
        try
        {
            if (!ReferenceEquals(_sendCompressor, NoCompression.Instance))
            {
                _sendCompressor.Dispose();
            }
            _sendCompressor = compressor;
        }
        finally
        {
            _writeSide.Exit();
        }
    }

    /// <summary>换掉接收方向的压缩器。</summary>
    public void SetReceiveCompressor(ISshCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(compressor);
        _readSide.Enter();
        try
        {
            if (!ReferenceEquals(_receiveCompressor, NoCompression.Instance))
            {
                _receiveCompressor.Dispose();
            }
            _receiveCompressor = compressor;
        }
        finally
        {
            _readSide.Exit();
        }
    }

    /// <summary>把已写入的报文刷到底层流。</summary>
    /// <remarks>刷到一半时传输被释放：这次刷出被取消、照常返回（<see cref="FlushResult.IsCanceled"/>），下一次写报「已释放」。</remarks>
    public async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        _writeSide.Enter();
        try
        {
            return await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeSide.Exit();
        }
    }

    // ------------------------------------------------------------ 密钥切换

    /// <summary>
    /// 切换**接收**方向的密码套件（收到对端的 <c>SSH_MSG_NEWKEYS</c> 之后）。
    /// </summary>
    /// <param name="suite">新套件。本对象接管它的生命周期。</param>
    /// <param name="resetSequenceNumber">
    /// 启用严格 KEX 时为 <see langword="true"/> —— 此时序号归零
    /// （OpenSSH <c>kex-strict-*-v00@openssh.com</c>，Terrapin 缓解）。
    /// </param>
    public void SetReceiveCipherSuite(ISshCipherSuite suite, bool resetSequenceNumber)
    {
        ArgumentNullException.ThrowIfNull(suite);
        _readSide.Enter();
        try
        {
            _receiveSuite.Dispose();
            _receiveSuite = suite;
            Volatile.Write(ref _receivePacketsUnderKey, 0);
            if (resetSequenceNumber)
            {
                ReceiveSequenceNumber = 0;
            }
        }
        finally
        {
            _readSide.Exit();
        }
    }

    /// <summary>
    /// 切换**发送**方向的密码套件（发出 <c>SSH_MSG_NEWKEYS</c> 之后）。
    /// </summary>
    /// <remarks>
    /// 与接收方向**互不等待**：我们发出 NEWKEYS 之后发的下一个报文就用新密钥，
    /// 而收的方向要等对端的 NEWKEYS 到达（RFC 4253 §7.3）。
    /// </remarks>
    public void SetSendCipherSuite(ISshCipherSuite suite, bool resetSequenceNumber)
    {
        ArgumentNullException.ThrowIfNull(suite);
        _writeSide.Enter();
        try
        {
            _sendSuite.Dispose();
            _sendSuite = suite;
            Volatile.Write(ref _sendPacketsUnderKey, 0);
            if (resetSequenceNumber)
            {
                SendSequenceNumber = 0;
            }
        }
        finally
        {
            _writeSide.Exit();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 哪一端还有调用在途，这里只取消它挂起的读 / 刷出、<b>不等</b>它；那一端由在途的调用退出时收尾
    /// （velashell-docs/zh/ssh/spec/01 §4「释放」）。不等是因为底层流不一定理会取消，而释放常跑在错误路径的收尾里。
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // ⚠️ 曾经在这里当场完成 reader / writer。两端的缓冲是从共享内存池租的、完成时还回去 ——
        //    另一个任务若还停在读里，底层流那次读手里攥着那块 64 KB，数据一到照写，写进的是别人刚租走的数组
        //    （宿主 CI 上一条握手用例读到被截短的 KEXINIT：上一条用例的服务端还在等 NEWKEYS，那次读写了进来）。
        //    写端同理：刷到一半时还回去，底层流照样从里面读 —— 发出去的就是别人的数据。
        //    接收套件也一样：当场清零密钥，在途的那次解密会算出一个假的 MAC 错误。
        if (!_readSide.Close())
        {
            CancelQuietly(_reader.CancelPendingRead);
        }
        if (!_writeSide.Close())
        {
            CancelQuietly(_writer.CancelPendingFlush);
        }

        if (_ownsStream)
        {
            // 关掉底层流：不理会取消的读写（Windows 上 ProxyCommand 的匿名管道）也随之结束，它们的收尾才轮得到。
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 释放路径不抛，见 CloseReadSide。
            }
        }
    }

    /// <summary>取消挂起的读 / 刷出。在途的调用可能恰好刚退出、已经收了尾 —— 那就没什么可取消的了。</summary>
    private static void CancelQuietly(Action cancel)
    {
        try
        {
            cancel();
        }
        catch (Exception)
        {
            // 同上。
        }
    }

    /// <summary>读端收尾：接收方向的套件与解压器，以及 <see cref="_reader"/>（它的缓冲还回内存池）。</summary>
    private void CloseReadSide()
    {
        _receiveSuite.Dispose();
        if (!ReferenceEquals(_receiveCompressor, NoCompression.Instance))
        {
            _receiveCompressor.Dispose();
        }

        // 释放路径**不抛异常**：它通常跑在某条错误路径的收尾里（或者是在途的那次读写的 finally），
        // 这时冒出来的次要异常只会盖住真正的失败原因。
        try
        {
            _reader.Complete();
        }
        catch (Exception)
        {
            // 同上。
        }
    }

    /// <summary>写端收尾：发送方向的套件与压缩器，以及 <see cref="_writer"/>。</summary>
    private void CloseWriteSide()
    {
        _sendSuite.Dispose();
        if (!ReferenceEquals(_sendCompressor, NoCompression.Instance))
        {
            _sendCompressor.Dispose();
        }

        try
        {
            // ⚠️ **带着异常完成，不再往流上写。**不带异常的完成会把缓冲里剩下的字节刷出去 ——
            //    而剩下的只可能是一次被取消的 flush 丢下的（正常路径上每一帧都显式 flush 过）。
            //    那次 flush 被取消，多半正是因为对端不读了（TCP 零窗口、半开的链路）：
            //    这里再去写，就是在释放路径上等一个永远不来的对端，直到 TCP 自己放弃（十几分钟）。
            _writer.Complete(new ObjectDisposedException(nameof(SshPacketTransport)));
        }
        catch (Exception)
        {
            // 同 CloseReadSide。
        }
    }
}

/// <summary>数一数流上过了多少字节。</summary>
/// <remarks>
/// 它<b>不改变任何语义</b>，只是在读写路径上各加一次 <c>Interlocked.Add</c>。
/// 放在这一层而不是更上面：只有这里看得到真正上线的字节。
/// </remarks>
internal sealed class CountingStream(Stream inner) : Stream
{
    private long _bytesRead;
    private long _bytesWritten;

    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>度量的 <c>host</c> 标签；<see langword="null"/> 时不记度量（<c>velashell.ssh.bytes</c>）。</summary>
    public string? MetricsHost { get; set; }

    private void Meter(int bytes, KeyValuePair<string, object?> direction)
    {
        if (bytes > 0 && MetricsHost is { } host && Diagnostics.SshMetrics.Bytes.Enabled)
        {
            Diagnostics.SshMetrics.Bytes.Add(bytes, Diagnostics.SshMetrics.HostTag(host), direction);
        }
    }

    public override bool CanRead => inner.CanRead;

    public override bool CanWrite => inner.CanWrite;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _bytesRead, read);
        Meter(read, Diagnostics.SshMetrics.DirectionReceived);
        return read;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _bytesWritten, buffer.Length);
        Meter(buffer.Length, Diagnostics.SshMetrics.DirectionSent);
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Interlocked.Add(ref _bytesRead, read);
        Meter(read, Diagnostics.SshMetrics.DirectionReceived);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Interlocked.Add(ref _bytesWritten, count);
        Meter(count, Diagnostics.SshMetrics.DirectionSent);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) =>
        // **不释放内层流** —— 谁拥有它由 SshPacketTransport 决定。
        base.Dispose(disposing);
}

/// <summary>一个收到的报文。</summary>
/// <remarks>
/// <b><see cref="Payload"/> 只在下一次读取之前有效。</b>
/// 要留着就自己复制 —— 零拷贝的代价就在这里。
/// </remarks>
internal readonly struct SshInboundPacket
{
    internal SshInboundPacket(ReadOnlyMemory<byte> payload)
    {
        Payload = payload;
        IsEndOfStream = false;
    }

    private SshInboundPacket(bool endOfStream)
    {
        Payload = default;
        IsEndOfStream = endOfStream;
    }

    /// <summary>对端干净关闭时返回的哨兵。</summary>
    public static SshInboundPacket EndOfStream { get; } = new(endOfStream: true);

    /// <summary>报文载荷，第一个字节是消息编号。</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>是否表示对端已在帧边界上干净关闭。</summary>
    public bool IsEndOfStream { get; }

    /// <summary>
    /// 消息编号（载荷的第一个字节）。
    /// </summary>
    /// <exception cref="SshFrameFormatException">载荷为空。</exception>
    /// <remarks>
    /// 载荷为空的报文在协议里不存在 —— 每个报文至少有一个消息编号字节
    /// （velashell-docs/zh/ssh/spec/01 §5）。
    /// </remarks>
    public SshMessageNumber MessageNumber => Payload.Length > 0
        ? (SshMessageNumber)Payload.Span[0]
        : throw new SshFrameFormatException("报文载荷为空，没有消息编号。");
}
