using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>
/// 内存账:每个客户端与全部客户端合计各有上限,超了回 BadAlloc;释放、删除、断开时如数退还 ——
/// 服务端与宿主在同一个进程里,一个客户端不能把整个进程的内存吃光。
/// </summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class ResourceQuotaTests
{
    private const byte BadAlloc = 11;
    private const long MiB = 1024 * 1024;

    private static (byte, byte, Action<XTestClient.Body>?) CreatePixmap(XTestClient c, uint id, ushort size) =>
        (53, 24, b => b.U32(id).U32(c.RootWindow).U16(size).U16(size));

    private static async Task<XMessage?> ErrorOfAsync(XTestClient c, ushort sequence)
    {
        // 往返一次:之前那条请求要是出错,错误已经先到了。
        await c.SyncAsync();
        try
        {
            return await c.NextAsync(m => m.IsError && m.Sequence == sequence, timeoutMs: 200);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [TestMethod]
    public async Task 像素图超出每客户端上限回BadAlloc_释放之后又能建()
    {
        await using X11Server server = new(new X11ServerOptions { MaxClientMemory = 4 * MiB });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint[] ids = [c.NewId(), c.NewId(), c.NewId(), c.NewId()];
        for (int i = 0; i < 3; i++)
        {
            (byte op, byte data, Action<XTestClient.Body>? body) = CreatePixmap(c, ids[i], 512);   // 1 MiB 一块
            Assert.IsNull(await ErrorOfAsync(c, await c.SendAsync(op, data, body)), $"第 {i + 1} 块应当建得起来");
        }
        (byte op4, byte data4, Action<XTestClient.Body>? body4) = CreatePixmap(c, ids[3], 600);   // 再要 1.4 MiB:超过 4 MiB
        XMessage? error = await ErrorOfAsync(c, await c.SendAsync(op4, data4, body4));
        Assert.IsNotNull(error);
        Assert.AreEqual(BadAlloc, error.Detail);

        await c.SendAsync(54, 0, b => b.U32(ids[0]));   // FreePixmap:账退还
        Assert.IsNull(await ErrorOfAsync(c, await c.SendAsync(op4, data4, body4)), "释放之后应当建得起来");
    }

    [TestMethod]
    public async Task 全部客户端合计超出上限时回BadAlloc()
    {
        await using X11Server server = new(new X11ServerOptions { MaxClientMemory = 4 * MiB, MaxTotalMemory = 5 * MiB });
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        (byte op, byte data, Action<XTestClient.Body>? body) = CreatePixmap(a, a.NewId(), 1000);   // 3.8 MiB
        Assert.IsNull(await ErrorOfAsync(a, await a.SendAsync(op, data, body)));
        (op, data, body) = CreatePixmap(b, b.NewId(), 600);   // 1.4 MiB:b 自己没超,合计超了
        XMessage? error = await ErrorOfAsync(b, await b.SendAsync(op, data, body));
        Assert.IsNotNull(error);
        Assert.AreEqual(BadAlloc, error.Detail);
    }

    private static Task<ushort> ChangePropertyAsync(XTestClient c, uint window, uint property, int bytes) =>
        c.SendAsync(18, 0, b => b.U32(window).U32(property).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)bytes).Bytes(new byte[bytes]).Pad(),
            bigRequest: true);

    [TestMethod]
    public async Task 往几MB的属性上反复追加几个字节_只拷新字节()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        const uint property = 1;   // PRIMARY 当属性名用
        byte[] chunk = new byte[200_000];
        for (int i = 0; i < 20; i++)   // 4 MB:每次 Append 20 万字节
        {
            await c.SendAsync(18, 2, b => b.U32(c.RootWindow).U32(property).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)chunk.Length).Bytes(chunk));
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int appends = 3000;
        await c.SendManyAsync(Enumerable.Range(0, appends).Select<int, (byte, byte, Action<XTestClient.Body>?)>(i =>
            (18, 2, b => b.U32(c.RootWindow).U32(property).U32(31).U8(8).U8(0).U8(0).U8(0).U32(4).U8((byte)i).U8(1).U8(2).U8(3))));
        await c.SyncAsync();
        Assert.IsLessThan(2_000, watch.ElapsedMilliseconds, "原先每次追加都整份复制 4 MB");

        // 末尾的值对:最后一次追加的 4 个字节。
        XMessage tail = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(property).U32(0)
            .U32((uint)(((chunk.Length * 20) + (appends * 4) - 4) / 4)).U32(1));
        Assert.AreEqual(4u, tail.U32(16));
        Assert.AreEqual(unchecked((byte)(appends - 1)), tail.Bytes[32]);
        Assert.AreEqual(3, tail.Bytes[35]);
    }

    [TestMethod]
    public async Task 属性值计入写它的客户端名下_删掉之后退还()
    {
        await using X11Server server = new(new X11ServerOptions { MaxClientMemory = 2 * MiB });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte[] name = "BIG-REQUESTS"u8.ToArray();
        XMessage ext = await c.RequestAsync(98, 0, b => b.U16((ushort)name.Length).U16(0).Bytes(name).Pad());
        await c.RequestAsync(ext.Bytes[9], 0);   // BigReqEnable

        uint first = 1, second = 2;   // 预定义原子 PRIMARY、SECONDARY 当属性名用
        Assert.IsNull(await ErrorOfAsync(c, await ChangePropertyAsync(c, c.RootWindow, first, 1200 * 1024)));
        XMessage? error = await ErrorOfAsync(c, await ChangePropertyAsync(c, c.RootWindow, second, 1200 * 1024));
        Assert.IsNotNull(error, "第二个 1.2 MiB 的属性超过 2 MiB");
        Assert.AreEqual(BadAlloc, error.Detail);

        await c.SendAsync(19, 0, b => b.U32(c.RootWindow).U32(first));   // DeleteProperty
        Assert.IsNull(await ErrorOfAsync(c, await ChangePropertyAsync(c, c.RootWindow, second, 1200 * 1024)), "删掉之后应当写得进");
    }

    [TestMethod]
    public async Task 映射一个缓冲超出上限的顶层窗口回BadAlloc_窗口不映射()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { MaxClientMemory = 2 * MiB }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint window = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(window).U32(c.RootWindow).I16(0).I16(0).U16(1000).U16(1000).U16(0).U16(1).U32(0).U32(0));
        XMessage? error = await ErrorOfAsync(c, await c.SendAsync(8, 0, b => b.U32(window)));   // 4 MB 的缓冲
        Assert.IsNotNull(error);
        Assert.AreEqual(BadAlloc, error.Detail);
        Assert.IsFalse(host.Mapped.ContainsKey(window));

        // 缩到记得下的尺寸就能映射;之后再放大同样要核账。
        await c.SendAsync(12, 0, b => b.U32(window).U16(0xC).U16(0).U32(500).U32(500));   // ConfigureWindow 宽高
        Assert.IsNull(await ErrorOfAsync(c, await c.SendAsync(8, 0, b => b.U32(window))));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(window));
        error = await ErrorOfAsync(c, await c.SendAsync(12, 0, b => b.U32(window).U16(0xC).U16(0).U32(1000).U32(1000)));
        Assert.IsNotNull(error);
        Assert.AreEqual(BadAlloc, error.Detail);
    }

    [TestMethod]
    public async Task 客户端断开之后它名下的内存全部退还()
    {
        await using X11Server server = new();
        long baseline = await server.InvokeAsync(() => server.MemoryInUse);
        XTestClient c = await XTestClient.ConnectAsync(server);
        uint window = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(window).U32(c.RootWindow).I16(0).I16(0).U16(300).U16(200).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(window));
        await c.SendAsync(53, 24, b => b.U32(c.NewId()).U32(c.RootWindow).U16(100).U16(100));
        await c.SendAsync(18, 0, b => b.U32(window).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32(1000).Bytes(new byte[1000]));
        await c.SyncAsync();
        Assert.IsGreaterThan(baseline + (300 * 200 * 4), await server.InvokeAsync(() => server.MemoryInUse));

        await c.DisposeAsync();
        await using XTestClient other = await XTestClient.ConnectAsync(server);
        for (int i = 0; i < 50 && await server.InvokeAsync(() => server.MemoryInUse) != baseline; i++)
        {
            await Task.Delay(20);
            await other.SyncAsync();
        }
        Assert.AreEqual(baseline, await server.InvokeAsync(() => server.MemoryInUse));
    }
}
