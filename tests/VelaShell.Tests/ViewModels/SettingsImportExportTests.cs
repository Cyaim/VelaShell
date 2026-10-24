using System.Text.Json;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 设置的导入导出。导出文件的用途决定了它一定会离开本机(备份、换机、贴给别人排查),
/// 所以这里守三件事:秘密不能跟着走,导入只动勾上的分类,以及每一次导入导出都要有可见的结果。
/// 文件格式本身的细节在 Core 的 <c>SettingsTransferTests</c>。
/// </summary>
[TestClass]
[TestCategory("Settings")]
public sealed class SettingsImportExportTests
{
    private const string SecretProxyPassword = "hunter2-should-never-leave-this-machine";

    private static readonly string[] AllIds = [.. SettingsCategories.All.Select(c => c.Id)];

    /// <summary>导出里不能出现代理密码,勾了代理也一样。</summary>
    /// <remarks>
    /// 旧实现直接序列化整份 AppSettings。文件看上去只是一堆设置,用户不会想到里面有凭据,
    /// 于是它被随手发到工单、同步到网盘、提交进仓库。
    /// </remarks>
    [TestMethod]
    public async Task Export_DoesNotCarryTheProxyPassword()
    {
        SettingsViewModel viewModel = await CreateAsync(ConfiguredProxy());

        string json = viewModel.BuildExportJson(AllIds);

        Assert.DoesNotContain(SecretProxyPassword, json, "代理密码被写进导出文件了。");
    }

    /// <summary>脱敏只作用于导出的副本,不能顺手把用户当前配好的密码清掉。</summary>
    [TestMethod]
    public async Task Export_LeavesTheLiveSettingsAlone()
    {
        SettingsViewModel viewModel = await CreateAsync(ConfiguredProxy());

        viewModel.BuildExportJson(AllIds);

        Assert.AreEqual(SecretProxyPassword, viewModel.Proxy.Password, "导出把内存里的密码也抹了。");
    }

    /// <summary>导出的是设置页上此刻的样子:没保存的根级字段(主题、字号)也算。</summary>
    [TestMethod]
    public async Task Export_TakesWhatTheSettingsPageShowsNow()
    {
        SettingsViewModel viewModel = await CreateAsync(new AppSettings());
        viewModel.TerminalFontSize = 21;
        viewModel.General.ConnectTimeoutSeconds = 42;

        AppSettings exported = JsonSerializer.Deserialize<AppSettings>(
            viewModel.BuildExportJson([SettingsCategories.Terminal, SettingsCategories.General]), SettingsTransfer.Options)!;

        Assert.AreEqual(21, exported.TerminalFontSize, "以前根级字段导出的是上次载入的值");
        Assert.AreEqual(42, exported.General.ConnectTimeoutSeconds);
    }

    /// <summary>非秘密设置要能原样往返;没勾的分类不动。</summary>
    [TestMethod]
    public async Task Export_ThenImport_RoundTripsTheTickedCategories()
    {
        SettingsViewModel source = await CreateAsync(ConfiguredProxy());
        source.General.ConnectTimeoutSeconds = 42;
        string json = source.BuildExportJson(AllIds);

        var targetInitial = new AppSettings();
        targetInitial.General.ConnectTimeoutSeconds = 7;
        SettingsViewModel target = await CreateAsync(targetInitial);
        SettingsImportFile file = SettingsViewModel.ParseImport(json)!;
        SettingsImportOutcome outcome = target.ApplyImport(file, [SettingsCategories.Proxy]);

        Assert.IsTrue(outcome.ProxyPasswordMissing, "代理换了台机器、本机没配过:密码要重填");
        Assert.AreEqual("proxy.example.com", target.Proxy.Host);
        Assert.AreEqual(7, target.General.ConnectTimeoutSeconds, "常规没勾,不该被文件覆盖");
    }

    /// <summary>导入成功的回显要说清导入了哪几类、哪些机密要补填。</summary>
    [TestMethod]
    public async Task Import_TellsWhichCategoriesWereImported_AndWhatToRefill()
    {
        SettingsViewModel viewModel = await CreateAsync(new AppSettings());
        SettingsImportFile file = SettingsViewModel.ParseImport(JsonSerializer.Serialize(new
        {
            theme = "light",
            proxy = new { type = "socks5", host = "h", port = 1080, username = "u", password = "" }
        }))!;

        SettingsImportOutcome outcome = viewModel.ApplyImport(file, AllIds);

        Assert.AreEqual(
            Strings.Format("SetGeneral_ImportDoneNeedsSecrets",
                SettingsCategories.JoinLabels([SettingsCategories.Find("appearance")!, SettingsCategories.Find("proxy")!]),
                Strings.Get("SetGeneral_SecretProxyPassword")),
            outcome.Describe());
        Assert.AreEqual("light", viewModel.Theme);
    }

    /// <summary>不需要认证的代理不该冒出"去补密码"的提示。</summary>
    [TestMethod]
    public async Task Import_WithoutProxyAuth_DoesNotAskForSecrets()
    {
        SettingsViewModel viewModel = await CreateAsync(new AppSettings());
        SettingsImportFile file = SettingsViewModel.ParseImport(JsonSerializer.Serialize(new { proxy = new { type = "none" } }))!;

        SettingsImportOutcome outcome = viewModel.ApplyImport(file, AllIds);

        Assert.IsFalse(outcome.NeedsSecrets);
        Assert.AreEqual(Strings.Format("SetGeneral_ImportDone", Strings.Get("SetVm_SectionProxy")), outcome.Describe());
    }

    /// <summary>
    /// 非法内容要报失败,而且不能动现有设置。
    /// </summary>
    /// <remarks>
    /// 调用方以前把返回值整个丢掉,选错文件时界面毫无反应 —— 用户只能靠猜。
    /// </remarks>
    [TestMethod]
    public async Task Import_OfSomethingThatIsNotSettings_FailsAndChangesNothing()
    {
        SettingsViewModel viewModel = await CreateAsync(ConfiguredProxy());

        Assert.IsNull(SettingsViewModel.ParseImport("{ not json"));
        Assert.IsNull(SettingsViewModel.ParseImport("null"));
        Assert.IsNull(SettingsViewModel.ParseImport("{}"));
        Assert.AreEqual(SecretProxyPassword, viewModel.Proxy.Password, "导入失败却改动了现有设置。");
    }

    private static AppSettings ConfiguredProxy()
    {
        var settings = new AppSettings();
        settings.Proxy.Type = "socks5";
        settings.Proxy.Host = "proxy.example.com";
        settings.Proxy.Port = 1080;
        settings.Proxy.Username = "vela";
        settings.Proxy.Password = SecretProxyPassword;
        return settings;
    }

    private static async Task<SettingsViewModel> CreateAsync(AppSettings initial)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_ => Task.FromResult(initial));
        var viewModel = new SettingsViewModel(settings, new ThemeService(initial.Theme));
        await viewModel.LoadCommand.Execute().FirstAsync();
        return viewModel;
    }
}
