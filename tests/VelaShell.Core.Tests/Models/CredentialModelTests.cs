using System.Reflection;
using System.Text.Json;
using VelaShell.Core.Credentials;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 共享凭据(#550)在 Core 里的那几条规矩:引用的形状、落盘格式、「本次手输」的判定。
/// </summary>
[TestClass]
[TestCategory("DataStore")]
public sealed class CredentialModelTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [TestMethod]
    public void SharedReference_RoundTripsItsId()
    {
        var id = Guid.NewGuid();

        var reference = CredentialReference.ForShared(id);

        Assert.AreEqual(CredentialReference.SharedProviderId, reference.ProviderId);
        Assert.IsTrue(reference.TryGetSharedId(out Guid parsed));
        Assert.AreEqual(id, parsed);
    }

    [TestMethod]
    public void ReferenceFromAnotherProvider_IsNotASharedCredential()
    {
        var reference = new CredentialReference { ProviderId = "1password", ItemId = Guid.NewGuid().ToString("D") };

        Assert.IsFalse(reference.TryGetSharedId(out Guid parsed));
        Assert.AreEqual(Guid.Empty, parsed);
    }

    /// <summary>老数据没有这个字段:读出来就是 null(在本配置中填写),零迁移。</summary>
    [TestMethod]
    public void LegacyProfileJson_HasNoCredentialSource()
    {
        SessionProfile? profile = JsonSerializer.Deserialize<SessionProfile>("""{"name":"legacy","host":"h"}""", Options);

        Assert.IsNotNull(profile);
        Assert.IsNull(profile.CredentialSource);
    }

    [TestMethod]
    public void CredentialSource_RoundTripsThroughJson()
    {
        var profile = new SessionProfile { Name = "n", CredentialSource = CredentialReference.ForShared(Guid.NewGuid()) };

        string json = JsonSerializer.Serialize(profile, Options);
        SessionProfile? back = JsonSerializer.Deserialize<SessionProfile>(json, Options);

        Assert.IsNotNull(back);
        Assert.AreEqual(profile.CredentialSource, back.CredentialSource);
        // 方法而不是属性:引用里不该多出一个派生字段。
        Assert.DoesNotContain("sharedId", json);
    }

    /// <summary>派生的 HasSecret 不进落盘 JSON(它随认证方式与材料算出来,存下来只会过时)。</summary>
    [TestMethod]
    public void SharedCredential_DoesNotSerializeDerivedState()
    {
        string json = JsonSerializer.Serialize(new SharedCredential { Name = "n", Password = "p" }, Options);

        Assert.DoesNotContain("hasSecret", json);
    }

    [TestMethod]
    public void SharedCredentialClone_CopiesEveryProperty()
    {
        var source = new SharedCredential
        {
            Name = "switches",
            Username = "admin",
            AuthMethod = AuthMethod.Certificate,
            Password = "pw",
            PrivateKeyPath = "/k",
            PrivateKeyPassphrase = "phrase",
            CertificatePath = "/k-cert.pub",
            Notes = "机房"
        };

        SharedCredential copy = source.Clone();

        foreach (PropertyInfo property in typeof(SharedCredential).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.AreEqual(property.GetValue(source), property.GetValue(copy), $"{property.Name} 没被 Clone 拷过去");
        }
    }

    [TestMethod]
    [DataRow(AuthMethod.Password, "pw", null, null, true)]
    [DataRow(AuthMethod.Password, "", null, null, false)]
    [DataRow(AuthMethod.PrivateKey, null, "/k", null, true)]
    [DataRow(AuthMethod.PrivateKey, null, null, null, false)]
    [DataRow(AuthMethod.Certificate, null, "/k", "/c", true)]
    [DataRow(AuthMethod.Certificate, null, "/k", null, false)]
    [DataRow(AuthMethod.Agent, null, null, null, true)]
    public void SharedCredential_HasSecret_FollowsAuthMethod(AuthMethod method, string? password, string? key, string? cert, bool expected)
    {
        var credential = new SharedCredential { AuthMethod = method, Password = password, PrivateKeyPath = key, CertificatePath = cert };

        Assert.AreEqual(expected, credential.HasSecret);
    }

    /// <summary>
    /// 「本次手输」的判定:带引用的配置落盘时材料是空的,内存里有材料只可能来自登录框。
    /// Agent 没有材料可带,永远不算手输 —— 否则一条 Agent 认证的旧配置挂上凭据后永远不去取凭据。
    /// </summary>
    [TestMethod]
    [DataRow(AuthMethod.Password, "pw", null, null, true)]
    [DataRow(AuthMethod.Password, null, "/k", null, false)]
    [DataRow(AuthMethod.PrivateKey, null, "/k", null, true)]
    [DataRow(AuthMethod.Certificate, null, "/k", null, false)]
    [DataRow(AuthMethod.Certificate, null, "/k", "/c", true)]
    [DataRow(AuthMethod.Agent, "pw", "/k", "/c", false)]
    [DataRow(AuthMethod.Tailscale, "pw", "/k", "/c", false)]
    public void HasInline_LooksAtTheMaterialOfTheChosenMethod(AuthMethod method, string? password, string? key, string? cert, bool expected)
    {
        var profile = new SessionProfile { AuthMethod = method, Password = password, PrivateKeyPath = key, CertificatePath = cert };

        Assert.AreEqual(expected, CredentialMaterial.HasInline(profile));
    }

    [TestMethod]
    public void NeedsResolution_OnlyWithAReferenceAndNoTypedMaterial()
    {
        var reference = CredentialReference.ForShared(Guid.NewGuid());

        Assert.IsFalse(CredentialMaterial.NeedsResolution(new SessionProfile { Password = "pw" }));
        Assert.IsTrue(CredentialMaterial.NeedsResolution(new SessionProfile { CredentialSource = reference }));
        Assert.IsFalse(CredentialMaterial.NeedsResolution(new SessionProfile { CredentialSource = reference, Password = "typed" }));
    }

    [TestMethod]
    public void ClearInline_KeepsUsernameAndAuthMethod()
    {
        var profile = new SessionProfile
        {
            Username = "ops",
            AuthMethod = AuthMethod.Certificate,
            Password = "pw",
            PrivateKeyPath = "/k",
            PrivateKeyPassphrase = "phrase",
            CertificatePath = "/c"
        };

        CredentialMaterial.ClearInline(profile);

        Assert.AreEqual("ops", profile.Username);
        Assert.AreEqual(AuthMethod.Certificate, profile.AuthMethod);
        Assert.IsNull(profile.Password);
        Assert.IsNull(profile.PrivateKeyPath);
        Assert.IsNull(profile.PrivateKeyPassphrase);
        Assert.IsNull(profile.CertificatePath);
    }

    [TestMethod]
    [DataRow(AuthMethod.Password, 0)]
    [DataRow(AuthMethod.PrivateKey, 1)]
    [DataRow(AuthMethod.Certificate, 2)]
    [DataRow(AuthMethod.Agent, 3)]
    [DataRow(AuthMethod.Tailscale, 4)]
    public void AuthMethod_PreservesStoredNumbers(AuthMethod method, int number)
    {
        SessionProfile profile = JsonSerializer.Deserialize<SessionProfile>($$"""{"authMethod":{{number}}}""", Options)!;

        Assert.AreEqual(method, profile.AuthMethod);
        Assert.AreEqual(number, (int)method);
    }

    [TestMethod]
    [DataRow(AuthMethod.Password)]
    [DataRow(AuthMethod.PrivateKey)]
    [DataRow(AuthMethod.Certificate)]
    [DataRow(AuthMethod.Agent)]
    [DataRow(AuthMethod.Tailscale)]
    public void ClearTailscaleCredentials_OnlyClearsTailscale(AuthMethod method)
    {
        var profile = new SessionProfile
        {
            Username = "ops", AuthMethod = method, ConnectionType = ConnectionType.SFTP,
            Password = "pw", PrivateKeyPath = "/k", PrivateKeyPassphrase = "phrase", CertificatePath = "/c",
            CredentialSource = CredentialReference.ForShared(Guid.NewGuid()), RememberPassword = true,
            JumpHostProfileId = Guid.NewGuid(), Port = 2222
        };
        SessionProfile original = profile.Clone();

        CredentialMaterial.ClearTailscaleCredentials(profile);

        Assert.AreEqual(original.Username, profile.Username);
        Assert.AreEqual(original.AuthMethod, profile.AuthMethod);
        Assert.AreEqual(original.JumpHostProfileId, profile.JumpHostProfileId);
        Assert.AreEqual(ConnectionType.SFTP, profile.ConnectionType);
        Assert.AreEqual(2222, profile.Port);
        bool tailscale = method == AuthMethod.Tailscale;
        Assert.AreEqual(tailscale ? null : original.Password, profile.Password);
        Assert.AreEqual(tailscale ? null : original.PrivateKeyPath, profile.PrivateKeyPath);
        Assert.AreEqual(tailscale ? null : original.PrivateKeyPassphrase, profile.PrivateKeyPassphrase);
        Assert.AreEqual(tailscale ? null : original.CertificatePath, profile.CertificatePath);
        Assert.AreEqual(tailscale ? null : original.CredentialSource, profile.CredentialSource);
        Assert.AreEqual(!tailscale, profile.RememberPassword);
        Assert.AreEqual(method == AuthMethod.Agent, CredentialMaterial.NeedsResolution(profile));
        original.AuthMethod = AuthMethod.Tailscale;
        Assert.IsFalse(CredentialMaterial.NeedsResolution(original), "隐藏引用不能让 Tailscale 去取共享凭据");
    }
}
