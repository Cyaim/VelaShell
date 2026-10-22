// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 9110 §9.3.6   CONNECT 方法
//   RFC 9110 §11.7    Proxy-Authenticate / Proxy-Authorization
//   RFC 9110 §15.5.8  407 Proxy Authentication Required
//   RFC 7617          Basic 认证方案
//   行为规格:         velashell-docs/zh/ssh/spec/09-dialing.md §4

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Transport;

/// <summary>经 HTTP 代理（<c>CONNECT</c> 方法）拨号。</summary>
/// <param name="Proxy">代理的地址。</param>
/// <remarks>
/// <para>
/// 只做 Basic 认证（RFC 7617），而且<b>第一个请求就带上</b> —— 等 407 再重试意味着
/// 代理可能关掉这条连接，而多一个往返在建连路径上是纯粹的延迟（velashell-docs/zh/ssh/spec/09 §4.1）。
/// NTLM / Negotiate / Digest 需要多轮往返，不做；要用就实现自己的拨号器。
/// </para>
/// <para>
/// 到达代理本身走 <see cref="Inner"/>，默认直连 TCP。连 HTTPS 代理时把 <see cref="Inner"/>
/// 换成 <see cref="DialerChain.Tls"/> 即可。
/// </para>
/// </remarks>
internal sealed record HttpConnectDialer(SshEndPoint Proxy) : ISshTransportDialer, ISshDialKindSource
{
    /// <summary>响应头的上限 —— 防一个坏代理（或根本不是代理的东西）把内存吃光。</summary>
    internal const int MaxResponseHeaderBytes = 16 * 1024;

    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    /// <summary>代理的凭据；<see langword="null"/> 表示不带 <c>Proxy-Authorization</c>。</summary>
    public SshProxyCredentials? Credentials { get; init; }

    /// <summary>怎么到达代理本身。默认直连 TCP。</summary>
    public ISshTransportDialer Inner { get; init; } = TcpTransportDialer.Shared;

    /// <inheritdoc />
    public SshDialKind Kind => SshDialKind.HttpConnect;

