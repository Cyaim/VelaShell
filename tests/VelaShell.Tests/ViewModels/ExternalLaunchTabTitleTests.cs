using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 外部拉起的 <c>-newtab &lt;会话名&gt;</c> 拿去当标签页标题(#475 的下一步):名字只上标签,
/// 不改配置、不改状态栏 —— 状态栏那一行说的是「真连到了哪」。
/// </summary>
[TestClass]
[TestCategory("Startup")]
public sealed class ExternalLaunchTabTitleTests
{
    private static SessionProfile Profile() =>
        new()
        {
            Name = "JMS-7f3a@192.168.1.1:2222",
            Host = "192.168.1.1",
            Port = 2222,
            Username = "JMS-7f3a",
            AuthMethod = AuthMethod.Password,
            Password = "lD9xKq2m",
            RememberPassword = false
        };

    /// <summary>握手停在半路,标签停在「连接中」:标题在握手开始前就该是对的。</summary>
    private static MainWindowViewModel Hanging()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow
            .ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, (CancellationToken)call[1]);
                return null!;
            });
        return new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), () => FakeTerminal.Emulator());
    }

    [TestMethod]
    public async Task GivenTabTitle_NamesTheTabButNotTheProfile()
    {
        MainWindowViewModel vm = Hanging();
        SessionProfile profile = Profile();

        Task<TerminalTabViewModel?> connecting = vm.TryConnectProfileAsync(profile, "root@Linux[2026_09_20_09_45_18]");

        TerminalTabViewModel tab = vm.TerminalTabs.Single();
        Assert.AreEqual("root@Linux[2026_09_20_09_45_18]", tab.Title);
        Assert.AreEqual("root@Linux[2026_09_20_09_45_18]",
            vm.Layout.AllDocuments().OfType<TerminalDocument>().Single().Title,
            "标签条画的是文档的标题,它在建文档时从终端抄一次");
        Assert.AreEqual("JMS-7f3a@192.168.1.1:2222", profile.Name, "配置本身不改名:复用已保存的配置时这会随最近连接时间一起落盘");
        StringAssert.Contains(tab.ConnectionSummary, "JMS-7f3a@192.168.1.1:2222", "状态栏照旧说真连到了哪");

        vm.CloseTerminalTab(tab);
        Assert.IsNull(await connecting);
    }

    [TestMethod]
    public async Task WithoutTabTitle_TheTabIsNamedAfterTheProfile()
    {
        MainWindowViewModel vm = Hanging();

        Task<TerminalTabViewModel?> connecting = vm.TryConnectProfileAsync(Profile(), "   ");

        TerminalTabViewModel tab = vm.TerminalTabs.Single();
        Assert.AreEqual("JMS-7f3a@192.168.1.1:2222", tab.Title, "空白的名字等于没给");

        vm.CloseTerminalTab(tab);
        Assert.IsNull(await connecting);
    }
}
