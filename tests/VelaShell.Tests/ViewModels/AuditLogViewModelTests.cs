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
        audit.QueryAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
             .Returns(_ => [.. Enumerable.Range(0, AuditLogViewModel.MaxRows).Select(i => new AuditEntry { Category = "connection", Action = "connect", Detail = $"h{i}" })]);
        var vm = new AuditLogViewModel(audit);

        await vm.LoadAsync(TestContext.CancellationToken);

        Assert.EndsWith(Strings.Format("AuditLog_Truncated", AuditLogViewModel.MaxRows), vm.Status);
    }

    [TestMethod]
    public async Task AReadFailure_IsShown_NotThrown()
    {
        IAuditLogService audit = Substitute.For<IAuditLogService>();
        audit.QueryAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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

        await audit.Received(1).QueryAsync(AuditLogViewModel.MaxRows, null, Arg.Any<CancellationToken>());
        Assert.HasCount(4, vm.Rows);
    }

    private static AuditLogViewModel Create(out IAuditLogService audit)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        audit = Substitute.For<IAuditLogService>();
        audit.QueryAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
             .Returns(_ =>
             [
                 new AuditEntry { Timestamp = now, Category = "connection", Action = "connect-failed", ProfileId = WebId, Detail = "root@10.0.0.1:22" },
                 new AuditEntry { Timestamp = now.AddMinutes(-1), Category = "connection", Action = "connect", ProfileId = WebId, Detail = "root@10.0.0.1:22" },
                 new AuditEntry { Timestamp = now.AddMinutes(-2), Category = "security", Action = "hostkey-trusted-once", Detail = "10.0.0.2:22 SHA256:abc" },
                 new AuditEntry { Timestamp = now.AddMinutes(-3), Category = "connection", Action = "custom-action", ProfileId = Guid.NewGuid(), Detail = "ops@db:22" },
             ]);
        ISessionRepository sessions = Substitute.For<ISessionRepository>();
        sessions.GetAllSessionsAsync().Returns([new SessionProfile { Id = WebId, Name = "web-prod", Host = "10.0.0.1" }]);
        return new AuditLogViewModel(audit, sessions);
    }
}
