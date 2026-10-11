// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 8 节「Connection Setup」(客户端声明字节序)、
//   附录 B「Protocol Encoding」开头的「Syntactic Conventions」(字节序、补齐到 4 字节、请求 / 回复 / 事件 / 错误的格式)

using System.Buffers.Binary;
using System.Text;

namespace VelaShell.XServer.Protocol;

/// <summary>请求执行失败:按协议发一条错误给客户端。</summary>
/// <param name="code">错误码。</param>
/// <param name="badValue">出错的值(资源 ID、原子、越界的数);没有意义时为 0。</param>
internal class XProtocolError(XErrorCode code, uint badValue = 0)
    : Exception($"X error {code} (bad value 0x{badValue:x})")
{
    /// <summary>错误码。</summary>
    public XErrorCode Code { get; } = code;

    /// <summary>出错的值。</summary>
    public uint BadValue { get; } = badValue;
}

/// <summary>
/// 按客户端字节序读一条请求。越界读一律 BadLength —— 请求长度字段与内容对不上就是长度错。
/// </summary>
/// <remarks>
/// ⚠️ <b>字节序是逐连接的</b>,由连接建立时第一个字节决定('B' 大端 / 'l' 小端);
/// 只按本机字节序解析的症状是「某些客户端能连,某些连不上」。
/// </remarks>
/// <param name="data">放着这条请求的缓冲(可以是池里租来的、比请求长的数组)。</param>
/// <param name="length">请求的长度:只读前这么多字节,后面的是缓冲里别的东西。</param>
/// <param name="bigEndian">客户端是不是大端。</param>
internal sealed class XRequestReader(byte[] data, int length, bool bigEndian)
{
    private readonly byte[] _data = data;
    private readonly bool _bigEndian = bigEndian;
    private int _pos = 4;

    /// <param name="data">整条请求(含 4 字节头;BIG-REQUESTS 的扩展长度已剥掉)。</param>
    /// <param name="bigEndian">客户端是不是大端。</param>
    public XRequestReader(byte[] data, bool bigEndian) : this(data, data.Length, bigEndian)
    {
    }

    /// <summary>主操作码。</summary>
    public byte Opcode => _data[0];

    /// <summary>
    /// 接下来 <paramref name="length" /> 字节单独成一个读取器(同一字节序),本读取器跳过它们 ——
    /// 请求里自带长度的子结构(比如 XIChangeHierarchy 的每一项)按它读,读多读少都不会越界到下一项。
    /// </summary>
    public XRequestReader Slice(int length)
    {
        ReadOnlySpan<byte> part = Take(length);   // 先核长度再分配:原先先按客户端给的长度 new,再在 Take 里回 BadLength
        byte[] copy = new byte[4 + part.Length];
        part.CopyTo(copy.AsSpan(4));
        return new XRequestReader(copy, _bigEndian);
    }

    /// <summary>头里的数据字节(不同请求含义不同)。</summary>
    public byte Data => _data[1];

    /// <summary>剩余可读字节数。</summary>
    public int Remaining => Length - _pos;

    /// <summary>整条请求的长度(字节)。</summary>
    public int Length { get; } = length;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Length - _pos)   // 原先 _pos + count:count 接近 int.MaxValue 时回绕成负数,检查放行,AsSpan 再抛,成了 BadImplementation
        {
            throw new XProtocolError(XErrorCode.Length);
        }
        ReadOnlySpan<byte> span = _data.AsSpan(_pos, count);
        _pos += count;
        return span;
    }

    public byte U8() => Take(1)[0];

    public sbyte I8() => (sbyte)Take(1)[0];

    public bool Bool() => Take(1)[0] != 0;

    public ushort U16() =>
        _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public short I16() => (short)U16();

    public uint U32() =>
        _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int I32() => (int)U32();

    /// <summary>CARD64(Present 等扩展):8 字节,按客户端字节序。</summary>
    public ulong U64() =>
        _bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(Take(8)) : BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public void Skip(int count) => Take(count);

    /// <summary>读 <paramref name="count" /> 字节,再跳过补齐到 4 字节边界的部分。</summary>
    public byte[] BytesPadded(int count)
    {
        byte[] bytes = Take(count).ToArray();
        int pad = XWire.Pad(count) - count;
        if (pad > 0 && _pos + pad <= Length)
        {
            _pos += pad;
        }
        return bytes;
    }

    public byte[] Bytes(int count) => Take(count).ToArray();

    /// <summary>剩下的全部字节(不复制)。</summary>
    public ReadOnlySpan<byte> Rest() => Take(Remaining);

    /// <summary>读一个 STRING8(Latin-1),再跳过补齐。</summary>
    public string String8(int count) => XWire.Latin1.GetString(BytesPadded(count));
}

