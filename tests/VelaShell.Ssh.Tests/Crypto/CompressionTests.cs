// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: RFC 4253 §6.2;OpenSSH PROTOCOL 的 zlib@openssh.com

using System.Buffers;
using System.IO.Compression;
using System.Text;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Crypto;

[TestClass]
[TestCategory("Crypto")]
public sealed class CompressionTests
{
    private static byte[] RoundTrip(ISshCompressor sender, ISshCompressor receiver, byte[] payload)
    {
        ArrayBufferWriter<byte> compressed = new();
        sender.Compress(payload, compressed);

        ArrayBufferWriter<byte> decompressed = new();
        receiver.Decompress(
            new ReadOnlySequence<byte>(compressed.WrittenMemory), decompressed, 16 * 1024 * 1024);

        return decompressed.WrittenSpan.ToArray();
    }

    [TestMethod]
    public void 压了能解回来()
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        byte[] payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("重复的内容 ", 200)));
        byte[] back = RoundTrip(sender, receiver, payload);

        Assert.AreSequenceEqual(payload, back);
    }

    [TestMethod]
    public void 字典跨报文保留所以后面的报文越压越小()
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        byte[] payload = Encoding.UTF8.GetBytes("GET /api/status HTTP/1.1\r\nHost: example.com\r\n\r\n");

        int firstSize = 0;
        int lastSize = 0;

        for (int i = 0; i < 10; i++)
        {
            ArrayBufferWriter<byte> compressed = new();
            sender.Compress(payload, compressed);

            // ⚠️ 收方**每一个报文都要跟着解**，两边的字典才对得上。
            //    漏解一个，后面所有报文在收方看来都是从流中间开始的 ——
            //    zlib 会直接报「unknown compression method」。
            ArrayBufferWriter<byte> sink = new();
            receiver.Decompress(new ReadOnlySequence<byte>(compressed.WrittenMemory), sink, 1 << 20);
            Assert.AreSequenceEqual(payload, sink.WrittenSpan.ToArray(), $"第 {i} 个");

            if (i == 0)
            {
                firstSize = compressed.WrittenCount;
            }
            lastSize = compressed.WrittenCount;
        }

        // **这就是压缩率的全部来源。**每个报文各压各的话，
        // SSH 那种几十字节的小报文几乎压不动 —— 共用一本字典才有意义。
        Assert.IsLessThan(
            firstSize / 2, lastSize,
            $"第 10 个相同报文应当明显更小：首个 {firstSize} 字节，第 10 个 {lastSize} 字节");
    }

    [TestMethod]
    public void 连续多个不同报文都能对上()
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        for (int i = 0; i < 50; i++)
        {
            byte[] payload = Encoding.UTF8.GetBytes($"第 {i} 个报文，内容长度不一 {new string('x', i * 7)}");
            byte[] back = RoundTrip(sender, receiver, payload);

            // 一个报文对不上，后面全乱 —— 字典是跨报文的。
            Assert.AreSequenceEqual(payload, back, $"第 {i} 个");
        }
    }

    [TestMethod]
    public void 空载荷也能往返()
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        byte[] back = RoundTrip(sender, receiver, []);
        Assert.IsEmpty(back);
    }

    [TestMethod]
    public void 大载荷能往返()
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        byte[] payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);   // 随机数据压不动，走的是「压完更长」那条路

        byte[] back = RoundTrip(sender, receiver, payload);
        Assert.AreSequenceEqual(payload, back);
    }

    [TestMethod]
    public void 压缩炸弹被上限挡住()
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        // 4 MiB 的零 —— 压完只有几 KiB。
        byte[] bomb = new byte[4 * 1024 * 1024];

        ArrayBufferWriter<byte> compressed = new();
        sender.Compress(bomb, compressed);

        Assert.IsLessThan(64 * 1024, compressed.WrittenCount, "前提：它确实压得很小");

        ArrayBufferWriter<byte> output = new();

        // 上限要在**写出之前**检查 —— 等写完再看就已经把内存吃掉了。
        SshProtocolException error = Assert.ThrowsExactly<SshProtocolException>(
            () => receiver.Decompress(
                new ReadOnlySequence<byte>(compressed.WrittenMemory), output, maxOutputLength: 64 * 1024));

        Assert.Contains("压缩炸弹", error.Message);
    }

    /// <summary>
    /// 〔spec/01 §六〕SSH 的压缩流不会结束。对端在载荷里写了 zlib 的流结束标记：<b>这一报文</b>当场报错，
    /// 而不是收下它、到下一帧才因为解码器已经结束报一句看不出缘由的「解压失败」。
    /// </summary>
    [TestMethod]
    public void 对端结束了zlib流就在那一报文当场报错()
    {
        using ZlibCompressor receiver = new();
        using ZLibEncoder peer = new(6);
        byte[] buffer = new byte[4096];

        // 第一个报文照常：压完同步 flush。
        peer.Compress("第一个报文"u8, buffer, out _, out int written, isFinalBlock: false);
        peer.Flush(buffer.AsSpan(written), out int flushed);
        ArrayBufferWriter<byte> first = new();
        receiver.Decompress(new ReadOnlySequence<byte>(buffer.AsMemory(0, written + flushed)), first, 64 * 1024);
        Assert.AreEqual("第一个报文", Encoding.UTF8.GetString(first.WrittenSpan), "前提：同步 flush 的报文照常解开");

        // 第二个报文带着流结束标记。
        peer.Compress("第二个报文"u8, buffer, out _, out int final, isFinalBlock: true);
        SshProtocolException error = Assert.ThrowsExactly<SshProtocolException>(
            () => receiver.Decompress(new ReadOnlySequence<byte>(buffer.AsMemory(0, final)), new ArrayBufferWriter<byte>(), 64 * 1024));

        Assert.Contains("流结束标记", error.Message);
    }

    /// <summary>
    /// 输入喂完了、解码器手里还压着没吐出来的输出：要接着取，直到它不再有输出。
    /// </summary>
    /// <remarks>
    /// <para>
    /// OpenSSH 每个报文之后做的是 partial flush：报文末尾只有几个位的空块，不像同步 flush 那样以字节对齐的空存储块收尾。
    /// 报文最后一个匹配的编码一读完，整段输入就可能都进了解码器 —— 而那个匹配还没复制完，输出缓冲恰好在它中间用满。
    /// 曾经「输入吃完」就收工，压着的那几个字节留到下一个报文开头才吐出来：这一报文短了几个字节，下一报文全部错位。
    /// 线上的样子是 X11 转发的 GTK 程序一画大窗口（经压缩的 32 KiB 通道数据），整条连接以「读取 string 需要 32768 字节，
    /// 只剩 32759 字节」断开 —— 同一连接上的终端一起陪葬。
    /// </para>
    /// <para>
    /// 同步 flush 的报文以 <c>00 00 FF FF</c> 结尾，输出用满时这几个字节还没被读走，所以拿我们自己的压缩器往返测不出来。
    /// 这里照 RFC 1951 手搭一段固定哈夫曼编码的流，把最后一个匹配的距离码对齐到最后一个字节上。
    /// </para>
    /// </remarks>
    [TestMethod]
    public void 输入吃完时解码器还压着输出也要吐干净()
    {
        using ZlibCompressor receiver = new();

        // 第一个报文：3 个字面量 0xFF（9 位码）、127 个「长 258、距 1」的匹配、块尾 —— zlib 头之后共 1688 位，正好 211 字节，
        // 最后一个匹配的距离码的最后一位落在最后一个字节上。解出 32769 字节：按 16 KiB 一块取，第二块在最后一个匹配中间用满，
        // 而那时整段输入已经读完。
        DeflateBits first = new();
        first.Raw(0x78, 8);
        first.Raw(0x01, 8);           // zlib 头：deflate、32 KiB 窗口
        first.Raw(0, 1);              // BFINAL = 0：流不结束
        first.Raw(1, 2);              // BTYPE = 01：固定哈夫曼
        for (int i = 0; i < 3; i++)
        {
            first.Code(0x190 + (0xFF - 144), 9);
        }
        for (int i = 0; i < 127; i++)
        {
            first.Code(0xC0 + (285 - 280), 8);   // 长度 258
            first.Code(0, 5);                    // 距离 1
        }
        first.Code(0, 7);                        // 块尾（256）

        ArrayBufferWriter<byte> firstOut = new();
        receiver.Decompress(new ReadOnlySequence<byte>(first.Take()), firstOut, 64 * 1024);
        Assert.AreEqual(3 + (258 * 127), firstOut.WrittenCount, "一个报文的载荷要整个解出来，不能把压着的尾巴留给下一个报文");
        Assert.IsFalse(firstOut.WrittenSpan.ContainsAnyExcept((byte)0xFF));

        // 第二个报文接着上一个的位置：一个固定哈夫曼块（字面量 'z'、块尾），再以空的存储块收尾。
        DeflateBits second = new();
        second.Raw(0, 1);
        second.Raw(1, 2);
        second.Code(0x30 + 'z', 8);
        second.Code(0, 7);
        second.Raw(0, 1);
        second.Raw(0, 2);             // BTYPE = 00：存储块，之后对齐到字节
        second.Align();
        second.Raw(0x0000, 16);
        second.Raw(0xFFFF, 16);
        ArrayBufferWriter<byte> secondOut = new();
        receiver.Decompress(new ReadOnlySequence<byte>(second.Take()), secondOut, 64 * 1024);
        Assert.AreEqual("z", Encoding.ASCII.GetString(secondOut.WrittenSpan), "下一个报文从它自己的第一个字节开始");
    }

    /// <summary>
    /// 解出来的长度恰好用满输出块（16 KiB 的整数倍）、或恰好等于上限：解码器最后报的是「缓冲不够」，
    /// 再取一次什么都没有 —— 照常收下，不当成压缩炸弹，也不当成数据坏了。
    /// </summary>
    [TestMethod]
    [DataRow(16 * 1024 - 1)]
    [DataRow(16 * 1024)]
    [DataRow(16 * 1024 + 1)]
    [DataRow(32 * 1024)]
    [DataRow(64 * 1024)]
    public void 解出的长度恰好用满输出块或上限也照常(int length)
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        for (int round = 0; round < 3; round++)
        {
            byte[] payload = new byte[length];
            payload.AsSpan().Fill((byte)('a' + round));

            ArrayBufferWriter<byte> compressed = new();
            sender.Compress(payload, compressed);
            ArrayBufferWriter<byte> back = new();
            receiver.Decompress(new ReadOnlySequence<byte>(compressed.WrittenMemory), back, maxOutputLength: length);

            Assert.AreSequenceEqual(payload, back.WrittenSpan.ToArray(), $"第 {round} 个");
        }
    }

    /// <summary>照 RFC 1951 §3.1.1 拼位流：数值从低位起写，哈夫曼码从高位起写。</summary>
    private sealed class DeflateBits
    {
        private readonly List<byte> _bytes = [];
        private int _used = 8;

        public void Raw(uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Bit((value >> i) & 1);
            }
        }

        public void Code(int code, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                Bit((uint)(code >> i) & 1);
            }
        }

        public void Align() => _used = 8;

        public byte[] Take()
        {
            Assert.AreEqual(8, _used, "前提：报文在字节边界上结束");
            return [.. _bytes];
        }

        private void Bit(uint bit)
        {
            if (_used == 8)
            {
                _bytes.Add(0);
                _used = 0;
            }
            _bytes[^1] |= (byte)(bit << _used);
            _used++;
        }
    }

    [TestMethod]
    public void 直通压缩器什么都不做()
    {
        NoCompression none = NoCompression.Instance;
        Assert.IsFalse(none.IsActive);

        byte[] payload = Encoding.UTF8.GetBytes("原样");
        byte[] back = RoundTrip(none, none, payload);

        Assert.AreSequenceEqual(payload, back);
    }

    [TestMethod]
    public void 算法名的分类()
    {
        Assert.IsTrue(SshCompressorFactory.IsDelayed(SshAlgorithmNames.ZlibOpenSsh));
        Assert.IsFalse(SshCompressorFactory.IsDelayed(SshAlgorithmNames.None));

        Assert.IsInstanceOfType<ZlibCompressor>(
            SshCompressorFactory.Create(SshAlgorithmNames.ZlibOpenSsh));
        Assert.IsInstanceOfType<NoCompression>(
            SshCompressorFactory.Create(SshAlgorithmNames.None));

        // 裸 zlib 不实现（认证报文会进压缩流）—— 要响亮地失败，不能悄悄当成不压缩。
        _ = Assert.ThrowsExactly<Ssh.Crypto.Kex.SshKeyExchangeException>(
            () => SshCompressorFactory.Create("zlib"));
    }

    [TestMethod]
    public void 算法清单里能打开压缩()
    {
        SshAlgorithmSet withCompression = SshAlgorithmSet.Default.WithCompression();

        Assert.Contains(
SshAlgorithmNames.ZlibOpenSsh, [.. withCompression.CompressionClientToServer]);
        Assert.Contains(
SshAlgorithmNames.None, [.. withCompression.CompressionClientToServer], "none 要留在清单里 —— 对端不支持压缩时还得能连上");

        // 默认**不开**压缩：交互式会话上它几乎没有收益（终端输出本来就小），
        // 而 OpenSSH 的默认也是 none。
        Assert.AreSequenceEqual(
            [SshAlgorithmNames.None], [.. SshAlgorithmSet.Default.CompressionClientToServer]);
    }

    // ------------------------------------------------------------ 接进传输层

    [TestMethod]
    public async Task 传输层挂上压缩之后报文照样对得上()
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) =
            InMemoryTransport.CreatePair(new InMemoryTransportOptions
            {
                PauseWriterThreshold = 4 * 1024 * 1024,
                ResumeWriterThreshold = 2 * 1024 * 1024,
            });

        await using SshPacketTransport client = new(clientStream);
        await using SshPacketTransport server = new(serverStream);

        // 两个方向各挂一对 —— 客户端的发送压缩器要对上服务端的接收解压器。
        client.SetSendCompressor(new ZlibCompressor());
        server.SetReceiveCompressor(new ZlibCompressor());
        server.SetSendCompressor(new ZlibCompressor());
        client.SetReceiveCompressor(new ZlibCompressor());

        for (int i = 0; i < 20; i++)
        {
            byte[] up = Encoding.UTF8.GetBytes($"客户端第 {i} 条：{new string('a', 500)}");
            client.WritePacket(up);
            await client.FlushAsync();

            SshInboundPacket received = await server.ReadPacketAsync();
            Assert.AreSequenceEqual(up, received.Payload.ToArray(), $"上行第 {i} 条");

            byte[] down = Encoding.UTF8.GetBytes($"服务端第 {i} 条：{new string('b', 700)}");
            server.WritePacket(down);
            await server.FlushAsync();

            SshInboundPacket back = await client.ReadPacketAsync();
            Assert.AreSequenceEqual(down, back.Payload.ToArray(), $"下行第 {i} 条");
        }
    }

    [TestMethod]
    public async Task 只压一个方向也能跑()
    {
        // 两个方向是**各自独立协商**的 —— 一边压一边不压是合法的。
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) =
            InMemoryTransport.CreatePair();

        await using SshPacketTransport client = new(clientStream);
        await using SshPacketTransport server = new(serverStream);

        client.SetSendCompressor(new ZlibCompressor());
        server.SetReceiveCompressor(new ZlibCompressor());
        // 下行方向不压。

        byte[] up = Encoding.UTF8.GetBytes("压过的上行");
        client.WritePacket(up);
        await client.FlushAsync();
        Assert.AreSequenceEqual(up, (await server.ReadPacketAsync()).Payload.ToArray());

        byte[] down = Encoding.UTF8.GetBytes("没压的下行");
        server.WritePacket(down);
        await server.FlushAsync();
        Assert.AreSequenceEqual(down, (await client.ReadPacketAsync()).Payload.ToArray());
    }

    [TestMethod]
    public async Task 压缩确实让线上字节变少()
    {
        byte[] payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("可压缩的日志行\n", 500)));

        long withoutCompression = await MeasureWireBytesAsync(payload, compress: false);
        long withCompression = await MeasureWireBytesAsync(payload, compress: true);

        Assert.IsLessThan(
            withoutCompression / 2, withCompression,
            $"压过之后线上字节应当明显更少：{withCompression} vs {withoutCompression}");
    }

    private static async Task<long> MeasureWireBytesAsync(byte[] payload, bool compress)
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) =
            InMemoryTransport.CreatePair(new InMemoryTransportOptions
            {
                PauseWriterThreshold = 8 * 1024 * 1024,
                ResumeWriterThreshold = 4 * 1024 * 1024,
            });

        await using SshPacketTransport client = new(clientStream);
        await using SshPacketTransport server = new(serverStream);

        if (compress)
        {
            client.SetSendCompressor(new ZlibCompressor());
            server.SetReceiveCompressor(new ZlibCompressor());
        }

        client.WritePacket(payload);
        await client.FlushAsync();

        SshInboundPacket received = await server.ReadPacketAsync();
        Assert.AreSequenceEqual(payload, received.Payload.ToArray(), "压缩不能改变内容");

        return server.BytesReceived;
    }

    [TestMethod]
    public async Task 整条连接开着压缩也能跑通()
    {
        // 前面那些用例都是手搭传输。这一条走**完整的连接建立流程**，
        // 因为「认证成功之后才挂压缩」这个切换点只有在真流程里才存在，
        // 而切换点错一个报文，症状就是连上之后第一个报文解不开。
        byte[] payload = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("可压缩的一行\n", 100)));

        await using TestKit.TestSshServerHost host = await TestKit.TestSshServerHost.StartAsync(
            new TestKit.TestChannelScript { StandardOutput = payload, ExitCode = 0 },
            SshAlgorithmSet.Default.WithCompression());

        Assert.AreEqual(
            SshAlgorithmNames.ZlibOpenSsh,
            host.Connection.Algorithms.CompressionServerToClient);

        Ssh.Channels.SshCommandResult output =
            await Ssh.Session.SshConnectionExtensions.RunAsync(host.Connection, "压", cancellationToken: host.Token);

        Assert.AreSequenceEqual(
            payload, Encoding.UTF8.GetBytes(output.StandardOutput), "整条连接上压缩要能原样往返");
    }

    /// <summary>
    /// 使用者手动把裸 <c>zlib</c> 塞进清单、且对端也谈成它时，要在协商当场失败。
    /// </summary>
    /// <remarks>
    /// 裸 zlib 从首次 NEWKEYS 起就压，我们不实现它。若悄悄按不压缩处理，
    /// 对端从 NEWKEYS 起在压、我们按明文发认证请求，两端在第一个认证报文上就错位 ——
    /// 报出来的是一个看不出缘由的解压错误。
    /// </remarks>
    [TestMethod]
    public async Task 谈成裸zlib时在协商当场失败()
    {
        SshAlgorithmSet plainZlib = SshAlgorithmSet.Default with
        {
            CompressionClientToServer = ["zlib"],
            CompressionServerToClient = ["zlib"],
        };

        Exception ex = await Assert.ThrowsAsync<Exception>(async () =>
        {
            await using TestKit.TestSshServerHost host = await TestKit.TestSshServerHost.StartAsync(
                new TestKit.TestChannelScript { ExitCode = 0 }, plainZlib);
        });

        Assert.Contains("zlib", ex.Message);
    }
}
