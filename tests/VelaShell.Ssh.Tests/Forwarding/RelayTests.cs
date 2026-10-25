// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §2.2、§三、§五、§六
//
// 〔决策 §六〕搬运层与 SSH 无关 —— 所以半关闭、计量、错误收尾这条主路径
// 不必架一台真服务器就能验证。这一整个文件都不需要任何 SSH。

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Forwarding;

[TestClass]
[TestCategory("Forwarding")]
public sealed class RelayTests
{
    /// <summary>一个纯内存的搬运端点。</summary>
    private sealed class FakeEndpoint : IRelayEndpoint
    {
        private readonly Pipe _inbound = new(new PipeOptions(useSynchronizationContext: false));
        private readonly Pipe _outbound = new(new PipeOptions(useSynchronizationContext: false));

        /// <inheritdoc />
        public PipeReader Input => _inbound.Reader;

        /// <inheritdoc />
        public PipeWriter Output => _outbound.Writer;

        /// <summary>被搬进来的内容从这里读。</summary>
        public PipeReader Received => _outbound.Reader;

        /// <summary>对面告诉过我们「不再发了」几次。</summary>
        public int SendCompletedCount { get; private set; }

        /// <summary>往这一端喂数据（模拟外部写入）。</summary>
        public async ValueTask FeedAsync(byte[] data) => await _inbound.Writer.WriteAsync(data);

        /// <summary>这一端不再有数据了。</summary>
        public void FeedComplete() => _inbound.Writer.Complete();

        /// <summary>从这一端读出错（连接被重置、会话断了）。</summary>
        public void FeedFail(Exception error) => _inbound.Writer.Complete(error);

        private readonly CancellationTokenSource _closed = new();

        /// <summary>这一端整个结束了（通道收到 CLOSE）。</summary>
        public void CloseEntirely() => _closed.Cancel();

        /// <inheritdoc />
        public CancellationToken Closed => _closed.Token;

        /// <summary>被中止过几次。</summary>
        public int AbortCount => Volatile.Read(ref _aborts);

        private int _aborts;

