// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 3 节「Window Hierarchy」、「CreateWindow」一节
//   (属性默认值、InputOnly 的限制)、「GetWindowAttributes」(map-state)

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;

namespace VelaShell.XServer.Windowing;

/// <summary>
/// 一个客户端在一个窗口上的 XI2 事件选择。按 deviceid 分开存(XI2 协议「XISelectEvents」:每个设备一份掩码,再选同一个设备只换它那一份,
/// 掩码长度为 0 清掉它;0 = XIAllDevices、1 = XIAllMasterDevices),投递时看按设备种类合起来的 <see cref="Master" /> / <see cref="Slave" />
/// (由服务端按当前的设备层级算)。原先每窗口每客户端只记主、从两个掩码:先给 XIAllMasterDevices 选原始事件、再给 XIAllDevices 选
/// HierarchyChanged,前一份就被盖掉;一次请求里给设备 2 选指针事件、给设备 3 选按键,指针事件也全丢。
/// </summary>
internal sealed class XiSelection
{
    /// <summary>deviceid → 掩码(按 evtype 的位)。</summary>
    public SortedDictionary<ushort, ulong> ByDevice { get; } = [];

    /// <summary>主设备的事件看的掩码:XIAllDevices、XIAllMasterDevices 与各主设备那一份的并集。</summary>
    public ulong Master { get; set; }

    /// <summary>从设备的事件看的掩码:XIAllDevices 与各从设备那一份的并集。</summary>
    public ulong Slave { get; set; }

    public void Deconstruct(out ulong master, out ulong slave) => (master, slave) = (Master, Slave);
}

/// <summary>窗口上的一个属性(ChangeProperty 存进来的东西)。</summary>
/// <remarks>
/// 不可变:改一个属性就换一个新对象(快照、图标缓存按引用判断变没变)。追加(ChangeProperty 的 Append)与上一版共用一块
/// 留了余量的存储,只拷新加的那几个字节 —— 原先每次追加都整份复制,往 30 MB 的属性上一次追加 4 字节就是 30 MB 的复制。
/// 旧对象看到的仍是它自己的那一段(新加的字节写在它的长度之外)。
/// </remarks>
internal sealed class XProperty
{
    private readonly Storage _storage;

    public XProperty(uint type, byte format, byte[] data)
        : this(type, format, new Storage(data, data.Length), data.Length)
    {
    }

    private XProperty(uint type, byte format, Storage storage, int length)
    {
        Type = type;
        Format = format;
        _storage = storage;
        Length = length;
    }

    /// <summary>类型原子。</summary>
    public uint Type { get; }

    /// <summary>8 / 16 / 32。</summary>
    public byte Format { get; }

    /// <summary>值的字节数。</summary>
    public int Length { get; }

    /// <summary>值:按<b>存进来的那个客户端的字节序</b>规整成本机序存放,取出时再按取的人的字节序写出(只在执行线程上读)。</summary>
    public ReadOnlySpan<byte> Data => _storage.Bytes.AsSpan(0, Length);

    /// <summary>值记在谁的账上(写它的客户端);服务端自己写的为 null(见 <c>X11Server.ChargeMemory</c>)。</summary>
    public XClient? ChargedTo { get; init; }

    /// <summary>在末尾接上 <paramref name="tail" />,返回新的属性对象(本对象不变)。</summary>
    public XProperty Append(ReadOnlySpan<byte> tail, XClient? chargedTo)
    {
        int length = Length + tail.Length;
        Storage storage = _storage;
        if (storage.Used != Length || storage.Bytes.Length < length)
        {
            // 存储已被别的版本接着写过、或者放不下:另起一块,容量翻倍(最多到单个属性的上限),之后的追加就只拷新字节。
            int capacity = (int)Math.Clamp(Length * 2L, length, Math.Max(length, X11Server.MaxPropertyBytes));
            byte[] bigger = new byte[capacity];
            Data.CopyTo(bigger);
            storage = new Storage(bigger, Length);
        }
        tail.CopyTo(storage.Bytes.AsSpan(Length));
        storage.Used = length;
        return new XProperty(Type, Format, storage, length) { ChargedTo = chargedTo };
    }

    /// <summary>几个版本共用的一块存储;<see cref="Used" /> 是最新那个版本的长度。</summary>
    private sealed class Storage(byte[] bytes, int used)
    {
        public byte[] Bytes { get; } = bytes;

        public int Used { get; set; } = used;
    }
}

/// <summary>一个窗口。</summary>
/// <remarks>
/// 坐标约定:<see cref="X" />/<see cref="Y" /> 是相对父窗口内区(不含边框)的位置,指的是本窗口<b>外框</b>
/// (含边框)的左上角 —— 与协议一致。内区原点 = 外框左上角 + 边框宽。
/// </remarks>
internal sealed class XWindow(uint id, XClient? owner, XWindow? parent) : XResource(id, owner)
{
    public XWindow? Parent { get; set; } = parent;

