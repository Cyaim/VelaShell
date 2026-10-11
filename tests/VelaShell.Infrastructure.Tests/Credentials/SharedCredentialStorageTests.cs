using VelaShell.Core.Credentials;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests.Credentials;

/// <summary>
/// 共享凭据(#550)的落盘:凭据本身加密存,引用它的连接一律不存认证材料。真实 SonnetDB。
/// </summary>
[TestClass]
[TestCategory("DataStore")]
public sealed class SharedCredentialStorageTests : IDisposable
{
    private readonly SonnetDbEngine _engine;
    private readonly AesSecretProtector _protector;
    private readonly string _testDirectory;

    public SharedCredentialStorageTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_credtest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _engine = new(Path.Combine(_testDirectory, "sonnetdb"));
        _protector = new(Path.Combine(_testDirectory, "secret.key"));
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    public async Task Credential_RoundTrips_WithSecretsEncryptedAtRest()
    {
        var repo = new SonnetDbSharedCredentialRepository(_engine, _protector);
        var credential = new SharedCredential
        {
            Name = "switches",
            Username = "admin",
            AuthMethod = AuthMethod.Password,
            Password = "plaintext-pass",
            PrivateKeyPassphrase = "plaintext-phrase",
            Notes = "机房"
        };

        await repo.SaveAsync(credential);

        string rawJson = await _engine.WithCollectionAsync(
            SonnetDbEngine.SharedCredentialsCollection,
            store => store.Get(credential.Id.ToString("D"))!.Json);
        Assert.DoesNotContain("plaintext-pass", rawJson, "落盘 JSON 不应包含明文密码");
        Assert.DoesNotContain("plaintext-phrase", rawJson, "落盘 JSON 不应包含明文口令");
        SharedCredential? loaded = await repo.GetAsync(credential.Id);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("plaintext-pass", loaded.Password, "读出的密码应已解密");
        Assert.AreEqual("plaintext-phrase", loaded.PrivateKeyPassphrase);
        Assert.AreEqual("admin", loaded.Username);
        Assert.AreEqual("机房", loaded.Notes);
        // 保存不改调用方手里那一份(界面还拿着它的明文)。
        Assert.AreEqual("plaintext-pass", credential.Password);
    }

    [TestMethod]
    public async Task GetAll_SortsByName_AndDeleteRemoves_RaisingChangedEachTime()
    {
        var repo = new SonnetDbSharedCredentialRepository(_engine, _protector);
        int changes = 0;
        repo.Changed += (_, _) => changes++;
        var beta = new SharedCredential { Name = "beta", Password = "b" };
        await repo.SaveAsync(beta);
        await repo.SaveAsync(new SharedCredential { Name = "Alpha", Password = "a" });

        List<SharedCredential> all = await repo.GetAllAsync();
        Assert.AreSequenceEqual(["Alpha", "beta"], [.. all.Select(c => c.Name)]);

        await repo.DeleteAsync(beta.Id);

        Assert.IsNull(await repo.GetAsync(beta.Id));
        Assert.HasCount(1, await repo.GetAllAsync());
        Assert.AreEqual(3, changes);
    }

    /// <summary>
    /// 仓储不变量:引用凭据的连接,不管从哪条保存路径进来,认证材料都不落盘 ——
    /// 用户名照常存(它可以是有意的覆盖)。
    /// </summary>
    [TestMethod]
    public async Task ReferencingProfile_NeverPersistsItsAuthMaterial()
    {
        var repo = new SonnetDbSessionRepository(_engine, _protector);
        var profile = new SessionProfile
        {
            Name = "web-01",
            Host = "10.0.0.1",
            Username = "deploy",
            AuthMethod = AuthMethod.Certificate,
            // 登录框里手输的那一份:这次连接用,不落盘。
            Password = "typed-pass",
            PrivateKeyPath = "/k",
            PrivateKeyPassphrase = "typed-phrase",
            CertificatePath = "/c",
            CredentialSource = CredentialReference.ForShared(Guid.NewGuid())
        };

        await repo.SaveSessionAsync(profile);

        SessionProfile? loaded = await repo.GetSessionAsync(profile.Id);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(profile.CredentialSource, loaded.CredentialSource);
        Assert.AreEqual("deploy", loaded.Username);
        Assert.IsNull(loaded.Password);
        Assert.IsNull(loaded.PrivateKeyPath);
        Assert.IsNull(loaded.PrivateKeyPassphrase);
        Assert.IsNull(loaded.CertificatePath);
        string rawJson = await _engine.WithCollectionAsync(
            SonnetDbEngine.ProfilesCollection,
            store => store.Get(profile.Id.ToString("D"))!.Json);
        Assert.DoesNotContain("typed-pass", rawJson);
        // 调用方那一份不动:它正拿着这份材料去连。
        Assert.AreEqual("typed-pass", profile.Password);
    }

    /// <summary>插件协议的机密字典不是认证材料,引用凭据时照样加密保存(不能被不变量顺手丢掉)。</summary>
    [TestMethod]
    public async Task ReferencingPluginProfile_StillEncryptsPluginSecrets()
    {
        var repo = new SonnetDbSessionRepository(_engine, _protector);
        var profile = new SessionProfile
        {
            Name = "s3",
            Host = "s3.example.com",
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = "velashell.s3",
            PluginSecrets = new() { ["sessionToken"] = "plaintext-token" },
            CredentialSource = CredentialReference.ForShared(Guid.NewGuid())
        };

        await repo.SaveSessionAsync(profile);

        string rawJson = await _engine.WithCollectionAsync(
            SonnetDbEngine.ProfilesCollection,
            store => store.Get(profile.Id.ToString("D"))!.Json);
        Assert.DoesNotContain("plaintext-token", rawJson);
        SessionProfile? loaded = await repo.GetSessionAsync(profile.Id);
        Assert.AreEqual("plaintext-token", loaded!.PluginSecrets!["sessionToken"]);
    }
}
