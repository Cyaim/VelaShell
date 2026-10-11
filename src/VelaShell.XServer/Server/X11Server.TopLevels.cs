// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   ICCCM 2.0 —— §4.1.2.1 WM_NAME、§4.1.2.5 WM_CLASS、§4.1.2.6 WM_TRANSIENT_FOR、§4.1.2.7 WM_PROTOCOLS、
//   §4.1.5(窗口管理器移动顶层后发合成的 ConfigureNotify,根坐标)、§4.2.8(WM_DELETE_WINDOW)
//   EWMH 1.5 —— §5「Application Window Properties」(_NET_WM_NAME、_NET_WM_SYNC_REQUEST_COUNTER)、
//   §6.2「_NET_WM_SYNC_REQUEST」(改尺寸前的 ClientMessage、序号的高低 32 位、第一次管窗口时设计数器)
//   架构:velashell-docs/zh/xserver/design/architecture.md §6(rootless:宿主就是窗口管理器)
//
//   服务端与宿主之间关于顶层窗口的一切:给宿主的句柄与快照、快照的变化、损伤的交付,
//   以及宿主作为窗口管理器对顶层做的动作(焦点、移动、缩放、关闭、状态、外框)。

using System.Runtime.InteropServices;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    // ------------------------------------------------------------------ 句柄与快照

    private XTopLevelWindow HandleFor(XWindow top)
    {
        if (!_topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
        {
            handle = new XTopLevelWindow(top, _pixelGate, this);
            _topLevelHandles[top] = handle;
        }
        return handle;
    }

    /// <summary>从窗口与它的 ICCCM / EWMH 属性重建宿主看到的快照,返回比上一份变了哪几组。</summary>
    private XTopLevelChanges RefreshSnapshot(XWindow top, XTopLevelWindow handle)
    {
        XTopLevelSnapshot previous = handle.Snapshot;
        XTopLevelSnapshot next = BuildSnapshot(top, previous);
        handle.Snapshot = next;
        return Diff(previous, next);
    }

    /// <summary>顶层窗口的几何、形状或属性可能变了:刷新快照,真有变化且映射中时告诉宿主。</summary>
    private void RefreshTopLevel(XWindow top)
    {
        if (_topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle)
            && RefreshSnapshot(top, handle) is var changes and not XTopLevelChanges.None
            && top.Mapped && !Rootful)
        {
            _host.TopLevelChanged(handle, changes);
        }
    }

    /// <summary>顶层窗口的属性变了:标题、类名、协议、提示可能跟着变。</summary>
    private void OnTopLevelPropertyChanged(XWindow window, uint property)
    {
        OnTrayIconPropertyChanged(window, property);   // 停靠着的托盘图标跟着 _XEMBED_INFO 映射 / 取消映射
        if (window.IsTopLevel && AffectsHandle(property))   // _NET_WM_USER_TIME 之类每次输入都改,与宿主无关
        {
            RefreshTopLevel(window);
        }
    }

    private static void SetMapped(XTopLevelWindow handle, bool mapped) => handle.Snapshot = handle.Snapshot with { IsMapped = mapped };

    /// <summary>窗口不再是这个句柄的顶层了(销毁、被 reparent 走):快照标成未映射,句柄的 <see cref="XTopLevelWindow.IsAlive" /> 变 false。</summary>
    private static void RetireHandle(XTopLevelWindow handle)
    {
        SetMapped(handle, false);
        handle.Retire();
    }

    private XTopLevelSnapshot BuildSnapshot(XWindow top, XTopLevelSnapshot previous)
    {
        Dictionary<uint, XProperty> props = top.Properties;
        // 字符串只取有限的一段、去掉控制字符(见 HostText):属性能有 32 MB,快照每次几何刷新都要重建。
        string title = props.TryGetValue(_netWmNameAtom, out XProperty? utf8) && utf8.Format == 8
            ? HostText(utf8.Data, utf8: true, MaxHostTitleChars)
            : props.TryGetValue(XAtom.WmName, out XProperty? name) && name.Format == 8
                ? HostText(name.Data, TextEncodingOf(name.Type), MaxHostTitleChars)   // WM_NAME 是 TEXT:类型可以是 STRING、UTF8_STRING、COMPOUND_TEXT
                : "";

        string className = "", instanceName = "";
        if (props.TryGetValue(XAtom.WmClass, out XProperty? cls) && cls.Format == 8)
        {
            // WM_CLASS = "instance\0class\0"
            ReadOnlySpan<byte> data = cls.Data[..Math.Min(cls.Data.Length, (2 * MaxHostNameChars) + 2)];
            int split = data.IndexOf((byte)0);
            ReadOnlySpan<byte> classPart = split < 0 ? data : data[(split + 1)..];
            int end = classPart.IndexOf((byte)0);
            className = HostText(end < 0 ? classPart : classPart[..end], utf8: false, MaxHostNameChars);
            instanceName = split < 0 ? "" : HostText(data[..split], utf8: false, MaxHostNameChars);
        }

        uint transientId = props.TryGetValue(XAtom.WmTransientFor, out XProperty? transient) && transient is { Format: 32, Data.Length: >= 4 }
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(transient.Data)
            : 0;
        XTopLevelWindow? transientFor = transientId != 0 && Lookup<XWindow>(transientId) is { IsTopLevel: true } parent && !ReferenceEquals(parent, top)
            ? HandleFor(parent)
            : null;

        IReadOnlyList<XRect>? shape = top.BoundingShape is { } bounding
            ? [.. bounding.Clone().Intersect(new XRect(0, 0, top.Width, top.Height)).Rects]
            : null;
        if (shape is not null && previous.Shape is not null && shape.SequenceEqual(previous.Shape))
        {
            shape = previous.Shape;   // 形状没变就沿用上一份:宿主按引用判断要不要整窗重画
        }
        // 输入形状:SHAPE 1.1 的有效输入区是输入形状与有效边界形状的交集。原先快照里没有,宿主做不出「形状以外不接收鼠标」。
        IReadOnlyList<XRect>? inputShape = top.InputShape is { } input
            ? [.. input.Clone().Intersect(EffectiveShape(top, ShapeBounding)).Intersect(new XRect(0, 0, top.Width, top.Height)).Rects]
            : null;
        if (inputShape is not null && previous.InputShape is not null && inputShape.SequenceEqual(previous.InputShape))
        {
            inputShape = previous.InputShape;
        }

        (int x, int y) = PendingMove(top) ?? (top.X, top.Y);   // 宿主拖着、还没交给客户端的位置:原生窗口已经在那儿了
        XTopLevelSnapshot snapshot = previous with
        {
            X = x,
            Y = y,
            Width = top.Width,
            Height = top.Height,
            BorderWidth = top.BorderWidth,
            NeedsPlacement = top.PositionRequested && !top.OverrideRedirect,
            Title = title,
            ClassName = className,
            InstanceName = instanceName,
            OverrideRedirect = top.OverrideRedirect,
            TransientFor = transientFor,
            SupportsDeleteWindow = SupportsProtocol(top, _wmDeleteWindowAtom),
            ClientId = top.Owner?.Index ?? 0,
            // 托盘图标的嵌入窗口是服务端的,来源取停靠进来的那个图标所属的连接(宿主据此在提示里标出来源)。
            ClientLabel = top.Owner?.Label ?? TrayIconOwnerLabel(top),
            HasAlpha = HasAlphaOnHost(top, inputShape),
            InputOnly = top.IsInputOnly,
            Shape = shape,
            InputShape = inputShape,
        };
        return ReadWindowManagerHints(top, snapshot, hasTransientFor: transientId != 0);
    }

    /// <summary>
    /// 宿主按逐像素透明显示这个顶层吗:深度 32(ARGB 视觉)的窗口,而且服务端在当合成管理器(<see cref="X11ServerOptions.CompositingManager" />,
    /// 单窗口模式下不当)—— 没有合成管理器时 X 显示 ARGB 窗口不看 alpha。原先只看深度:设置关着(默认)时,要了 8 位 alpha 的 GL 程序
    /// (GLFW 默认就要,只拿得到 ARGB 视觉)清屏的 alpha 是 0,整个窗口透出后面的东西,还白付了系统合成的开销。
    /// 托盘图标例外:托盘管理器就是服务端自己,ARGB 图标本来就是要透明的。
    /// 输入形状只占窗口一部分的 ARGB 窗口也例外:GTK 4 不论有没有合成管理器,弹出菜单(GtkPopover)都按 ARGB 视觉画、四周留二十来像素
    /// alpha 为 0 的阴影区,再用 SHAPE 的输入形状只圈出菜单本体 —— 不看 alpha 的话,那一圈在宿主上是一道黑边(gnome-calculator 的模式下拉菜单实测)。
    /// 客户端自己声明了「这一圈不是窗口的一部分」,照 alpha 显示它;画不透明的 GL 程序不设输入形状,仍按不透明显示。
    /// </summary>
    private bool HasAlphaOnHost(XWindow top, IReadOnlyList<XRect>? inputShape) =>
        top.Depth == 32 && (IsTrayEmbedder(top) || (!Rootful && (_options.CompositingManager || CoversPart(inputShape, top))));

    /// <summary>输入形状(已经裁到窗口里)只占窗口的一部分:四周有客户端声明为「不接收输入」的地方。</summary>
    private static bool CoversPart(IReadOnlyList<XRect>? inputShape, XWindow top)
    {
        if (inputShape is null)
        {
            return false;
        }
        long area = 0;
        foreach (XRect r in inputShape)
        {
            area += (long)r.Width * r.Height;   // 区域的矩形互不重叠(按 y 分带)
        }
        return area < (long)top.Width * top.Height;
    }

    /// <summary>两份快照之间哪几组字段不同;不属于前几组的字段一律算 <see cref="XTopLevelChanges.Hints" />。</summary>
    private static XTopLevelChanges Diff(XTopLevelSnapshot a, XTopLevelSnapshot b)
    {
        XTopLevelChanges changes = XTopLevelChanges.None;
        if (a.X != b.X || a.Y != b.Y || a.Width != b.Width || a.Height != b.Height || a.BorderWidth != b.BorderWidth
            || a.NeedsPlacement != b.NeedsPlacement)
        {
            changes |= XTopLevelChanges.Geometry;
        }
        if (a.Title != b.Title || a.ClassName != b.ClassName || a.InstanceName != b.InstanceName)
        {
            changes |= XTopLevelChanges.Title;
        }
        if (a.States != b.States)
        {
            changes |= XTopLevelChanges.States;
        }
        if (!ReferenceEquals(a.Icons, b.Icons))
        {
            changes |= XTopLevelChanges.Icons;
        }
        if (!ReferenceEquals(a.Shape, b.Shape) || !ReferenceEquals(a.InputShape, b.InputShape))
        {
            changes |= XTopLevelChanges.Shape;
        }
        // 把已经比过的字段抹平之后整份比较:以后加的字段自动归进 Hints,不会漏报。
        XTopLevelSnapshot rest = a with
        {
            X = b.X,
            Y = b.Y,
            Width = b.Width,
            Height = b.Height,
            BorderWidth = b.BorderWidth,
            NeedsPlacement = b.NeedsPlacement,
            IsMapped = b.IsMapped,
            Title = b.Title,
            ClassName = b.ClassName,
            InstanceName = b.InstanceName,
            States = b.States,
            Icons = b.Icons,
            Shape = b.Shape,
            InputShape = b.InputShape,
        };
        if (rest != b)
        {
            changes |= XTopLevelChanges.Hints;
        }
        return changes;
    }

    /// <summary>客户端在 WM_PROTOCOLS 里声明了这个协议(WM_DELETE_WINDOW、WM_TAKE_FOCUS)。</summary>
    private bool SupportsProtocol(XWindow top, uint protocol) =>
        top.Properties.TryGetValue(_wmProtocolsAtom, out XProperty? p) && p.Format == 32
        && MemoryMarshal.Cast<byte, uint>(p.Data[..(p.Data.Length & ~3)]).Contains(protocol);

    /// <summary>WM_HINTS 的 input 字段(ICCCM §4.1.2.4);没给(flags 里没有 InputHint)时按 True 算。</summary>
    private static bool AcceptsInputHint(XWindow top)
    {
        uint[] hints = ReadCard32s(top.Properties.GetValueOrDefault(XAtom.WmHints), 9);
        return hints.Length < 2 || (hints[0] & 1) == 0 || hints[1] != 0;
    }

    /// <summary>把这一批攒下的损伤一次性交给宿主(每个顶层合并成一组矩形)。</summary>
    private void FlushDamage()
    {
        if (_damage.Count == 0)
        {
            return;
        }
        KeyValuePair<XWindow, List<XRect>>[] batch = [.. _damage];
        _damage.Clear();
        foreach ((XWindow top, List<XRect> rects) in batch)
        {
            if (top.Mapped && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
            {
                _host.TopLevelDamaged(handle, rects);
            }
        }
    }

    // ------------------------------------------------------------------ 宿主作为窗口管理器的动作(见 X11Server.cs 的公开方法)

    /// <summary>
    /// 键盘焦点给 <paramref name="top" />;null = 所有顶层都失去焦点(焦点 None)。按 ICCCM §4.1.7 的四种输入模型:
    /// WM_HINTS.input 为 True(Passive、Locally Active)时由窗口管理器 SetInputFocus;声明了 WM_TAKE_FOCUS(Locally Active、
    /// Globally Active)时发 WM_TAKE_FOCUS,由客户端自己决定焦点给哪个窗口;两样都没有(No Input)什么也不做。
    /// override-redirect 窗口不归窗口管理器管(弹出菜单、提示框自己抓键盘),宿主激活了它的原生窗口也不动焦点。
    /// </summary>
    private void ApplyFocus(XWindow? top)
    {
        // 宿主换了焦点就是窗口管理器换了焦点:推进 last-focus-change time,早于此刻的客户端 SetInputFocus 随后都不生效
        // (见 SetFocusFromClient)。WM_TAKE_FOCUS 带的也是这个时间,客户端拿它回 SetInputFocus 照样生效。
        uint now = Math.Max(1u, Now);
        if (top is null)
        {
            _lastFocusChangeTime = now;
            SetFocus(null, 0);
            return;
        }
        if (top.OverrideRedirect || !top.IsViewable)
        {
            return;
        }
        _lastFocusChangeTime = now;
        RaiseAboveNormalTopLevels(top);
        if (AcceptsInputHint(top) && (_focus is null || ReferenceEquals(_focus, Root) || !ReferenceEquals(_focus.TopLevel, top)))
        {
            // 与窗口管理器的做法一致:把焦点给顶层,revert-to PointerRoot。客户端之后可以自己把焦点挪到子窗口。
            SetFocus(top, 1);
        }
        if (SupportsProtocol(top, _wmTakeFocusAtom) && top.Owner is { Closed: false } owner)
        {
            // ICCCM §4.2.8:ClientMessage,类型 WM_PROTOCOLS,data[0] = WM_TAKE_FOCUS,data[1] 是一个有效的时间戳(不是 CurrentTime)。
            owner.Event(XEventCode.ClientMessage, 32, w => w.U32(top.Id).U32(_wmProtocolsAtom).U32(_wmTakeFocusAtom).U32(now).Zero(12), sent: true);
        }
    }

    /// <summary>
    /// 宿主激活了这个顶层(用户把它的原生窗口提到了前面):X 这边也把它抬到普通顶层的最上面(override-redirect 的弹层仍在它之上),
    /// 发 ConfigureNotify、更新 <c>_NET_CLIENT_LIST_STACKING</c> —— 原先 X 的堆叠只随创建先后变,与屏幕上看到的次序对不上。
    /// </summary>
    private void RaiseAboveNormalTopLevels(XWindow top)
    {
        List<XWindow> siblings = Root.Children;
        int own = siblings.IndexOf(top);
        for (int i = siblings.Count - 1; i > own; i--)
        {
            if (!siblings[i].OverrideRedirect)
            {
                Configure(top, top.X, top.Y, top.Width, top.Height, top.BorderWidth, siblings[i], stackMode: 0);   // Above
                return;
            }
        }
    }

    /// <summary>
    /// 原生窗口被用户挪了:像真的移动窗口一样走 <see cref="Configure" /> —— 真实的 ConfigureNotify(窗口上选了 StructureNotify、
    /// 根窗口上选了 SubstructureNotify 的都收到)、Present 的 ConfigureNotify、重算指针所在的窗口;再按 ICCCM §4.1.5 补一条合成的
    /// ConfigureNotify(根坐标)。原先只发合成的那条,根窗口上的监听者收不到,指针所在的窗口也不重算。
    /// 宿主按重力摆好了客户端请求的位置(<see cref="XTopLevelSnapshot.NeedsPlacement" />)也走这里,之后不再要摆。
    /// 客户端还没处理完上一次移动时只记下最新的位置,等它处理完再交(见 <see cref="StartMoveBarrier" />)。
    /// </summary>
    private void ApplyMove(XWindow top, int x, int y)
    {
        bool placed = top.PositionRequested;
        top.PositionRequested = false;
        if (_moveBarriers.TryGetValue(top, out MoveBarrier? barrier))
        {
            barrier.Pending = (x, y);
            if (_topLevelHandles.TryGetValue(top, out XTopLevelWindow? waiting))
            {
                waiting.Snapshot = waiting.Snapshot with { X = x, Y = y, NeedsPlacement = false };   // 宿主自己挪的,不再回报
            }
            return;
        }
        if (top.X == x && top.Y == y)
        {
            if (placed && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? same))
            {
                same.Snapshot = same.Snapshot with { NeedsPlacement = false };   // 摆好的位置恰好就是请求的位置
            }
            return;
        }
        DeliverMove(top, x, y);
        StartMoveBarrier(top);
    }

    /// <summary>真的把顶层挪到 (<paramref name="x" />, <paramref name="y" />):真实的 ConfigureNotify,再补 ICCCM §4.1.5 的合成事件。</summary>
    private void DeliverMove(XWindow top, int x, int y)
    {
        if (_topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
        {
            // 宿主自己挪的,不再回报(快照先改好,Configure 比不出变化)
            handle.Snapshot = handle.Snapshot with { X = x, Y = y, NeedsPlacement = false };
        }
        Configure(top, x, y, top.Width, top.Height, top.BorderWidth, null, -1);
        DeliverToSelectors(top, XEventMask.StructureNotify, c => c.Event(XEventCode.ConfigureNotify, 0, w => w
            .U32(top.Id).U32(top.Id).U32(0).I16(x).I16(y).U16((ushort)top.Width).U16((ushort)top.Height)
            .U16((ushort)top.BorderWidth).Bool(top.OverrideRedirect), sent: true));
    }

    private void ApplyResize(XWindow top, int width, int height)
    {
        if (top.Width != width || top.Height != height)
        {
            BeginRedrawSync(top);   // _NET_WM_SYNC_REQUEST 要先于 ConfigureNotify 发出
            Configure(top, top.X, top.Y, width, height, top.BorderWidth, null, -1);
        }
    }

    // ------------------------------------------------------------------ 宿主移动的流量控制

    /// <summary>宿主挪过、客户端还没处理完那次移动的顶层 → 屏障(见 <see cref="StartMoveBarrier" />)。</summary>
    private readonly Dictionary<XWindow, MoveBarrier> _moveBarriers = [];

    /// <summary>一次移动交出去之后等客户端处理完:<see cref="Pending" /> 是这期间宿主报来的最新位置,<see cref="PingTime" /> 是随移动发的 ping(0 = 没发)。</summary>
    private sealed class MoveBarrier
    {
        public (int X, int Y)? Pending;
        public uint PingTime;
    }

    /// <summary>上一个移动屏障用的 ping 时间戳:同一毫秒里的两次移动也要分得开。</summary>
    private uint _lastMovePing;

    /// <summary>ping 迟迟不回(客户端卡住、忙)时,过了这么久照样交下一次移动(测试可以调短)。</summary>
    internal TimeSpan MovePingTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>没声明 <c>_NET_WM_PING</c> 的窗口,两次移动之间至少隔这么久(测试可以调)。</summary>
    internal TimeSpan MoveInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 宿主拖动原生窗口时每秒报几十上百次位置(Windows 每个 WM_MOVE 一次),原先每次都发一组 ConfigureNotify。GTK 处理一次移动要做几次
    /// 同步往返(gnome-calculator 实测每次五次:真实的那条两次 TranslateCoordinates 与一次 GetGeometry,合成的那条 GetGeometry 与
    /// TranslateCoordinates 各一次),而客户端按到达的先后一条条处理:往返 20 毫秒时拖一秒,松手之后程序还要冻住近九秒才处理完。
    /// 现在交出一次移动之后先等客户端处理完 —— 窗口声明了 <c>_NET_WM_PING</c> 就随移动发一条 ping(EWMH「_NET_WM_PING」),客户端按序处理事件,
    /// 回了 pong 就说明它前面的 ConfigureNotify 都处理完了;没声明的至少隔 <see cref="MoveInterval" />。等的期间宿主再报的位置只记最新的一个,
    /// 屏障放开时有新位置才交。链路越慢,中间位置交得越稀,松手后至多再处理一次。X 窗口在这期间停在上一次交出的位置 ——
    /// 什么时候真的挪 X 窗口本来就由窗口管理器决定,协议上与一个只在松手时才挪窗口的窗口管理器无异。
    /// </summary>
    private void StartMoveBarrier(XWindow top)
    {
        MoveBarrier barrier = new();
        _moveBarriers[top] = barrier;
        uint ping = Intern("_NET_WM_PING");
        TimeSpan wait = MoveInterval;
        if (top.Owner is { Closed: false } owner && !IsRetained(owner) && SupportsProtocol(top, ping))
        {
            uint time = Now;
            time = barrier.PingTime = time > _lastMovePing && time != 0 ? time : _lastMovePing + 1;
            _lastMovePing = time;
            owner.Event(XEventCode.ClientMessage, 32, w => w.U32(top.Id).U32(_wmProtocolsAtom).U32(ping).U32(time).U32(top.Id).Zero(8), sent: true);
            wait = MovePingTimeout;
        }
        _ = DelayThenPostAsync((uint)wait.TotalMilliseconds, () => ReleaseMoveBarrier(top, barrier), _lifetime.Token);
    }

    /// <summary>客户端处理完了上一次移动(pong 到了或等到了时限):期间宿主又报了新位置就交出去,开始下一道屏障。</summary>
    private void ReleaseMoveBarrier(XWindow top, MoveBarrier barrier)
    {
        if (!_moveBarriers.TryGetValue(top, out MoveBarrier? current) || !ReferenceEquals(current, barrier))
        {
            return;   // 已经放开过(pong 与时限都会来),或窗口已经不是顶层
        }
        _moveBarriers.Remove(top);
        if (barrier.Pending is { } pending && (pending.X != top.X || pending.Y != top.Y) && _topLevelHandles.ContainsKey(top))
        {
            DeliverMove(top, pending.X, pending.Y);
            StartMoveBarrier(top);
        }
    }

    /// <summary>客户端自己挪了顶层(ConfigureWindow、<c>_NET_MOVERESIZE_WINDOW</c>):宿主还没交出去的拖动位置作废,以客户端要的为准。</summary>
    private void DropPendingMove(XWindow top)
    {
        if (_moveBarriers.TryGetValue(top, out MoveBarrier? barrier))
        {
            barrier.Pending = null;
        }
    }

    /// <summary>宿主报来、还没交出去的位置(快照照它给,免得别的属性一变就把原生窗口拉回旧位置);没有为 null。</summary>
    private (int X, int Y)? PendingMove(XWindow top) =>
        _moveBarriers.TryGetValue(top, out MoveBarrier? barrier) ? barrier.Pending : null;

    // ------------------------------------------------------------------ _NET_WM_SYNC_REQUEST

    /// <summary>等客户端重画完的顶层:→ (它的 SYNC 计数器, 要等到的值)。</summary>
    private readonly Dictionary<XWindow, (XSyncCounter Counter, long Value)> _redrawSyncs = [];

    /// <summary>每个顶层发过的最后一个更新序号(从 1 起;第一次用时把计数器设成 0)。</summary>
    private readonly Dictionary<XWindow, long> _redrawSerials = [];

    /// <summary>客户端迟迟不把计数器推上去时,过了这么久不再等(测试可以调短)。</summary>
    internal TimeSpan RedrawSyncTimeout { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// EWMH「_NET_WM_SYNC_REQUEST」:宿主改一个顶层的尺寸时,窗口在 WM_PROTOCOLS 里声明了它、_NET_WM_SYNC_REQUEST_COUNTER 指着一个 SYNC 计数器,
    /// 就先发一条 ClientMessage(data:_NET_WM_SYNC_REQUEST、时间戳、更新序号的低 32 位、高 32 位),再发 ConfigureNotify;客户端处理完、重画完
    /// 把计数器设成这个序号(见 <see cref="CheckRedrawSync" />)。等的期间顶层句柄的 <see cref="XTopLevelWindow.AwaitingRedraw" /> 为真,
    /// 宿主据此先不把画到一半的帧显示出来,等 <see cref="IX11ServerHost.TopLevelRedrawn" /> 再显示 —— 拖动缩放 GTK / Qt 窗口不再闪出没画完的空白。
    /// 规范:窗口管理器第一次管一个窗口时必须设一次计数器的值(这里设成 0)。
    /// </summary>
    private void BeginRedrawSync(XWindow top)
    {
        uint request = Intern("_NET_WM_SYNC_REQUEST");
        if (top.Owner is not { Closed: false } owner || !SupportsProtocol(top, request) || RedrawCounterOf(top) is not { } counter
            || !_topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
        {
            return;
        }
        if (!_redrawSerials.TryGetValue(top, out long serial))
        {
            counter.Value = 0;
            EvaluateSync();
        }
        long value = ++serial;
        _redrawSerials[top] = serial;
        uint time = Now;
        _redrawSyncs[top] = (counter, value);
        handle.AwaitingRedraw = true;   // 先置上再发:客户端收到请求时宿主已经看得到在等
        owner.Event(XEventCode.ClientMessage, 32, w => w.U32(top.Id).U32(_wmProtocolsAtom).U32(request).U32(time)
            .U32(unchecked((uint)value)).U32((uint)(value >> 32)).Zero(4), sent: true);
        _ = DelayThenPostAsync((uint)RedrawSyncTimeout.TotalMilliseconds, () =>
        {
            if (_redrawSyncs.TryGetValue(top, out (XSyncCounter Counter, long Value) pending) && pending.Value == value)
            {
                EndRedrawSync(top);   // 客户端没跟上:不再等,画到哪儿显示到哪儿
            }
        }, _lifetime.Token);
    }

    /// <summary>窗口的 _NET_WM_SYNC_REQUEST_COUNTER(CARDINAL/32,扩展形式有两个,第一个是基本计数器)指着的 SYNC 计数器。</summary>
    private XSyncCounter? RedrawCounterOf(XWindow top) =>
        top.Properties.TryGetValue(Intern("_NET_WM_SYNC_REQUEST_COUNTER"), out XProperty? property) && property.Format == 32 && property.Data.Length >= 4
            ? Lookup<XSyncCounter>(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(property.Data))
            : null;

    /// <summary>某个 SYNC 计数器的值变了:等它的顶层到了要等的值就算重画完了。</summary>
    private void CheckRedrawSync(XSyncCounter counter)
    {
        if (_redrawSyncs.Count == 0)
        {
            return;
        }
        foreach ((XWindow top, (XSyncCounter Counter, long Value) pending) in _redrawSyncs.ToArray())
        {
            if (ReferenceEquals(pending.Counter, counter) && counter.Value >= pending.Value)
            {
                EndRedrawSync(top);
            }
        }
    }

    /// <summary>窗口不再是顶层(销毁、被 reparent 走):忘掉它的同步状态(句柄已经作废,不必再报)。</summary>
    private void ForgetRedrawSync(XWindow window)
    {
        _redrawSyncs.Remove(window);
        _redrawSerials.Remove(window);
        _moveBarriers.Remove(window);   // 还没交出去的拖动位置也不再交
    }

    private void EndRedrawSync(XWindow top)
    {
        _redrawSyncs.Remove(top);
        if (_topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle) && handle.AwaitingRedraw)
        {
            handle.AwaitingRedraw = false;
            _host.TopLevelRedrawn(handle);
        }
    }

    /// <summary>关闭:声明了 WM_DELETE_WINDOW 就发 ClientMessage 请它自己关(ICCCM §4.2.8),否则断开它的客户端。</summary>
    private void ApplyClose(XWindow top)
    {
        if (top.Owner is not { } owner || top.OverrideRedirect)
        {
            return;   // override-redirect 的弹层不归窗口管理器管(同 FocusTopLevel):关它不会去断开整个客户端
        }
        if (SupportsProtocol(top, _wmDeleteWindowAtom) && !IsRetained(owner))
        {
            uint time = Now;
            owner.Event(XEventCode.ClientMessage, 32, w => w.U32(top.Id).U32(_wmProtocolsAtom).U32(_wmDeleteWindowAtom).U32(time).Zero(12), sent: true);
            Ping(top, owner, time);
            return;
        }
        // 没声明 WM_DELETE_WINDOW:断开它(与窗口管理器的 XKillClient 一致)。已经以 Retain 模式断开的客户端消息发不过去、
        // 也没有连接可断,原先窗口成了关不掉的僵尸 —— 销毁它留下的资源。
        KillClientOf(owner);
    }

    /// <summary>_NET_WM_PING 发出去还没回的:顶层 → 发出时的时间戳。</summary>
    private readonly Dictionary<XWindow, uint> _pendingPings = [];

    /// <summary>ping 等回应的时限,过了还没回就告诉宿主它无响应(测试可以调短)。</summary>
    internal TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// EWMH「_NET_WM_PING」:窗口在 WM_PROTOCOLS 里声明了它,就随关闭请求发一条 ping(ClientMessage,data 依次是 _NET_WM_PING、
    /// 时间戳、窗口),客户端应当把它原样发回根窗口(见 <see cref="OnPong" />)。<see cref="PingTimeout" /> 之内没回就请宿主处理
    /// (<see cref="XNotRespondingRequest" />):声明了 WM_DELETE_WINDOW 却卡死的程序,原先用户关不掉,只能停掉整个 X Server。
    /// </summary>
    private void Ping(XWindow top, XClient owner, uint time)
    {
        uint ping = Intern("_NET_WM_PING");
        if (!SupportsProtocol(top, ping) || _pendingPings.ContainsKey(top))
        {
            return;
        }
        _pendingPings[top] = time;
        owner.Event(XEventCode.ClientMessage, 32, w => w.U32(top.Id).U32(_wmProtocolsAtom).U32(ping).U32(time).U32(top.Id).Zero(8), sent: true);
        _ = DelayThenPostAsync((uint)PingTimeout.TotalMilliseconds, () => PingExpired(top, time), _lifetime.Token);
    }

    private void PingExpired(XWindow top, uint time)
    {
        if (!_pendingPings.TryGetValue(top, out uint sent) || sent != time)
        {
            return;   // 回过了
        }
        _pendingPings.Remove(top);
        if (top.Mapped && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
        {
            _host.WindowManagerRequested(new XNotRespondingRequest(handle));
        }
    }

    /// <summary>客户端回了 ping:发回根窗口的 ClientMessage,data[1] 是时间戳、data[2] 是它的窗口。</summary>
    private void OnPong(uint time, uint windowId)
    {
        if (Lookup<XWindow>(windowId) is not { } window)
        {
            return;
        }
        if (_pendingPings.TryGetValue(window, out uint sent) && sent == time)
        {
            _pendingPings.Remove(window);
        }
        if (_moveBarriers.TryGetValue(window, out MoveBarrier? barrier) && barrier.PingTime == time)
        {
            ReleaseMoveBarrier(window, barrier);   // 随移动发的 ping 回来了:那次移动处理完了
        }
    }

    /// <summary>宿主设定的窗口状态写进 _NET_WM_STATE / WM_STATE;Focused 位由服务端按焦点维护,保留现值。</summary>
    private void ApplyStates(XWindow top, XWindowStates states)
    {
        XWindowStates focused = ReadNetWmStates(top) & XWindowStates.Focused;
        WriteStates(top, (states & ~XWindowStates.Focused) | focused);
    }

    private void ApplyFrameExtents(XWindow top, XFrameExtents extents)
    {
        _frameExtents[top] = extents;
        WriteFrameExtents(top);
    }
}