        /// <inheritdoc />
        public ValueTask AbortAsync()
        {
            Interlocked.Increment(ref _aborts);

            // 像真的套接字被重置那样：这一端上悬着的读随之失败。
            _inbound.Writer.Complete(new IOException("连接被中止"));
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask CompleteSendAsync(CancellationToken cancellationToken)
        {
            SendCompletedCount++;
            _outbound.Writer.Complete();
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            _inbound.Writer.Complete();
            _inbound.Reader.Complete();
            return ValueTask.CompletedTask;
        }

        public async Task<byte[]> ReadAllReceivedAsync()
        {
            ArrayBufferWriter<byte> buffer = new();
            while (true)
            {
                ReadResult read = await Received.ReadAsync();
                foreach (ReadOnlyMemory<byte> segment in read.Buffer)
                {
                    buffer.Write(segment.Span);
                }
                Received.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted)
                {
                    break;
                }
            }
            return buffer.WrittenSpan.ToArray();
        }
    }

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    [TestMethod]
    public async Task 双向都搬到了()
    {
        FakeEndpoint left = new();
        FakeEndpoint right = new();

        await left.FeedAsync(Text("从左到右"));
        left.FeedComplete();
        await right.FeedAsync(Text("从右到左"));
        right.FeedComplete();

        Task<RelayResult> relay = DuplexRelay.RunAsync(left, right);

        byte[] atRight = await right.ReadAllReceivedAsync();
        byte[] atLeft = await left.ReadAllReceivedAsync();
        RelayResult result = await relay;

        Assert.AreEqual("从左到右", Encoding.UTF8.GetString(atRight));
        Assert.AreEqual("从右到左", Encoding.UTF8.GetString(atLeft));
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public async Task 一端发完之后另一端仍然能继续发()
    {
        // 这是半关闭的决定性用例。把 EOF 当成「连接结束」的实现会在这里
        // 把整条连接关掉 —— 症状是 curl 通过隧道 POST 完请求体后收不到响应。
        FakeEndpoint left = new();
        FakeEndpoint right = new();

        Task<RelayResult> relay = DuplexRelay.RunAsync(left, right);

        // 左边发完就不发了。
        await left.FeedAsync(Text("请求体"));
        left.FeedComplete();

        // 右边**在这之后**才开始发 —— 响应。
        await Task.Delay(50);
        await right.FeedAsync(Text("响应体"));
        right.FeedComplete();

        byte[] atRight = await right.ReadAllReceivedAsync();
        byte[] atLeft = await left.ReadAllReceivedAsync();
        await relay;

        Assert.AreEqual("请求体", Encoding.UTF8.GetString(atRight));
        Assert.AreEqual("响应体", Encoding.UTF8.GetString(atLeft),
            "左端发完 EOF 之后，右端的数据必须照样能搬过来");
    }

    [TestMethod]
    public async Task 每个方向各自发出一次半关闭()
    {
        FakeEndpoint left = new();
        FakeEndpoint right = new();

        left.FeedComplete();
        right.FeedComplete();

        await DuplexRelay.RunAsync(left, right);

        // 半关闭是**逐方向**的：左读完 → 告诉右「我发完了」，反之亦然。
        Assert.AreEqual(1, left.SendCompletedCount);
        Assert.AreEqual(1, right.SendCompletedCount);
    }

    [TestMethod]
    public async Task 字节计量分方向且不含协议开销()
    {
        FakeEndpoint left = new();
        FakeEndpoint right = new();

        await left.FeedAsync(new byte[1000]);
        left.FeedComplete();
        await right.FeedAsync(new byte[250]);
        right.FeedComplete();

        long up = 0;
        long down = 0;

        Task<RelayResult> relay = DuplexRelay.RunAsync(
            left, right,
            onBytesFromLeft: n => Interlocked.Add(ref up, n),
            onBytesFromRight: n => Interlocked.Add(ref down, n));

        _ = await right.ReadAllReceivedAsync();
        _ = await left.ReadAllReceivedAsync();
        RelayResult result = await relay;

        // 计量在搬运循环里累加 —— 通道层的数字含协议开销，
        // 而面板上要显示的是应用数据量。
        Assert.AreEqual(1000, up);
        Assert.AreEqual(250, down);
        Assert.AreEqual(1000, result.BytesFromLeft);
        Assert.AreEqual(250, result.BytesFromRight);
    }

    [TestMethod]
    public async Task 大块数据能完整搬过去()
    {
        byte[] payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);

        FakeEndpoint left = new();
        FakeEndpoint right = new();

        Task<RelayResult> relay = DuplexRelay.RunAsync(left, right);

        var feed = Task.Run(async () =>
        {
            await left.FeedAsync(payload);
            left.FeedComplete();
            right.FeedComplete();
        });

        byte[] atRight = await right.ReadAllReceivedAsync();
        _ = await left.ReadAllReceivedAsync();
        await feed;
        await relay;

        Assert.AreSequenceEqual(payload, atRight);
    }

    /// <summary>
    /// 〔FW-P1〕目的端是一条流时直接写进去（不经 PipeWriter 的缓冲再拷一遍）：内容、计数、半关闭照旧。
    /// </summary>
    [TestMethod]
    public async Task 目的端是流时直接写进去且内容完整()
    {
        byte[] payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);

        (Ssh.Transport.InMemoryDuplexStream ours, Ssh.Transport.InMemoryDuplexStream theirs) = Ssh.Transport.InMemoryTransport.CreatePair();
        FakeEndpoint left = new();
        await using StreamRelayEndpoint right = new(ours, shutdownSend: ours.CompleteWrites);

        Task<RelayResult> relay = DuplexRelay.RunAsync(left, right);

        Task<byte[]> atRight = Task.Run(async () =>
        {
            using MemoryStream received = new();
            await theirs.CopyToAsync(received);   // 半关闭之后读到结尾
            return received.ToArray();
        });

        await left.FeedAsync(payload);
        left.FeedComplete();