    /// <summary>子窗口,<b>从下到上</b>的堆叠顺序(最后一个在最上面)。</summary>
    public List<XWindow> Children { get; } = [];

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int BorderWidth { get; set; }

    /// <summary>1 InputOutput,2 InputOnly。</summary>
    public ushort Class { get; set; } = 1;

    public bool IsInputOnly => Class == 2;

    public byte Depth { get; set; }

    public uint Visual { get; set; }

    /// <summary>背景:None(0,不画)、ParentRelative(1)或像素图 ID;为 <see cref="BackgroundPixmapNone" /> 时看 <see cref="BackgroundPixel" />。</summary>
    public uint BackgroundPixmap { get; set; }

    public const uint BackgroundPixmapNone = 0;
    public const uint BackgroundParentRelative = 1;

    /// <summary>实色背景;null 且 BackgroundPixmap = None 时背景为 None(不画)。</summary>
    public uint? BackgroundPixel { get; set; }

    public XPixmap? BackgroundTile { get; set; }

    public uint BorderPixel { get; set; }

    public XPixmap? BorderTile { get; set; }

    public byte BitGravity { get; set; }

    public byte WinGravity { get; set; } = 1;

    public byte BackingStore { get; set; }

    public uint BackingPlanes { get; set; } = 0xFFFFFFFF;

    public uint BackingPixel { get; set; }

    public bool OverrideRedirect { get; set; }

    /// <summary>
    /// 顶层窗口的位置是客户端自己给的(建窗口、移动窗口、reparent 到根窗口时),窗口管理器(宿主)还没按重力摆过
    /// (ICCCM §4.1.2.3、§4.1.5;见 <see cref="XTopLevelSnapshot.NeedsPlacement" />)。宿主报回摆好的位置时清掉。
    /// </summary>
    public bool PositionRequested { get; set; } = true;

    /// <summary>
    /// 顶层窗口:解析出宿主快照里图标的那份 _NET_WM_ICON 属性值与解析结果。属性值总是整份替换,
    /// 同一个引用就不必重新解析(图标动辄几百 KB)。
    /// </summary>
    public (XProperty? Source, IReadOnlyList<XWindowIcon> Icons) ParsedIcons { get; set; } = (null, []);

    public bool SaveUnder { get; set; }

    public ushort DoNotPropagateMask { get; set; }

    public uint Colormap { get; set; }

    public XCursorResource? Cursor { get; set; }

    public bool Mapped { get; set; }

    /// <summary>各客户端在这个窗口上选择的事件。</summary>
    public Dictionary<XClient, uint> EventSelections { get; } = [];

    /// <summary>最近一次报出去(或登记时算出)的 VisibilityNotify 状态:0 Unobscured、1 PartiallyObscured、2 FullyObscured、255 不可见。</summary>
    public byte VisibilityState { get; set; } = 255;

    /// <summary>可见区域缓存(ClipByChildren / VisibleInner),按服务端的可见性代号与顶层缓冲尺寸失效。只读共享,用的人自己 Clone。</summary>
    internal (int Generation, int BufferWidth, int BufferHeight, Region? ClipByChildren, Region? VisibleInner) VisibilityCache { get; set; }

    /// <summary>XInput2 的事件选择:客户端 → 它在这个窗口上按设备选的掩码(见 <see cref="XiSelection" />)。</summary>
    public Dictionary<XClient, XiSelection> Xi2Selections { get; } = [];

    /// <summary>这个客户端在这个窗口上有没有经 XI2 选 <paramref name="evtype" />。</summary>
    public bool Xi2Selects(XClient client, int evtype) =>
        Xi2Selections.TryGetValue(client, out XiSelection? masks) && ((masks.Master | masks.Slave) & (1UL << evtype)) != 0;

    /// <summary>这个窗口上有没有客户端经 XI2 选了 <paramref name="evtype" />。</summary>
    public bool AnyXi2Selects(int evtype)
    {
        foreach ((ulong master, ulong slave) in Xi2Selections.Values)
        {
            if (((master | slave) & (1UL << evtype)) != 0)
            {
                return true;
            }
        }
        return false;
    }

    public Dictionary<uint, XProperty> Properties { get; } = [];

    /// <summary>顶层窗口(根的直接子窗口)的像素缓冲;子窗口画在所属顶层的缓冲里(架构 §6)。</summary>
    public PixelBuffer? Buffer { get; set; }

    /// <summary><see cref="Buffer" /> 记在 <see cref="XResource.Owner" /> 名下的字节数(见 <c>X11Server.SyncBufferCharge</c>)。</summary>
    public long BufferCharged { get; set; }

    /// <summary>被动按钮抓取(GrabButton)。</summary>
    public Input.PassiveGrabTable ButtonGrabs { get; } = [];

