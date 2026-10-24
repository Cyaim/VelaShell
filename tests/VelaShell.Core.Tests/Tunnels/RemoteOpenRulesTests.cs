using VelaShell.Core.Tunnels;

namespace VelaShell.Core.Tests.Tunnels;

/// <summary>
/// 远程动态转发放行名单的写法:与 ssh_config 的 <c>PermitRemoteOpen</c>、SSH 库 <c>RemoteOpenPolicy</c> 认的同一套。
/// 表单靠它当场指出写错的那一条,服务层靠它拦住空名单。
/// </summary>
[TestClass]
[TestCategory("Tunnel")]
public sealed class RemoteOpenRulesTests
{
    [TestMethod]
    public void TryParse_SplitsOnWhitespaceAndCommas()
    {
        Assert.IsTrue(RemoteOpenRules.TryParse(" localhost:8080, *.corp.example:443\n10.0.0.?:*，[::1]:22 ", out IReadOnlyList<string> rules, out string? invalid));
        Assert.IsNull(invalid);
        Assert.AreSequenceEqual(["localhost:8080", "*.corp.example:443", "10.0.0.?:*", "[::1]:22"], [.. rules]);
    }

    /// <summary>单独一条 any 是全放(大小写不论,统一成小写存);和别的写在一起不行 —— 那不该悄悄变成全放。</summary>
    [TestMethod]
    public void TryParse_AnyMustStandAlone()
    {
        Assert.IsTrue(RemoteOpenRules.TryParse("ANY", out IReadOnlyList<string> rules, out _));
        Assert.AreSequenceEqual([RemoteOpenRules.Any], [.. rules]);

        Assert.IsFalse(RemoteOpenRules.TryParse("any localhost:80", out _, out string? invalid));
        Assert.AreEqual(RemoteOpenRules.Any, invalid);
    }

    /// <summary>一条也没写:不合格,但不算「写错了哪一条」(表单不报红字,只是「创建」点不了)。</summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ,  ")]
    public void TryParse_EmptyIsNotValidButNamesNoRule(string? text)
    {
        Assert.IsFalse(RemoteOpenRules.TryParse(text, out IReadOnlyList<string> rules, out string? invalid));
        Assert.IsEmpty(rules);
        Assert.IsNull(invalid);
    }

    [TestMethod]
    [DataRow("localhost")]
    [DataRow(":80")]
    [DataRow("host:0")]
    [DataRow("host:65536")]
    [DataRow("host:http")]
    [DataRow("[::1]22")]
    [DataRow("[::1")]
    [DataRow("host:")]
    public void TryParse_PointsAtTheBadRule(string bad)
    {
        Assert.IsFalse(RemoteOpenRules.TryParse($"ok.example:443 {bad}", out _, out string? invalid));
        Assert.AreEqual(bad, invalid);
    }

    [TestMethod]
    public void Format_JoinsWithSpaces() =>
        Assert.AreEqual("a:1 b:*", RemoteOpenRules.Format(["a:1", "b:*"]));
}
