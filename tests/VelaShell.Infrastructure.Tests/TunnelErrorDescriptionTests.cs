using System.Net.Sockets;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Tunnels;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Infrastructure.Tests;

/// <summary>隧道错误的提示按原因码给,不去解析库的句子。</summary>
[TestClass]
public class TunnelErrorDescriptionTests
{
    /// <summary>服务端拒绝转发(原因码 1)与连不上目标(原因码 2)各有各的提示。</summary>
    [TestMethod]
    public void 按开通道失败的原因码给提示()
    {
        Assert.AreEqual(
            Strings.Get("TunnelSvc_ForwardProhibited"),
            TunnelService.DescribeForwardError(new SshChannelException(
                SshFailureReason.ChannelOpenFailed, "x", SshChannelOpenFailureReason.AdministrativelyProhibited), TunnelType.LocalForward));

        // ConnectFailed 曾经没有映射,直接显示库的原文。
        Assert.AreEqual(
            Strings.Get("TunnelSvc_TargetRefused"),
            TunnelService.DescribeForwardError(new InvalidOperationException("外层", new SshChannelException(
                SshFailureReason.ChannelOpenFailed, "x", SshChannelOpenFailureReason.ConnectFailed)), TunnelType.LocalForward));
    }

    /// <summary>消息里碰巧有「administratively prohibited」不算数 —— 曾经宿主会去匹配这串字。</summary>
    [TestMethod]
    public void 不去解析消息里的句子()
    {
        string description = TunnelService.DescribeForwardError(
            new InvalidOperationException("administratively prohibited (by a string match)"), TunnelType.LocalForward);

        Assert.AreNotEqual(Strings.Get("TunnelSvc_ForwardProhibited"), description);
    }

    /// <summary>
    /// 本机这头连不上目标(套接字报的错)只会出在远程 / 远程动态转发上 —— 它们的目标是本机去连的。
    /// 提示要说「本机这头」;曾经一律说「目标地址是从服务器视角解析的」,对本机目标正好说反了。
    /// 本地 / 动态转发的目标由服务器去连,仍是「从服务器视角」那一句。
    /// </summary>
    [TestMethod]
    [DataRow(TunnelType.RemoteForward, "TunnelSvc_LocalTargetRefused", "TunnelSvc_LocalTargetUnreachable")]
    [DataRow(TunnelType.RemoteDynamicForward, "TunnelSvc_LocalTargetRefused", "TunnelSvc_LocalTargetUnreachable")]
    [DataRow(TunnelType.LocalForward, "TunnelSvc_TargetRefused", "TunnelSvc_TargetUnreachable")]
    [DataRow(TunnelType.DynamicForward, "TunnelSvc_TargetRefused", "TunnelSvc_TargetUnreachable")]
    public void 套接字错误按隧道类型说是哪一头(TunnelType type, string refusedKey, string unreachableKey)
    {
        Assert.AreEqual(Strings.Get(refusedKey), TunnelService.DescribeForwardError(
            new SocketException((int)SocketError.ConnectionRefused), type));
        Assert.AreEqual(Strings.Get(unreachableKey), TunnelService.DescribeForwardError(
            new InvalidOperationException("外层", new SocketException((int)SocketError.TimedOut)), type));
        Assert.AreEqual(Strings.Get(unreachableKey), TunnelService.DescribeForwardError(
            new SocketException((int)SocketError.HostNotFound), type));
    }

    /// <summary>
    /// 本机监听没起来(库报 ForwardBindFailed,消息是写给开发者的中文):按配置说清在哪儿、为什么(套接字错误码)。
    /// TCP 端口被占交给面板的「本地端口 N 已被占用」;远程转发的监听在服务器上,不归这里。
    /// </summary>
    [TestMethod]
    public void 本机监听失败按配置说清在哪儿()
    {
        static Exception BindFailed(SocketError code) => new InvalidOperationException("宿主包的一层",
            new SshForwardException(SshFailureReason.ForwardBindFailed, "库的中文消息", new SocketException((int)code)));

        PortForwardRequest tcp = new(PortForwardKind.Local, "192.0.2.10", 8080, "db", 5432);
        Assert.AreEqual(Strings.Format("TunnelSvc_LocalBindFailed", "192.0.2.10:8080", nameof(SocketError.AddressNotAvailable)),
            TunnelService.DescribeLocalBindFailure(BindFailed(SocketError.AddressNotAvailable), tcp));
        Assert.AreEqual(Strings.Format("TunnelSvc_LocalBindFailed", "[::1]:1080", nameof(SocketError.AccessDenied)),
            TunnelService.DescribeLocalBindFailure(BindFailed(SocketError.AccessDenied),
                new PortForwardRequest(PortForwardKind.Dynamic, "[::1]", 1080)));
        Assert.IsNull(TunnelService.DescribeLocalBindFailure(BindFailed(SocketError.AddressAlreadyInUse), tcp),
            "端口被占由面板按端口说。");

        PortForwardRequest socket = tcp with { BoundSocketPath = "/run/user/1000/x.sock" };
        Assert.AreEqual(Strings.Format("TunnelSvc_LocalSocketBindFailed", "/run/user/1000/x.sock", nameof(SocketError.AddressAlreadyInUse)),
            TunnelService.DescribeLocalBindFailure(BindFailed(SocketError.AddressAlreadyInUse), socket),
            "套接字路径上的「地址已被占用」不是端口的事。");
        Assert.AreEqual(Strings.Format("TunnelSvc_LocalSocketBindFailedNoDetail", "/run/user/1000/x.sock"),
            TunnelService.DescribeLocalBindFailure(new SshForwardException(SshFailureReason.ForwardBindFailed, "库的中文消息"), socket));

        Assert.IsNull(TunnelService.DescribeLocalBindFailure(BindFailed(SocketError.AccessDenied),
            new PortForwardRequest(PortForwardKind.Remote, "localhost", 8080, "127.0.0.1", 80)), "远程转发的监听在服务器上。");
        Assert.IsNull(TunnelService.DescribeLocalBindFailure(
            new SshForwardException(SshFailureReason.ForwardRejected, "x"), tcp), "别的原因不归这里。");
    }
}