    /// <summary>被动按键抓取(GrabKey)。</summary>
    public Input.PassiveGrabTable KeyGrabs { get; } = [];

    /// <summary>SHAPE 扩展的边界形状(窗口坐标,原点是内区左上角,可以为负以覆盖边框);null = 默认矩形。</summary>
    public Region? BoundingShape { get; set; }

    /// <summary>SHAPE 的裁剪形状(窗口坐标);null = 默认(内区矩形)。</summary>
    public Region? ClipShape { get; set; }

    /// <summary>SHAPE 的输入形状(窗口坐标);null = 默认(与边界形状相同)。</summary>
    public Region? InputShape { get; set; }

    /// <summary>选了 ShapeNotify 的客户端。</summary>
    public HashSet<XClient> ShapeSelections { get; } = [];

    /// <summary>边界形状的默认值:含边框的外框。</summary>
    public XRect DefaultBounding => new(-BorderWidth, -BorderWidth, Width + (2 * BorderWidth), Height + (2 * BorderWidth));

    /// <summary>裁剪形状的默认值:内区。</summary>
    public XRect DefaultClip => new(0, 0, Width, Height);

    public bool IsRoot => Parent is null;

    public bool IsTopLevel => Parent is { IsRoot: true };

    /// <summary>所有客户端在这个窗口上选择的事件的并集(GetWindowAttributes 的 all-event-masks)。</summary>
    public uint AllEventMasks
    {
        get
        {
            uint all = 0;
            foreach (uint mask in EventSelections.Values)
            {
                all |= mask;
            }
            return all;
        }
    }

    /// <summary>可见 = 自己与全部祖先都已映射(根总是映射的)。</summary>
    public bool IsViewable
    {
        get
        {
            for (XWindow? w = this; w is not null; w = w.Parent)
            {
                if (!w.Mapped && !w.IsRoot)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>GetWindowAttributes 的 map-state:0 Unmapped,1 Unviewable,2 Viewable。</summary>
    public byte MapState => !Mapped ? (byte)0 : IsViewable ? (byte)2 : (byte)1;

    /// <summary>所属顶层窗口;根自己返回 null。</summary>
    public XWindow? TopLevel
    {
        get
        {
            XWindow? w = this;
            while (w is { IsRoot: false, IsTopLevel: false })
            {
                w = w.Parent;
            }
            return w is { IsTopLevel: true } ? w : null;
        }
    }

    /// <summary>内区原点在根坐标系里的位置。</summary>
    public (int X, int Y) AbsoluteInner()
    {
        int x = 0, y = 0;
        for (XWindow w = this; w.Parent is not null; w = w.Parent)
        {
            x += w.X + w.BorderWidth;
            y += w.Y + w.BorderWidth;
        }
        return (x, y);
    }

    /// <summary>
    /// 内区原点在所属顶层缓冲里的位置。顶层缓冲只含顶层的内区(边框由宿主的原生窗口替代),所以顶层自己是 (0, 0)。
    /// </summary>
    public (int X, int Y) OffsetInTopLevel()
    {
        int x = 0, y = 0;
        for (XWindow w = this; w is { IsRoot: false, IsTopLevel: false }; w = w.Parent!)
        {
            x += w.X + w.BorderWidth;
            y += w.Y + w.BorderWidth;
        }
        return (x, y);
    }

    /// <summary>嵌套层数:根为 0,顶层为 1,逐层加一。</summary>
    public int Level
    {
        get
        {
            int level = 0;
            for (XWindow? w = Parent; w is not null; w = w.Parent)
            {
                level++;
            }
            return level;
        }
    }

    /// <summary>子树的高度:没有子窗口为 0,只有一层子窗口为 1。用显式栈走,不递归。</summary>
    public int SubtreeHeight()
    {
        int height = 0;
        Stack<(XWindow Window, int Depth)> pending = new();
        pending.Push((this, 0));
        while (pending.TryPop(out (XWindow Window, int Depth) item))
        {
            height = Math.Max(height, item.Depth);
            foreach (XWindow child in item.Window.Children)
            {
                pending.Push((child, item.Depth + 1));
            }
        }
        return height;
    }

    /// <summary>是不是 <paramref name="ancestor" /> 的后代(不含自己)。</summary>
    public bool IsDescendantOf(XWindow ancestor)
    {
        for (XWindow? w = Parent; w is not null; w = w.Parent)
        {
            if (ReferenceEquals(w, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>这个客户端有没有选这些事件。</summary>
    public bool Selects(XClient client, XEventMask mask) =>
        EventSelections.TryGetValue(client, out uint m) && (m & (uint)mask) != 0;

    /// <summary>有没有任何客户端选了这些事件。</summary>
    public bool AnySelects(XEventMask mask) => (AllEventMasks & (uint)mask) != 0;
}
