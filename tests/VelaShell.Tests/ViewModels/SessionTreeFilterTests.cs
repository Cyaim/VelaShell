using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 资源管理器的过滤框:按名称 / 主机 / 标签 / 备注(及分组名)过滤;分组里有命中的就留下并展开,
/// 展开是临时的,清掉过滤回到原样。
/// </summary>
/// <remarks>
/// <c>SessionProfile.Tags</c> 原先只有编辑框与导入导出在读(§180),存了等于没存;这组用例守的是它终于有了用处。
/// </remarks>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeFilterTests
{
    private readonly ISessionRepository _repository = Substitute.For<ISessionRepository>();
    private ServerGroup _prod = null!;
    private ServerGroup _test = null!;

    /// <summary>
    /// 「生产 {web-1[prod,nginx], db-1[prod] 备注:主库}」「测试 {web-t[staging]}」+ 根级 laptop(10.9.9.9)。
    /// </summary>
    private void StubTree(params SessionProfile[] extra)
    {
        _prod = new() { Id = Guid.NewGuid(), Name = "生产", SortOrder = 0 };
        _test = new() { Id = Guid.NewGuid(), Name = "测试", SortOrder = 1 };
        _repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(new List<ServerGroup> { _prod, _test }));
        List<SessionProfile> sessions =
        [
            Session("web-1", "10.0.0.1", _prod.Id, tags: ["prod", "nginx"]),
            Session("db-1", "10.0.0.2", _prod.Id, tags: ["prod"], notes: "主库\n值班:运维二组"),
            Session("web-t", "10.1.0.1", _test.Id, tags: ["staging"]),
            Session("laptop", "10.9.9.9"),
            .. extra
        ];
        _repository.GetAllSessionsAsync().Returns(_ => Task.FromResult(sessions.ToList()));
    }

    private static SessionProfile Session(
        string name,
        string host,
        Guid? groupId = null,
        List<string>? tags = null,
        string? notes = null,
        bool pinned = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Host = host,
            Username = "root",
            GroupId = groupId,
            Tags = tags ?? [],
            Notes = notes,
            IsPinned = pinned
        };

    private static ISettingsService Settings(bool collapse)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync()
                .Returns(_ => new AppSettings { General = { CollapseGroupsByDefault = collapse } });
        return settings;
    }

    private async Task<SessionTreeViewModel> LoadAsync(bool collapse = false)
    {
        SessionTreeViewModel vm = new(_repository, Settings(collapse));
        await vm.LoadCommand.Execute().FirstAsync();
        return vm;
    }

    private static List<string> RowNames(SessionTreeViewModel vm) => [.. vm.Rows.Select(static row => row.Name)];

    private static SessionTreeNodeViewModel Group(SessionTreeViewModel vm, ServerGroup group) =>
        vm.Nodes.Single(node => node.IsGroup && node.Id == group.Id);

    [TestMethod]
    public async Task FilterByTag_KeepsOnlyMatchingSessions_AndTheirGroupsExpanded()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync(collapse: true);
        Assert.AreSequenceEqual(["生产", "测试", "laptop"], [.. RowNames(vm)], "前提:分组是折着的");

        vm.FilterText = "nginx";

        Assert.AreSequenceEqual(["生产", "web-1"], [.. RowNames(vm)], "折着的分组有命中就撑开;没有命中的分组与根级连接藏起来");
        Assert.IsTrue(vm.IsFiltering);
        Assert.IsFalse(vm.HasNoFilterMatches);
    }

    [TestMethod]
    public async Task FilterMatchesNameHostTagNotesAndGroupName()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync();

        vm.FilterText = "10.9.9";
        Assert.AreSequenceEqual(["laptop"], [.. RowNames(vm)], "主机");

        vm.FilterText = "主库";
        Assert.AreSequenceEqual(["生产", "db-1"], [.. RowNames(vm)], "备注(多行的也算)");

        vm.FilterText = "WEB";
        Assert.AreSequenceEqual(["生产", "web-1", "测试", "web-t"], [.. RowNames(vm)], "名字,不分大小写");

        vm.FilterText = "测试";
        Assert.AreSequenceEqual(["测试", "web-t"], [.. RowNames(vm)], "输入分组名就把整组摊出来");

        vm.FilterText = "root";
        Assert.IsTrue(vm.HasNoFilterMatches, "用户名不参与:输一个 root 会把几乎所有连接都留下");
    }

    [TestMethod]
    public async Task EveryTermMustMatch_PossiblyInDifferentFields()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync();

        vm.FilterText = "prod  web";

        Assert.AreSequenceEqual(["生产", "web-1"], [.. RowNames(vm)], "prod 落在标签上、web 落在名字上 —— 「环境 + 角色」的组合");

        vm.FilterText = "生产 staging";
        Assert.IsTrue(vm.HasNoFilterMatches, "两个词都要命中:生产组里没有打 staging 标签的");
        Assert.IsEmpty(vm.Rows, "一条都没命中时分组行也不留");
    }

    [TestMethod]
    public async Task ClearingTheFilter_RestoresEachGroupsExpansion_WithoutRememberingTheTemporaryOne()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync(collapse: true);
        Group(vm, _test).IsExpanded = true; // 用户手动展开了「测试」,「生产」还折着

        vm.FilterText = "prod";
        Assert.IsTrue(Group(vm, _prod).IsExpanded);
        // 过滤期间手动折一下:清掉过滤之后不该被记住。
        Group(vm, _prod).IsExpanded = false;
        Assert.AreSequenceEqual(["生产"], [.. RowNames(vm)], "过滤期间照样可以手动折起");

        vm.FilterText = "";

        Assert.IsFalse(vm.IsFiltering);
        Assert.IsFalse(Group(vm, _prod).IsExpanded, "「生产」回到过滤之前的折叠态");
        Assert.IsTrue(Group(vm, _test).IsExpanded, "「测试」回到用户手动展开的样子");
        Assert.AreSequenceEqual(["生产", "测试", "web-t", "laptop"], [.. RowNames(vm)]);

        // 重建一次树:过滤撑开的那一下没有混进展开态记忆(#474)。
        await vm.LoadCommand.Execute().FirstAsync();
        Assert.IsFalse(Group(vm, _prod).IsExpanded);
        Assert.IsTrue(Group(vm, _test).IsExpanded);
    }

    [TestMethod]
    public async Task PinnedSessions_AreFilteredToo()
    {
        StubTree(Session("pinned-db", "10.0.0.9", tags: ["prod"], pinned: true),
                 Session("pinned-misc", "10.0.0.8", pinned: true));
        SessionTreeViewModel vm = await LoadAsync();

        vm.FilterText = "prod";

        Assert.AreEqual("pinned-db", vm.Rows[0].Name, "命中的置顶项照旧排在最前");
        Assert.DoesNotContain("pinned-misc", RowNames(vm), "没命中的置顶项同样藏起来");
    }

    [TestMethod]
    public async Task SelectionHiddenByTheFilter_IsDropped_AndFollowingTheActiveTabSkipsHiddenSessions()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync();
        SessionTreeNodeViewModel laptop = vm.Rows.Single(row => row.Name == "laptop");
        vm.SelectedNode = laptop;

        vm.FilterText = "prod";

        Assert.IsNull(vm.SelectedNode, "被过滤掉的不算「收进了分组」:选中落空,右键菜单不会对着另一行执行");
        Assert.IsFalse(vm.SelectSession(laptop.Id), "跟随活动标签不去选一行看不见的");
        Assert.IsNull(vm.SelectedNode);
        SessionTreeNodeViewModel db = vm.Rows.Single(row => row.Name == "db-1");
        Assert.IsTrue(vm.SelectSession(db.Id), "看得见的照常跟随");
    }

    [TestMethod]
    public async Task SingleFilterMatch_IsOnlySetWhenExactlyOneSessionIsLeft()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync();
        Assert.IsNull(vm.SingleFilterMatch, "不过滤时 Enter 不连任何一条");

        vm.FilterText = "prod";
        Assert.IsNull(vm.SingleFilterMatch, "剩两条时不猜");

        vm.FilterText = "nginx";
        Assert.AreEqual("web-1", vm.SingleFilterMatch?.Name);
    }

    [TestMethod]
    public async Task ReloadingWhileFiltering_KeepsTheMatchesVisible()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync(collapse: true);
        vm.FilterText = "nginx";

        // 编辑 / 新建一条连接、云同步回写都会整树重建。
        await vm.LoadCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual(["生产", "web-1"], [.. RowNames(vm)]);
    }

    [TestMethod]
    public async Task WhitespaceOnly_DoesNotFilter()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync();
        int all = vm.Rows.Count;

        vm.FilterText = "   ";

        Assert.IsFalse(vm.IsFiltering);
        Assert.HasCount(all, vm.Rows);
    }
}
