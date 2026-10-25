using NSubstitute;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Credentials;

namespace VelaShell.Infrastructure.Tests.Credentials;

/// <summary>
/// 凭据解析(#550):引用换成认证材料的规则 —— 什么时候不取、取了怎么填、哪些情形要报错退回登录框。
/// </summary>
[TestClass]
public sealed class CredentialResolverTests
{
    private readonly ISharedCredentialRepository _repository = Substitute.For<ISharedCredentialRepository>();
    private readonly CredentialResolver _resolver;

    public CredentialResolverTests() =>
        _resolver = new([new SharedCredentialProvider(_repository)]);

    private SharedCredential Store(SharedCredential credential)
    {
        _repository.GetAsync(credential.Id).Returns(credential);
        return credential;
    }

    private static SessionProfile Referencing(SharedCredential credential, ConnectionType type = ConnectionType.SSH) =>
        new()
        {
            Name = "web-01",
            Host = "10.0.0.1",
            ConnectionType = type,
            CredentialSource = CredentialReference.ForShared(credential.Id)
        };

    /// <summary>
    /// 共享凭据里存着的那份在这台设备上解不开(载入时仍是密文):不交给连接去试登录,报「解不开」并点名是哪条凭据,
    /// 连接流程据此退回登录框(第二轮第 20 条)。本次在登录框里手输过的照用。
    /// </summary>
    [TestMethod]
    public async Task UnreadableSharedSecret_IsReported_NotHandedToTheConnection()
    {
        SharedCredential credential = Store(new() { Name = "ops", Username = "root", Password = "enc1:AAAAAAAAAAAAAAAAAAAAAAAA" });
        SessionProfile profile = Referencing(credential);

        CredentialProviderException error = await Assert.ThrowsExactlyAsync<CredentialProviderException>(
            () => _resolver.ResolveAsync(profile));

        Assert.AreEqual(CredentialFailure.UnreadableSecret, error.Kind);
        Assert.Contains("ops", error.Message);

        profile.Password = "typed-in-the-login-dialog";
        Assert.AreEqual("typed-in-the-login-dialog", (await _resolver.ResolveAsync(profile)).Password);
    }

    [TestMethod]
    public async Task ProfileWithoutReference_IsReturnedAsIs()
    {
        var profile = new SessionProfile { Name = "p", Host = "h", Username = "root", Password = "pw" };

        Assert.AreSame(profile, await _resolver.ResolveAsync(profile));
    }

    [TestMethod]
    public async Task Reference_FillsACopy_AndLeavesTheProfileUntouched()
    {
        SharedCredential credential = Store(new() { Name = "ops", Username = "root", Password = "s3cret" });
        SessionProfile profile = Referencing(credential);

        SessionProfile effective = await _resolver.ResolveAsync(profile);

        Assert.AreNotSame(profile, effective);
        Assert.AreEqual("root", effective.Username);
        Assert.AreEqual(AuthMethod.Password, effective.AuthMethod);
        Assert.AreEqual("s3cret", effective.Password);
        // 明文只进副本,原配置(会话树缓存、tab.Profile 里那一份)不沾。
        Assert.AreEqual(string.Empty, profile.Username);
        Assert.IsNull(profile.Password);
    }

    [TestMethod]
    public async Task ConnectionUsername_OverridesTheCredentials()
    {
        SharedCredential credential = Store(new()
        {
            Name = "aws",
            Username = "ec2-user",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/keys/aws",
            PrivateKeyPassphrase = "phrase"
        });
        SessionProfile profile = Referencing(credential);
        profile.Username = "ubuntu";

        SessionProfile effective = await _resolver.ResolveAsync(profile);

        Assert.AreEqual("ubuntu", effective.Username);
        Assert.AreEqual(AuthMethod.PrivateKey, effective.AuthMethod);
        Assert.AreEqual("/keys/aws", effective.PrivateKeyPath);
        Assert.AreEqual("phrase", effective.PrivateKeyPassphrase);
    }

    /// <summary>登录框里手输的材料优先:否则用户刚输的新密码会被凭据里那份旧的盖掉,重试永远用旧密码。</summary>
    [TestMethod]
    public async Task TypedMaterialWithUsername_WinsWithoutTouchingTheCredential()
    {
        SessionProfile profile = Referencing(new SharedCredential { Name = "gone" });
        profile.Username = "root";
        profile.Password = "typed";

        Assert.AreSame(profile, await _resolver.ResolveAsync(profile));
        await _repository.DidNotReceive().GetAsync(Arg.Any<Guid>());
    }

