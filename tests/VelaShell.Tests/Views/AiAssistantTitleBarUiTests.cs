using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Core.Localization;
using VelaShell.Localization;
using VelaShell.Presentation.Commands;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("UI")]
public sealed class AiAssistantTitleBarUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AiAssistantTitleBarUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public Task ClickingTheAiButton_RunsTheChatCommand_WithoutMaximizing() =>
        _session.RunOnUiAsync(() =>
        {
            var commands = new CommandRegistry();
            int opened = 0;
            commands.Register(new CommandDescriptor("velashell.ai.chat", "AI", "AI", () => opened++));
            var titleBar = new TitleBarView { DataContext = new MainWindowViewModel(commandRegistry: commands) };
            var window = new Window { Width = 1200, Height = 200, Content = titleBar };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Button button = titleBar.GetControl<Button>("AiAssistantButton");
                Assert.AreSame(titleBar.FindResource("VelaTitleActionButtonTheme"), button.Theme);
                Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.IsNotNull(ToolTip.GetTip(button));
                Point point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                for (int i = 0; i < 2; i++)
                {
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                }
                Dispatcher.UIThread.RunJobs();

                Assert.AreEqual(2, opened);
                Assert.AreEqual(WindowState.Normal, window.WindowState);
            }
            finally
            {
                window.Close();
            }
        });
}
