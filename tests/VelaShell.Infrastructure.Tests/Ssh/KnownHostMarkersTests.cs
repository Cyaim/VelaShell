using VelaShell.Core.Models;
using VelaShell.Infrastructure.Ssh;
using static VelaShell.Infrastructure.Tests.Ssh.HostCaPolicyTests;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>手动添加受信主机 CA 时的校验:模式、公钥、整行复制来的标记行。</summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class KnownHostMarkersTests
{
    [TestMethod]
    public void CA公钥行加模式_注释取自公钥行()
    {
        Assert.IsTrue(KnownHostMarkers.TryCreate(" *.corp.example , !legacy.corp.example ", Fixture("hostcert-ca.pub"), null,
            out KnownHostMarker? marker, out string? error), error);

        Assert.AreEqual(KnownHostMarkerKind.CertificateAuthority, marker.Kind);
        Assert.AreEqual("*.corp.example,!legacy.corp.example", marker.HostPatterns, "模式间的空白去掉,写回 known_hosts 才不会被切成两栏");
        Assert.AreEqual("ssh-ed25519", marker.KeyType);
        Assert.AreEqual(Key("hostcert-ca.pub").Sha256Fingerprint, marker.Fingerprint);
        Assert.AreEqual("velashell-ssh test key (hostcert-ca)", marker.Comment);
        Assert.AreEqual("@cert-authority *.corp.example,!legacy.corp.example ssh-ed25519 " +
                        Fixture("hostcert-ca.pub").Split(' ')[1] + " velashell-ssh test key (hostcert-ca)",
            KnownHostMarkers.FormatLine(marker));
    }

    [TestMethod]
    public void 整行复制来的标记行_模式框空着就用那一行的()
    {
        string line = "@cert-authority [bastion.example]:2222,*.lab " + Fixture("hostcert-ca.pub").Split(' ', 3)[0] + " "
                      + Fixture("hostcert-ca.pub").Split(' ', 3)[1] + " lab CA";

        Assert.IsTrue(KnownHostMarkers.TryCreate("", line, "", out KnownHostMarker? fromLine, out string? error), error);
        Assert.AreEqual("[bastion.example]:2222,*.lab", fromLine.HostPatterns);
        Assert.AreEqual("lab CA", fromLine.Comment);

        Assert.IsTrue(KnownHostMarkers.TryCreate("*.other", line, "mine", out KnownHostMarker? overridden, out _));
        Assert.AreEqual("*.other", overridden.HostPatterns, "模式框填了就以它为准");
        Assert.AreEqual("mine", overridden.Comment);

        Assert.IsTrue(KnownHostMarkers.TryCreate(null, "@revoked * " + Fixture("hostcert-key.pub"), null, out KnownHostMarker? revoked, out _));
        Assert.AreEqual(KnownHostMarkerKind.Revoked, revoked.Kind);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" , ")]
    [DataRow("bad host")]
    [DataRow("host#1")]
    [DataRow("@cert-authority")]
    [DataRow("|1|abc|def")]
    [DataRow("[host.example]")]
    [DataRow("[host.example]:")]
    [DataRow("host.example]:22")]
    [DataRow("!")]
    [DataRow("!a.example,!b.example")]
    [DataRow("!!a.example")]
    public void 写坏的模式不收(string patterns)
    {
        Assert.IsFalse(KnownHostMarkers.TryCreate(patterns, Fixture("hostcert-ca.pub"), null, out _, out string? error));
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    [DataRow("*")]
    [DataRow("*.example,!legacy.example")]
    [DataRow("[*.example]:2222")]
    [DataRow("10.0.0.?")]
    [DataRow("fe80::1%eth0")]
    public void 合法的模式照收(string patterns)
    {
        Assert.IsTrue(KnownHostMarkers.TryCreate(patterns, Fixture("hostcert-ca.pub"), null, out _, out string? error), error);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not a key")]
    [DataRow("ssh-ed25519 !!!notbase64")]
    [DataRow("@revoke * ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGMWXV92etxq692u66vFteIxPvwc5ItXG0BaESU/976V")]
    public void 读不出的公钥不收(string keyText)
    {
        Assert.IsFalse(KnownHostMarkers.TryCreate("*.example", keyText, null, out _, out string? error));
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    /// <summary>CA 公钥本身不能是证书;RSA 的 CA 低于 2048 位签出的证书库验不过 —— 加进来只会让人以为配好了。</summary>
    [TestMethod]
    [DataRow("hostcert-key-cert.pub")]
    [DataRow("hostcert-rsa1024.pub")]
    public void 证书与短RSA不能当CA(string keyFile)
    {
        Assert.IsFalse(KnownHostMarkers.TryCreate("*.example", Fixture(keyFile), null, out _, out string? error));
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    public void 吊销可以是一整张证书()
    {
        Assert.IsTrue(KnownHostMarkers.TryCreate(null, "@revoked * " + Fixture("hostcert-key-cert.pub"), null,
            out KnownHostMarker? marker, out string? error), error);
        Assert.AreEqual(KnownHostMarkerKind.Revoked, marker.Kind);
        Assert.AreEqual("ssh-ed25519-cert-v01@openssh.com", marker.KeyType);
    }
}
