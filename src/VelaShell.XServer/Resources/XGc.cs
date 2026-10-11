// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「CreateGC」一节(各分量的含义与默认值)

namespace VelaShell.XServer.Resources;

/// <summary>图形上下文(GC)。默认值逐项照 CreateGC 一节。</summary>
internal sealed class XGc(uint id, Server.XClient? owner, byte depth) : XResource(id, owner)
{

    /// <summary>创建时所基于的可绘对象深度 —— GC 只能用在同深度的可绘对象上(否则 BadMatch)。</summary>
    public byte Depth { get; } = depth;

    /// <summary>光栅操作,0–15;默认 GXcopy(3)。</summary>
    public byte Function { get; set; } = 3;

    public uint PlaneMask { get; set; } = 0xFFFFFFFF;

    public uint Foreground { get; set; }

    public uint Background { get; set; } = 1;

    public ushort LineWidth { get; set; }

    /// <summary>0 Solid,1 OnOffDash,2 DoubleDash。</summary>
    public byte LineStyle { get; set; }

    /// <summary>0 NotLast,1 Butt(默认),2 Round,3 Projecting。</summary>
    public byte CapStyle { get; set; } = 1;

    /// <summary>0 Miter(默认),1 Round,2 Bevel。</summary>
    public byte JoinStyle { get; set; }

    /// <summary>0 Solid,1 Tiled,2 Stippled,3 OpaqueStippled。</summary>
    public byte FillStyle { get; set; }

    /// <summary>0 EvenOdd,1 Winding。</summary>
    public byte FillRule { get; set; }

    /// <summary>平铺像素图;null = 协议里的默认值(一块填满前景色的像素图,效果同实色)。</summary>
    public XPixmap? Tile { get; set; }

    /// <summary>点画位图;null = 默认值(全 1,效果同实色)。</summary>
    public XPixmap? Stipple { get; set; }

    public short TileStipXOrigin { get; set; }

    public short TileStipYOrigin { get; set; }

    public XFontResource? Font { get; set; }

    /// <summary>0 ClipByChildren,1 IncludeInferiors。</summary>
    public byte SubwindowMode { get; set; }

    public bool GraphicsExposures { get; set; } = true;

    public short ClipXOrigin { get; set; }

    public short ClipYOrigin { get; set; }

    /// <summary>裁剪位图(clip-mask 设成像素图时)。</summary>
    public XPixmap? ClipPixmap { get; set; }

    /// <summary>裁剪矩形(SetClipRectangles);与 <see cref="ClipPixmap" /> 互斥。null = 不裁剪。换的时候整份替换,不就地改。</summary>
    public List<XRect>? ClipRects
    {
        get;
        set
        {
            field = value;
            _clipRegion = null;
        }
    }

    /// <summary>上一次按裁剪矩形建出来的区域与它平移的量(见 <see cref="ClipRegionAt" />)。</summary>
    private (int Dx, int Dy, Drawing.Region Region)? _clipRegion;

    /// <summary>
    /// 裁剪矩形平移 (<paramref name="dx" />, <paramref name="dy" />) 之后的区域(调用方只读);没有裁剪矩形时为 null。
    /// 按平移量缓存:同一个 GC 连着画同一个可绘对象时不必每个请求都 FromRects 一次。
    /// </summary>
    public Drawing.Region? ClipRegionAt(int dx, int dy)
    {
        if (ClipRects is not { } rects)
        {
            return null;
        }
        if (_clipRegion is { } cached && cached.Dx == dx && cached.Dy == dy)
        {
            return cached.Region;
        }
        Drawing.Region region = Drawing.Region.FromRects(rects).Translate(dx, dy);
        _clipRegion = (dx, dy, region);
        return region;
    }

    public ushort DashOffset { get; set; }

    public byte[] Dashes { get; set; } = [4, 4];

    /// <summary>0 Chord,1 PieSlice(默认)。</summary>
    public byte ArcMode { get; set; } = 1;
}
