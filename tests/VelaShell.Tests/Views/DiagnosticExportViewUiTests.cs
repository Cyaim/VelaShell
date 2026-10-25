using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Localization;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Diagnostics;
using VelaShell.Localization;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 导出诊断包之前的勾选框:可选的几类日志默认一个都不勾,每一项写明带着什么;确定交回勾上的那几类,取消交回 null;
/// 外壳与别的对话框同一规格。
/// </summary>
[TestClass]
[TestCategory("SettingsUi")]
public sealed class DiagnosticExportViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DiagnosticExportViewUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    private static readonly DiagnosticLogOption[] Options =
    [
        new("transfer", DiagnosticLogKind.Transfer, null, ["a", "b"], 3 * 1024),
        new("remote-edit", DiagnosticLogKind.RemoteEdit, null, ["c"], 10),
        new("vcxsrv", DiagnosticLogKind.VcXsrv, null, ["d"], 10),
        new("plugin:velashell.ai", DiagnosticLogKind.Plugin, "velashell.ai", ["e"], 10)
    ];

    [TestMethod]
    public void Options_StartUnticked_AndOnlyTickedOnesAreSelected()
    {
        var vm = new DiagnosticExportViewModel(Options);

        Assert.IsTrue(vm.Items.All(item => !item.IsChecked), "默认一个都不勾");
        Assert.IsEmpty(vm.SelectedOptions, "一类都不勾也能导出:默认那两类总在");
        Assert.AreSequenceEqual(
            [Strings.Get("DiagBundle_OptionTransfer"), Strings.Get("DiagBundle_OptionRemoteEdit"), Strings.Get("DiagBundle_OptionVcXsrv"),
                Strings.Format("DiagBundle_OptionPlugin", "velashell.ai")],
            [.. vm.Items.Select(item => item.Label)]);
        Assert.AreEqual(Strings.Get("DiagBundle_OptionTransferHint"), vm.Items[0].Hint);
        Assert.AreEqual(Strings.Format("DiagBundle_OptionSize", 2, TransferItemViewModel.FormatBytes(3 * 1024)), vm.Items[0].Size);

        vm.Items[3].IsChecked = true;
        vm.Items[0].IsChecked = true;

        Assert.AreSequenceEqual(["transfer", "plugin:velashell.ai"], [.. vm.SelectedOptions.Select(option => option.Id)], "按界面顺序");
    }

    [TestMethod]
    public Task TheDialog_FollowsTheDialogTitleBarSpec_AndListsEachOptionWithWhatItCarries() => _session.RunOnUiAsync(() =>
    {
        var view = new DiagnosticExportView { DataContext = new DiagnosticExportViewModel(Options) };
        view.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            DialogTitleBarAssert.FollowsSpec(view);
            Assert.AreEqual(Strings.Get("DiagBundle_PickTitle"), view.Title);
            CheckBox[] boxes = [.. view.GetVisualDescendants().OfType<CheckBox>()];
            Assert.HasCount(4, boxes);
            Assert.IsTrue(boxes.All(box => box.IsChecked == false));
            Assert.IsTrue(Visible(view, Strings.Get("DiagBundle_OptionRemoteEditHint")), "每一项下面写明它带着什么");
            Assert.IsTrue(Visible(view, Strings.Get("DiagBundle_PickSummary")));
            Assert.IsFalse(view.FindControl<TextBlock>("NoOptionsText")!.IsVisible);
        }
        finally
        {
            view.Close();
        }
    });

    [TestMethod]
    public Task NoOptions_SaysSoInsteadOfShowingAnEmptyList() => _session.RunOnUiAsync(() =>
    {
        var view = new DiagnosticExportView { DataContext = new DiagnosticExportViewModel([]) };
        view.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.IsTrue(view.FindControl<TextBlock>("NoOptionsText")!.IsEffectivelyVisible);
            Assert.IsFalse(view.FindControl<ItemsControl>("OptionList")!.IsEffectivelyVisible);
            Assert.IsTrue(view.FindControl<Button>("ConfirmButton")!.IsEnabled, "照样能导出默认那两类");
        }
        finally
        {
            view.Close();
        }
    });

    [TestMethod]
    public Task Confirm_ReturnsTheTickedOptions_AndCancelReturnsNull() => _session.RunOnUiAsync(async () =>
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var vm = new DiagnosticExportViewModel(Options);
            var view = new DiagnosticExportView { DataContext = vm };
            Task<IReadOnlyList<DiagnosticLogOption>?> shown = view.ShowDialog<IReadOnlyList<DiagnosticLogOption>?>(owner);
            Dispatcher.UIThread.RunJobs();
            vm.Items[1].IsChecked = true;
            view.FindControl<Button>("ConfirmButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            IReadOnlyList<DiagnosticLogOption>? picked = await shown.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(picked);
            Assert.AreSequenceEqual(["remote-edit"], [.. picked.Select(option => option.Id)]);

            var cancelled = new DiagnosticExportView { DataContext = new DiagnosticExportViewModel(Options) };
            Task<IReadOnlyList<DiagnosticLogOption>?> second = cancelled.ShowDialog<IReadOnlyList<DiagnosticLogOption>?>(owner);
            Dispatcher.UIThread.RunJobs();
            cancelled.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.IsNull(await second.WaitAsync(TimeSpan.FromSeconds(10)), "取消就不导出");
        }
        finally
        {
            owner.Close();
        }
    });

    [TestMethod]
    public Task CloseHover_StaysInsideTheRoundedCorner() => _session.RunOnUiAsync(() =>
    {
        var view = new DiagnosticExportView { DataContext = new DiagnosticExportViewModel(Options) };
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

    private static bool Visible(Window view, string text) =>
        view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);
}
