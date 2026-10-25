using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>设置 → 输出规则页的 headless 回归:编辑框的开合、校验挡住「完成」、测试区出结果。</summary>
[TestClass]
[TestCategory("OutputRules")]
public sealed class OutputRulesPageUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(OutputRulesPageUiTests).Assembly);

    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [TestMethod]
    public void AddRule_EditorValidates_AndTheTestAreaShowsMatches()
    {
        OnUi(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            var viewModel = new SettingsViewModel(settings, theme);
            await viewModel.LoadCommand.Execute().FirstAsync();
            viewModel.SelectSection(SettingsSectionKey.OutputRules);

            var window = new SettingsView { DataContext = viewModel };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Border editor = Find<Border>(window, "RuleEditor");
                Assert.IsFalse(editor.IsVisible, "出厂没有规则,编辑框关着。");

                Find<Button>(window, "AddRuleButton").Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(editor.IsVisible);
                Button commit = Find<Button>(window, "CommitRuleButton");
                Assert.IsFalse(commit.IsEffectivelyEnabled, "空正则不能点「完成」。");

                Find<TextBox>(window, "RulePatternBox").Text = "ERROR";
                Find<ComboBox>(window, "RuleForegroundBox").SelectedIndex = 10; // 亮红(下标 9)
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(commit.IsEffectivelyEnabled);

                Find<TextBox>(window, "RuleTestBox").Text = "ok\nERROR: disk full";
                Dispatcher.UIThread.RunJobs();
                ItemsControl results = Find<ItemsControl>(window, "RuleTestResults");
                Assert.AreEqual(1, results.ItemCount);

                commit.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.IsFalse(editor.IsVisible);
                Assert.AreEqual(1, Find<ItemsControl>(window, "RuleList").ItemCount);
                Assert.AreEqual(9, viewModel.OutputRules.Rules[0].Rule.Foreground);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void AutoAnswerOnAPasswordPrompt_ShowsWhyAndBlocksDone()
    {
        OnUi(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            var viewModel = new SettingsViewModel(settings, theme);
            await viewModel.LoadCommand.Execute().FirstAsync();
            viewModel.SelectSection(SettingsSectionKey.OutputRules);

            var window = new SettingsView { DataContext = viewModel };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Find<Button>(window, "AddRuleButton").Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Find<ComboBox>(window, "RuleActionBox").SelectedIndex = 1;
                Find<TextBox>(window, "RulePatternBox").Text = @":\s*$";
                Find<TextBox>(window, "RuleResponseBox").Text = "hunter2";
                Dispatcher.UIThread.RunJobs();

                TextBlock error = Find<TextBlock>(window, "RuleDraftError");
                Assert.IsTrue(error.IsVisible);
                Assert.IsFalse(string.IsNullOrEmpty(error.Text));
                Assert.IsFalse(Find<Button>(window, "CommitRuleButton").IsEffectivelyEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
