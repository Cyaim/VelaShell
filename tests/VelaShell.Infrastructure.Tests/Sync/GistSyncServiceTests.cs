using NSubstitute;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sync;
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
