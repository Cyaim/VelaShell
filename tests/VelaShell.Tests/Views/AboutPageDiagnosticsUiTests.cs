using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;
using VelaShell.Views.Settings;

namespace VelaShell.Tests.Views;

/// <summary>关于页的「导出诊断包」:按钮在「打开日志目录」旁边,结果独占一行,没有结果时不占位。</summary>
[TestClass]
[TestCategory("SettingsUi")]
public sealed class AboutPageDiagnosticsUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AboutPageDiagnosticsUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void ExportButton_SitsBesideOpenLogsAndTheResultLineAppearsOnlyWithText()
    {
        _session.Dispatch(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            var viewModel = new SettingsViewModel(settings, Substitute.For<IThemeService>());
            await viewModel.LoadCommand.Execute().FirstAsync();
            var window = new SettingsView { DataContext = viewModel };
            window.Show();
            try
            {
                viewModel.SelectSection(SettingsSectionKey.About);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                AboutPage page = window.GetVisualDescendants().OfType<AboutPage>().Single();
                Button export = page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ExportDiagnosticsButton");
                Assert.AreEqual(Strings.Get("SetAbout_ExportDiagnostics"), export.Content);
                Assert.AreEqual(Strings.Get("SetAbout_ExportDiagnosticsTip"), ToolTip.GetTip(export));
                var row = (StackPanel)export.GetVisualParent()!;
                Assert.Contains(button => Equals(button.Content, Strings.Get("SetAbout_OpenLogs")), row.Children.OfType<Button>(),
                    "和「打开日志目录」放在同一排");

                string done = Strings.Format("SetAbout_ExportDiagnosticsDone", @"C:\Downloads\velashell-diagnostics.zip", 3);
                viewModel.DiagnosticsStatus = done;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                TextBlock result = page.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == done);
                Assert.IsTrue(result.IsVisible);
                Assert.AreSame(row.GetVisualParent(), result.GetVisualParent(), "结果独占一行,不挤在按钮排里");

                viewModel.DiagnosticsStatus = "";
                Dispatcher.UIThread.RunJobs();
                Assert.IsFalse(result.IsVisible, "没有结果时不占位");
            }
            finally
            {
                window.Close();
            }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void Export_ClearsThePreviousResultFirstAndShowsFailuresOfAnyKind()
    {
        _session.Dispatch(async () =>
        {
            var profiles = new TaskCompletionSource<List<SessionProfile>>();
            ISessionRepository sessions = Substitute.For<ISessionRepository>();
            sessions.GetAllSessionsAsync().Returns(profiles.Task);
            ISettingsService settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(_ => Task.FromResult(new AppSettings()));
            var viewModel = new SettingsViewModel(settings, Substitute.For<IThemeService>(), sessionRepository: sessions)
            {
                DiagnosticsStatus = Strings.Format("SetAbout_ExportDiagnosticsDone", @"C:\Downloads\old.zip", 3)
            };
            var button = new Button();

            Task export = AboutPage.ExportDiagnosticsToAsync(viewModel, button,
                Path.Combine(Path.GetTempPath(), $"velashell_diagexport_{Guid.NewGuid():N}.zip"), []);

            Assert.AreEqual("", viewModel.DiagnosticsStatus, "上一次的「已保存到 …」一开始就清掉,不留到这一次出结果");
            Assert.IsFalse(button.IsEnabled);

            // 读连接配置失败不是 IO 错:原先漏给 FireAndForget,只进日志,页面上什么也不显示
            profiles.SetException(new InvalidOperationException("database is locked"));
            await export;

            Assert.AreEqual(Strings.Format("SetAbout_ExportDiagnosticsFailed", "database is locked"), viewModel.DiagnosticsStatus);
            Assert.IsTrue(button.IsEnabled);
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
