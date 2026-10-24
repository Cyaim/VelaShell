using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 按会话自定义标签页图标:连接对话框里挑、存进 <see cref="TerminalOverrides.TabIcon" />、资源管理器的会话行带上它。
/// </summary>
[TestClass]
[TestCategory("SessionTabIcon")]
public class SessionTabIconProfileTests
{
    [TestMethod]
    public async Task PickedIcon_IsSaved_AndRestoredOnEdit()
    {
        var vm = new ConnectionProfileViewModel { Host = "10.0.0.2", Username = "dba" };
        vm.TabIcon = "database";

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.AreEqual("database", saved!.Terminal?.TabIcon);
        Assert.AreEqual("database", new ConnectionProfileViewModel(saved).TabIcon);
    }

    [TestMethod]
    public async Task IconWorksForEveryProtocol_NotJustSsh()
    {
        // 「终端」页只有 SSH 才有;图标在「常规」页挑,SFTP / FTP / 插件标签也画。
        var vm = new ConnectionProfileViewModel { Host = "ftp.example.com", Username = "deploy" };
        await vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).FirstAsync();
        vm.TabIcon = "hard-drive";

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.AreEqual("hard-drive", saved!.Terminal?.TabIcon);
        Assert.AreEqual(VelaShell.Services.ConnectionIcon.FileKey, vm.TabIconDefaultKey, "跟协议时字段上画的是这个协议的默认图标");
    }

    [TestMethod]
    public async Task FollowProtocol_StoresNothing()
    {
        var vm = new ConnectionProfileViewModel { Host = "10.0.0.2", Username = "dba" };
        vm.TabIcon = "database";
        vm.TabIcon = null;

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNull(saved!.Terminal, "一项都没覆盖时整个对象存 null,而不是一个全空对象");
    }

    [TestMethod]
    public void UnknownIcon_IsTreatedAsNotPicked()
    {
        // 配置文件可以手改,更新的版本也可能加了这一版不认识的图标:当作没挑,不让图标消失。
        var existing = new SessionProfile { Host = "h", Terminal = new() { TabIcon = "unicorn" } };

        Assert.IsNull(new ConnectionProfileViewModel(existing).TabIcon);
        Assert.IsNull(SessionTabIcons.For(existing));
        Assert.AreEqual("server", SessionTabIcons.Normalize(" server "));
    }

    [TestMethod]
    public void TabIconAlone_IsAnOverride()
    {
        Assert.IsFalse(new TerminalOverrides { TabIcon = "flame" }.IsEmpty, "只挑了图标也不能被当成「没有覆盖」存成 null");
    }

    [TestMethod]
    public void Catalog_HasUniqueIdsAndNameKeys()
    {
        Assert.HasCount(SessionTabIcons.All.Count, SessionTabIcons.All.Select(static c => c.Id).Distinct());
        Assert.HasCount(SessionTabIcons.All.Count, SessionTabIcons.All.Select(static c => c.NameKey).Distinct());
    }

    [TestMethod]
    public void TreeNodes_CarryThePickedIcon()
    {
        var tree = new SessionTreeViewModel(NSubstitute.Substitute.For<Core.Data.ISessionRepository>());
        var picked = new SessionProfile { Name = "db", Host = "10.0.0.2", Terminal = new() { TabIcon = "database" } };
        var plain = new SessionProfile { Name = "web", Host = "10.0.0.3" };

        tree.AddSession(picked);
        tree.AddSession(plain);

        SessionTreeNodeViewModel pickedNode = tree.Nodes.Single(n => n.Id == picked.Id);
        Assert.AreEqual("database", pickedNode.TabIconId);
        Assert.IsTrue(pickedNode.HasTabIcon);
        Assert.IsFalse(tree.Nodes.Single(n => n.Id == plain.Id).HasTabIcon, "没挑的行照旧只有圆点与名字");
    }
}
