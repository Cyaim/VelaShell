using NSubstitute;
using ReactiveUI.Builder;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 审计日志窗口:按时间倒序载入、对上会话名、按类别 / 只看异常 / 关键字筛、摘要说清楚筛出了几条。
/// </summary>
[TestClass]
[TestCategory("Security")]
public sealed class AuditLogViewModelTests
{
    private static readonly Guid WebId = Guid.NewGuid();

    static AuditLogViewModelTests()
    {
        try
        {
            RxAppBuilder
                .CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadSequencer.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // Already initialized
        }
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Load_TranslatesEntries_AndResolvesSessionNames()
    {
        AuditLogViewModel vm = Create(out _);

        await vm.LoadAsync(TestContext.CancellationToken);

        Assert.HasCount(4, vm.Rows);
        AuditLogRow failed = vm.Rows[0];
        Assert.AreEqual(Strings.Get("AuditLog_ActionConnectFailed"), failed.ActionLabel);
        Assert.AreEqual(Strings.Get("AuditLog_CategoryConnection"), failed.CategoryLabel);
        Assert.AreEqual("web-prod", failed.SessionName);
        Assert.IsTrue(failed.IsProblem);
        Assert.AreEqual("—", vm.Rows[2].SessionName, "没有配置 Id 的记录(指纹裁决)会话一栏显示 —");
        Assert.AreEqual("—", vm.Rows[3].SessionName, "配置已经删掉的记录同样显示 —,详情里仍看得出是哪台");
        Assert.AreEqual("custom-action", vm.Rows[3].ActionLabel, "认不出的动作原样显示");
        Assert.AreEqual(Strings.Format("AuditLog_Count", 4), vm.Status);
        Assert.IsFalse(vm.IsEmpty);
    }

    [TestMethod]
    public async Task Filters_Combine_AndTheStatusSaysHowManyMatched()
    {
        AuditLogViewModel vm = Create(out _);
        await vm.LoadAsync(TestContext.CancellationToken);

        vm.CategoryIndex = 1;
        Assert.HasCount(3, vm.Rows, "连接类");

        vm.OnlyProblems = true;
        Assert.AreEqual("root@10.0.0.1:22", vm.Rows.Single().Detail);
        Assert.AreEqual(Strings.Format("AuditLog_CountFiltered", 1, 4), vm.Status);

        vm.OnlyProblems = false;
        vm.CategoryIndex = 0;
        vm.SearchText = "WEB-PROD";
        Assert.HasCount(2, vm.Rows, "关键字不区分大小写,也在会话名里找");

        vm.SearchText = "no-such-host";
        Assert.IsTrue(vm.IsEmpty);
    }

    [TestMethod]
    public async Task AFullPage_SaysOnlyTheLatestWereLoaded()
    {
        IAuditLogService audit = Substitute.For<IAuditLogService>();
        audit.QueryAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
             .Returns(_ => [.. Enumerable.Range(0, AuditLogViewModel.MaxRows).Select(i => new AuditEntry { Category = "connection", Action = "connect", Detail = $"h{i}" })]);
        var vm = new AuditLogViewModel(audit);

        await vm.LoadAsync(TestContext.CancellationToken);

        Assert.EndsWith(Strings.Format("AuditLog_Truncated", AuditLogViewModel.MaxRows), vm.Status);
    }

    [TestMethod]
    public async Task AReadFailure_IsShown_NotThrown()
    {
        IAuditLogService audit = Substitute.For<IAuditLogService>();
        audit.QueryAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
             .Returns<Task<List<AuditEntry>>>(_ => throw new IOException("disk gone"));
        var vm = new AuditLogViewModel(audit);

        await vm.LoadAsync(TestContext.CancellationToken);

        Assert.AreEqual(Strings.Format("AuditLog_LoadFailed", "disk gone"), vm.Status);
        Assert.IsTrue(vm.IsEmpty);
    }

    [TestMethod]
    public async Task Refresh_ReadsAgain()
    {
        AuditLogViewModel vm = Create(out IAuditLogService audit);

        await vm.RefreshCommand.Execute().FirstAsync();

        await audit.Received(1).QueryAsync(AuditLogViewModel.MaxRows, null, null, null, Arg.Any<CancellationToken>());
        Assert.HasCount(4, vm.Rows);
    }

    [TestMethod]
    public async Task ConfigCategories_HaveTheirOwnFilterAndLabels()
    {
        IAuditLogService audit = Substitute.For<IAuditLogService>();
        audit.QueryAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
             .Returns(_ =>
             [
                 new AuditEntry { Category = AuditCategories.Profile, Action = AuditActions.ProfileUpdated, ProfileId = WebId, Detail = "web-prod · root@h:22" },
                 new AuditEntry { Category = AuditCategories.Settings, Action = AuditActions.SettingsChanged, Detail = "外观" },
                 new AuditEntry { Category = AuditCategories.Credential, Action = AuditActions.CredentialUpdated, Detail = "ops" },
                 new AuditEntry { Category = AuditCategories.Connection, Action = "connect", Detail = "root@h:22" },
             ]);
        var vm = new AuditLogViewModel(audit);
        await vm.LoadAsync(TestContext.CancellationToken);

        Assert.AreEqual(Strings.Get("AuditLog_ActionProfileUpdated"), vm.Rows[0].ActionLabel);
        Assert.AreEqual(Strings.Get("AuditLog_CategorySettings"), vm.Rows[1].CategoryLabel);
        Assert.AreEqual(Strings.Get("AuditLog_ActionCredentialUpdated"), vm.Rows[2].ActionLabel);
        Assert.HasCount(6, vm.CategoryOptions, "全部 / 连接 / 安全 / 连接配置 / 设置 / 共享凭据");

        vm.CategoryIndex = 3;
        Assert.AreEqual(AuditActions.ProfileUpdated, vm.Rows.Single().Action);
        vm.CategoryIndex = 5;
        Assert.AreEqual(AuditActions.CredentialUpdated, vm.Rows.Single().Action);
    }

    [TestMethod]
    public async Task TimeRange_IsPushedDownToTheQuery()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        IAuditLogService audit = Substitute.For<IAuditLogService>();
        audit.QueryAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
             .Returns(_ => new List<AuditEntry>());
        var vm = new AuditLogViewModel(audit, timeProvider: new FixedTime(now, TimeZoneInfo.Utc));
        await vm.LoadAsync(TestContext.CancellationToken);

        vm.RangeIndex = 2; // 最近 7 天:换了就重读
        await audit.Received(1).QueryAsync(AuditLogViewModel.MaxRows, null, now.AddDays(-7), null, Arg.Any<CancellationToken>());

        vm.RangeIndex = vm.RangeOptions.Count - 1; // 自定义
        Assert.IsTrue(vm.IsCustomRange);
        vm.CustomFrom = new DateTime(2026, 7, 10);
        vm.CustomTo = new DateTime(2026, 7, 1); // 起止填反了也认
        Assert.AreEqual(
            (new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), (DateTimeOffset?)new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero)),
            vm.SelectedRange(), "按本地日期,结束日当天整天都算");
        await audit.Received(1).QueryAsync(AuditLogViewModel.MaxRows, null,
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Csv_ExportsTheFilteredRows_AndDefusesFormulas()
    {
        IAuditLogService audit = Substitute.For<IAuditLogService>();
        var at = new DateTimeOffset(2026, 10, 24, 1, 2, 3, TimeSpan.Zero);
        audit.QueryAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
             .Returns(_ =>
             [
                 new AuditEntry { Timestamp = at, Category = "security", Action = "hostkey-rejected", Detail = "=HYPERLINK(\"http://evil\")" },
                 new AuditEntry { Timestamp = at, Category = "connection", Action = "connect", ProfileId = WebId, Detail = "root@10.0.0.1:22" },
             ]);
        ISessionRepository sessions = Substitute.For<ISessionRepository>();
        sessions.GetAllSessionsAsync().Returns([new SessionProfile { Id = WebId, Name = "@web, prod", Host = "10.0.0.1" }]);
        var vm = new AuditLogViewModel(audit, sessions);
        await vm.LoadAsync(TestContext.CancellationToken);

        string[] lines = vm.BuildCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(3, lines, "表头 + 两行");
        Assert.StartsWith(Strings.Get("AuditLog_ColTime") + ",", lines[0]);
        Assert.EndsWith("\"'=HYPERLINK(\"\"http://evil\"\")\"", lines[1], "以 = 开头的详情补了单引号");
        Assert.Contains($",connect,,\"'@web, prod\",{WebId:D},", lines[2], "会话名以 @ 开头同样补单引号,带逗号的加引号;连接记录没有来源,那一栏留空");
        Assert.StartsWith(at.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture), lines[2]);

        vm.CategoryIndex = 1;
        Assert.HasCount(2, vm.BuildCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries), "导出的是筛出来的那些");
    }

    /// <summary>配置类记录带来源:界面一栏显示来源文字、CSV 带一列、关键字搜得到;没有来源的(连接类、旧记录)显示 —、CSV 留空。</summary>
    [TestMethod]
    public async Task Source_IsShown_Exported_AndSearchable()
    {
        IAuditLogService audit = Substitute.For<IAuditLogService>();
        audit.QueryAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
             .Returns(_ =>
             [
                 new AuditEntry { Category = "profile", Action = "profile-updated", Detail = "a", Source = AuditSources.CloudSync },
                 new AuditEntry { Category = "credential", Action = "credential-updated", Detail = "b", Source = AuditSources.LoginPrompt },
                 new AuditEntry { Category = "profile", Action = "profiles-ungrouped", Detail = "c", Source = AuditSources.Local },
                 new AuditEntry { Category = "profile", Action = "profile-created", Detail = "d", Source = AuditSources.Import },
                 new AuditEntry { Category = "connection", Action = "connect", Detail = "e" },
             ]);
        var vm = new AuditLogViewModel(audit);
        await vm.LoadAsync(TestContext.CancellationToken);

        Assert.AreSequenceEqual(
        [
            Strings.Get("AuditLog_SourceCloudSync"), Strings.Get("AuditLog_SourceLoginPrompt"), Strings.Get("AuditLog_SourceLocal"),
            Strings.Get("AuditLog_SourceImport"), "—"
        ], [.. vm.Rows.Select(r => r.SourceLabel)]);
        Assert.AreEqual(Strings.Get("AuditLog_ActionProfilesUngrouped"), vm.Rows[2].ActionLabel);

        string[] lines = vm.BuildCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains($",{Strings.Get("AuditLog_ColActionCode")},{Strings.Get("AuditLog_ColSource")},{Strings.Get("AuditLog_ColSession")},", lines[0]);
        Assert.Contains($",profile-updated,{Strings.Get("AuditLog_SourceCloudSync")},,", lines[1]);
        Assert.Contains(",connect,,,", lines[5], "没有来源的记录那一栏留空,不写 —");

        vm.SearchText = Strings.Get("AuditLog_SourceLoginPrompt");
        Assert.AreEqual("b", vm.Rows.Single().Detail);
    }

    [TestMethod]
    public async Task OpenProfile_OpensTheStoredProfile_OrSaysItIsGone()
    {
        AuditLogViewModel vm = Create(out _, out ISessionRepository sessions);
        await vm.LoadAsync(TestContext.CancellationToken);
        Assert.IsTrue(vm.Rows[0].ProfileExists);
        Assert.IsTrue(vm.Rows[3].HasProfile);
        Assert.IsFalse(vm.Rows[3].ProfileExists, "配置已删:打开配置灰着");
        Assert.IsFalse(vm.Rows[2].HasProfile, "没有配置 Id 的记录没有这个按钮");

        SessionProfile? opened = null;
        vm.OpenProfile = profile =>
        {
            opened = profile;
            return Task.CompletedTask;
        };
        var stored = new SessionProfile { Id = WebId, Name = "web-prod", Host = "10.0.0.1" };
        sessions.GetSessionAsync(WebId).Returns(stored);
        await vm.OpenProfileAsync(vm.Rows[0]);
        Assert.AreSame(stored, opened, "打开的是库里最新的那份");

        sessions.GetSessionAsync(WebId).Returns((SessionProfile?)null);
        opened = null;
        await vm.OpenProfileAsync(vm.Rows[0]);
        Assert.IsNull(opened);
        Assert.AreEqual(Strings.Get("AuditLog_ProfileGone"), vm.Status, "窗口开着时被删了:说一声");
    }

    private sealed class FixedTime(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private static AuditLogViewModel Create(out IAuditLogService audit) => Create(out audit, out _);

    private static AuditLogViewModel Create(out IAuditLogService audit, out ISessionRepository sessions)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        audit = Substitute.For<IAuditLogService>();
        audit.QueryAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
             .Returns(_ =>
             [
                 new AuditEntry { Timestamp = now, Category = "connection", Action = "connect-failed", ProfileId = WebId, Detail = "root@10.0.0.1:22" },
                 new AuditEntry { Timestamp = now.AddMinutes(-1), Category = "connection", Action = "connect", ProfileId = WebId, Detail = "root@10.0.0.1:22" },
                 new AuditEntry { Timestamp = now.AddMinutes(-2), Category = "security", Action = "hostkey-trusted-once", Detail = "10.0.0.2:22 SHA256:abc" },
                 new AuditEntry { Timestamp = now.AddMinutes(-3), Category = "connection", Action = "custom-action", ProfileId = Guid.NewGuid(), Detail = "ops@db:22" },
             ]);
        sessions = Substitute.For<ISessionRepository>();
        sessions.GetAllSessionsAsync().Returns([new SessionProfile { Id = WebId, Name = "web-prod", Host = "10.0.0.1" }]);
        return new AuditLogViewModel(audit, sessions);
    }
}
