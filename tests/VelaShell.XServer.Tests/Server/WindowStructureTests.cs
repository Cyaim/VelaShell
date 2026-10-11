using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>结构请求:DestroySubwindows、CirculateWindow、ReparentWindow、ConfigureWindow 的事件、次序与改道。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class WindowStructureTests
{
    private const uint ExposureMask = 0x8000, VisibilityChangeMask = 0x10000, StructureNotifyMask = 0x20000, ResizeRedirectMask = 0x40000,
        SubstructureNotifyMask = 0x80000, SubstructureRedirectMask = 0x100000;
    private const byte Expose = 12, VisibilityNotify = 15, DestroyNotify = 17, UnmapNotify = 18, MapRequest = 20, ConfigureNotify = 22, GravityNotify = 24, ResizeRequest = 25,
        CirculateNotify = 26, CirculateRequest = 27;

    /// <summary>在 <paramref name="parent" /> 下建一个子窗口(InputOutput,背景色 <paramref name="background" />)。</summary>
    private static async Task<uint> CreateChildAsync(XTestClient c, uint parent, short x, short y, ushort width, ushort height,
        uint background = 0xFFFFFF, uint eventMask = 0)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(parent).I16(x).I16(y).U16(width).U16(height).U16(0).U16(1).U32(0)
            .U32(0x802).U32(background).U32(eventMask));
        return id;
    }

    private static Task<ushort> SelectInputAsync(XTestClient c, uint window, uint mask) =>
        c.SendAsync(2, 0, b => b.U32(window).U32(0x800).U32(mask));

    private static async Task<uint[]> QueryChildrenAsync(XTestClient c, uint window)
    {
        XMessage tree = await c.RequestAsync(15, 0, b => b.U32(window));
        return [.. Enumerable.Range(0, tree.U16(16)).Select(i => tree.U32(32 + (i * 4)))];
    }

    [TestMethod]
    public async Task CirculateWindow按遮挡挑窗口_有改道者时发CirculateRequest_压下去让出来的兄弟重画()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient wm = await XTestClient.ConnectAsync(server);
        uint frame = await CreateChildAsync(c, c.RootWindow, 0, 0, 100, 100, background: 0xFFFFFF);
        await c.SendAsync(8, 0, b => b.U32(frame));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(frame));
        // 从下到上:alone(谁也不挡)、red、blue(挡住 red 的右半边)。
        uint alone = await CreateChildAsync(c, frame, 0, 70, 20, 20, background: 0x00FF00);
        uint red = await CreateChildAsync(c, frame, 0, 0, 40, 40, background: 0xFF0000);
        uint blue = await CreateChildAsync(c, frame, 20, 0, 40, 40, background: 0x0000FF);
        await c.SendAsync(9, 0, b => b.U32(frame));   // MapSubwindows
        await c.SyncAsync();
        XTopLevelWindow handle = host.Mapped[frame];
        uint Pixel(int x, int y)
        {
            (uint[] pixels, int width, _) = RecordingHost.Snapshot(handle);
            return pixels[(y * width) + x] & 0xFFFFFF;
        }
        Assert.AreEqual(0x0000FFu, Pixel(30, 10), "重叠处是上面的 blue");

        // 别的客户端在 frame 上选了 SubstructureRedirect:只发 CirculateRequest,次序不动。挑的是被挡着的最低的 red,不是最低的 alone。
        await SelectInputAsync(wm, frame, SubstructureRedirectMask);
        await wm.SyncAsync();
        await c.SendAsync(13, 0, b => b.U32(frame));   // CirculateWindow RaiseLowest
        XMessage request = await wm.NextEventAsync(CirculateRequest);
        Assert.AreEqual(frame, request.U32(4), "parent");
        Assert.AreEqual(red, request.U32(8), "被挡着的最低的那个(原先总挪最低的 alone)");
        Assert.AreEqual(0, request.Bytes[16], "place = Top");
        Assert.AreSequenceEqual([alone, red, blue], await QueryChildrenAsync(c, frame), "改道:不再处理");
        await SelectInputAsync(wm, frame, 0);
        await wm.SyncAsync();

        // 没有改道:LowerHighest 把挡着别人的最高的 blue 压到最下,red 原先被挡的部分重画。
        await SelectInputAsync(c, frame, SubstructureNotifyMask);
        await c.SendAsync(13, 1, b => b.U32(frame));   // CirculateWindow LowerHighest
        XMessage notify = await c.NextEventAsync(CirculateNotify);
        Assert.AreEqual(blue, notify.U32(8));
        Assert.AreEqual(1, notify.Bytes[16], "place = Bottom");
        Assert.AreSequenceEqual([blue, alone, red], await QueryChildrenAsync(c, frame));
        await c.SyncAsync();
        Assert.AreEqual(0xFF0000u, Pixel(30, 10), "原先 red 露出来的部分还留着 blue 的旧像素");
        Assert.AreEqual(0x0000FFu, Pixel(50, 10), "blue 没被挡的部分照旧");

        // 谁也不挡谁:什么也不做,不发 CirculateNotify。
        await c.SendAsync(13, 0, b => b.U32(alone));
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(CirculateNotify, timeoutMs: 100));
    }

    [TestMethod]
    public async Task ReparentWindow自动重映射算请求方发的_窗口管理器自己reparent进外框时不给自己发MapRequest()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient wm = await XTestClient.ConnectAsync(server);
        await using XTestClient app = await XTestClient.ConnectAsync(server);
        uint frame = await CreateChildAsync(wm, wm.RootWindow, 0, 0, 200, 200);
        await wm.SendAsync(8, 0, b => b.U32(frame));
        await SelectInputAsync(wm, frame, SubstructureRedirectMask);
        uint first = await CreateChildAsync(app, app.RootWindow, 10, 10, 50, 50);
        uint second = await CreateChildAsync(app, app.RootWindow, 10, 10, 50, 50);
        await app.SendAsync(8, 0, b => b.U32(first));
        await app.SendAsync(8, 0, b => b.U32(second));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(first) && host.Mapped.ContainsKey(second));
        async Task<byte> MapStateAsync(uint window) => (await app.RequestAsync(3, 0, b => b.U32(window))).Bytes[26];

        // 窗口管理器把映射着的窗口 reparent 进自己的外框:自动重映射就像它自己发的 MapWindow,直接映射。
        await wm.SendAsync(7, 0, b => b.U32(first).U32(frame).I16(5).I16(20));
        await wm.SyncAsync();
        Assert.AreNotEqual(0, await MapStateAsync(first), "映射着(原先成了发给窗口管理器自己的 MapRequest,窗口一直没映射)");
        await Assert.ThrowsAsync<OperationCanceledException>(() => wm.NextEventAsync(MapRequest, timeoutMs: 100));

        // 别的客户端把窗口 reparent 进外框:照常改道成发给窗口管理器的 MapRequest。
        await app.SendAsync(7, 0, b => b.U32(second).U32(frame).I16(5).I16(80));
        XMessage request = await wm.NextEventAsync(MapRequest);
        Assert.AreEqual(second, request.U32(8));
        Assert.AreEqual(0, await MapStateAsync(second));
    }

    [TestMethod]
    public async Task ConfigureWindow的stack_mode越界回BadValue_TopIf_BottomIf_Opposite按遮挡决定()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint parent = await CreateChildAsync(c, c.RootWindow, 0, 0, 200, 200);
        uint low = await CreateChildAsync(c, parent, 0, 0, 50, 50);
        uint high = await CreateChildAsync(c, parent, 40, 40, 50, 50);   // 挡着 low 的右下角
        uint far = await CreateChildAsync(c, parent, 150, 150, 20, 20);  // 谁也不挡
        await c.SendAsync(9, 0, b => b.U32(parent));   // MapSubwindows
        Task<ushort> StackAsync(uint window, uint mode, uint? sibling = null) => c.SendAsync(12, 0, b =>
        {
            b.U32(window).U16((ushort)(sibling is null ? 0x40 : 0x60)).U16(0);
            if (sibling is { } s)
            {
                b.U32(s);
            }
            b.U32(mode);
        });

        await StackAsync(low, 5);
        Assert.AreEqual(2, (await c.NextAsync(m => m.IsError)).Detail, "stack-mode 5:BadValue(原先截成一个字节照用)");

        await StackAsync(far, 2);   // TopIf:没有兄弟挡着它 → 不动(原先按 Above 抬到最上)
        Assert.AreSequenceEqual([low, high, far], await QueryChildrenAsync(c, parent));
        await StackAsync(low, 2);   // TopIf:high 挡着它 → 最上
        Assert.AreSequenceEqual([high, far, low], await QueryChildrenAsync(c, parent));
        await StackAsync(far, 3);   // BottomIf:它谁也不挡 → 不动(原先按 Below 压到最下)
        Assert.AreSequenceEqual([high, far, low], await QueryChildrenAsync(c, parent));
        await StackAsync(low, 4, high);   // Opposite + 兄弟:low 挡着 high → 最下
        Assert.AreSequenceEqual([low, high, far], await QueryChildrenAsync(c, parent));
        await StackAsync(low, 4, far);    // Opposite + 不相交的兄弟:不动
        Assert.AreSequenceEqual([low, high, far], await QueryChildrenAsync(c, parent));
    }

    [TestMethod]
    public async Task 父窗口改尺寸时子窗口按win_gravity挪并发GravityNotify_Unmap重力的取消映射_ResizeRedirect改道成ResizeRequest()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient other = await XTestClient.ConnectAsync(server);
        uint parent = await CreateChildAsync(c, c.RootWindow, 10, 10, 100, 100);
        async Task<uint> ChildAsync(short x, short y, uint gravity)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 24, b => b.U32(id).U32(parent).I16(x).I16(y).U16(10).U16(10).U16(0).U16(1).U32(0)
                .U32(0x20).U32(gravity));   // win-gravity
            return id;
        }
        uint southEast = await ChildAsync(80, 80, 9);
        uint center = await ChildAsync(45, 45, 5);
        uint unmap = await ChildAsync(0, 0, 0);
        uint fixedToRoot = await ChildAsync(20, 20, 10);   // Static
        await c.SendAsync(9, 0, b => b.U32(parent));
        await SelectInputAsync(c, parent, StructureNotifyMask | SubstructureNotifyMask);
        await c.SyncAsync();

        // 往左挪 4、变成 150×121:SouthEast 挪 (50, 21),Center 挪 (25, 10),Static 抵消父窗口的位移 (+4, 0)。
        await c.SendAsync(12, 0, b => b.U32(parent).U16(0x1 | 0x4 | 0x8).U16(0).U32(6).U32(150).U32(121));
        XMessage configure = await c.NextEventAsync(ConfigureNotify);
        Assert.AreEqual(parent, configure.U32(8));
        Dictionary<uint, (short X, short Y)> moved = [];
        for (int i = 0; i < 3; i++)
        {
            XMessage gravity = await c.NextEventAsync(GravityNotify);
            Assert.AreEqual(parent, gravity.U32(4), "父窗口上选了 SubstructureNotify 的收到");
            moved[gravity.U32(8)] = (gravity.I16(12), gravity.I16(14));
        }
        Assert.AreEqual(((short)130, (short)101), moved[southEast]);
        Assert.AreEqual(((short)70, (short)55), moved[center]);
        Assert.AreEqual(((short)24, (short)20), moved[fixedToRoot]);
        XMessage unmapped = await c.NextEventAsync(UnmapNotify);
        Assert.AreEqual(unmap, unmapped.U32(8));
        Assert.AreEqual(1, unmapped.Bytes[12], "from-configure");
        XMessage geometry = await c.RequestAsync(14, 0, b => b.U32(southEast));
        Assert.AreEqual(((short)130, (short)101), (geometry.I16(12), geometry.I16(14)));

        // 别的客户端选了 ResizeRedirect:改尺寸变成发给它的 ResizeRequest,尺寸不变,位置照改。
        await SelectInputAsync(other, parent, ResizeRedirectMask);
        await other.SyncAsync();
        await c.SendAsync(12, 0, b => b.U32(parent).U16(0x1 | 0x4).U16(0).U32(20).U32(300));
        XMessage request = await other.NextEventAsync(ResizeRequest);
        Assert.AreEqual(parent, request.U32(4));
        Assert.AreEqual(300, request.U16(8), "请求的宽");
        Assert.AreEqual(121, request.U16(10), "高没改:现值");
        XMessage after = await c.RequestAsync(14, 0, b => b.U32(parent));
        Assert.AreEqual((20, 150), (after.I16(12), after.U16(16)), "位置照改,尺寸保持");
    }

    [TestMethod]
    public async Task 子窗口被兄弟挡住或露出时发VisibilityNotify_在它的Expose之前()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint frame = await CreateChildAsync(c, c.RootWindow, 0, 0, 100, 100);
        await c.SendAsync(8, 0, b => b.U32(frame));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(frame));
        uint watched = await CreateChildAsync(c, frame, 10, 10, 40, 40, eventMask: VisibilityChangeMask | ExposureMask);
        uint cover = await CreateChildAsync(c, frame, 30, 10, 40, 40);
        async Task<byte> NextStateAsync() => (await c.NextEventAsync(VisibilityNotify)).Bytes[8];

        await c.SendAsync(8, 0, b => b.U32(watched));
        Assert.AreEqual(0, await NextStateAsync(), "映射:Unobscured(原先从不发)");
        await c.SendAsync(8, 0, b => b.U32(cover));
        Assert.AreEqual(1, await NextStateAsync(), "右半边被挡:PartiallyObscured");
        await c.SendAsync(12, 0, b => b.U32(cover).U16(0x1).U16(0).U32(10));   // 挪到正好盖住
        Assert.AreEqual(2, await NextStateAsync(), "FullyObscured");

        await c.SyncAsync();
        try
        {
            while (true)
            {
                await c.NextAsync(m => !m.IsReply && !m.IsError, timeoutMs: 50);   // 之前映射时的 Expose 之类读掉
            }
        }
        catch (OperationCanceledException)
        {
        }
        // 挡着的兄弟取消映射:先 Unobscured,再是它的 Expose。
        await c.SendAsync(10, 0, b => b.U32(cover));
        await c.SyncAsync();
        XMessage first = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode is VisibilityNotify or Expose);
        Assert.AreEqual(VisibilityNotify, first.EventCode, "VisibilityNotify 在这个窗口的 Expose 之前");
        Assert.AreEqual(0, first.Bytes[8]);
        Assert.AreEqual(watched, (await c.NextEventAsync(Expose)).U32(4));

        await c.SendAsync(10, 0, b => b.U32(watched));   // 自己取消映射:不报
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(VisibilityNotify, timeoutMs: 100));
    }

    [TestMethod]
    public async Task 几百个互相重叠的子窗口改尺寸重画后每个像素都是最上面那个窗口的颜色()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint frame = await CreateChildAsync(c, c.RootWindow, 0, 0, 200, 200, background: 0x000000);
        await c.SendAsync(8, 0, b => b.U32(frame));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(frame));
        // 400 个 15×15 的子窗口,步长 9:相邻的互相压住一块,颜色各不相同;第 i 个在第 i − 1 个之上。
        List<(int X, int Y, uint Color)> children = [];
        await c.SendManyAsync(Enumerable.Range(0, 400).Select<int, (byte, byte, Action<XTestClient.Body>?)>(i =>
        {
            uint id = c.NewId();
            (int x, int y, uint color) = (i % 20 * 9, i / 20 * 9, (uint)((i * 2654435761u) & 0xFFFFFF));
            children.Add((x, y, color));
            return (1, 24, b => b.U32(id).U32(frame).I16((short)x).I16((short)y).U16(15).U16(15).U16(0).U16(1).U32(0)
                .U32(0x802).U32(color).U32(0));
        }));
        await c.SendAsync(9, 0, b => b.U32(frame));   // MapSubwindows
        server.ResizeTopLevel(host.Mapped[frame], 201, 200);   // 整窗重画
        await c.SyncAsync();

        (uint[] pixels, int width, _) = RecordingHost.Snapshot(host.Mapped[frame]);
        for (int y = 0; y < 200; y += 3)
        {
            for (int x = 0; x < 200; x += 3)
            {
                uint expected = 0;
                foreach ((int cx, int cy, uint color) in children)
                {
                    if (x >= cx && x < cx + 15 && y >= cy && y < cy + 15)
                    {
                        expected = color;   // 后建的在上面
                    }
                }
                Assert.AreEqual(expected, pixels[(y * width) + x] & 0xFFFFFF, $"({x},{y})");
            }
        }
    }

    [TestMethod]
    public async Task DestroySubwindows按堆叠次序从下到上销毁()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint parent = await CreateChildAsync(c, c.RootWindow, 0, 0, 100, 100);
        uint bottom = await CreateChildAsync(c, parent, 0, 0, 10, 10);
        uint middle = await CreateChildAsync(c, parent, 0, 0, 10, 10);
        uint top = await CreateChildAsync(c, parent, 0, 0, 10, 10);
        await SelectInputAsync(c, parent, SubstructureNotifyMask);
        await c.SendAsync(5, 0, b => b.U32(parent));   // DestroySubwindows
        uint[] order = [(await c.NextEventAsync(DestroyNotify)).U32(8), (await c.NextEventAsync(DestroyNotify)).U32(8),
            (await c.NextEventAsync(DestroyNotify)).U32(8)];
        Assert.AreSequenceEqual([bottom, middle, top], order, "协议「DestroySubwindows」:从下到上(原先从上到下)");
    }
}
