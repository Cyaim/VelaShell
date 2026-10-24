using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Audit;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests.Audit;

/// <summary>
/// 配置类审计:连接配置、设置、共享凭据的改动在落盘这一层记下来 —— 只记哪一块变了,不记值;
/// 没改到东西的保存(连接成功的回写、窗口尺寸、云同步拉下来一样的)不留记录。
/// </summary>
[TestClass]
[TestCategory("DataStore")]
public sealed class ConfigAuditTests : IDisposable
{
    private const string Secret = "hunter2-must-never-reach-the-audit-log";

    private readonly SonnetDbEngine _engine;
    private readonly AesSecretProtector _protector;
    private readonly string _directory;
    private readonly RecordingAuditLog _audit = new();

    public ConfigAuditTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_audit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _engine = new(Path.Combine(_directory, "sonnetdb"));
        _protector = new(Path.Combine(_directory, "secret.key"));
    }

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_directory, true);
    }

    // ———————————————————— 连接配置 ————————————————————

    [TestMethod]
    public async Task Profile_CreateUpdateDelete_EachLeaveOneRecord()
    {
        AuditedSessionRepository repo = Sessions();
        var profile = new SessionProfile { Name = "web-prod", Host = "10.0.0.1", Port = 22, Username = "root", Password = Secret };

        await repo.SaveSessionAsync(profile);
        AuditEntry created = _audit.Single();
        Assert.AreEqual(AuditCategories.Profile, created.Category);
        Assert.AreEqual(AuditActions.ProfileCreated, created.Action);
        Assert.AreEqual(profile.Id, created.ProfileId, "记录里带着配置 Id,查看器据此打开配置");
        Assert.AreEqual("web-prod · root@10.0.0.1:22", created.Detail);

        _audit.Entries.Clear();
        profile.Host = "10.0.0.2";
        await repo.SaveSessionAsync(profile);
        AuditEntry updated = _audit.Single();
        Assert.AreEqual(AuditActions.ProfileUpdated, updated.Action);
        Assert.AreEqual(
            $"web-prod · root@10.0.0.1:22 → root@10.0.0.2:22 · {Strings.Format("AuditLog_DetailChanged", Strings.Get("AuditLog_AreaAddress"))}",
            updated.Detail);

        _audit.Entries.Clear();
        await repo.DeleteSessionAsync(profile.Id);
        AuditEntry deleted = _audit.Single();
        Assert.AreEqual(AuditActions.ProfileDeleted, deleted.Action);
        Assert.AreEqual("web-prod · root@10.0.0.2:22", deleted.Detail);
    }

    [TestMethod]
    public async Task Profile_PasswordChange_IsRecordedAsCredentials_WithoutTheValue()
    {
        AuditedSessionRepository repo = Sessions();
        var profile = new SessionProfile { Name = "db", Host = "db.internal", Username = "ops", Password = "old" };
        await repo.SaveSessionAsync(profile);
        _audit.Entries.Clear();

        profile.Password = Secret;
        await repo.SaveSessionAsync(profile);

        AuditEntry entry = _audit.Single();
        StringAssert.Contains(entry.Detail, Strings.Get("AuditLog_AreaCredentials"));
        Assert.DoesNotContain(Secret, entry.Detail);
    }

    [TestMethod]
    public async Task Profile_SavesThatChangeNothingReal_LeaveNoRecord()
    {
        AuditedSessionRepository repo = Sessions();
        var profile = new SessionProfile { Name = "web", Host = "10.0.0.1", Username = "root" };
        await repo.SaveSessionAsync(profile);
        _audit.Entries.Clear();

        // 连接成功的回写、置顶、原样再存一次(云同步拉下来的一模一样)。
        profile.LastConnectedAt = DateTime.UtcNow;
        await repo.SaveSessionAsync(profile);
        profile.IsPinned = true;
        await repo.SaveSessionAsync(profile);
        await repo.ReplaceSessionAsync(profile.Clone());

        Assert.IsEmpty(_audit.Entries);
    }

    /// <summary>
    /// 引用了共享凭据的配置,仓储落盘前会清掉认证材料 —— 内存里那份手输的密码没落盘,不能记成「改了登录凭据」。
    /// </summary>
    [TestMethod]
    public async Task Profile_ComparesWhatWasPersisted_NotWhatWasPassedIn()
    {
        AuditedSessionRepository repo = Sessions();
        var profile = new SessionProfile
        {
            Name = "edge", Host = "10.0.0.9", Username = "root", CredentialSource = CredentialReference.ForShared(Guid.NewGuid())
        };
        await repo.SaveSessionAsync(profile);
        _audit.Entries.Clear();

        profile.Password = Secret; // 登录框里手输的,这次连接用
        await repo.SaveSessionAsync(profile);

        Assert.IsEmpty(_audit.Entries);
    }

    [TestMethod]
    public async Task Profile_AnAuditThatCannotBeWritten_DoesNotFailTheSave()
    {
        var repo = new AuditedSessionRepository(new SonnetDbSessionRepository(_engine, _protector), new FailingAuditLog());
        var profile = new SessionProfile { Name = "web", Host = "h" };

        await repo.SaveSessionAsync(profile);

        Assert.IsNotNull(await repo.GetSessionAsync(profile.Id));
    }

    // ———————————————————— 共享凭据 ————————————————————

    [TestMethod]
    public async Task Credential_PasswordChange_RecordsHowManyConnectionsUseIt()
    {
        AuditedSessionRepository sessions = Sessions();
        AuditedSharedCredentialRepository credentials = Credentials(sessions);
        var credential = new SharedCredential { Name = "ops-root", Username = "root", Password = "old" };
        await credentials.SaveAsync(credential);
        Assert.AreEqual(AuditActions.CredentialCreated, _audit.Single().Action);
        foreach (string host in new[] { "a", "b", "c" })
        {
            await sessions.SaveSessionAsync(new SessionProfile { Name = host, Host = host, CredentialSource = CredentialReference.ForShared(credential.Id) });
        }
        _audit.Entries.Clear();

        credential.Password = Secret;
        await credentials.SaveAsync(credential);

        AuditEntry entry = _audit.Single();
        Assert.AreEqual(AuditCategories.Credential, entry.Category);
        Assert.AreEqual(AuditActions.CredentialUpdated, entry.Action);
        Assert.AreEqual(
            $"ops-root · {Strings.Format("AuditLog_DetailChanged", Strings.Get("AuditLog_AreaCredentials"))} · {Strings.Format("AuditLog_DetailCredentialUsers", 3)}",
            entry.Detail);
        Assert.DoesNotContain(Secret, entry.Detail);
    }

    [TestMethod]
    public async Task Credential_UnchangedSave_LeavesNoRecord_AndDeleteIsRecorded()
    {
        AuditedSharedCredentialRepository credentials = Credentials(Sessions());
        var credential = new SharedCredential { Name = "ops", Username = "root", Password = "pw" };
        await credentials.SaveAsync(credential);
        _audit.Entries.Clear();

        await credentials.SaveAsync(credential.Clone());
        Assert.IsEmpty(_audit.Entries);

        await credentials.DeleteAsync(credential.Id);
        AuditEntry deleted = _audit.Single();
        Assert.AreEqual(AuditActions.CredentialDeleted, deleted.Action);
        Assert.AreEqual("ops", deleted.Detail, "没人引用时不写连接数");
    }

    [TestMethod]
    public async Task Credential_ChangedEvent_StillReachesSubscribers()
    {
        AuditedSharedCredentialRepository credentials = Credentials(Sessions());
        int raised = 0;
        credentials.Changed += (_, _) => raised++;

        await credentials.SaveAsync(new SharedCredential { Name = "x" });

        Assert.AreEqual(1, raised);
    }

    // ———————————————————— 设置 ————————————————————

    [TestMethod]
    public async Task Settings_RecordWhichCategoriesChanged_NotTheValues()
    {
        var service = new AuditedSettingsService(new MemorySettingsService(), _audit);
        AppSettings settings = await service.GetSettingsAsync();

        settings.Theme = "light";
        settings.Proxy.Password = Secret;
        await service.SaveSettingsAsync(settings);

        AuditEntry entry = _audit.Single();
        Assert.AreEqual(AuditCategories.Settings, entry.Category);
        Assert.AreEqual(AuditActions.SettingsChanged, entry.Action);
        Assert.AreEqual(
            SettingsCategories.JoinLabels([SettingsCategories.Find("appearance")!, SettingsCategories.Find("proxy")!]),
            entry.Detail);
        Assert.DoesNotContain(Secret, entry.Detail);
    }

    [TestMethod]
    public async Task Settings_RuntimeStateOnly_LeavesNoRecord()
    {
        var service = new AuditedSettingsService(new MemorySettingsService(), _audit);
        AppSettings settings = await service.GetSettingsAsync();

        // 每次退出都会写的窗口尺寸与上次打开的会话。
        settings.Appearance.LastWindowWidth = 1600;
        settings.General.LastOpenProfileIds = [Guid.NewGuid()];
        await service.SaveSettingsAsync(settings);
        await service.SaveSettingsAsync(settings);

        Assert.IsEmpty(_audit.Entries);
    }

    [TestMethod]
    public async Task Settings_SavedEvent_AndSnapshot_StillComeFromTheInnerService()
    {
        var service = new AuditedSettingsService(new MemorySettingsService(), _audit);
        AppSettings? saved = null;
        service.SettingsSaved += s => saved = s;
        AppSettings settings = await service.GetSettingsAsync();
        settings.TerminalFontSize = 19;

        await service.SaveSettingsAsync(settings);

        Assert.AreSame(settings, saved);
        Assert.AreEqual(19, service.CurrentSnapshot?.TerminalFontSize);
    }

    private AuditedSessionRepository Sessions() => new(new SonnetDbSessionRepository(_engine, _protector), _audit);

    private AuditedSharedCredentialRepository Credentials(ISessionRepository sessions) =>
        new(new SonnetDbSharedCredentialRepository(_engine, _protector), sessions, _audit);

    private sealed class RecordingAuditLog : IAuditLogService
    {
        public List<AuditEntry> Entries { get; } = [];

        public AuditEntry Single()
        {
            Assert.HasCount(1, Entries, string.Join(" | ", Entries.Select(e => $"{e.Action}: {e.Detail}")));
            return Entries[0];
        }

        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<List<AuditEntry>> QueryAsync(int limit, string? category = null, DateTimeOffset? from = null,
            DateTimeOffset? to = null, CancellationToken cancellationToken = default) => Task.FromResult(Entries.ToList());

        public Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// 内存里的设置服务:真的 <c>SonnetDbSettingsService</c> 保存时会把外观镜像进本机 <c>~/.velashell</c>
    /// 的启动外观文件,拿它测「主题改成亮色」会改掉跑测试这台机器上的真设置。快照与独立实例的口径照搬它。
    /// </summary>
    private sealed class MemorySettingsService : ISettingsService
    {
        private string _json = System.Text.Json.JsonSerializer.Serialize(new AppSettings());

        public event Action<AppSettings>? SettingsSaved;

        public AppSettings? CurrentSnapshot { get; private set; }

        public Task<AppSettings> GetSettingsAsync()
        {
            AppSettings settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(_json)!;
            settings.Normalize();
            CurrentSnapshot ??= System.Text.Json.JsonSerializer.Deserialize<AppSettings>(_json);
            return Task.FromResult(settings);
        }

        public Task SaveSettingsAsync(AppSettings settings)
        {
            _json = System.Text.Json.JsonSerializer.Serialize(settings);
            CurrentSnapshot = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(_json);
            SettingsSaved?.Invoke(settings);
            return Task.CompletedTask;
        }

        public Task<AppState> GetStateAsync() => Task.FromResult(new AppState());

        public Task SaveStateAsync(AppState state) => Task.CompletedTask;
    }

    private sealed class FailingAuditLog : IAuditLogService
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            throw new IOException("disk full");

        public Task<List<AuditEntry>> QueryAsync(int limit, string? category = null, DateTimeOffset? from = null,
            DateTimeOffset? to = null, CancellationToken cancellationToken = default) => Task.FromResult(new List<AuditEntry>());

        public Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
