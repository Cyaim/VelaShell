using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Ssh;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
public sealed class SudoPromptDialogUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SudoPromptDialogUiTests).Assembly);

    [TestMethod]
    public void PasswordIsMaskedUtf8AndClearedAfterConfirmation()
    {
        _session.Dispatch(async () =>
        {
            Window owner = new();
            owner.Show();
            try
            {
                Task<SudoPassword?> pending = SudoPromptDialog.RequestPasswordAsync(owner, new("real.plugin", "joe@host:22"), "id | cat > /root/file", 1, CancellationToken.None);
                Dispatcher.UIThread.RunJobs();
                Window dialog = owner.OwnedWindows.Single();
                TextBox password = dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "SudoPasswordInput");
                Assert.AreNotEqual(default, password.PasswordChar);
                Assert.IsFalse(password.IsUndoEnabled);
                password.Text = "密碼é🙂";
                dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(pending.IsCompletedSuccessfully);
                using SudoPassword secret = pending.Result!;
                Assert.AreEqual("密碼é🙂", Encoding.UTF8.GetString(secret.Utf8.Span));
                Assert.AreEqual(string.Empty, password.Text);
                await Task.CompletedTask;
            }
            finally { owner.Close(); }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void RootConfirmationDisplaysTrustedIdentityAndFullCommandBeforeApproval()
    {
        _session.Dispatch(async () =>
        {
            Window owner = new();
            owner.Show();
            const string command = "printf 'value' | cat > /root/protected-file";
            try
            {
                Task<bool> pending = SudoPromptDialog.ConfirmAsync(owner,
                    new("trusted.plugin", "sudoer@host.example:2222"), command, CancellationToken.None);
                Dispatcher.UIThread.RunJobs();
                Window dialog = owner.OwnedWindows.Single();
                TextBlock[] labels = [.. dialog.GetVisualDescendants().OfType<TextBlock>()];
                Assert.Contains(label => label.Text?.Contains("trusted.plugin", StringComparison.Ordinal) == true, labels);
                Assert.Contains(label => label.Text?.Contains("sudoer@host.example:2222", StringComparison.Ordinal) == true, labels);
                TextBox commandBox = dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.IsReadOnly && input.Text == command);
                Assert.AreEqual(command, commandBox.Text);
                Assert.IsTrue(commandBox.IsReadOnly);
                Assert.IsFalse(pending.IsCompleted, "免密执行也必须等待宿主确认，不能自动批准");
                dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmButton")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(await pending);
            }
            finally { owner.Close(); }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void CancellingRootConfirmationNeverApprovesTheCommand()
    {
        _session.Dispatch(async () =>
        {
            Window owner = new();
            owner.Show();
            using var cancellation = new CancellationTokenSource();
            try
            {
                Task<bool> pending = SudoPromptDialog.ConfirmAsync(owner, new("plugin", "user@host"), "id", cancellation.Token);
                Dispatcher.UIThread.RunJobs();
                Assert.HasCount(1, owner.OwnedWindows);
                cancellation.Cancel();
                Dispatcher.UIThread.RunJobs();
                Assert.IsFalse(await pending);
                Assert.IsEmpty(owner.OwnedWindows);
                Assert.IsFalse(await SudoPromptDialog.ConfirmAsync(owner, new("plugin", "user@host"), "id", cancellation.Token));
                Assert.IsEmpty(owner.OwnedWindows);
            }
            finally { owner.Close(); }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void CancelClosesPromptAndAlreadyCancelledDoesNotOpenOne()
    {
        _session.Dispatch(async () =>
        {
            Window owner = new();
            owner.Show();
            using CancellationTokenSource cancellation = new();
            try
            {
                Task<SudoPassword?> pending = SudoPromptDialog.RequestPasswordAsync(owner, new("plugin", "user@host"), "id", 1, cancellation.Token);
                Dispatcher.UIThread.RunJobs();
                cancellation.Cancel();
                Dispatcher.UIThread.RunJobs();
                Assert.IsEmpty(owner.OwnedWindows);
                Assert.IsNull(pending.GetAwaiter().GetResult());
                Task<SudoPassword?> cancelled = SudoPromptDialog.RequestPasswordAsync(owner, new("plugin", "user@host"), "id", 1, cancellation.Token);
                Dispatcher.UIThread.RunJobs();
                Assert.IsNull(cancelled.GetAwaiter().GetResult());
                Assert.IsEmpty(owner.OwnedWindows);
                await Task.CompletedTask;
            }
            finally { owner.Close(); }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
