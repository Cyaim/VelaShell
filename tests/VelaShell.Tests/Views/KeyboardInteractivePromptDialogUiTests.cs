using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Ssh;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 动态码框(真控件):连接目标与说明看得见、按服务端的 echo 标志决定遮不遮、
/// 按提示顺序交回答案、连接被取消时窗口当场收起。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public class KeyboardInteractivePromptDialogUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    // 共用全程序集的宿主(见 VelaHeadlessApp):不能各起各的 App。
    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(KeyboardInteractivePromptDialogUiTests).Assembly);

    private static KeyboardInteractiveRequest Request() => new(
        "root@bastion:22",
        "Duo",
        "Choose a method",
        [new("Passcode or option (1-3):", true), new("Verification code:", false)]);

    [TestMethod]
    public void Confirm_ReturnsAnswersInPromptOrder_AndMasksWhatTheServerSaysNotToEcho()
    {
        OnUi(() =>
        {
            var owner = new Window();
            owner.Show();
            try
            {
                Task<IReadOnlyList<string>?> pending = KeyboardInteractivePromptDialog.ShowAsync(owner, Request(), CancellationToken.None);
                Dispatcher.UIThread.RunJobs();
                Window dialog = owner.OwnedWindows.Single();

                Assert.AreEqual("Duo", dialog.Title);
                string[] texts = [.. dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];
                CollectionAssert.Contains(texts, "root@bastion:22", "要看得出是哪台机器在要动态码");
                CollectionAssert.Contains(texts, "Choose a method");

                TextBox[] inputs = Inputs(dialog);
                Assert.HasCount(2, inputs);
                Assert.AreEqual(default, inputs[0].PasswordChar, "echo = true 的照原样显示");
                Assert.AreNotEqual(default, inputs[1].PasswordChar, "echo = false 的要遮住");
                Assert.IsTrue(inputs[0].IsFocused, "打开即可直接输入");

                inputs[0].Text = "1";
                inputs[1].Text = "123456";
                ButtonNamed(dialog, "ConfirmButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.IsTrue(pending.IsCompletedSuccessfully);
                Assert.AreSequenceEqual(["1", "123456"], [.. pending.Result!]);
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
                Task<IReadOnlyList<string>?> pending = KeyboardInteractivePromptDialog.ShowAsync(owner, Request(), CancellationToken.None);
                Dispatcher.UIThread.RunJobs();

                ButtonNamed(owner.OwnedWindows.Single(), "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
    public void ATriggeredToken_ClosesTheDialog()
    {
        OnUi(() =>
        {
            var owner = new Window();
            owner.Show();
            using var cts = new CancellationTokenSource();
            try
            {
                Task<IReadOnlyList<string>?> pending = KeyboardInteractivePromptDialog.ShowAsync(owner, Request(), cts.Token);
                Dispatcher.UIThread.RunJobs();
                Inputs(owner.OwnedWindows.Single())[1].Text = "123456";

                // 关了正在连的标签 / 认证超时
                cts.Cancel();
                Dispatcher.UIThread.RunJobs();

                Assert.IsEmpty(owner.OwnedWindows, "窗口要当场收起,而不是留着一个再也交不上去的框");
                Assert.IsTrue(pending.IsCompletedSuccessfully);
                Assert.IsNull(pending.Result);
            }
            finally
            {
                owner.Close();
            }
        });
    }

    /// <summary>应答输入框:排除 MessageDialog 自带(此形态下隐藏)的单行输入框。</summary>
    private static TextBox[] Inputs(Window dialog) =>
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