    /// <summary>手输了材料但用户名一直跟着凭据走:材料用手输的,用户名仍从凭据取。</summary>
    [TestMethod]
    public async Task TypedMaterialWithoutUsername_TakesOnlyTheUsernameFromTheCredential()
    {
        SharedCredential credential = Store(new() { Name = "ops", Username = "root", Password = "old" });
        SessionProfile profile = Referencing(credential);
        profile.Password = "typed";

        SessionProfile effective = await _resolver.ResolveAsync(profile);

        Assert.AreEqual("root", effective.Username);
        Assert.AreEqual("typed", effective.Password);
    }

    [TestMethod]
    public async Task DeletedCredential_IsReportedAsNotFound()
    {
        SessionProfile profile = Referencing(new SharedCredential { Name = "gone" });

        CredentialProviderException ex = await Assert.ThrowsExactlyAsync<CredentialProviderException>(
            () => _resolver.ResolveAsync(profile));

        Assert.AreEqual(CredentialFailure.NotFound, ex.Kind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(ex.Message));
    }

    [TestMethod]
    public async Task UnknownProvider_IsReportedAsDisabled()
    {
        var profile = new SessionProfile
        {
            Name = "p",
            Host = "h",
            CredentialSource = new() { ProviderId = "1password", ItemId = "abc" }
        };

        CredentialProviderException ex = await Assert.ThrowsExactlyAsync<CredentialProviderException>(
            () => _resolver.ResolveAsync(profile));

        Assert.AreEqual(CredentialFailure.ProviderDisabled, ex.Kind);
        Assert.AreEqual("1password", ex.ProviderId);
    }

    [TestMethod]
    public async Task NoUsernameAnywhere_IsReported()
    {
        SharedCredential credential = Store(new() { Name = "ops", Password = "pw" });

        CredentialProviderException ex = await Assert.ThrowsExactlyAsync<CredentialProviderException>(
            () => _resolver.ResolveAsync(Referencing(credential)));

        Assert.AreEqual(CredentialFailure.MissingUsername, ex.Kind);
    }

    /// <summary>插件协议允许没有用户名(匿名桶、没设 requirepass 的 Redis)。</summary>
    [TestMethod]
    public async Task PluginConnection_MayGoWithoutUsername()
    {
        SharedCredential credential = Store(new() { Name = "redis", Password = "pw" });

        SessionProfile effective = await _resolver.ResolveAsync(Referencing(credential, ConnectionType.Plugin));

        Assert.AreEqual(string.Empty, effective.Username);
        Assert.AreEqual("pw", effective.Password);
    }

    /// <summary>云同步没开端到端口令时拉来的凭据没有密码:报出来退回登录框,而不是带空密码去试一次。</summary>
    [TestMethod]
    public async Task CredentialWithoutSecret_IsReportedAsMissingSecret()
    {
        SharedCredential credential = Store(new() { Name = "ops", Username = "root" });

        CredentialProviderException ex = await Assert.ThrowsExactlyAsync<CredentialProviderException>(
            () => _resolver.ResolveAsync(Referencing(credential)));

        Assert.AreEqual(CredentialFailure.MissingSecret, ex.Kind);
    }

    [TestMethod]
    public async Task AgentCredential_NeedsNoMaterial()
    {
        SharedCredential credential = Store(new() { Name = "agent", Username = "root", AuthMethod = AuthMethod.Agent });

        SessionProfile effective = await _resolver.ResolveAsync(Referencing(credential));

        Assert.AreEqual(AuthMethod.Agent, effective.AuthMethod);
    }

    [TestMethod]
    [DataRow(ConnectionType.FTP)]
    [DataRow(ConnectionType.Plugin)]
    public async Task KeyCredential_OnAPasswordOnlyProtocol_IsRejected(ConnectionType type)
    {
        SharedCredential credential = Store(new()
        {
            Name = "key",
            Username = "root",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/k"
        });

        CredentialProviderException ex = await Assert.ThrowsExactlyAsync<CredentialProviderException>(
            () => _resolver.ResolveAsync(Referencing(credential, type)));

        Assert.AreEqual(CredentialFailure.UnsupportedAuthMethod, ex.Kind);
    }

