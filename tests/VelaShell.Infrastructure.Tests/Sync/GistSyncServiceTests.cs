using NSubstitute;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sync;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Sync;

namespace VelaShell.Infrastructure.Tests.Sync;

/// <summary>Gist 远端载荷应用后的运行时刷新通知。</summary>
[TestClass]
public sealed class GistSyncServiceTests
{
    [TestMethod]
    public async Task ApplyRemoteAsync_WithProfiles_NotifiesAfterRepositoryIsUpdated()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        ISessionRepository sessions = Substitute.For<ISessionRepository>();
        IAppDataStore store = Substitute.For<IAppDataStore>();
        IQuickCommandRepository snippets = Substitute.For<IQuickCommandRepository>();
        ISecretProtector secrets = Substitute.For<ISecretProtector>();
        sessions.GetAllSessionsAsync().Returns([]);
        var service = new GistSyncService(settings, sessions, store, snippets, secrets);
        SessionProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "cloud-server",
            Host = "cloud.example.com",
            Username = "root",
        };
        bool profileWasSavedWhenNotified = false;
        service.ProfilesApplied += (_, _) =>
            profileWasSavedWhenNotified = sessions.ReceivedCalls().Any(call =>
                call.GetMethodInfo().Name == nameof(ISessionRepository.ReplaceSessionAsync)
            );

        SyncResult result = await service.ApplyRemoteAsync(
            new SyncSettings
            {
                SyncAppSettings = false,
                SyncProfiles = true,
                SyncSnippets = false,
            },
            new SyncEnvelope
            {
                Payload = new SyncPayload
                {
                    DeviceName = "device-1",
                    UpdatedAtUtc = DateTime.UtcNow,
                    Profiles = [profile],
                },
            },
            remoteVersion: "revision-1",
            CancellationToken.None
        );

        Assert.IsTrue(result.Success);
        Assert.AreEqual(SyncAction.Pulled, result.Action);
        Assert.IsTrue(profileWasSavedWhenNotified, "通知必须发生在连接配置写入仓储之后。");
        await sessions.Received(1).ReplaceSessionAsync(profile);
    }

    // ———— 共享凭据(#550) ————

    private static (GistSyncService Service, ISessionRepository Sessions, ISharedCredentialRepository Credentials) CreateWithCredentials()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        ISessionRepository sessions = Substitute.For<ISessionRepository>();
        IAppDataStore store = Substitute.For<IAppDataStore>();
        IQuickCommandRepository snippets = Substitute.For<IQuickCommandRepository>();
        ISecretProtector secrets = Substitute.For<ISecretProtector>();
        ISharedCredentialRepository credentials = Substitute.For<ISharedCredentialRepository>();
        sessions.GetAllSessionsAsync().Returns([]);
        sessions.GetAllGroupsAsync().Returns([]);
        credentials.GetAllAsync().Returns([]);
        return (new GistSyncService(settings, sessions, store, snippets, secrets, sharedCredentials: credentials), sessions, credentials);
    }

    // 每次新建:ApplyRemoteAsync 会往配置上写同步状态,共用一个实例会让用例之间互相串。
    private static SyncSettings ProfilesOnly() => new() { SyncAppSettings = false, SyncProfiles = true, SyncSnippets = false };

    /// <summary>没设端到端口令:凭据随连接上传,但密码与口令剥掉 —— 与连接配置同一条规矩。</summary>
    [TestMethod]
    public async Task Push_WithoutPassphrase_CarriesCredentialsWithoutSecrets_AsSchema3()
    {
        (GistSyncService service, _, ISharedCredentialRepository credentials) = CreateWithCredentials();
        credentials.GetAllAsync().Returns([
            new SharedCredential { Name = "switches", Username = "admin", Password = "plaintext-pass", PrivateKeyPassphrase = "plaintext-phrase" }
        ]);

        string content = await service.BuildGistContentAsync(ProfilesOnly(), CancellationToken.None);

        Assert.Contains("switches", content);
        Assert.Contains("admin", content);
        Assert.DoesNotContain("plaintext-pass", content, "未加密载荷不得带凭据的明文密码");
        Assert.DoesNotContain("plaintext-phrase", content);
        SyncEnvelope envelope = System.Text.Json.JsonSerializer.Deserialize<SyncEnvelope>(
            content, System.Text.Json.JsonSerializerOptions.Web)!;
        Assert.AreEqual(3, envelope.SchemaVersion, "用到共享凭据的载荷要让不认识它的旧客户端拒收");
        Assert.AreEqual(3, envelope.Payload!.SchemaVersion);
    }

    /// <summary>
    /// 同步口令解不开(本机加密密钥这次没取到、数据目录从别的电脑拷来)时必须停下:
    /// 拿密文本身当口令去加密上传,远端那份就成了别的设备谁也解不开的东西。
    /// </summary>
    [TestMethod]
    public async Task Push_WithAnUndecryptablePassphrase_RefusesInsteadOfEncryptingWithTheCiphertext()
    {
        string directory = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ISessionRepository sessions = Substitute.For<ISessionRepository>();
            sessions.GetAllSessionsAsync().Returns([]);
            sessions.GetAllGroupsAsync().Returns([]);
            var service = new GistSyncService(Substitute.For<ISettingsService>(), sessions, Substitute.For<IAppDataStore>(),
                Substitute.For<IQuickCommandRepository>(), new AesSecretProtector(Path.Combine(directory, "secret.key")));
            SyncSettings config = ProfilesOnly();
            // 另一把密钥加密的口令:在这里解不开。
            config.ProtectedPassphrase = new AesSecretProtector(Path.Combine(directory, "other.key")).Protect("e2e-passphrase");

            InvalidOperationException ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.BuildGistContentAsync(config, CancellationToken.None));

            Assert.AreEqual(Strings.Get("SyncSvc_PassphraseDecryptFailed"), ex.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>没用这个功能的人,载荷版本不变,照旧与旧客户端互通。</summary>
    [TestMethod]
    public void PayloadWithoutSharedCredentials_StaysOnSchema2()
    {
        var plain = new SyncPayload { Profiles = [new SessionProfile { Name = "p" }] };
        var referencing = new SyncPayload
        {
            Profiles = [new SessionProfile { Name = "p", CredentialSource = CredentialReference.ForShared(Guid.NewGuid()) }]
        };
        var withCredential = new SyncPayload { SharedCredentials = [new SharedCredential { Name = "c" }] };

        Assert.AreEqual(2, GistSyncService.PayloadSchemaVersion(plain));
        Assert.AreEqual(3, GistSyncService.PayloadSchemaVersion(referencing));
        Assert.AreEqual(3, GistSyncService.PayloadSchemaVersion(withCredential));
    }

    /// <summary>拉到不带密码的凭据:保留本机已存的那一份,而不是把它抹掉;凭据先于连接落库。</summary>
    [TestMethod]
    public async Task Pull_KeepsLocalSecrets_AndSavesCredentialsBeforeProfiles()
    {
        (GistSyncService service, ISessionRepository sessions, ISharedCredentialRepository credentials) = CreateWithCredentials();
        var id = Guid.NewGuid();
        credentials.GetAllAsync().Returns([new SharedCredential { Id = id, Name = "old", Password = "local-pass", PrivateKeyPassphrase = "local-phrase" }]);
        var incoming = new SharedCredential { Id = id, Name = "renamed", Username = "admin" };
        var profile = new SessionProfile { Name = "web", Host = "h", CredentialSource = CredentialReference.ForShared(id) };
        var order = new List<string>();
        credentials.When(c => c.SaveAsync(Arg.Any<SharedCredential>())).Do(_ => order.Add("credential"));
        sessions.When(s => s.ReplaceSessionAsync(Arg.Any<SessionProfile>())).Do(_ => order.Add("profile"));

        SyncResult result = await service.ApplyRemoteAsync(
            ProfilesOnly(),
            new SyncEnvelope
            {
                SchemaVersion = 3,
                Payload = new SyncPayload { SchemaVersion = 3, Profiles = [profile], SharedCredentials = [incoming] }
            },
            remoteVersion: "rev",
            CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        await credentials.Received(1).SaveAsync(Arg.Is<SharedCredential>(c =>
            c.Id == id && c.Name == "renamed" && c.Password == "local-pass" && c.PrivateKeyPassphrase == "local-phrase"));
        Assert.AreSequenceEqual(["credential", "profile"], order);
    }

    /// <summary>
    /// 旧版本客户端推上去的载荷没有 <c>directoryBookmarks</c> 字段:拉下来不能当成「收藏清空了」整条覆盖,
    /// 而要走保留库里现存收藏的 <see cref="ISessionRepository.SaveSessionAsync" />。
    /// </summary>
    [TestMethod]
    public async Task Pull_ProfileWithoutBookmarksField_KeepsLocalBookmarks()
    {
        (GistSyncService service, ISessionRepository sessions, _) = CreateWithCredentials();
        var id = Guid.NewGuid();

        SyncResult result = await service.ApplyRemoteAsync(
            ProfilesOnly(), GistSyncService.ParseEnvelope(EnvelopeJson(id, bookmarksField: null)), "rev", CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        await sessions.Received(1).SaveSessionAsync(Arg.Is<SessionProfile>(p =>
            p.Id == id && p.Name == "web" && p.DirectoryBookmarks != null && p.DirectoryBookmarks.Count == 0));
        await sessions.DidNotReceive().ReplaceSessionAsync(Arg.Any<SessionProfile>());
    }

    /// <summary>字段在、是空数组:那是对方真的清空了收藏,照旧整条覆盖。</summary>
    [TestMethod]
    public async Task Pull_ProfileWithEmptyBookmarksArray_ReplacesTheWholeProfile()
    {
        (GistSyncService service, ISessionRepository sessions, _) = CreateWithCredentials();
        var id = Guid.NewGuid();

        SyncResult result = await service.ApplyRemoteAsync(
            ProfilesOnly(), GistSyncService.ParseEnvelope(EnvelopeJson(id, bookmarksField: "[]")), "rev", CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        await sessions.Received(1).ReplaceSessionAsync(Arg.Is<SessionProfile>(p =>
            p.Id == id && p.DirectoryBookmarks != null && p.DirectoryBookmarks.Count == 0));
        await sessions.DidNotReceive().SaveSessionAsync(Arg.Any<SessionProfile>());
    }

    /// <summary>字段里有收藏:整条覆盖,收藏取载荷里的。</summary>
    [TestMethod]
    public async Task Pull_ProfileWithBookmarks_ReplacesWithTheIncomingBookmarks()
    {
        (GistSyncService service, ISessionRepository sessions, _) = CreateWithCredentials();
        var id = Guid.NewGuid();
        const string bookmarks = """[{"name":"web","localPath":"C:\\site","remotePath":"/var/www"}]""";

        SyncResult result = await service.ApplyRemoteAsync(
            ProfilesOnly(), GistSyncService.ParseEnvelope(EnvelopeJson(id, bookmarks)), "rev", CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        await sessions.Received(1).ReplaceSessionAsync(Arg.Is<SessionProfile>(p =>
            p.Id == id && p.DirectoryBookmarks.Count == 1 && p.DirectoryBookmarks[0].RemotePath == "/var/www"));
    }

    /// <summary>未加密信封的 JSON;<paramref name="bookmarksField" /> 为 null 时连字段都不写(旧客户端的形状)。</summary>
    private static string EnvelopeJson(Guid profileId, string? bookmarksField)
    {
        string bookmarks = bookmarksField is null ? string.Empty : ",\"directoryBookmarks\":" + bookmarksField;
        return "{\"schemaVersion\":2,\"updatedAtUtc\":\"2026-10-01T00:00:00Z\",\"deviceName\":\"old\",\"encrypted\":false,"
               + "\"payload\":{\"schemaVersion\":2,\"updatedAtUtc\":\"2026-10-01T00:00:00Z\",\"deviceName\":\"old\","
               + "\"profiles\":[{\"id\":\"" + profileId.ToString("D") + "\",\"name\":\"web\",\"host\":\"web.example.com\","
               + "\"username\":\"deploy\"" + bookmarks + "}]}}";
    }

    // ———— 设置里的机密:代理密码、告警 Webhook 地址 ————

    private const string ProxyPassword = "proxy-pass-should-not-reach-github";
    private const string WebhookUrl = "https://oapi.dingtalk.com/robot/send?access_token=webhook-token-123";

    private static AppSettings SettingsWithSecrets()
    {
        var settings = new AppSettings();
        settings.Proxy.Type = "socks5";
        settings.Proxy.Host = "proxy.corp.example";
        settings.Proxy.Port = 1080;
        settings.Proxy.Username = "vela";
        settings.Proxy.Password = ProxyPassword;
        settings.Security.AlertWebhook = true;
        settings.Security.WebhookUrl = WebhookUrl;
        settings.Normalize();
        return settings;
    }

    private static SyncSettings SettingsOnly(string? protectedPassphrase = null) => new()
    {
        SyncAppSettings = true,
        SyncProfiles = false,
        SyncSnippets = false,
        ProtectedPassphrase = protectedPassphrase,
    };

    /// <summary>
    /// 没设端到端口令:设置照常同步,但代理密码与 Webhook 地址(本身就是令牌)不上传 —— 与连接配置的密码同一条规矩,
    /// 名单取 §237 的 <see cref="SettingsCategories.SecretFields" />。
    /// </summary>
    [TestMethod]
    public async Task Push_WithoutPassphrase_LeavesOutTheProxyPasswordAndWebhook()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_ => SettingsWithSecrets());
        var service = new GistSyncService(settings, Substitute.For<ISessionRepository>(), Substitute.For<IAppDataStore>(),
            Substitute.For<IQuickCommandRepository>(), Substitute.For<ISecretProtector>());

        string content = await service.BuildGistContentAsync(SettingsOnly(), CancellationToken.None);

        Assert.DoesNotContain(ProxyPassword, content, "无口令载荷不得带代理密码");
        Assert.DoesNotContain("webhook-token-123", content, "无口令载荷不得带 Webhook 地址");
        Assert.Contains("proxy.corp.example", content, "代理地址不是机密,照常同步");
        SyncEnvelope envelope = GistSyncService.ParseEnvelope(content);
        Assert.IsFalse(envelope.Encrypted);
        Assert.IsTrue(envelope.Payload!.Settings!.Security.AlertWebhook, "开关照带");
    }

    /// <summary>设了口令:载荷整份加密,机密随同步走(带机密换机本来就走这条路);明文里看不到它们。</summary>
    [TestMethod]
    public async Task Push_WithPassphrase_CarriesThemInsideTheCiphertext()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_ => SettingsWithSecrets());
        ISecretProtector protector = Substitute.For<ISecretProtector>();
        protector.Unprotect("protected-passphrase").Returns("correct horse battery staple");
        var service = new GistSyncService(settings, Substitute.For<ISessionRepository>(), Substitute.For<IAppDataStore>(),
            Substitute.For<IQuickCommandRepository>(), protector);

        string content = await service.BuildGistContentAsync(SettingsOnly("protected-passphrase"), CancellationToken.None);

        Assert.DoesNotContain(ProxyPassword, content);
        SyncEnvelope envelope = GistSyncService.ParseEnvelope(content);
        Assert.IsTrue(envelope.Encrypted);
        string plaintext = SyncCrypto.Decrypt(envelope.CipherText!, "correct horse battery staple");
        Assert.Contains(ProxyPassword, plaintext);
        Assert.Contains("webhook-token-123", plaintext);
    }

    /// <summary>
    /// 拉到不带机密的设置:与设置导入同一口径 —— 代理的类型、主机、端口、用户名都与本机相同才留本机密码,
    /// 换了一台代理就不留(本机密码不能交给别人的代理);Webhook 地址一律留本机的。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Pull_SettingsWithoutSecrets_KeepTheLocalOnes(bool sameProxy)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_ => SettingsWithSecrets());
        var service = new GistSyncService(settings, Substitute.For<ISessionRepository>(), Substitute.For<IAppDataStore>(),
            Substitute.For<IQuickCommandRepository>(), Substitute.For<ISecretProtector>());
        AppSettings remote = SettingsWithSecrets();
        remote.Proxy.Password = string.Empty;
        remote.Security.WebhookUrl = string.Empty;
        remote.Theme = "light";
        if (!sameProxy)
        {
            remote.Proxy.Host = "proxy.elsewhere.example";
        }

        SyncResult result = await service.ApplyRemoteAsync(
            SettingsOnly(),
            new SyncEnvelope { Payload = new SyncPayload { DeviceName = "laptop", UpdatedAtUtc = DateTime.UtcNow, Settings = remote } },
            remoteVersion: "rev",
            CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        await settings.Received(1).SaveSettingsAsync(Arg.Is<AppSettings>(s =>
            s.Theme == "light"
            && s.Proxy.Password == (sameProxy ? ProxyPassword : string.Empty)
            && s.Security.WebhookUrl == WebhookUrl));
    }

    /// <summary>拉到的设置自己带着机密(加密载荷、旧版本推的明文):照用。</summary>
    [TestMethod]
    public async Task Pull_SettingsCarryingSecrets_UseTheIncomingOnes()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_ => SettingsWithSecrets());
        var service = new GistSyncService(settings, Substitute.For<ISessionRepository>(), Substitute.For<IAppDataStore>(),
            Substitute.For<IQuickCommandRepository>(), Substitute.For<ISecretProtector>());
        AppSettings remote = SettingsWithSecrets();
        remote.Proxy.Password = "rotated-on-the-other-device";
        remote.Security.WebhookUrl = "https://hooks.example.com/new";

        SyncResult result = await service.ApplyRemoteAsync(
            SettingsOnly(),
            new SyncEnvelope { Payload = new SyncPayload { DeviceName = "laptop", UpdatedAtUtc = DateTime.UtcNow, Settings = remote } },
            remoteVersion: "rev",
            CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        await settings.Received(1).SaveSettingsAsync(Arg.Is<AppSettings>(s =>
            s.Proxy.Password == "rotated-on-the-other-device" && s.Security.WebhookUrl == "https://hooks.example.com/new"));
    }

    [TestMethod]
    public async Task Pull_RejectsAPayloadNewerThanItUnderstands()
    {
        (GistSyncService service, _, _) = CreateWithCredentials();

        SyncResult result = await service.ApplyRemoteAsync(
            ProfilesOnly(),
            new SyncEnvelope { SchemaVersion = 4, Payload = new SyncPayload { SchemaVersion = 4 } },
            remoteVersion: "rev",
            CancellationToken.None);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    [TestCategory("BackgroundActivity")]
    public async Task SyncEntryPoints_ReportToTheBackgroundLedger_AndAlwaysClearIt()
    {
        // 云同步一向静默(启动拉取、保存后防抖推送,失败都不打扰用户)。接进账本之后
        // "现在有没有在同步"至少有个去处 —— 但静默的另一面是它出错也不吭声,
        // 所以**每一条出口都必须把活动收干净**,包括配置无效直接返回的那条快速失败路径。
        ISettingsService settings = Substitute.For<ISettingsService>();
        ISessionRepository sessions = Substitute.For<ISessionRepository>();
        IAppDataStore store = Substitute.For<IAppDataStore>();
        IQuickCommandRepository snippets = Substitute.For<IQuickCommandRepository>();
        ISecretProtector secrets = Substitute.For<ISecretProtector>();
        sessions.GetAllSessionsAsync().Returns([]);
        using var activity = new Core.Services.BackgroundActivityService();
        var seen = new List<string>();
        activity.Changed += () =>
        {
            lock (seen)
            {
                foreach (Core.Services.BackgroundActivitySnapshot snapshot in activity.Activities)
                {
                    if (snapshot.Detail is { } detail && !seen.Contains(detail))
                    {
                        seen.Add(detail);
                    }
                }
            }
        };
        var service = new GistSyncService(settings, sessions, store, snippets, secrets, activity);

        // 未配置 Gist:四条出口都走 Validate 的快速失败,正好用来验"开了必收"。
        Assert.IsFalse((await service.SyncNowAsync()).Success);
        Assert.IsFalse((await service.PushAsync()).Success);
        Assert.IsFalse((await service.PullAsync()).Success);
        Assert.IsFalse((await service.RestoreRevisionAsync("rev-1")).Success);

        Assert.IsEmpty(activity.Activities, "同步的每一条出口都必须把活动收干净。");
        // 副标题用设置页同名按钮的文案,四条各不相同 —— 用户能分清是在推还是在拉。
        Assert.HasCount(4, seen, $"四条出口应各自可辨:{string.Join(" / ", seen)}");
    }
}
