using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Headless;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Tests.TestSupport;
using VelaShell.Views;
using VelaShell.ViewModels;

namespace VelaShell.Tests.Views;

/// <summary>
/// 授权对话框的字段填充回归:曾因手写 InitializeComponent 覆盖编译器生成的
/// 字段填充,导致 x:Name 控件(TitleText 等)为 null,构造即抛 NRE
/// (宿主侧被 RPC 折叠成错误码回传插件,表现为终端回写命令报"对象引用为 null")。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class PluginPermissionDialogTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PluginPermissionDialogTests).Assembly);

    [TestMethod]
    public Task UploadPermission_OnlyOffersCurrentTask_WithoutDefaultApproval() =>
        _session.RunOnUiAsync(() =>
        {
            var dialog = new PluginPermissionDialog();
            try
            {
                dialog.ConfigureUploadOnly();
                Button once = dialog.FindControl<Button>("OnceButton")!;
                Button session = dialog.FindControl<Button>("SessionButton")!;
                Button always = dialog.FindControl<Button>("AlwaysButton")!;
                Button deny = dialog.FindControl<Button>("DenyButton")!;
                Assert.IsTrue(once.IsVisible);
                Assert.AreEqual(Strings.Get("PluginPermUpload_AllowOnce"), once.Content);
                Assert.IsFalse(session.IsVisible);
                Assert.IsFalse(always.IsVisible);
                Assert.IsFalse(once.IsDefault);
                Assert.IsFalse(session.IsDefault);
                Assert.IsFalse(always.IsDefault);
                Assert.IsTrue(deny.IsCancel);
            }
            finally
            {
                dialog.Close();
            }
        });

    [TestMethod]
    public Task UploadPermission_AlreadyCancelled_DeniesWithoutShowingDialog() =>
        _session.RunOnUiAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            PluginPermissionDecision decision = await PluginPermissionDialog.AskFileUploadAsync(
                "acme.plugin", "prod-1", "C:/source.bin", "/target.bin", false, cancellation.Token);
            Assert.AreEqual(PluginPermissionDecision.Deny, decision);
        });

    [TestMethod]
    public Task UploadPermission_WithoutDesktopOwner_Denies() =>
        _session.RunOnUiAsync(async () =>
        {
            var lifetime = Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            Window? previous = lifetime?.MainWindow;
            try
            {
                if (lifetime is not null) lifetime.MainWindow = null;
                PluginPermissionDecision decision = await PluginPermissionDialog.AskFileUploadAsync(
                    "acme.plugin", "prod-1", "C:/source.bin", "/target.bin", false);
                Assert.AreEqual(PluginPermissionDecision.Deny, decision);
            }
            finally { if (lifetime is not null) lifetime.MainWindow = previous; }
        });

    [TestMethod]
    public void UploadPermission_FieldsEscapeControlAndBidiWithoutChangingOrdinaryUnicode()
    {
        string path = "/tmp/秘密\nRemote destination: harmless\u202Etxt.exe\u2069\t\"\\";
        string preview = PluginPermissionDialog.EscapeUploadField(path);
        Assert.AreEqual("\"/tmp/秘密\\u000ARemote destination: harmless\\u202Etxt.exe\\u2069\\u0009\\\"\\\\\"", preview);
        Assert.IsFalse(preview.Contains('\n'));
        Assert.IsFalse(preview.Contains('\u202E'));
        Assert.AreEqual("\"" + new string('x', 10_000) + "\"",
            PluginPermissionDialog.EscapeUploadField(new string('x', 10_000)), "完整路径不应被截断");
    }

    [TestMethod]
    public Task UploadPermission_LongPreviewIsScrollableAndSelectable() =>
        _session.RunOnUiAsync(() =>
        {
            var dialog = new PluginPermissionDialog();
            try
            {
                dialog.ConfigureUploadOnly();
                var preview = dialog.FindControl<SelectableTextBlock>("PreviewText")!;
                var scroll = dialog.FindControl<ScrollViewer>("PreviewScroll")!;
                preview.Text = new string('x', 10_000);
                Assert.AreEqual(TextWrapping.NoWrap, preview.TextWrapping);
                Assert.AreEqual(240d, scroll.MaxHeight);
                Assert.AreEqual(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
                Assert.AreEqual(10_000, preview.Text.Length);
                Assert.AreEqual(120d, dialog.FindControl<ScrollViewer>("MessageScroll")!.MaxHeight);
            }
            finally { dialog.Close(); }
        });

    [TestMethod]
    public void PluginPermission_RevokeVisibilityNotifiesExistingRow()
    {
        var row = new PluginRowViewModel(new PluginDescriptor { Directory = "plugin" }, false);
        int changes = 0;
        row.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(row.HasTerminalGrant)) changes++; };
        row.HasTerminalGrant = true;
        Assert.IsTrue(row.HasTerminalGrant);
        row.HasTerminalGrant = false;
        Assert.IsFalse(row.HasTerminalGrant);
        Assert.AreEqual(2, changes, "已显示的行应更新撤销入口，不依赖重建管理页");
    }

    [TestMethod]
    public void ParameterizedConstructor_PopulatesNamedFields_AndDoesNotThrow()
    {
        _session.Dispatch(() =>
        {
            // 私有的带参构造会给 TitleText/MessageText/… 赋文本;字段没被 XAML 填充就 NRE。
            ConstructorInfo ctor = typeof(PluginPermissionDialog).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                [typeof(string), typeof(string), typeof(string), typeof(string)], null)!;
            object dialog = ctor.Invoke(["Terminal write request", "acme.plugin → prod-1", "echo hi", "Icon.terminal"]);

            // 命名控件应已由编译器生成的填充逻辑绑定(非 null),且带上了文案。
            var title = (TextBlock)typeof(PluginPermissionDialog)
                .GetField("TitleText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
            Assert.IsNotNull(title);
            Assert.IsFalse(string.IsNullOrEmpty(title.Text));

            // 开会话那一版换的是图标 —— 它也是个 x:Name 控件,同样会因填充缺失而 NRE。
            Assert.IsNotNull(typeof(PluginPermissionDialog)
                .GetField("HeaderIcon", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog));

            ((Window)dialog).Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
