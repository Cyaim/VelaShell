// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: RFC 8305(Happy Eyeballs v2);velashell-docs/zh/ssh/spec/09-dialing.md §3

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Transport;

[TestClass]
[TestCategory("Transport")]
public sealed class TcpDialerTests
{
    private static readonly IPAddress V6 = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress V6b = IPAddress.Parse("2001:db8::2");
    private static readonly IPAddress V4 = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress V4b = IPAddress.Parse("192.0.2.2");

    /// <summary>〔F22〕AddressFamily 只连那一族的目标地址；BindAddress 从指定的本机地址发起。</summary>
    [TestMethod]
    public async Task 只连限定的地址族_从指定的本机地址发起()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var target = SshDialTarget.Direct("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);

        // 目标只有 IPv4 地址，却只许 IPv6：没有能连的地址，不去试。
        SshConnectException none = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await new TcpTransportDialer { AddressFamily = AddressFamily.InterNetworkV6 }.DialAsync(target));
        Assert.AreEqual(SshFailureReason.DnsFailure, none.Reason);
        Assert.Contains("地址族", none.Message);

        // 本机只绑了 IPv6 地址：IPv4 的目标同样不连。
        await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await new TcpTransportDialer { LocalAddresses = [IPAddress.IPv6Loopback] }.DialAsync(target));

        // 从 127.0.0.2 发起（Linux 与 Windows 上整个 127/8 都是环回）：对面看到的来源就是它。
        // macOS 的 lo0 上默认只有 127.0.0.1，绑 127.0.0.2 报 AddressNotAvailable —— 那里退回 127.0.0.1。
        IPAddress local = OperatingSystem.IsMacOS() ? IPAddress.Loopback : IPAddress.Parse("127.0.0.2");
        Task<Socket> accepting = listener.AcceptSocketAsync();
        await using Stream stream = await new TcpTransportDialer
        {
            AddressFamily = AddressFamily.InterNetwork,
            LocalAddresses = [local],
        }.DialAsync(target);
        using Socket accepted = await accepting;
        Assert.AreEqual(local, ((IPEndPoint)accepted.RemoteEndPoint!).Address);
    }

    /// <summary>一条真连上的套接字（连到本机的一个监听上）。</summary>
    private static async Task<Socket> ConnectedSocketAsync(TcpListener listener)
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        return socket;
    }

    /// <summary>
    /// 原因码要说真话：宿主按它本地化、按它决定要不要重试。
    /// 曾经认不出的错误一律报成「拒绝连接」—— 断网时的 DNS 暂时失败也在其中。
    /// </summary>
    [TestMethod]
    [DataRow(SocketError.HostNotFound, SshFailureReason.DnsFailure)]
    [DataRow(SocketError.NoData, SshFailureReason.DnsFailure)]
    [DataRow(SocketError.TryAgain, SshFailureReason.DnsFailure)]
    [DataRow(SocketError.NoRecovery, SshFailureReason.DnsFailure)]
    [DataRow(SocketError.ConnectionRefused, SshFailureReason.TcpRefused)]
    [DataRow(SocketError.TimedOut, SshFailureReason.TcpTimeout)]
    [DataRow(SocketError.NetworkUnreachable, SshFailureReason.TcpUnreachable)]
    [DataRow(SocketError.HostUnreachable, SshFailureReason.TcpUnreachable)]
    [DataRow(SocketError.NetworkDown, SshFailureReason.TcpUnreachable)]
    [DataRow(SocketError.HostDown, SshFailureReason.TcpUnreachable)]
    [DataRow(SocketError.AccessDenied, SshFailureReason.Unknown)]
    [DataRow(SocketError.AddressNotAvailable, SshFailureReason.Unknown)]
    public void 套接字错误翻成说真话的原因码(SocketError error, SshFailureReason expected)
    {
        SshConnectException translated = TcpTransportDialer.Translate(
            new SocketException((int)error), new SshEndPoint("host.example", 22));

        Assert.AreEqual(expected, translated.Reason);
        Assert.AreEqual(SshPhase.Dialing, translated.Phase);

        // 说不清是哪一类的(Unknown)照样交出种类与目标,界面按它与内层的错误码说;原因码说得清的不归类。
        if (expected == SshFailureReason.Unknown)
        {
            Assert.AreEqual($"连 host.example:22 失败：{error}。", translated.Message);
            Assert.AreEqual(SshConnectFailureKind.TcpConnectFailed, translated.FailureKind);
            Assert.AreEqual("host.example:22", translated.Target);
        }
        else
        {
            Assert.AreEqual(SshConnectFailureKind.Unknown, translated.FailureKind);
        }
    }

    [TestMethod]
    public void 默认不另设上限_跟连接的超时走() =>
        // 曾经默认 30 秒：使用者给连接设了更长的超时，TCP 这一步照样在 30 秒被掐断。
        Assert.AreEqual(Timeout.InfiniteTimeSpan, TcpTransportDialer.Shared.ConnectTimeout);

    [TestMethod]
    public void 地址按族交替排从第一个族开始()
    {
        Assert.AreSequenceEqual(
            [V6, V4, V6b, V4b],
            TcpTransportDialer.Interleave([V6, V6b, V4, V4b]));

        Assert.AreSequenceEqual(
            [V4, V6, V4b],
            TcpTransportDialer.Interleave([V4, V4b, V6]));
    }

    [TestMethod]
    public async Task 第一个地址不通时不等它超时_错开之后就试下一个()
    {
        // 通告了 IPv6 却不通的网络：第一个地址的 SYN 石沉大海。
        // 顺序试的话要等系统的 SYN 超时（Windows 约 21 秒）才轮到 IPv4。
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        bool firstCancelled = false;
        var watch = Stopwatch.StartNew();

        using Socket socket = await TcpTransportDialer.RaceAsync(
            [V6, V4],
            async (address, token) =>
            {
                if (address.Equals(V6))
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        firstCancelled = true;
                        throw;
                    }
                }
                return await ConnectedSocketAsync(listener);
            },
            attemptDelay: TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.IsTrue(socket.Connected);
        Assert.IsLessThan(TimeSpan.FromSeconds(5), watch.Elapsed);

        // 输了的那条要被叫停，而不是一直挂着。
        for (int i = 0; i < 100 && !firstCancelled; i++)
        {
            await Task.Delay(10);
        }
        Assert.IsTrue(firstCancelled);
    }

    [TestMethod]
    public async Task 一个地址失败了立刻试下一个_不干等错开时间()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var watch = Stopwatch.StartNew();

        using Socket socket = await TcpTransportDialer.RaceAsync(
            [V6, V4],
            async (address, _) => address.Equals(V6)
                ? throw new SocketException((int)SocketError.NetworkUnreachable)
                : await ConnectedSocketAsync(listener),
            attemptDelay: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.IsTrue(socket.Connected);
        Assert.IsLessThan(TimeSpan.FromSeconds(5), watch.Elapsed, "失败了还干等 30 秒的错开时间");
    }

    [TestMethod]
    public async Task 全都失败时报最后一个错误()
    {
        SocketException error = await Assert.ThrowsExactlyAsync<SocketException>(
            () => TcpTransportDialer.RaceAsync(
                [V6, V4],
                (address, _) => Task.FromException<Socket>(new SocketException((int)(address.Equals(V6)
                    ? SocketError.NetworkUnreachable
                    : SocketError.ConnectionRefused))),
                attemptDelay: TimeSpan.FromMilliseconds(50),
                CancellationToken.None));

        Assert.AreEqual(SocketError.ConnectionRefused, error.SocketErrorCode);
    }

    [TestMethod]
    public async Task 输了却也连上的那条会被关掉()
    {
        // 取消之前恰好连上的那一条没人要 —— 不关就一直挂着一个 TCP 连接。
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        TaskCompletionSource<Socket> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Socket lateSocket = await ConnectedSocketAsync(listener);

        using Socket winner = await TcpTransportDialer.RaceAsync(
            [V6, V4],
            (address, _) => address.Equals(V6) ? late.Task : ConnectedSocketAsync(listener),
            attemptDelay: TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        // 赢家已经定了，输家这时才「连上」。
        late.SetResult(lateSocket);

        for (int i = 0; i < 100 && !lateSocket.SafeHandle.IsClosed; i++)
        {
            await Task.Delay(10);
        }
        Assert.IsTrue(lateSocket.SafeHandle.IsClosed);
    }

    [TestMethod]
    public async Task 名字解析到多个地址时用连得上的那一个()
    {
        // localhost 通常解析成 ::1 与 127.0.0.1；监听只开在 IPv4 上。
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using Stream stream = await TcpTransportDialer.Shared.DialAsync(
            SshDialTarget.Direct("localhost", port));

        Assert.IsTrue(stream.CanWrite);
    }

    [TestMethod]
    public async Task 计时器到点时停下并叫停在途的尝试()
    {
        // 不连真实地址：「文档专用、不会有人应答」的地址在装了透明代理的机器上照样连得上。
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(100));
        int startedAttempts = 0;
        int cancelledAttempts = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => TcpTransportDialer.RaceAsync(
                [V6, V4],
                async (_, token) =>
                {
                    Interlocked.Increment(ref startedAttempts);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref cancelledAttempts);
                        throw;
                    }
                    return null!;
                },
                attemptDelay: TimeSpan.FromMilliseconds(20),
                timeout.Token));

        // 满载时第二条可能还没来得及发起计时器就到了 —— 要求的是「发起了的都被叫停」。
        for (int i = 0; i < 100 && Volatile.Read(ref cancelledAttempts) < Volatile.Read(ref startedAttempts); i++)
        {
            await Task.Delay(10);
        }
        Assert.IsGreaterThanOrEqualTo(1, startedAttempts);
        Assert.AreEqual(startedAttempts, Volatile.Read(ref cancelledAttempts), "在途的尝试都要被叫停");
    }
}
