using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Models;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 快捷命令变量询问框(真控件):每个变量一行、预填默认值、预览随输入实时变、确认 / 取消的返回值。
/// </summary>
[TestClass]
[TestCategory("QuickCommands")]
public class QuickCommandVariablesPromptUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    // 共用全程序集的宿主(见 VelaHeadlessApp):不能各起各的 App。
    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(QuickCommandVariablesPromptUiTests).Assembly);

    [TestMethod]
    public void Confirm_ReturnsTypedValues_AndPreviewFollowsTheInput()
    {
        OnUi(() =>
        {
            var owner = new Window();
            owner.Show();
            try
            {
                Task<IReadOnlyDictionary<string, string>?> pending = QuickCommandVariablesPrompt.ShowAsync(
                    owner, "查看日志", QuickCommandTemplate.Parse("kubectl logs -f {{pod}} -n {{ns=default}}"));
                Dispatcher.UIThread.RunJobs();
                Window dialog = owner.OwnedWindows.Single();

                TextBox[] inputs = VariableInputs(dialog);
                Assert.HasCount(2, inputs);
                Assert.AreEqual("", inputs[0].Text);
                Assert.AreEqual("default", inputs[1].Text, "默认值要预填");
                Assert.IsTrue(inputs[0].IsFocused, "打开即可直接键入第一个值");
                TextBlock preview = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("mono-accent"));
                Assert.AreEqual("kubectl logs -f  -n default", preview.Text);

                inputs[0].Text = "web-1";
                inputs[1].Text = "prod";
                Dispatcher.UIThread.RunJobs();
                Assert.AreEqual("kubectl logs -f web-1 -n prod", preview.Text);

                ButtonNamed(dialog, "ConfirmButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.IsTrue(pending.IsCompletedSuccessfully);
                IReadOnlyDictionary<string, string> values = pending.Result!;
                Assert.AreEqual("web-1", values["pod"]);
                Assert.AreEqual("prod", values["ns"]);
            }
            finally
            {
                owner.Close();
            }
        });
    }

    [TestMethod]
    public void Cancel_ReturnsNull()
    {
        OnUi(() =>
        {
            var owner = new Window();
            owner.Show();
            try
            {
                Task<IReadOnlyDictionary<string, string>?> pending = QuickCommandVariablesPrompt.ShowAsync(
                    owner, "查看日志", QuickCommandTemplate.Parse("kubectl logs -f {{pod}}"));
                Dispatcher.UIThread.RunJobs();
                Window dialog = owner.OwnedWindows.Single();
                VariableInputs(dialog)[0].Text = "web-1";

                ButtonNamed(dialog, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.IsTrue(pending.IsCompletedSuccessfully);
                Assert.IsNull(pending.Result);
            }
            finally
            {
                owner.Close();
            }
        });
    }

    [TestMethod]
    public void InitialValues_ArePrefilled_AndThePreviewUsesThem()
    {
        OnUi(() =>
        {
            var owner = new Window();
            owner.Show();
            try
            {
                // 宿主按「上一次填的值」给初值;没给的那一格照旧是占位的默认值。
                Task<IReadOnlyDictionary<string, string>?> pending = QuickCommandVariablesPrompt.ShowAsync(
                    owner, "查看日志", QuickCommandTemplate.Parse("kubectl logs -f {{pod}} -n {{ns=default}}"),
                    new Dictionary<string, string> { ["pod"] = "web-1" });
                Dispatcher.UIThread.RunJobs();
                Window dialog = owner.OwnedWindows.Single();

                TextBox[] inputs = VariableInputs(dialog);
                Assert.AreEqual("web-1", inputs[0].Text);
                Assert.AreEqual("default", inputs[1].Text);
                TextBlock preview = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("mono-accent"));
                Assert.AreEqual("kubectl logs -f web-1 -n default", preview.Text);

                ButtonNamed(dialog, "ConfirmButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.AreEqual("web-1", pending.Result!["pod"], "不改就照预填的值发");
            }
            finally
            {
                owner.Close();
            }
        });
    }

    /// <summary>变量输入框:排除 MessageDialog 自带(此形态下隐藏)的单行输入框。</summary>
    private static TextBox[] VariableInputs(Window dialog) =>
        [.. dialog.GetVisualDescendants().OfType<TextBox>().Where(t => t.Name != "InputBox")];

    private static Button ButtonNamed(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    private static void OnUi(Action body) =>
        _session.Dispatch(
            () =>
            {
                body();
                return Task.CompletedTask;
            },
            CancellationToken.None
        ).GetAwaiter().GetResult();
}