    /// <summary>匿名 FTP 不发凭据:哪怕引用指向一条已删除的凭据,也不该凭空报错。</summary>
    [TestMethod]
    public async Task AnonymousFtp_IsNotResolved()
    {
        SessionProfile profile = Referencing(new SharedCredential { Name = "gone" }, ConnectionType.FTP);
        profile.Ftp = new() { Anonymous = true };

        Assert.AreSame(profile, await _resolver.ResolveAsync(profile));
    }

    /// <summary>系统密钥库不走共享凭据:万一带着引用(手改的配置),不去取它,钥照用自己的。</summary>
    [TestMethod]
    public async Task KeyStore_IgnoresACredentialReference()
    {
        SharedCredential credential = Store(new() { Name = "ops", Username = "shared-user", Password = "shared-secret" });
        SessionProfile profile = Referencing(credential);
        profile.AuthMethod = AuthMethod.KeyStore;
        profile.Username = "deploy";
        profile.KeyStoreKey = new SystemKeyReference { Provider = SystemKeyReference.PlatformProvider, KeyName = "deploy" };

        SessionProfile effective = await _resolver.ResolveAsync(profile);

        Assert.AreEqual(AuthMethod.KeyStore, effective.AuthMethod);
        Assert.AreEqual(profile.KeyStoreKey, effective.KeyStoreKey);
        Assert.IsNull(effective.CredentialSource);
        Assert.IsNull(effective.Password);
        Assert.IsNotNull(profile.CredentialSource, "改的是副本");
        await _repository.DidNotReceive().GetAsync(Arg.Any<Guid>());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("deploy")]
    public async Task Tailscale_CleansACopyWithoutReadingOrInheritingTheCredential(string username)
    {
        SharedCredential credential = Store(new() { Name = "ops", Username = "shared-user", Password = "shared-secret" });
        SessionProfile profile = Referencing(credential, ConnectionType.SFTP);
        profile.AuthMethod = AuthMethod.Tailscale;
        profile.Username = username;
        profile.Password = "old";
        profile.PrivateKeyPath = "/missing-key";
        profile.PrivateKeyPassphrase = "old-phrase";
        profile.CertificatePath = "/missing-cert";
        profile.RememberPassword = true;
        profile.JumpHostProfileId = Guid.NewGuid();

        SessionProfile effective = await _resolver.ResolveAsync(profile);

        Assert.AreNotSame(profile, effective);
        Assert.AreEqual(AuthMethod.Tailscale, effective.AuthMethod);
        Assert.AreEqual(username, effective.Username);
        Assert.AreEqual(ConnectionType.SFTP, effective.ConnectionType);
        Assert.AreEqual(profile.JumpHostProfileId, effective.JumpHostProfileId);
        Assert.IsNull(effective.CredentialSource);
        Assert.IsNull(effective.Password);
        Assert.IsNull(effective.PrivateKeyPath);
        Assert.IsNull(effective.PrivateKeyPassphrase);
        Assert.IsNull(effective.CertificatePath);
        Assert.IsFalse(effective.RememberPassword);
        Assert.IsNotNull(profile.CredentialSource);
        Assert.AreEqual("old", profile.Password);
        await _repository.DidNotReceive().GetAsync(Arg.Any<Guid>());
    }

    [TestMethod]
    [DataRow(ConnectionType.SSH)]
    [DataRow(ConnectionType.SFTP)]
    [DataRow(ConnectionType.FTP)]
    [DataRow(ConnectionType.Plugin)]
    public async Task TailscaleSharedCredential_IsRejectedWithoutChangingTheSession(ConnectionType type)
    {
        SharedCredential credential = Store(new() { Name = "invalid", Username = "root", AuthMethod = AuthMethod.Tailscale });
        SessionProfile profile = Referencing(credential, type);
        profile.Password = "typed";

        CredentialProviderException ex = await Assert.ThrowsExactlyAsync<CredentialProviderException>(
            () => _resolver.ResolveAsync(profile));

        Assert.AreEqual(CredentialFailure.UnsupportedAuthMethod, ex.Kind);
        Assert.AreEqual(AuthMethod.Password, profile.AuthMethod);
        Assert.AreEqual(string.Empty, profile.Username);
        Assert.AreEqual("typed", profile.Password);
    }
}
