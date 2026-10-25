using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Resources;
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

    /// <summary>
    /// 弹出那一瞬鼠标恰好落在「批准」上:批准键先灰着约 0.6 秒,那一下落空(#616–#636 复审④,维护者 2026-10-25 决定);
    /// 时间一到就能点,取消从头到尾都能点。
    /// </summary>
    [TestMethod]
    public void RootConfirmation_ApproveStaysDisabledBriefly_SoAClickAtPopUpNeverApproves()
    {
        _session.Dispatch(async () =>
        {
            Window owner = new();
            owner.Show();
            try
            {
                Task<bool> pending = SudoPromptDialog.ConfirmAsync(owner, new("plugin", "user@host"), "systemctl restart app", CancellationToken.None);
                Dispatcher.UIThread.RunJobs();
                Window dialog = owner.OwnedWindows.Single();
                Button approve = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmButton");
                Button cancel = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CancelButton");
                Assert.IsFalse(approve.IsEnabled, "刚弹出时批准键不能点");
                Assert.IsTrue(cancel.IsEnabled, "取消随时能点");
                Assert.AreEqual(TimeSpan.FromMilliseconds(600), SudoPromptDialog.ApproveDelay);

                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (!approve.IsEnabled && waited.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(20);
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.IsTrue(approve.IsEnabled, "到时间就能点");
                Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(500), waited.Elapsed, "确实灰了一阵,不是一拍就亮");
                Assert.IsFalse(pending.IsCompleted);
                approve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(await pending);
            }
            finally { owner.Close(); }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 审批框会在用户正往终端里打字时异步弹出(Bridge/MCP、免确认模式):回车与初始焦点都落在取消上,
    /// 顺手的一个回车只能拒绝,绝不能替用户批准一条 root 命令。
    /// </summary>
    [TestMethod]
    public void RootConfirmationDefaultsToCancel_SoAStrayEnterNeverApproves()
    {
        _session.Dispatch(async () =>
        {
            Window owner = new();
            owner.Show();
            try
            {
                Task<bool> pending = SudoPromptDialog.ConfirmAsync(owner, new("plugin", "user@host"), "rm -rf /var/lib/app", CancellationToken.None);
                Dispatcher.UIThread.RunJobs();
                Window dialog = owner.OwnedWindows.Single();
                Button approve = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmButton");
                Button cancel = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CancelButton");
                Assert.IsFalse(approve.IsDefault, "批准不能是默认键");
                Assert.IsTrue(cancel.IsDefault, "回车必须落在取消上");
                Assert.IsTrue(cancel.IsCancel);
                Assert.IsTrue(cancel.IsFocused, "弹出时焦点应当在取消上");

                dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(pending.IsCompleted);
                Assert.IsFalse(await pending);
            }
            finally { owner.Close(); }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 双向控制符、零宽字符与控制字符在命令框里写成 ⟨U+XXXX⟩;多行或很长的命令注明行数与长度,
    /// 几百个换行藏在可视区外的末尾也提醒得到。
    /// </summary>
    [TestMethod]
    public void RootConfirmationRevealsInvisibleCharactersAndReportsLongCommands()
    {
        _session.Dispatch(async () =>
        {
            Window owner = new();
            owner.Show();
            string command = "echo safe‮txt.exe​\u001b[2J" + new string('\n', 300) + "rm -rf /";
            try
            {
                Task<bool> pending = SudoPromptDialog.ConfirmAsync(owner, new("plugin", "user@host"), command, CancellationToken.None);
                Dispatcher.UIThread.RunJobs();
                Window dialog = owner.OwnedWindows.Single();
                TextBox shown = dialog.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "SudoCommandText");
                Assert.Contains("⟨U+202E⟩", shown.Text!);
                Assert.Contains("⟨U+200B⟩", shown.Text!);
                Assert.Contains("⟨U+001B⟩", shown.Text!);
                Assert.DoesNotContain("‮", shown.Text!);
                Assert.DoesNotContain("\u001b", shown.Text!);
                Assert.EndsWith("rm -rf /", shown.Text!);
                string[] notes = [.. dialog.GetVisualDescendants().OfType<TextBlock>().Select(label => label.Text ?? string.Empty)];
                Assert.Contains(note => note == Strings.Format("Sudo_CommandSize", 301, command.Length), notes);
                Assert.Contains(note => note == Strings.Format("Sudo_CommandHidden", 3), notes);
                dialog.Close(false);
                Dispatcher.UIThread.RunJobs();
                Assert.IsFalse(await pending);
            }
            finally { owner.Close(); }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void RevealCommandKeepsOrdinaryTextAndLayoutCharacters()
    {
        Assert.AreEqual("printf '漢🙂'\n\tls -la", SudoPromptDialog.RevealCommand("printf '漢🙂'\n\tls -la", out int none));
        Assert.AreEqual(0, none);
        Assert.AreEqual("a⟨U+000D⟩b⟨U+00A0⟩c⟨U+3164⟩d⟨U+E0041⟩", SudoPromptDialog.RevealCommand("a\rb cㅤd\U000E0041", out int hidden));
        Assert.AreEqual(4, hidden);
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