/// <summary>按客户端字节序组装回复、事件与错误。</summary>
internal sealed class XWriter(bool bigEndian, int capacity = 32)
{
    private byte[] _buffer = new byte[Math.Max(32, capacity)];
    private bool _bigEndian = bigEndian;

    /// <summary>已写的字节数。</summary>
    public int Length { get; private set; }

    /// <summary>缓冲的大小。</summary>
    public int Capacity => _buffer.Length;

    /// <summary>清空重用,顺带换成要发给的那个客户端的字节序。</summary>
    public XWriter Reset(bool bigEndian)
    {
        _bigEndian = bigEndian;
        Length = 0;
        return this;
    }

    /// <summary>已写的前 <paramref name="count" /> 字节拷成一个新数组 —— 复用的写入器交出内容时用,缓冲留着接着写下一条。</summary>
    public byte[] CopyPrefix(int count) => _buffer.AsSpan(0, count).ToArray();

    private Span<byte> Grow(int count)
    {
        if (Length + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));
        }
        Span<byte> span = _buffer.AsSpan(Length, count);
        Length += count;
        return span;
    }

    public XWriter U8(byte value)
    {
        Grow(1)[0] = value;
        return this;
    }

    public XWriter Bool(bool value) => U8(value ? (byte)1 : (byte)0);

    public XWriter U16(ushort value)
    {
        if (_bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(Grow(2), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), value);
        }
        return this;
    }

    public XWriter I16(int value) => U16(unchecked((ushort)(short)value));

    public XWriter U32(uint value)
    {
        if (_bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(Grow(4), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), value);
        }
        return this;
    }

    public XWriter I32(int value) => U32(unchecked((uint)value));

    public XWriter U64(ulong value)
    {
        if (_bigEndian)
        {
            BinaryPrimitives.WriteUInt64BigEndian(Grow(8), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), value);
        }
        return this;
    }

    public XWriter Zero(int count)
    {
        Grow(count).Clear();
        return this;
    }

    public XWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Grow(bytes.Length));
        return this;
    }

    /// <summary>补零到 4 字节边界。</summary>
    public XWriter Pad4()
    {
        int pad = XWire.Pad(Length) - Length;
        return pad > 0 ? Zero(pad) : this;
    }

    /// <summary>在已写位置 <paramref name="offset" /> 处回填一个 CARD32(回复长度)。</summary>
    public void PatchU32(int offset, uint value)
    {
        Span<byte> span = _buffer.AsSpan(offset, 4);
        if (_bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        }
    }

    /// <summary>取出写好的字节。恰好写满缓冲时直接交出缓冲本身(事件正好 32 字节,省一次拷贝);之后不许再写。</summary>
    public byte[] ToArray() => Length == _buffer.Length ? _buffer : _buffer.AsSpan(0, Length).ToArray();
}

/// <summary>编码上的小工具。</summary>
internal static class XWire
{
    /// <summary>STRING8 按 Latin-1 解码(协议没规定编码,Latin-1 是逐字节保真的那一个)。</summary>
    public static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>向上补齐到 4 的倍数。</summary>
    public static int Pad(int length) => (length + 3) & ~3;
}
