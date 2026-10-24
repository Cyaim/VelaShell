using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Controls;
using VelaShell.Controls.Controls;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Docking.Controls;
using VelaShell.PluginSdk;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 按会话自定义的标签页图标真的画得出来:清单里每个 id 都能在真实资源字典里解析、各不相同,
/// 标签页与资源管理器画的是同一枚,挑了就压过插件自报的与协议的,认不出就退回协议的;图标字段点一格就写回。
/// </summary>
/// <remarks>
/// 图标键写错不报错、静默不画(§68 第四条),所以必须在活着的资源字典里解析,而不是只看映射。
/// body 同步 + <c>return Task.CompletedTask</c>,见 <see cref="SessionTabIconUiTests" />。
/// </remarks>
[TestClass]
[TestCategory("UI")]
public sealed class SessionTabIconCatalogUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SessionTabIconCatalogUiTests).Assembly);

    [TestMethod]
    public void EveryCatalogIconResolvesToItsOwnGeometry()
    {
        OnUi(() =>
        {
            List<Geometry> glyphs = [];
            foreach (SessionTabIconChoice choice in SessionTabIcons.All)
            {
                Geometry? geometry = ConnectionIcon.GeometryFor(SessionTabIcons.ResourceKey(choice.Id));
                Assert.IsNotNull(geometry, $"图标 {choice.Id} 在 Icons.axaml 里不存在 —— 挑了它,标签上会静默少一个图标。");
                Assert.IsGreaterThan(0d, geometry.Bounds.Width, $"图标 {choice.Id} 的路径是空的");
                glyphs.Add(geometry);
            }
            Assert.HasCount(glyphs.Count, glyphs.Distinct(), "两个 id 指向了同一个几何");
        });
    }

    [TestMethod]
    public void PickedIcon_BeatsThePluginsAndTheProtocols_UnknownFallsBack()
    {
        OnUi(() =>
        {
            Geometry database = ConnectionIcon.GeometryFor("Icon.database")!;
            var picked = new SessionProfile { ConnectionType = ConnectionType.Plugin, Terminal = new() { TabIcon = "database" } };

            Assert.AreSame(database, ConnectionIcon.ForSession(picked, PluginIcon.Stroked("M4 4h16v16H4Z"))!.Geometry,
                "用户亲手挑的压过插件自报的");

            var unknown = new SessionProfile { ConnectionType = ConnectionType.SSH, Terminal = new() { TabIcon = "unicorn" } };
            Assert.AreSame(
                ConnectionIcon.ForSession(new() { ConnectionType = ConnectionType.SSH })!.Geometry,
                ConnectionIcon.ForSession(unknown)!.Geometry,
                "认不出的 id 当作没挑,退回协议图标而不是不画");
        });
    }

    [TestMethod]
    public void TheTabAndTheExplorerRowDrawTheSameGlyph()
    {
        OnUi(() =>
        {
            Geometry flame = ConnectionIcon.GeometryFor("Icon.flame")!;
            var profile = new SessionProfile { Name = "prod-db", Host = "10.0.0.2", Terminal = new() { TabIcon = "flame" } };

            var document = new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator()) { Profile = profile });
            Show(new DockTabItem { DataContext = document }, tab =>
                Assert.Contains(i => ReferenceEquals(i.Data, flame), VisibleIcons(tab), "标签页上应当画出挑的那枚"));

            var tree = new SessionTreeViewModel(Substitute.For<ISessionRepository>());
            tree.AddSession(profile);
            tree.AddSession(new SessionProfile { Name = "plain", Host = "10.0.0.3" });
            Show(new SessionTreeView { DataContext = tree }, view =>
            {
                List<LucideIcon> custom = [.. VisibleIcons(view).Where(i => i.Classes.Contains("sessionIcon"))];
                Assert.HasCount(1, custom, "只有挑过图标的那一行多一枚");
                Assert.AreSame(flame, custom[0].Data, "资源管理器画的是同一枚");
            });
        });
    }

    [TestMethod]
    public void IconPicker_WritesTheCellItWasClickedOn_AndFollowProtocolClearsIt()
    {
        OnUi(() =>
        {
            var field = new IconPickerField { DefaultIconKey = ConnectionIcon.SshKey, EmptyText = "跟随协议" };
            Show(field, _ =>
            {
                Assert.AreEqual("跟随协议", field.ValueTextForTest);
                Assert.HasCount(SessionTabIcons.All.Count, field.CellsForTest);
                Assert.IsFalse(field.DefaultButtonForTest.IsEnabled, "已经跟着协议了,这一键是灰的");

                Button database = field.CellsForTest.Single(c => Equals(c.Tag, "database"));
                database.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual("database", field.Value);
                Assert.IsTrue(database.Classes.Contains("current"));
                Assert.AreNotEqual("跟随协议", field.ValueTextForTest);

                field.DefaultButtonForTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsNull(field.Value);
                Assert.IsFalse(database.Classes.Contains("current"));
            });
        });
    }

    private static IEnumerable<LucideIcon> VisibleIcons(Visual root) =>
        root.GetVisualDescendants().OfType<LucideIcon>().Where(i => i.IsEffectivelyVisible);

    private static void Show(Control content, Action<Control> body)
    {
        var window = new Window { Width = 600, Height = 300, Content = content };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            body(content);
        }
        finally
        {
            window.Close();
        }
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
}
