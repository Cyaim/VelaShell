// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 2 节「Syntactic Conventions」里的资源 ID
//   (resource-id-base / resource-id-mask,ID 由客户端在自己的范围里选,重复或越界是 BadIDChoice)
//   XC-MISC Extension —— XCMiscGetVersion 0、XCMiscGetXIDRange 1、XCMiscGetXIDList 2(客户端用完 ID 时向服务端要空闲的)

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>
    /// XCMiscGetXIDList 一次最多给这么多个 ID(规范:给的可以比要的少)。count 可以是 2³² − 1:照单全收要把 200 万个 ID
    /// 的空间整个扫一遍、回一个 8 MB 的回复;客户端用完一批再来要就是了。
    /// </summary>
    internal const uint MaxXidListCount = 1 << 16;

    internal T? Lookup<T>(uint id) where T : XResource =>
        _resources.TryGetValue(id, out XResource? r) ? r as T : null;

    internal void AddResource(XClient client, XResource resource)
    {
        if (!client.OwnsId(resource.Id) || _resources.ContainsKey(resource.Id))
        {
            throw new XProtocolError(XErrorCode.IDChoice, resource.Id);
        }
        long bytes = Footprint(resource);
        ChargeMemory(client, bytes);
        resource.Charged = bytes;
        if (resource is XGlyphSet glyphSet)
        {
            glyphSet.Table.References++;
        }
        _resources[resource.Id] = resource;
    }

    /// <summary>把资源从资源表里拿掉,记在它名下的内存如数退还。</summary>
    internal void RemoveResource(uint id)
    {
        if (!_resources.Remove(id, out XResource? resource))
        {
            return;
        }
        RefundMemory(resource.Owner, resource.Charged);
        resource.Charged = 0;
        if (resource is XGlyphSet glyphSet && --glyphSet.Table.References == 0)
        {
            ReleaseGlyphs(glyphSet.Table.Glyphs.Values);   // 最后一个引用这张字形表的 ID 没了:字形一并释放
            glyphSet.Table.Glyphs.Clear();
        }
        if (resource is XPixmap pixmap)
        {
            foreach (Extension extension in _extensionList)
            {
                extension.PixmapFreed?.Invoke(pixmap);
            }
        }
        foreach (Extension extension in _extensionList)
        {
            extension.ResourceFreed?.Invoke(resource);
        }
    }

    // ------------------------------------------------------------------ 内存账(xs_plan X-2)

    /// <summary>每个资源的固定开销:对象本身、资源表的一项。也给「建几百万个小资源」的客户端设了个底。</summary>
    internal const long ResourceOverheadBytes = 64;

    /// <summary>全部客户端名下记在账上的内存合计(字节);诊断与测试也看它。</summary>
    internal long MemoryInUse { get; private set; }

    /// <summary>资源进资源表时记的字节数:固定开销,外加它独占的像素 / 区域。顶层窗口的缓冲另算(会变,见 <see cref="SyncBufferCharge" />)。</summary>
    private static long Footprint(XResource resource) => ResourceOverheadBytes + resource switch
    {
        XPixmap { OwnsBuffer: true } pixmap => PixelBytes(pixmap.Width, pixmap.Height),
        XRegionResource region => RegionBytes(region.Region),
        XPicture { Fill: GradientSource gradient } => gradient.StopCount * 24L,   // 每个色标一个 double 与一个浮点颜色
        _ => 0,
    };

    /// <summary>一块 <paramref name="width" /> × <paramref name="height" /> 的像素缓冲占的字节(与 <see cref="PixelBuffer" /> 一样按像素上限截)。</summary>
    internal static long PixelBytes(int width, int height) =>
        Math.Min((long)Math.Max(width, 1) * Math.Max(height, 1), PixelBuffer.MaxPixels) * sizeof(uint);

    private static long RegionBytes(Region region) => region.Rects.Count * 16L;

    /// <summary>记 <paramref name="bytes" /> 在 <paramref name="client" /> 名下记得下不(每客户端与全局两道上限)。服务端自己的(null)不记账。</summary>
    internal bool CanCharge(XClient? client, long bytes) =>
        bytes <= 0 || client is null
        || (client.MemoryInUse + bytes <= Options.MaxClientMemory && MemoryInUse + bytes <= Options.MaxTotalMemory);

    /// <summary>记账;超出上限抛 Alloc(<paramref name="force" /> 时照记不抛:宿主发起的变化,比如用户拖大了原生窗口)。</summary>
    internal void ChargeMemory(XClient? client, long bytes, bool force = false)
    {
        if (bytes <= 0 || client is null)
        {
            return;
        }
        if (!force && !CanCharge(client, bytes))
        {
            throw new XProtocolError(XErrorCode.Alloc);
        }
        client.MemoryInUse += bytes;
        MemoryInUse += bytes;
    }

    internal void RefundMemory(XClient? client, long bytes)
    {
        if (bytes <= 0 || client is null)
        {
            return;
        }
        client.MemoryInUse -= bytes;
        MemoryInUse -= bytes;
    }

    /// <summary>客户端的请求要分配 <paramref name="bytes" />:先看记得下不,记不下回 Alloc —— 在分配之前,出错的请求不产生任何效果。</summary>
    internal void RequireMemory(XClient? client, long bytes)
    {
        if (!CanCharge(client, bytes))
        {
            throw new XProtocolError(XErrorCode.Alloc);
        }
    }

    /// <summary>资源的内容变了(区域换了一份):按新的大小对账,记不下抛 Alloc、账不变。</summary>
    private void Recharge(XResource resource, long newCharge)
    {
        long delta = newCharge - resource.Charged;
        if (delta > 0)
        {
            ChargeMemory(resource.Owner, delta);
        }
        else
        {
            RefundMemory(resource.Owner, -delta);
        }
        resource.Charged = newCharge;
    }

    /// <summary>顶层窗口的缓冲建了、改了尺寸或丢掉之后对账(照记不拒:客户端的请求在改之前已经用 <see cref="RequireBufferMemory" /> 核过)。</summary>
    private void SyncBufferCharge(XWindow window)
    {
        long now = window.Buffer is { } buffer ? PixelBytes(buffer.Width, buffer.Height) : 0;
        long delta = now - window.BufferCharged;
        if (delta > 0)
        {
            ChargeMemory(window.Owner, delta, force: true);
        }
        else
        {
            RefundMemory(window.Owner, -delta);
        }
        window.BufferCharged = now;
    }

    /// <summary>客户端的请求要让顶层窗口的缓冲变成 <paramref name="width" /> × <paramref name="height" />:记不下回 Alloc。</summary>
    private void RequireBufferMemory(XWindow window, int width, int height) =>
        RequireMemory(window.Owner, PixelBytes(width, height) - window.BufferCharged);

    /// <summary>属性值被替换或删掉:退还写它的那个客户端的账。</summary>
    private void ReleaseProperty(XProperty? property)
    {
        if (property is not null)
        {
            RefundMemory(property.ChargedTo, property.Data.Length);
        }
    }

    /// <summary>字形被释放:退还加它的那个客户端的账。</summary>
    private void ReleaseGlyphs(IEnumerable<XRenderGlyph> glyphs)
    {
        foreach (XRenderGlyph glyph in glyphs)
        {
            RefundMemory(glyph.ChargedTo, glyph.Bytes);
        }
    }

    /// <summary>资源表里的全部资源(只读遍历;只在执行线程上用)。</summary>
    internal IEnumerable<XResource> AllResources => _resources.Values;

    private XWindow CreateRootWindow() => new(RootWindowId, null, null)
    {
        Width = Options.ScreenWidth,
        Height = Options.ScreenHeight,
        Depth = 24,
        Visual = RootVisualId,
        Colormap = DefaultColormapId,
        BackgroundPixel = 0,
        Mapped = true,
    };

    // ------------------------------------------------------------------ XC-MISC

    private void XcMisc(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:
                c.Reply(0, w => w.U16(1).U16(1).Zero(20));
                break;
            case 1:
                // 客户端的 ID 用完了,找一段连续的空闲 ID 给它。
                (uint start, uint count) = LargestFreeRange(c);
                c.Reply(0, w => w.U32(start).U32(count).Zero(16));
                break;
            case 2:
                uint wanted = Math.Min(r.U32(), MaxXidListCount);
                List<uint> ids = [];
                for (uint i = 1; i <= XClient.ResourceMask && ids.Count < wanted; i++)
                {
                    uint id = c.ResourceBase | i;
                    if (!_resources.ContainsKey(id))
                    {
                        ids.Add(id);
                    }
                }
                c.Reply(0, w =>
                {
                    w.U32((uint)ids.Count).Zero(20);
                    foreach (uint id in ids)
                    {
                        w.U32(id);
                    }
                });
                break;
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private (uint Start, uint Count) LargestFreeRange(XClient c)
    {
        List<uint> used = [.. _resources.Keys.Where(c.OwnsId).Select(id => id & XClient.ResourceMask).Order()];
        uint bestStart = 0, bestCount = 0, cursor = 1;
        foreach (uint u in used.Append(XClient.ResourceMask + 1))
        {
            if (u > cursor && u - cursor > bestCount)
            {
                bestStart = cursor;
                bestCount = u - cursor;
            }
            cursor = Math.Max(cursor, u + 1);
        }
        return bestCount == 0 ? (0, 0) : (c.ResourceBase | bestStart, bestCount);
    }
}