    /// <inheritdoc />
    public ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        // 先拼好请求：主机名放不进请求时，连代理都不必去连。
        byte[] request = BuildRequest(target.EndPoint, Credentials);
        return ProxyDialing.DialAsync(
            SshDialKind.HttpConnect, "HTTP 代理", Inner, Proxy, target,
            (stream, ct) => HandshakeAsync(stream, request, target.EndPoint, ct),
            cancellationToken);
    }

    /// <summary>拼 CONNECT 请求。</summary>
    /// <exception cref="SshConnectException">主机名放不进 HTTP 请求（<see cref="SshFailureReason.InvalidConfiguration"/>）。</exception>
    internal static byte[] BuildRequest(SshEndPoint target, SshProxyCredentials? credentials)
    {
        string authority = Authority(target);

        StringBuilder request = new();
        request.Append(CultureInfo.InvariantCulture, $"CONNECT {authority} HTTP/1.1\r\n");
        request.Append(CultureInfo.InvariantCulture, $"Host: {authority}\r\n");

        if (credentials is not null)
        {
            // RFC 7617 §2.1：user-id ":" password，UTF-8，再 base64。
            string token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{credentials.UserName}:{credentials.Password}"));
            request.Append(CultureInfo.InvariantCulture, $"Proxy-Authorization: Basic {token}\r\n");
        }

        request.Append("\r\n");
        return Encoding.ASCII.GetBytes(request.ToString());
    }

    /// <summary>请求目标与 <c>Host</c> 头里的 <c>主机:端口</c>：国际化域名转 Punycode，IPv6 字面量加方括号。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>主机名原样拼进请求行与 <c>Host</c> 头的话，带 <c>\r\n</c> 就能往发给代理的请求里注入头部</b>
    /// （甚至在同一条连接上再塞一个请求）。主机名常常不是写配置的人给的 —— <c>ssh://</c> 链接、导入的会话、
    /// 快速连接框里粘进来的一串。SOCKS5 有域名映射兜着、ProxyCommand 有字符白名单，这里同样只放行合法主机名的字符。
    /// </para>
    /// <para>
    /// 非 ASCII 的名字先按 IDNA 转成 Punycode（与 SOCKS5 一致）：请求按 ASCII 编码，直接编码会把它们变成 <c>?</c>，
    /// 代理拿到的就是另一个（不存在的）名字。
    /// </para>
    /// </remarks>
    internal static string Authority(SshEndPoint target)
    {
        string host = target.Host;

        // 带冒号的只能是 IPv6 字面量：按地址解析过、再由它自己格式化，就不会夹带别的字符。
        if (host.Contains(':', StringComparison.Ordinal))
        {
            if (!IPAddress.TryParse(host, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw InvalidHost(host, "带冒号却不是 IPv6 地址");
            }
            return $"[{address}]:{target.Port}";
        }

        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(host);
        }
        catch (ArgumentException ex)
        {
            throw InvalidHost(host, ex.Message);
        }

        foreach (char c in ascii)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                throw InvalidHost(host, $"含有字符 U+{(int)c:X4}，合法的主机名只由字母、数字与 . - _ 组成");
            }
        }

        return $"{ascii}:{target.Port}";
    }

    /// <remarks>不是「代理拒绝」—— 那一类会被当成可重试的；这里重试多少次都一样，得改输入。</remarks>
    private static SshConnectException InvalidHost(string host, string why) =>
        new(SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
            $"主机名 {PeerText.Sanitize(host)} 不能放进 HTTP 代理的 CONNECT 请求：{why}。");

    private async ValueTask<Stream> HandshakeAsync(
        Stream stream, byte[] request, SshEndPoint target, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // 响应头的长度事先不知道，只能读到空行为止 —— 那就难免多读。
        // 多读到的字节属于 SSH 服务端（它一连上就先说话），**必须原样交还**（velashell-docs/zh/ssh/spec/09 §2.3）。
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxResponseHeaderBytes);
        try
        {
            int filled = 0;

            // 当前这个响应头在 buffer 里的起点：前面的是已经跳过的 1xx 中间响应。它们合起来也受 MaxResponseHeaderBytes 约束。
            int start = 0;
            while (true)
            {
                int headerEnd = buffer.AsSpan(start, filled - start).IndexOf(HeaderTerminator);
                if (headerEnd < 0)
                {
                    if (filled == MaxResponseHeaderBytes)
                    {
                        throw ProxyDialing.Refused(
                            $"HTTP 代理 {Proxy} 的响应头超过了 {MaxResponseHeaderBytes / 1024} KiB —— 它可能不是一个 HTTP 代理。");
                    }

                    int read = await stream.ReadAsync(buffer.AsMemory(filled, MaxResponseHeaderBytes - filled), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("响应头还没读完连接就关了。");
                    }
                    filled += read;
                    continue;
                }

                headerEnd += start;
                string head = Encoding.Latin1.GetString(buffer, start, headerEnd - start);
                int bodyStart = headerEnd + HeaderTerminator.Length;

                // 〔FW-E11〕1xx 是中间响应（RFC 9110 §15.2：客户端必须能在最终响应之前读过一个或多个 1xx），跳过它接着读。
                // 曾经当成拒绝。101（切换协议）对 CONNECT 没有意义，照最终响应处理。
                if (StatusOf(head) is >= 100 and < 200 and not 101)
                {
                    start = bodyStart;
                    continue;
                }

                ThrowIfNotSuccess(head, target);

                byte[] leftover = buffer.AsSpan(bodyStart, filled - bodyStart).ToArray();
                return leftover.Length == 0 ? stream : new PrefixedStream(leftover, stream);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>响应头里的状态码；状态行不是 <c>HTTP/x 三位数</c> 的样子时为 <see langword="null"/>。</summary>
    private static int? StatusOf(string head)
    {
        string statusLine = head.Split("\r\n", 2)[0];
        string[] parts = statusLine.Split(' ', 3);
        return parts.Length >= 2
            && parts[0].StartsWith("HTTP/", StringComparison.Ordinal)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int status)
                ? status
                : null;
    }

    private void ThrowIfNotSuccess(string head, SshEndPoint target)
    {
        string[] lines = head.Split("\r\n");
        string statusLine = lines[0];

        // 状态行：HTTP/1.x 空格 三位状态码 空格 原因短语。
        if (StatusOf(head) is not { } status)
        {
            throw ProxyDialing.Refused($"{Proxy} 回的不是 HTTP 响应（「{Truncate(statusLine)}」）—— 它可能是 SOCKS 代理，或者根本不是代理。");
        }

        if (status is >= 200 and < 300)
        {
            return;
        }

        if (status == 407)
        {
            string? challenge = lines
                .Skip(1)
                .FirstOrDefault(static l => l.StartsWith("Proxy-Authenticate:", StringComparison.OrdinalIgnoreCase));
            string scheme = challenge is null
                ? ""
                : $"（代理要求：{Truncate(challenge["Proxy-Authenticate:".Length..].Trim())}）";

            throw Credentials is null
                ? ProxyDialing.AuthRequired($"HTTP 代理 {Proxy} 要求认证，但没有配置代理凭据{scheme}。")
                : ProxyDialing.AuthFailed($"HTTP 代理 {Proxy} 拒绝了用户名 {Credentials.UserName} 的凭据{scheme}。");
        }

        // 〔velashell-docs/zh/ssh/spec/09 §4.2〕高频而且用户完全猜不到的一种失败：代理只放行 80/443。
        string hint = target.Port == 22 && status is 403 or 405 or 501
            ? " 这个代理可能只放行 80/443 端口 —— 请改用 SOCKS5，或让 SSH 服务端在 443 上监听。"
            : "";

        throw ProxyDialing.Refused($"HTTP 代理 {Proxy} 拒绝连接 {target}：{Truncate(statusLine)}。{hint}");
    }

    /// <summary>代理的应答进消息之前：截短，并清掉控制字符（见 <see cref="PeerText"/>）。</summary>
    private static string Truncate(string text) => PeerText.Sanitize(text, 200);
}

/// <summary>先吐出一段已经读到的字节，再接着读底层流。</summary>
/// <remarks>
/// 代理握手时多读到的那几个字节属于 SSH 服务端 —— 丢了它们，标识串就残缺了。
/// </remarks>
internal sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
{
    private int _offset;

    public override bool CanRead => true;

    public override bool CanWrite => inner.CanWrite;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < prefix.Length)
        {
            int take = Math.Min(buffer.Length, prefix.Length - _offset);
            prefix.AsSpan(_offset, take).CopyTo(buffer.Span);
            _offset += take;
            return take;
        }

        return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_offset < prefix.Length)
        {
            int take = Math.Min(count, prefix.Length - _offset);
            prefix.AsSpan(_offset, take).CopyTo(buffer.AsSpan(offset));
            _offset += take;
            return take;
        }

        return inner.Read(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private int _disposed;

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
