using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 设置导出 / 导入的分类勾选框:敏感分类默认不勾、勾上才出警示;一类都不勾不能确定;外壳与别的对话框同一规格。
/// </summary>
[TestClass]
[TestCategory("Settings")]
public sealed class SettingsTransferViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SettingsTransferViewUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void SensitiveCategories_StartUnticked_AndWarnOnceTicked()
    {
        var vm = new SettingsTransferViewModel(SettingsTransferMode.Export, SettingsCategories.All);

        Assert.AreSequenceEqual(
            [.. SettingsCategories.All.Where(c => !c.IsSensitive).Select(c => c.Id)],
            vm.SelectedIds, "默认勾上全部非敏感分类");
        SettingsTransferItem proxy = vm.Items.Single(i => i.Category.Id == SettingsCategories.Proxy);
        Assert.IsFalse(proxy.ShowWarning, "没勾的时候不显示警示");

        proxy.IsChecked = true;

        Assert.IsTrue(proxy.ShowWarning);
        Assert.AreEqual(Strings.Get("SetTransfer_ProxyExportWarning"), proxy.Warning);
        Assert.Contains(SettingsCategories.Proxy, vm.SelectedIds);
    }

    [TestMethod]
    public void Import_ListsOnlyWhatTheFileCarries_AndWarnsAboutWhatItChanges()
    {
        SettingsCategory[] carried = [SettingsCategories.Find("appearance")!, SettingsCategories.Find("security")!];
        var vm = new SettingsTransferViewModel(SettingsTransferMode.Import, carried, "backup.json");

        Assert.HasCount(2, vm.Items);
        Assert.AreEqual(Strings.Format("SetTransfer_ImportSummary", "backup.json"), vm.Summary);
        Assert.AreEqual(Strings.Get("SetTransfer_SecurityImportWarning"), vm.Items[1].Warning);
        Assert.AreSequenceEqual([SettingsCategories.Appearance], vm.SelectedIds, "导入同样默认不勾敏感分类");
    }

    [TestMethod]
    public void NothingTicked_CannotConfirm()
    {
        var vm = new SettingsTransferViewModel(SettingsTransferMode.Export, SettingsCategories.All);
        foreach (SettingsTransferItem item in vm.Items)
        {
            item.IsChecked = false;
        }
        Assert.IsFalse(vm.CanConfirm);

        vm.Items[0].IsChecked = true;
        Assert.IsTrue(vm.CanConfirm);
    }

    [TestMethod]
    public void TheDialog_FollowsTheDialogTitleBarSpec_AndShowsTheWarningRow()
    {
        OnUi(() =>
        {
            var vm = new SettingsTransferViewModel(SettingsTransferMode.Export, SettingsCategories.All);
            var view = new SettingsTransferView { DataContext = vm };
            view.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                DialogTitleBarAssert.FollowsSpec(view);
                string warning = Strings.Get("SetTransfer_SecurityExportWarning");
                Assert.IsFalse(Visible(view, warning), "没勾安全审计时警示不出现");

                vm.Items.Single(i => i.Category.Id == SettingsCategories.Security).IsChecked = true;
                Dispatcher.UIThread.RunJobs();

                Assert.IsTrue(Visible(view, warning), "勾上之后警示出现在那一行下面");
                Button confirm = view.FindControl<Button>("ConfirmButton")!;
                Assert.IsTrue(confirm.IsEnabled);
            }
            finally
            {
                view.Close();
            }
        });
    }

    [TestMethod]
    public void CloseHover_StaysInsideTheRoundedCorner()
    {
        OnUi(() =>
        {
            var view = new SettingsTransferView
            {
                DataContext = new SettingsTransferViewModel(SettingsTransferMode.Import, SettingsCategories.All, "x.json")
            };
            view.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                DialogTitleBarAssert.CloseHoverStaysInsideTheRoundedCorner(view);
            }
            finally
            {
                view.Close();
            }
        });
    }

    private static bool Visible(Window view, string text) =>
        view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

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
