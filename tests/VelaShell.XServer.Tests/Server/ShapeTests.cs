using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>SHAPE 扩展:非矩形窗口的可见区域、绘图裁剪、输入命中与宿主看到的形状。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class ShapeTests
{
    private static async Task<byte> ShapeMajorAsync(XTestClient c)
    {
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("SHAPE")));
        Assert.AreEqual(1, q.Bytes[8], "SHAPE 应当存在");
        Assert.AreEqual(64, q.Bytes[10], "first-event");
        return q.Bytes[9];
    }

    private static async Task<(uint Top, XTopLevelWindow Handle)> MapTopAsync(XTestClient c, RecordingHost host, uint bg, uint mask = 0)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x802).U32(bg).U32(mask));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return (id, host.Mapped[id]);
    }

    [TestMethod]
    public async Task QueryVersion报1点1()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        XMessage v = await c.RequestAsync(major, 0);
        Assert.AreEqual(1, v.U16(8));
        Assert.AreEqual(1, v.U16(10));
    }

    [TestMethod]
    public async Task 子窗口的边界形状之外露出父窗口背景()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        (uint top, XTopLevelWindow handle) = await MapTopAsync(c, host, 0x000000);

        uint child = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(child).U32(top).I16(10).I16(10).U16(20).U16(20).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0xFFFFFF));
        // 形状只留左上 5×5,再映射。
        await c.SendAsync(major, 1, b => b.U8(0).U8(0).U8(0).U8(0).U32(child).I16(0).I16(0).I16(0).I16(0).U16(5).U16(5));
        await c.SendAsync(8, 0, b => b.U32(child));
        await c.SyncAsync();

        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0xFFFFFFu, px[(12 * w) + 12], "形状里面是子窗口背景");
        Assert.AreEqual(0x000000u, px[(20 * w) + 20], "形状外面露出父窗口");

        XMessage rects = await c.RequestAsync(major, 8, b => b.U32(child).U8(0).U8(0).U8(0).U8(0));
        Assert.AreEqual(1u, rects.U32(8));
        Assert.AreEqual(5, rects.U16(36));
    }

    [TestMethod]
    public async Task ShapeCombine只按客户端给的偏移放源形状_子窗口在10_10时并进父窗口不偏两倍()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        uint parent = c.NewId(), child = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(parent).U32(c.RootWindow).I16(50).I16(50).U16(100).U16(100).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(1, 24, b => b.U32(child).U32(parent).I16(10).I16(10).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(major, 1, b => b.U8(0).U8(0).U8(0).U8(0).U32(child).I16(0).I16(0).I16(0).I16(0).U16(5).U16(5));   // 子窗口:左上 5×5
        // Xt / Motif 的写法:把子窗口的形状并进父窗口,偏移给子窗口在父窗口里的位置。
        await c.SendAsync(major, 3, b => b.U8(0).U8(0).U8(0).U8(0).U32(parent).I16(10).I16(10).U32(child));   // Combine:Set、Bounding ← Bounding
        XMessage rects = await c.RequestAsync(major, 8, b => b.U32(parent).U8(0).Pad());                       // GetRectangles
        Assert.AreEqual(1u, rects.U32(8), "一块");
        Assert.AreEqual((10, 10, 5, 5), (rects.I16(32), rects.I16(34), rects.U16(36), rects.U16(38)),
            "原先另外加了两个窗口内区原点之差,落在 (20, 20)");
    }

    [TestMethod]
    public async Task 宿主拿到顶层的输入形状_是输入形状与边界形状的交集()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        (uint top, XTopLevelWindow handle) = await MapTopAsync(c, host, 0x000000);
        Assert.IsNull(handle.Snapshot.InputShape, "没设输入形状");

        // 输入形状:左上 30×20(xeyes、透明的通知气泡只在眼睛 / 正文上接收鼠标)。
        await c.SendAsync(major, 1, b => b.U8(0).U8(2).U8(0).U8(0).U32(top).I16(0).I16(0).I16(0).I16(0).U16(30).U16(20));
        await host.WaitForAsync(() => handle.Snapshot.InputShape is not null);
        Assert.AreEqual(new XRect(0, 0, 30, 20), handle.Snapshot.InputShape!.Single(), "原先快照里没有输入形状");
        IReadOnlyList<XRect> first = handle.Snapshot.InputShape;

        // 边界形状只留右半边 → 有效输入区是两者的交集。
        await c.SendAsync(major, 1, b => b.U8(0).U8(0).U8(0).U8(0).U32(top).I16(0).I16(0).I16(20).I16(0).U16(40).U16(40));
        await host.WaitForAsync(() => handle.Snapshot.Shape is not null);
        Assert.AreEqual(new XRect(20, 0, 10, 20), handle.Snapshot.InputShape!.Single());
        Assert.AreNotSame(first, handle.Snapshot.InputShape);
    }

    /// <summary>
    /// 没有合成管理器时 ARGB 窗口按不透明显示,但输入形状只圈出一部分的除外:GTK 4 的弹出菜单不论有没有合成管理器都在四周画 alpha 为 0 的阴影区、
    /// 输入形状只圈菜单本体,按不透明显示就是一圈黑边(gnome-calculator 的模式下拉菜单实测)。
    /// </summary>
    [TestMethod]
    public async Task 没有合成管理器时_输入形状只圈出一部分的ARGB窗口按alpha显示_其余ARGB窗口仍不透明()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        uint popup = c.NewId();
        await c.SendAsync(1, 32, b => b.U32(popup).U32(c.RootWindow).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0x22)   // 深度 32、ARGB 视觉
            .U32(0x200).U32(1));   // override-redirect
        await c.SendAsync(8, 0, b => b.U32(popup));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(popup));
        XTopLevelWindow handle = host.Mapped[popup];
        Assert.IsFalse(handle.Snapshot.HasAlpha, "没有合成管理器:X 显示 ARGB 窗口不看 alpha(GL 程序清屏的 alpha 是 0)");

        // 输入形状只圈中间(GTK 的菜单本体),四周是不接收输入的阴影区。
        await c.SendAsync(major, 1, b => b.U8(0).U8(2).U8(0).U8(0).U32(popup).I16(0).I16(0).I16(10).I16(8).U16(40).U16(24));
        await host.WaitForAsync(() => handle.Snapshot.InputShape is not null);
        Assert.IsTrue(handle.Snapshot.HasAlpha, "客户端声明了四周不是窗口的一部分:照 alpha 显示,不再是一圈黑边");

        // 输入形状盖满整个窗口:没有声明为不接收输入的地方,仍按不透明。
        await c.SendAsync(major, 1, b => b.U8(0).U8(2).U8(0).U8(0).U32(popup).I16(0).I16(0).I16(0).I16(0).U16(60).U16(40));
        await host.WaitForAsync(() => handle.Snapshot.InputShape is [{ Width: 60, Height: 40 }]);
        Assert.IsFalse(handle.Snapshot.HasAlpha);

        // 24 位的窗口有没有输入形状都不透明(像素里没有 alpha)。
        (uint plain, XTopLevelWindow plainHandle) = await MapTopAsync(c, host, 0x000000);
        await c.SendAsync(major, 1, b => b.U8(0).U8(2).U8(0).U8(0).U32(plain).I16(0).I16(0).I16(10).I16(8).U16(20).U16(10));
        await host.WaitForAsync(() => plainHandle.Snapshot.InputShape is not null);
        Assert.IsFalse(plainHandle.Snapshot.HasAlpha);
    }

    [TestMethod]
    public async Task 顶层改输入形状不重画_边界形状变大只Expose新露出来的部分()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        (uint top, _) = await MapTopAsync(c, host, 0x000000, 0x8000);   // ExposureMask
        await c.SendAsync(major, 1, b => b.U8(0).U8(0).U8(0).U8(0).U32(top).I16(0).I16(0).I16(0).I16(0).U16(30).U16(40));   // 边界:左半边
        await c.SyncAsync();
        async Task<List<XRect>> ExposedAsync()
        {
            await c.SyncAsync();
            List<XRect> rects = [];
            try
            {
                while (true)
                {
                    XMessage e = await c.NextEventAsync(12, timeoutMs: 100);
                    rects.Add(new XRect(e.U16(8), e.U16(10), e.U16(12), e.U16(14)));
                }
            }
            catch (OperationCanceledException)
            {
            }
            return rects;
        }
        await ExposedAsync();   // 映射与第一次设形状的 Expose 读掉

        await c.SendAsync(major, 1, b => b.U8(0).U8(2).U8(0).U8(0).U32(top).I16(0).I16(0).I16(0).I16(0).U16(10).U16(10));   // 输入形状
        Assert.IsEmpty(await ExposedAsync(), "输入形状不改看得见的东西:原先整窗重画、整窗 Expose");

        await c.SendAsync(major, 1, b => b.U8(0).U8(0).U8(0).U8(0).U32(top).I16(0).I16(0).I16(0).I16(0).U16(60).U16(40));   // 边界:整个窗口
        List<XRect> exposed = await ExposedAsync();
        Assert.AreEqual(30 * 40, exposed.Sum(r => r.Width * r.Height), "只有新露出来的右半边");
        Assert.IsTrue(exposed.All(r => r.X >= 30));
    }

    [TestMethod]
    public async Task 设形状发ShapeNotify且宿主拿到顶层形状()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        (uint top, XTopLevelWindow handle) = await MapTopAsync(c, host, 0x123456);
        Assert.IsNull(handle.Snapshot.Shape);

        await c.SendAsync(major, 6, b => b.U32(top).U8(1).U8(0).U8(0).U8(0));   // SelectInput
        await c.SendAsync(major, 1, b => b.U8(0).U8(0).U8(0).U8(0).U32(top).I16(0).I16(0)
            .I16(0).I16(0).U16(30).U16(40).I16(30).I16(10).U16(30).U16(10));
        XMessage notify = await c.NextEventAsync(64);
        Assert.AreEqual(0, notify.Detail, "kind = Bounding");
        Assert.AreEqual(top, notify.U32(4));
        Assert.AreEqual(60, notify.U16(12), "extents 宽");
        Assert.AreEqual(1, notify.Bytes[20], "shaped");

        await host.WaitForAsync(() => handle.Snapshot.Shape is not null);
        Assert.AreEqual(1200 + 300, handle.Snapshot.Shape!.Sum(r => r.Width * r.Height));

        // Mask 源为 None:回到默认矩形。
        await c.SendAsync(major, 2, b => b.U8(0).U8(0).U8(0).U8(0).U32(top).I16(0).I16(0).U32(0));
        await host.WaitForAsync(() => handle.Snapshot.Shape is null);
    }

    [TestMethod]
    public async Task 输入形状之外的点击落到父窗口()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        (uint top, _) = await MapTopAsync(c, host, 0, mask: 0x4);   // ButtonPress

        uint child = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(child).U32(top).I16(0).I16(0).U16(40).U16(40).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x4));
        await c.SendAsync(8, 0, b => b.U32(child));
        // 输入形状(kind 2)只留左上 10×10。
        await c.SendAsync(major, 1, b => b.U8(0).U8(2).U8(0).U8(0).U32(child).I16(0).I16(0).I16(0).I16(0).U16(10).U16(10));
        await c.SyncAsync();

        server.InjectPointerButton(host.Mapped[top], 5, 5, 1, pressed: true);
        server.InjectPointerButton(host.Mapped[top], 5, 5, 1, pressed: false);
        XMessage inside = await c.NextEventAsync(4);
        Assert.AreEqual(child, inside.U32(12), "形状里面点到子窗口");

        server.InjectPointerButton(host.Mapped[top], 30, 30, 1, pressed: true);
        XMessage outside = await c.NextEventAsync(4);
        Assert.AreEqual(top, outside.U32(12), "形状外面穿过子窗口落到父窗口");
    }

    [TestMethod]
    public async Task 两万块矩形的形状回BadAlloc_不把执行线程拖住()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        uint window = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(window).U32(c.RootWindow).I16(0).I16(0).U16(400).U16(400).U16(0).U16(1).U32(0).U32(0));

        // 两万个互不相接的 1×1:旧的矩形表实现每加一块都要与已有的每一块互减。
        XMessage error = await c.RequestAsync(major, 1, b =>
        {
            b.U8(0).U8(0).U8(0).U8(0).U32(window).I16(0).I16(0);   // Set、Bounding、UnSorted
            for (int i = 0; i < 20000; i++)
            {
                b.I16((short)(i % 200 * 2)).I16((short)(i / 200 * 2)).U16(1).U16(1);
            }
        });
        Assert.IsTrue(error.IsError);
        Assert.AreEqual(11, error.Detail, "BadAlloc");
    }
}