        Assert.AreSequenceEqual(payload, await atRight.WaitAsync(TimeSpan.FromSeconds(10)));
        theirs.CompleteWrites();
        RelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(payload.Length, result.BytesFromLeft);
    }

    // ------------------------------------------------------------ 出错收尾

    [TestMethod]
    public async Task 一个方向出错时两端都被中止而不是收到EOF()
    {
        // 出错时照正常结束去关（发 EOF / FIN），对面会把截断的数据当成完整的 ——
        // 下载到一半的文件看起来就是下完了。
        FakeEndpoint left = new();
        FakeEndpoint right = new();

        Task<RelayResult> relay = DuplexRelay.RunAsync(left, right);

        // 右 → 左那个方向读出错（右端是 SSH 通道时就是会话断了）；左 → 右那个方向还挂在读上。
        await right.FeedAsync(Text("前一半"));
        right.FeedFail(new IOException("会话断了"));

        RelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, left.SendCompletedCount, "出错的方向不能给对面一个干净的 EOF");
        Assert.AreEqual(0, right.SendCompletedCount, "另一个方向是被中止的，也不是正常读完");
        Assert.AreEqual(1, left.AbortCount);
        Assert.AreEqual(1, right.AbortCount);

        // 原因要是真正出错的那一边，而不是被牵连取消的那一边。
        Assert.IsInstanceOfType<IOException>(result.Error);
        Assert.AreEqual("会话断了", result.Error.Message);
    }

    /// <summary>
    /// 〔FW-E8〕中止收场的搬运照实报搬过的字节数（下载到一半链路断了，面板上要看得到已经下了多少）。
    /// 曾经只看正常结束的方向，出错的那次报 0 / 0。
    /// </summary>
    [TestMethod]
    public async Task 出错收场时照实报已搬的字节数()
    {
        FakeEndpoint left = new();
        FakeEndpoint right = new();

        Task<RelayResult> relay = DuplexRelay.RunAsync(left, right);

        byte[] part = Text("前一半");
        await right.FeedAsync(part);

        // 等这一段确实搬到了左端，再让右端出错。
        long arrived = 0;
        while (arrived < part.Length)
        {
            ReadResult read = await left.Received.ReadAsync();
            arrived += read.Buffer.Length;
            left.Received.AdvanceTo(read.Buffer.End);
        }
        right.FeedFail(new IOException("会话断了"));

        RelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsNotNull(result.Error);
        Assert.AreEqual(part.Length, result.BytesFromRight, "出错前已经搬过去的不能报成 0");
        Assert.AreEqual(0, result.BytesFromLeft);
    }
    [TestMethod]
    public async Task 目的端整个关闭时停下往它写的方向()
    {
        // 右端（SSH 通道）收到 CLOSE：往它写已经没有意义。左端（本机程序）在等响应，不会先关 ——
        // 不停下左 → 右的那个方向，socket 与转发名额就一直占着。
        FakeEndpoint left = new();
        FakeEndpoint right = new();

        Task<RelayResult> relay = DuplexRelay.RunAsync(left, right);

        await right.FeedAsync(Text("响应"));
        right.FeedComplete();
        right.CloseEntirely();

        RelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        byte[] atLeft = await left.ReadAllReceivedAsync();

        Assert.AreEqual("响应", Encoding.UTF8.GetString(atLeft), "已经收到的数据照常排空");
        Assert.IsNull(result.Error, "对端关通道是正常结束，不是出错");
        Assert.AreEqual(0, left.AbortCount);
        Assert.AreEqual(0, right.AbortCount);
    }

    [TestMethod]
    public async Task 调用方取消时两端都被中止()
    {
        FakeEndpoint left = new();
        FakeEndpoint right = new();
        using CancellationTokenSource cts = new();

        Task<RelayResult> relay = DuplexRelay.RunAsync(left, right, cancellationToken: cts.Token);
        await cts.CancelAsync();

        RelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsInstanceOfType<OperationCanceledException>(result.Error);
        Assert.AreEqual(1, left.AbortCount);
        Assert.AreEqual(1, right.AbortCount);
        Assert.AreEqual(0, left.SendCompletedCount + right.SendCompletedCount);
    }

    // ------------------------------------------------------------ 限速（spec/07 §5.1）

    /// <summary>
    /// 一次读到的大块（机器忙时通道里攒下的）切开搬：当场只写出一秒的额度，之后每秒一段 ——
    /// 不是整块等完再整块写出。总共用的时间与整块等的一样：平均速率不变。
    /// </summary>
    [TestMethod]
    public async Task 限速时大块切开搬_单次突发不超过一秒额度_平均速率不变()
    {
        const int rate = 4000;
        const int payloadLength = 10 * rate;   // 十秒的量，搬运开始之前就整块到了
        ManualTimeProvider time = new();
        ByteRateLimiter limiter = new(rate, time);
        FakeEndpoint left = new();
        FakeEndpoint right = new();
        ConcurrentQueue<int> writes = new();
        long moved = 0;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        await left.FeedAsync(new byte[payloadLength]);
        left.FeedComplete();
        right.FeedComplete();

        Task<RelayResult> relay = DuplexRelay.RunAsync(
            left, right,
            onBytesFromLeft: n =>
            {
                writes.Enqueue(n);
                Interlocked.Add(ref moved, n);
            },
            throttleFromLeft: limiter.AcquireAsync);
        Task<byte[]> atRight = right.ReadAllReceivedAsync();

        for (int second = 1; second < 10; second++)
        {
            // 放行一段、写出去，下一段就要等满一秒：等它睡下，再看这时写出了多少。
            await time.WaitUntilArmedAsync(TimeSpan.FromSeconds(1), timeout.Token);
            Assert.AreEqual((long)second * rate, Interlocked.Read(ref moved), $"第 {second} 段等额度时，前面每段一秒的额度都已写出");
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.HasCount(payloadLength, await atRight.WaitAsync(timeout.Token));
        RelayResult result = await relay.WaitAsync(timeout.Token);
        Assert.IsNull(result.Error);
        Assert.AreEqual(payloadLength, result.BytesFromLeft);
        Assert.IsTrue(writes.All(n => n <= rate), $"单次写出不超过一秒的额度：{string.Join(", ", writes)}");
        Assert.AreEqual(TimeSpan.FromSeconds(9), time.GetElapsedTime(0), "十秒的量、一秒的突发：九秒搬完，与整块等的一样");
    }

    /// <summary>限速放得比一块缓冲还宽时也一块一块地搬：计数一块缓冲一跳，不随一次读到多少整块跳。</summary>
    [TestMethod]
    public async Task 限速时每段不超过一块缓冲()
    {
        ManualTimeProvider time = new();
        ByteRateLimiter limiter = new(16 * DuplexRelay.BufferSize, time);
        FakeEndpoint left = new();
        FakeEndpoint right = new();
        ConcurrentQueue<int> writes = new();
        byte[] payload = new byte[6 * DuplexRelay.BufferSize];
        Random.Shared.NextBytes(payload);

        Task<RelayResult> relay = DuplexRelay.RunAsync(
            left, right, onBytesFromLeft: writes.Enqueue, throttleFromLeft: limiter.AcquireAsync);
        Task<byte[]> atRight = right.ReadAllReceivedAsync();
        Task feed = Task.Run(async () =>
        {
            await left.FeedAsync(payload);
            left.FeedComplete();
            right.FeedComplete();
        });

        // 都在一秒的额度之内：时钟不必拨。
        Assert.AreSequenceEqual(payload, await atRight.WaitAsync(TimeSpan.FromSeconds(10)));
        await feed;
        await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(writes.All(n => n <= DuplexRelay.BufferSize), $"每段不超过一块缓冲：{string.Join(", ", writes)}");
    }

    /// <summary>等额度的时候取消：当场停下、两端中止，不等欠下的那几秒；只报放行过、写出去了的字节。</summary>
    [TestMethod]
    public async Task 限速等额度时取消当场停下()
    {
        const int rate = 4000;
        ManualTimeProvider time = new();
        ByteRateLimiter limiter = new(rate, time);
        FakeEndpoint left = new();
        FakeEndpoint right = new();
        using CancellationTokenSource cts = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        await left.FeedAsync(new byte[10 * rate]);
        Task<RelayResult> relay = DuplexRelay.RunAsync(
            left, right, throttleFromLeft: limiter.AcquireAsync, cancellationToken: cts.Token);

        // 第一段已经写出，第二段在等额度。
        await time.WaitUntilArmedAsync(TimeSpan.FromSeconds(1), timeout.Token);
        await cts.CancelAsync();

        // 时钟一下没拨：是取消把它放出来的。
        RelayResult result = await relay.WaitAsync(timeout.Token);
        Assert.IsInstanceOfType<OperationCanceledException>(result.Error);
        Assert.AreEqual(rate, result.BytesFromLeft, "只有放行的第一段写出去了");
        Assert.AreEqual(1, left.AbortCount);
        Assert.AreEqual(1, right.AbortCount);
    }
}
