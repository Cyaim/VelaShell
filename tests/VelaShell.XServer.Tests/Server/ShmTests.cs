using System.Runtime.InteropServices;
using System.Text;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>MIT-SHM:只对经 Unix 套接字连进来的客户端可见;段附加、ShmPutImage / ShmGetImage 经共享内存搬像素。只在 Linux 上跑。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed partial class ShmTests
{
    private static async Task<(byte Major, byte Event)?> ShmAsync(XTestClient c)
    {
        byte[] name = Encoding.Latin1.GetBytes("MIT-SHM");
        XMessage q = await c.RequestAsync(98, 0, b => b.U16((ushort)name.Length).U16(0).Bytes(name).Pad());
        return q.Bytes[8] == 1 ? (q.Bytes[9], q.Bytes[10]) : null;
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "MIT-SHM 只在 Linux 上提供")]
    public async Task 经Unix套接字的客户端用共享内存搬像素_别的客户端看不见这个扩展()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vx-shm-{Guid.NewGuid():N}.sock");
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path });
        await server.StartAsync();

        await using (XTestClient remote = await XTestClient.ConnectAsync(server))
        {
            Assert.IsNull(await ShmAsync(remote), "不经 Unix 套接字(比如 SSH 转发来的)看不见 MIT-SHM");
        }

        await using XTestClient c = await XTestClient.ConnectUnixAsync(path);
        (byte shm, byte completion) = await ShmAsync(c) ?? throw new AssertFailedException("Unix 套接字上应当有 MIT-SHM");
        XMessage version = await c.RequestAsync(shm, 0);
        Assert.AreEqual(1, version.U16(8), "major 1");
        Assert.AreEqual(0, version.Bytes[1], "shared-pixmaps = False");

        const int size = 16 * 16 * 4;
        int shmid = ShmGet(0, size, 0x380);   // IPC_PRIVATE,IPC_CREAT | 0600
        Assert.IsGreaterThanOrEqualTo(0, shmid);
        nint address = ShmAt(shmid, 0, 0);
        try
        {
            _ = ShmCtl(shmid, 0, 0);   // IPC_RMID:最后一个摘下时释放
            byte[] red = new byte[size];
            for (int i = 0; i < size; i += 4)
            {
                red[i + 2] = 0xFF;   // 0x00FF0000 小端
            }
            Marshal.Copy(red, 0, address, size);

            uint pixmap = c.NewId(), gc = c.NewId(), segment = c.NewId();
            await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(16).U16(16));
            await c.SendAsync(55, 0, b => b.U32(gc).U32(pixmap).U32(0x4).U32(0x00FF00));
            await c.SendAsync(shm, 1, b => b.U32(segment).U32((uint)shmid).U8(0).U8(0).U16(0));   // Attach
            // ShmPutImage:整幅 16×16,ZPixmap、深度 24,要 Completion 事件。
            await c.SendAsync(shm, 3, b => b.U32(pixmap).U32(gc).U16(16).U16(16).U16(0).U16(0).U16(16).U16(16)
                .I16(0).I16(0).U8(24).U8(2).U8(1).U8(0).U32(segment).U32(0));
            XMessage done = await c.NextEventAsync(completion);
            Assert.AreEqual(segment, done.U32(12), "Completion 带着段");

            XMessage image = await c.RequestAsync(73, 2, b => b.U32(pixmap).I16(5).I16(5).U16(1).U16(1).U32(0xFFFFFFFF));
            Assert.AreEqual(0xFF0000u, image.U32(32) & 0xFFFFFF, "共享内存里的红色画进了像素图");

            // 像素图填绿,ShmGetImage 写回段里。
            await c.SendAsync(70, 0, b => b.U32(pixmap).U32(gc).I16(0).I16(0).U16(16).U16(16));
            XMessage got = await c.RequestAsync(shm, 4, b => b.U32(pixmap).I16(0).I16(0).U16(16).U16(16).U32(0xFFFFFFFF)
                .U8(2).U8(0).U16(0).U32(segment).U32(0));
            Assert.AreEqual((uint)size, got.U32(12), "size");
            byte[] back = new byte[4];
            Marshal.Copy(address, back, 0, 4);
            Assert.AreEqual(0xFF, back[1], "绿色写回了共享内存");
            await c.SendAsync(shm, 2, b => b.U32(segment));   // Detach
            await c.SyncAsync();
        }
        finally
        {
            _ = ShmDt(address);
        }
    }

    [TestMethod]
    public async Task 段的XID别的客户端来用时按附加时记下的属主与权限再核一次()
    {
        await using X11Server server = new();
        XClient owner = new(1, bigEndian: false) { PeerUid = 1000 };
        XClient sameUser = new(2, bigEndian: false) { PeerUid = 1000 };
        XClient otherUser = new(3, bigEndian: false) { PeerUid = 2000 };
        XClient unknown = new(4, bigEndian: false);   // 取不到 uid
        // 段:属主 1000、权限 0644(别人只能读);地址 0 = 没真的映射,只测核对。
        XShmSegment segment = new(owner.ResourceBase | 1, owner, shmid: 7, readOnly: false, address: 0,
            new XShmAccess(Size: 64, Uid: 1000, Cuid: 1000, Perms: Convert.ToInt32("644", 8)));

        await server.InvokeAsync(() =>
        {
            server.AddResource(owner, segment);
            Assert.AreSame(segment, server.Segment(owner, segment.Id, write: true), "附加它的客户端");
            Assert.AreSame(segment, server.Segment(sameUser, segment.Id, write: true), "同一个用户");
            Assert.AreSame(segment, server.Segment(otherUser, segment.Id, write: false), "别的用户:0644 可读(ShmPutImage)");
            Assert.AreEqual(XErrorCode.Access, Assert.Throws<XProtocolError>(() => server.Segment(otherUser, segment.Id, write: true)).Code,
                "别的用户:不可写(ShmGetImage 会往段里写)");
            Assert.AreEqual(XErrorCode.Access, Assert.Throws<XProtocolError>(() => server.Segment(unknown, segment.Id, write: false)).Code,
                "取不到身份的一律不给");
            return true;
        });
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "MIT-SHM 只在 Linux 上提供")]
    public async Task QueryVersion回服务端的有效uid与gid()
    {
        // 规范:uid / gid 是服务端的有效 uid / gid(客户端据此决定段的权限给谁);原先一律回 0。
        string path = Path.Combine(Path.GetTempPath(), $"vx-shm-{Guid.NewGuid():N}.sock");
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path });
        await server.StartAsync();
        await using XTestClient c = await XTestClient.ConnectUnixAsync(path);
        (byte shm, _) = await ShmAsync(c) ?? throw new AssertFailedException("Unix 套接字上应当有 MIT-SHM");
        XMessage version = await c.RequestAsync(shm, 0);
        Assert.AreEqual((ushort)GetEffectiveUid(), version.U16(12), "uid");
        Assert.AreEqual((ushort)GetEffectiveGid(), version.U16(14), "gid");
        Assert.AreEqual(2, version.Bytes[16], "pixmap-format = ZPixmap");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "MIT-SHM 只在 Linux 上提供")]
    public async Task 反复Attach错的shmid时每秒失败几次之后不再整读段表_过了这一秒照常()
    {
        // 原先每条 Attach 都整读、整份拆分一遍 /proc/sysvipc/shm:一串错的 shmid 就能持续占着执行线程。
        string path = Path.Combine(Path.GetTempPath(), $"vx-shm-{Guid.NewGuid():N}.sock");
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path });
        await server.StartAsync();
        await using XTestClient c = await XTestClient.ConnectUnixAsync(path);
        (byte shm, _) = await ShmAsync(c) ?? throw new AssertFailedException("Unix 套接字上应当有 MIT-SHM");
        int before = server.ShmTableReads;
        await c.SendManyAsync(Enumerable.Range(0, 200).Select<int, (byte, byte, Action<XTestClient.Body>?)>(i =>
            (shm, 1, b => b.U32(c.NewId()).I32(int.MaxValue - i).U8(0).U8(0).U8(0).U8(0))));   // Attach(不存在的 shmid)
        await c.SyncAsync();
        Assert.IsLessThanOrEqualTo(3 * X11Server.MaxShmAttachFailuresPerSecond, server.ShmTableReads - before, "失败几次之后不再读段表(200 条最多跨两三秒)");

        await Task.Delay(1100);
        int shmid = ShmGet(0, 64, 0x380);   // IPC_PRIVATE,IPC_CREAT | 0600
        Assert.IsGreaterThanOrEqualTo(0, shmid);
        try
        {
            uint segment = c.NewId();
            ushort attach = await c.SendAsync(shm, 1, b => b.U32(segment).I32(shmid).U8(0).U8(0).U8(0).U8(0));
            await c.SendAsync(shm, 2, b => b.U32(segment));   // Detach:Attach 成了才不报 BadShmSeg
            await c.SyncAsync();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => c.NextAsync(m => m.IsError && m.Sequence >= attach, timeoutMs: 200), "过了这一秒,对的 shmid 照常 Attach");
        }
        finally
        {
            _ = ShmCtl(shmid, 0, 0);   // IPC_RMID
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUid();

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint GetEffectiveGid();

    [LibraryImport("libc", EntryPoint = "shmget")]
    private static partial int ShmGet(int key, nint size, int flags);

    [LibraryImport("libc", EntryPoint = "shmat")]
    private static partial nint ShmAt(int shmid, nint address, int flags);

    [LibraryImport("libc", EntryPoint = "shmdt")]
    private static partial int ShmDt(nint address);

    [LibraryImport("libc", EntryPoint = "shmctl")]
    private static partial int ShmCtl(int shmid, int command, nint buffer);
}
