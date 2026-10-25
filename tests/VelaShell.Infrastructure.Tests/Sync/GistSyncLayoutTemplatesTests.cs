using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSubstitute;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Layouts;
using VelaShell.Core.Sync;
using VelaShell.Infrastructure.Sync;

namespace VelaShell.Infrastructure.Tests.Sync;

/// <summary>
/// 布局模板的云同步:Gist 里单独一个文件、按模板合并。核心是旧客户端那条路 ——
/// 它只认识 <c>velashell-sync.json</c>,拉下来、再推上去,也碰不到旁边的模板文件。
/// </summary>
/// <remarks>
/// Gist 用一个「文件名 → 内容」的字典模拟,推送按 GitHub「更新 Gist」的约定只改请求里列出的文件。
/// 旧客户端 = 不带模板仓储的 <see cref="GistSyncService" />:它的 <see cref="GistSyncService.BuildGistContentAsync" />
/// 与旧版本是同一段代码,推送时也只写主载荷那一个文件(旧版本的 PushCoreAsync 就是这样)。
/// </remarks>
[TestClass]
public sealed class GistSyncLayoutTemplatesTests
{
    private const string SyncFile = "velashell-sync.json";
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class MemoryStore : IAppDataStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly ConcurrentDictionary<(string Collection, string Id), string> _rows = new();

