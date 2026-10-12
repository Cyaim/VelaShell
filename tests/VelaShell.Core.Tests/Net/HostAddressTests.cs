using VelaShell.Core.Net;

namespace VelaShell.Core.Tests.Net;

/// <summary>主机文本与 <c>host:port</c> 的换算:IPv6 字面量带不带方括号都要认对(#611)。</summary>
[TestClass]
public sealed class HostAddressTests
{
    [TestMethod]
    [DataRow("example.com", "example.com", null)]
    [DataRow("10.0.0.1", "10.0.0.1", null)]
    [DataRow("10.0.0.1:2222", "10.0.0.1", 2222)]
    [DataRow("  host:22  ", "host", 22)]
    [DataRow("2001:db8:0:1::10:20", "2001:db8:0:1::10:20", null)]
    [DataRow("::1", "::1", null)]
    [DataRow("fe80::1%en0", "fe80::1%en0", null)]
    [DataRow("[2001:db8::1]", "2001:db8::1", null)]
    [DataRow("[2001:db8::1]:2222", "2001:db8::1", 2222)]
    [DataRow("[fe80::1%eth0]:22", "fe80::1%eth0", 22)]
    [DataRow("[bastion.example.com]:2200", "bastion.example.com", 2200)]
    public void TrySplit_ReadsHostAndOptionalPort(string text, string host, int? port)
    {
        Assert.IsTrue(HostAddress.TrySplit(text, out string actualHost, out int? actualPort));

        Assert.AreEqual((host, port), (actualHost, actualPort));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("[2001:db8::1")]
    [DataRow("[]:22")]
    [DataRow("[2001:db8::1]2222")]
    [DataRow("[2001:db8::1]:")]
    [DataRow("[2001:db8::1]:0")]
    [DataRow("[2001:db8::1]:65536")]
    [DataRow("[2001:db8::1]:+22")]
    [DataRow("host:")]
    [DataRow(":22")]
    [DataRow("host:ssh")]
    public void TrySplit_RejectsMalformedText(string text)
    {
        Assert.IsFalse(HostAddress.TrySplit(text, out _, out _));
    }

    [TestMethod]
    public void TrySplit_UnbracketedIpv6_IsNeverCutAtTheLastColon()
    {
        // #611 的那个地址:最后一段全是数字,按最后一个冒号拆就成了「主机 2001:db8:0:1::10、端口 20」。
        Assert.IsTrue(HostAddress.TrySplit("2001:db8:0:1::10:20", out string host, out int? port));

        Assert.AreEqual("2001:db8:0:1::10:20", host);
        Assert.IsNull(port);
    }

    [TestMethod]
    [DataRow(" [2001:db8::1] ", "2001:db8::1")]
    [DataRow("[::1]", "::1")]
    [DataRow("2001:db8::1", "2001:db8::1")]
    [DataRow(" example.com ", "example.com")]
    [DataRow("COM3", "COM3")]
    [DataRow("/dev/ttyUSB0", "/dev/ttyUSB0")]
    [DataRow("[2001:db8::1]:22", "[2001:db8::1]:22")]
    [DataRow("[]", "[]")]
    [DataRow(null, "")]
    public void Normalize_StripsOnlyBracketsThatWrapTheWholeAddress(string? text, string expected)
    {
        Assert.AreEqual(expected, HostAddress.Normalize(text));
    }

    [TestMethod]
    public void Format_BracketsIpv6_AndLeavesOthersAlone()
    {
        Assert.AreEqual("[2001:db8::1]:22", HostAddress.FormatEndPoint("2001:db8::1", 22));
        Assert.AreEqual("[::1]:22", HostAddress.FormatEndPoint("[::1]", 22), "已经带着方括号的不再套一层");
        Assert.AreEqual("10.0.0.1:2222", HostAddress.FormatEndPoint("10.0.0.1", 2222));
        Assert.AreEqual("tzraeq@[2001:db8::1]:22", HostAddress.FormatTarget("tzraeq", "2001:db8::1", 22));
        Assert.AreEqual("[2001:db8::1]:22", HostAddress.FormatTarget("", "2001:db8::1", 22));
        Assert.AreEqual("root@example.com:22", HostAddress.FormatTarget("root", "example.com", 22));
        Assert.AreEqual("[fe80::1%en0]", HostAddress.Bracket("fe80::1%en0"));
        Assert.AreEqual("example.com", HostAddress.Bracket("example.com"));
    }

    [TestMethod]
    public void FormatThenSplit_RoundTrips()
    {
        foreach (string host in new[] { "2001:db8::1", "fe80::1%eth0", "10.0.0.1", "example.com" })
        {
            Assert.IsTrue(HostAddress.TrySplit(HostAddress.FormatEndPoint(host, 2222), out string back, out int? port));
            Assert.AreEqual((host, (int?)2222), (back, port));
        }
    }
}
