using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 临时连接的登录框上看得见的是「另存为连接」,不是「记住密码」;已保存的连接反过来(第二轮第 15 条)。
/// </summary>
[TestClass]
public sealed class TemporaryConnectionDialogUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TemporaryConnectionDialogUiTests).Assembly);

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void LoginDialog_OffersSaveAsConnection_OnlyForTemporaryProfiles(bool temporary)
    {
        _session.Dispatch(() =>
        {
            var vm = new AuthenticationDialogViewModel("bastion.example", 2222, "ops", temporaryProfile: temporary);
            var window = new AuthenticationDialogView { DataContext = vm };
            window.Show();
            vm.NextCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            try
            {
                Assert.AreEqual(temporary, CheckBoxShowing(window, Strings.Get("Auth_SaveAsConnection")));
                Assert.AreEqual(!temporary, CheckBoxShowing(window, Strings.Get("Profile_RememberPassword")));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static bool CheckBoxShowing(Window window, string label) =>
        window.GetVisualDescendants().OfType<CheckBox>()
            .Any(box => box.IsEffectivelyVisible
                        && box.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == label));
}