        public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_rows.TryGetValue((collection, id), out string? json) ? JsonSerializer.Deserialize<T>(json, Json) : null);

        public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_rows.Where(row => row.Key.Collection == collection)
                                 .Select(row => JsonSerializer.Deserialize<T>(row.Value, Json)!)
                                 .ToList());

        public Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
        {
            _rows[(collection, id)] = JsonSerializer.Serialize(value, Json);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            _rows.TryRemove((collection, id), out _);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>一台设备:自己的库、自己的同步服务;构造时 supportsTemplates 为假即没有这个功能的旧客户端。</summary>
    private sealed class Device
    {
        public Device(string name, bool supportsTemplates = true, string? passphrase = null, FixedTime? time = null)
        {
            Store = new MemoryStore();
            Templates = new LayoutTemplateStore(Store, time ?? new FixedTime(Now));
            ISecretProtector protector = Substitute.For<ISecretProtector>();
            protector.Unprotect("protected-passphrase").Returns(passphrase);
            Sync = new GistSyncService(Substitute.For<ISettingsService>(), Substitute.For<ISessionRepository>(), Store,
                Substitute.For<IQuickCommandRepository>(), protector, layoutTemplates: supportsTemplates ? Templates : null);
            Config = new SyncSettings
            {
                DeviceName = name,
                SyncAppSettings = false,
                SyncProfiles = false,
                SyncSnippets = false,
                ProtectedPassphrase = passphrase is null ? null : "protected-passphrase",
            };
        }

        public MemoryStore Store { get; }

        public LayoutTemplateStore Templates { get; }

        public GistSyncService Sync { get; }

        public SyncSettings Config { get; }

        /// <summary>推送:主载荷 + (合并过云端那份的)模板文件,与 PushCoreAsync 写的是同一组文件。</summary>
        public async Task PushAsync(Dictionary<string, string> gist)
        {
            gist[SyncFile] = await Sync.BuildGistContentAsync(Config, CancellationToken.None);
            if (await Sync.BuildLayoutTemplatesUploadAsync(Config, gist.GetValueOrDefault(GistSyncService.LayoutTemplatesFileName), CancellationToken.None)
                is { } layouts)
            {
                gist[GistSyncService.LayoutTemplatesFileName] = layouts;
            }
        }

        /// <summary>拉取:应用主载荷,再把模板文件合并进来(ReconcileLayoutTemplatesAsync 推送之前的那一步)。</summary>
        public async Task<string?> PullAsync(Dictionary<string, string> gist)
        {
            SyncResult applied = await Sync.ApplyRemoteAsync(Config, GistSyncService.ParseEnvelope(gist[SyncFile]), "rev", CancellationToken.None);
            Assert.IsTrue(applied.Success, applied.Message);
            if (!Config.SyncLayoutTemplates)
            {
                return null;
            }
            (_, string? note) = await Sync.MergeRemoteLayoutTemplatesAsync(
                Config, gist.GetValueOrDefault(GistSyncService.LayoutTemplatesFileName), CancellationToken.None);
            return note;
        }

        public async Task<string[]> NamesAsync() => [.. (await Templates.LoadAllAsync()).Select(t => t.Name)];
    }

    private static LayoutTemplate Template(string name, DateTimeOffset? updatedAt = null) => new()
    {
        Name = name,
        CreatedAt = updatedAt ?? Now.AddHours(-1),
        UpdatedAt = updatedAt ?? Now.AddHours(-1),
        Root = new() { Documents = [new() { Kind = LayoutDocumentKind.Terminal, ProfileId = Guid.NewGuid(), Title = "root@" + name }] },
    };

    [TestMethod]
    public async Task NewPushes_OldPullsAndPushes_NewPulls_TheTemplatesAreStillThere()
    {
        var gist = new Dictionary<string, string>();
        var desktop = new Device("desktop");
        await desktop.Templates.SaveAsync(Template("机房 A"));
        await desktop.Templates.SaveAsync(Template("机房 B"));
        await desktop.PushAsync(gist);
        string layoutsAfterNewPush = gist[GistSyncService.LayoutTemplatesFileName];

        var old = new Device("old-laptop", supportsTemplates: false);
        await old.PullAsync(gist);
        gist[SyncFile] = await old.Sync.BuildGistContentAsync(old.Config, CancellationToken.None); // 旧版本只写这一个文件

        Assert.AreEqual(layoutsAfterNewPush, gist[GistSyncService.LayoutTemplatesFileName], "旧客户端碰不到旁边的模板文件");
        var fresh = new Device("new-laptop");
        Assert.IsNull(await fresh.PullAsync(gist));
        Assert.AreSequenceEqual(new[] { "机房 A", "机房 B" }, await fresh.NamesAsync());
    }

    [TestMethod]
    public async Task TheMainPayload_DoesNotCarryTheTemplates()
    {
        var gist = new Dictionary<string, string>();
        var desktop = new Device("desktop");
        await desktop.Templates.SaveAsync(Template("rack-A"));

        await desktop.PushAsync(gist);

        Assert.DoesNotContain("rack-A", gist[SyncFile], "在主载荷里就会被旧客户端整份推回时抹掉(名字特意用 ASCII:同步的 JSON 会把中文转义掉)");
        StringAssert.Contains(gist[GistSyncService.LayoutTemplatesFileName], "rack-A");
    }

    [TestMethod]
    public async Task ADeletion_TravelsThroughAnOldClient_ToTheOtherDevice()
    {
        var gist = new Dictionary<string, string>();
        var time = new FixedTime(Now);
        var desktop = new Device("desktop", time: time);
        LayoutTemplate doomed = Template("doomed");
        await desktop.Templates.SaveAsync(doomed);
        await desktop.Templates.SaveAsync(Template("keep"));
        await desktop.PushAsync(gist);
        var laptop = new Device("laptop");
        await laptop.PullAsync(gist);
        Assert.HasCount(2, await laptop.NamesAsync());

        await desktop.Templates.DeleteAsync(doomed.Id);
        await desktop.PushAsync(gist);
        var old = new Device("old", supportsTemplates: false);
        await old.PullAsync(gist);
        gist[SyncFile] = await old.Sync.BuildGistContentAsync(old.Config, CancellationToken.None);
        await laptop.PullAsync(gist);

        Assert.AreSequenceEqual(new[] { "keep" }, await laptop.NamesAsync(), "墓碑在旧客户端不碰的那个文件里,删除照样传到");
        await laptop.PushAsync(gist);
        await desktop.PullAsync(gist);
        Assert.AreSequenceEqual(new[] { "keep" }, await desktop.NamesAsync(), "笔记本再推一次也不会把删掉的那个加回来");
    }

    [TestMethod]
    public async Task TwoDevicesSavingDifferentTemplates_BothSurvive()
    {
        var gist = new Dictionary<string, string>();
        var desktop = new Device("desktop");
        var laptop = new Device("laptop");
        await desktop.Templates.SaveAsync(Template("desk"));
        await laptop.Templates.SaveAsync(Template("lap"));

        await desktop.PushAsync(gist);
        await laptop.PushAsync(gist); // 后推的这台先把云端那份合进来再写

        await desktop.PullAsync(gist);
        Assert.AreSequenceEqual(new[] { "desk", "lap" }, await desktop.NamesAsync());
        Assert.AreSequenceEqual(new[] { "desk", "lap" }, await laptop.NamesAsync());
    }

    [TestMethod]
    public async Task WhenBothSidesAgree_NothingIsUploaded()
    {
        var gist = new Dictionary<string, string>();
        var desktop = new Device("desktop");
        await desktop.Templates.SaveAsync(Template("desk"));
        await desktop.PushAsync(gist);

        string? again = await desktop.Sync.BuildLayoutTemplatesUploadAsync(
            desktop.Config, gist[GistSyncService.LayoutTemplatesFileName], CancellationToken.None);

        Assert.IsNull(again, "没变就不写:两次推送之间模板文件不动");
        var blank = new Device("blank");
        Assert.IsNull(await blank.Sync.BuildLayoutTemplatesUploadAsync(blank.Config, null, CancellationToken.None),
            "没用过模板的人,Gist 里不多出一个空文件");
    }

    [TestMethod]
    public async Task WithAPassphrase_TheFileIsCiphertext_AndTheOtherDeviceReadsItBack()
    {
        var gist = new Dictionary<string, string>();
        var desktop = new Device("desktop", passphrase: "correct horse");
        await desktop.Templates.SaveAsync(Template("rack-A"));
        await desktop.PushAsync(gist);

        string file = gist[GistSyncService.LayoutTemplatesFileName];
        Assert.DoesNotContain("rack-A", file);
        Assert.DoesNotContain("root@", file, "标签标题(常常就是用户@主机)也在密文里");
        var laptop = new Device("laptop", passphrase: "correct horse");
        Assert.IsNull(await laptop.PullAsync(gist));
        Assert.AreSequenceEqual(new[] { "rack-A" }, await laptop.NamesAsync());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("a different passphrase")]
    public async Task AnEncryptedFile_ThisDeviceCannotOpen_IsLeftAlone(string? passphrase)
    {
        var desktop = new Device("desktop", passphrase: "correct horse");
        await desktop.Templates.SaveAsync(Template("机房 A"));
        string file = (await desktop.Sync.BuildLayoutTemplatesUploadAsync(desktop.Config, null, CancellationToken.None))!;
        var other = new Device("other", passphrase: passphrase);
        await other.Templates.SaveAsync(Template("mine"));

        (LayoutTemplateMergeResult? merge, string? note) =
            await other.Sync.MergeRemoteLayoutTemplatesAsync(other.Config, file, CancellationToken.None);

        Assert.IsNull(merge);
        Assert.AreEqual(VelaShell.Core.Resources.Strings.Get("SyncSvc_LayoutsLocked"), note);
        Assert.IsNull(await other.Sync.BuildLayoutTemplatesUploadAsync(other.Config, file, CancellationToken.None),
            "读不了就不写:不能拿本机的明文盖掉别人加了密的那份");
        Assert.AreSequenceEqual(new[] { "mine" }, await other.NamesAsync());
    }

    [TestMethod]
    public async Task AFileFromANewerVersion_IsNeitherReadNorWritten()
    {
        var desktop = new Device("desktop");
        await desktop.Templates.SaveAsync(Template("mine"));
        string newer = """
            { "format": "velashell-layout-templates-sync", "schemaVersion": 2, "encrypted": false,
              "data": { "templates": [], "tombstones": [] }, "somethingNew": {} }
            """;

        (LayoutTemplateMergeResult? merge, string? note) =
            await desktop.Sync.MergeRemoteLayoutTemplatesAsync(desktop.Config, newer, CancellationToken.None);

        Assert.IsNull(merge);
        Assert.AreEqual(VelaShell.Core.Resources.Strings.Get("SyncSvc_LayoutsNewer"), note);
        Assert.IsNull(await desktop.Sync.BuildLayoutTemplatesUploadAsync(desktop.Config, newer, CancellationToken.None),
            "写回去就是拿旧形状盖掉新版本的数据");
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("""{"format":"something-else","data":{"templates":[]}}""")]
    [DataRow("""{"format":"velashell-layout-templates-sync","schemaVersion":1,"encrypted":true,"cipherText":"%%%"}""")]
    public async Task ADamagedFile_IsTreatedAsMissing_AndRewritten(string damaged)
    {
        var desktop = new Device("desktop", passphrase: damaged.Contains("cipherText", StringComparison.Ordinal) ? "pw" : null);
        await desktop.Templates.SaveAsync(Template("mine"));

        string? rewritten = await desktop.Sync.BuildLayoutTemplatesUploadAsync(desktop.Config, damaged, CancellationToken.None);

        Assert.IsNotNull(rewritten, "坏文件没法合并,用本机的重写;旧内容还在 Gist 的历史版本里");
        Assert.AreEqual("mine", desktop.Sync.ReadLayoutTemplatesFile(desktop.Config, rewritten).State!.Templates.Single().Name);
    }

    [TestMethod]
    public async Task WithTemplateSyncTurnedOff_TheFileIsNotTouched()
    {
        var gist = new Dictionary<string, string>();
        var desktop = new Device("desktop");
        await desktop.Templates.SaveAsync(Template("desk"));
        await desktop.PushAsync(gist);
        var laptop = new Device("laptop");
        laptop.Config.SyncLayoutTemplates = false;
        await laptop.Templates.SaveAsync(Template("lap"));

        string before = gist[GistSyncService.LayoutTemplatesFileName];
        await laptop.PushAsync(gist);
        await laptop.PullAsync(gist);

        Assert.AreEqual(before, gist[GistSyncService.LayoutTemplatesFileName]);
        Assert.AreSequenceEqual(new[] { "lap" }, await laptop.NamesAsync());
    }

    [TestMethod]
    public async Task RestoringARevision_BringsItsTemplatesBack_EvenIfDeletedSince()
    {
        var time = new FixedTime(Now);
        var desktop = new Device("desktop", time: time);
        LayoutTemplate template = Template("prod");
        await desktop.Templates.SaveAsync(template);
        string revision = (await desktop.Sync.BuildLayoutTemplatesUploadAsync(desktop.Config, null, CancellationToken.None))!;
        await desktop.Templates.DeleteAsync(template.Id);
        string current = (await desktop.Sync.BuildLayoutTemplatesUploadAsync(desktop.Config, revision, CancellationToken.None))!;
        time.Now = Now.AddHours(1);

        Assert.IsNull(await desktop.Sync.RestoreLayoutTemplatesAsync(desktop.Config, revision));
        string? pushed = await desktop.Sync.BuildLayoutTemplatesUploadAsync(desktop.Config, current, CancellationToken.None);

        Assert.AreSequenceEqual(new[] { "prod" }, await desktop.NamesAsync());
        Assert.AreEqual("prod", desktop.Sync.ReadLayoutTemplatesFile(desktop.Config, pushed).State!.Templates.Single().Name,
            "恢复之后的推送胜过云端那块墓碑");
    }

    [TestMethod]
    public void ExistingSyncConfigurations_SyncTemplatesByDefault()
    {
        SyncSettings stored = JsonSerializer.Deserialize<SyncSettings>(
            """{"enabled":true,"syncAppSettings":true,"syncProfiles":true,"syncSnippets":false}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        Assert.IsTrue(stored.SyncLayoutTemplates, "与其余同步范围一样默认开;旧配置里没有这个字段");
    }
}
