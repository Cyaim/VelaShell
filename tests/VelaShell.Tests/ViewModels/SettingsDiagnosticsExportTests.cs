using System.IO.Compression;
using NSubstitute;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Infrastructure.Diagnostics;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 关于页「导出诊断包」:脱敏要认得出用户自己的机器 —— 已保存连接、最近连接与共享凭据里的主机和登录名都换成占位符。
/// </summary>
[TestClass]
[TestCategory("Settings")]
public sealed class SettingsDiagnosticsExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"velashell_diagexport_{Guid.NewGuid():N}");

    public SettingsDiagnosticsExportTests() => Directory.CreateDirectory(Path.Combine(_root, "logs"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task Export_MasksHostsAndUsersFromSavedRecentAndSharedCredentials()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "logs", "velashell-20261019.log"),
            "[SSH] deployer7@db7.corp.example:22 连接结束\n[SSH] quick-user@jump.corp.example:2222 连接结束\nshared svc-backup\n");
        ISessionRepository sessions = Substitute.For<ISessionRepository>();
        sessions.GetAllSessionsAsync().Returns([new SessionProfile { Host = "db7.corp.example", Username = "deployer7" }]);
        IRecentConnectionService recent = Substitute.For<IRecentConnectionService>();
        recent.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new RecentConnectionEntry { Host = "jump.corp.example", Username = "quick-user" }]);
        ISharedCredentialRepository shared = Substitute.For<ISharedCredentialRepository>();
        shared.ListMetadataAsync().Returns([new SharedCredentialMetadata(Guid.NewGuid(), "backup", "svc-backup", AuthMethod.Password, true)]);
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_ => Task.FromResult(new AppSettings()));
        var viewModel = new SettingsViewModel(settings, new ThemeService(new AppSettings().Theme), recentConnections: recent,
            sharedCredentials: shared, sessionRepository: sessions);
        string zipPath = Path.Combine(_root, "bundle.zip");

        DiagnosticBundleResult result = await viewModel.ExportDiagnosticsAsync(zipPath, Path.Combine(_root, "logs"));

        Assert.AreEqual(1, result.LogFiles);
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry("logs/velashell-20261019.log")!.Open());
        string log = await reader.ReadToEndAsync();
        foreach (string value in new[] { "db7.corp.example", "deployer7", "jump.corp.example", "quick-user", "svc-backup" })
        {
            Assert.DoesNotContain(value, log, $"包里不能出现 {value}");
        }
        StringAssert.Contains(log, "[SSH] <user-1>@<host-1>:22 连接结束");
        StringAssert.Contains(log, "[SSH] <user-2>@<host-2>:2222 连接结束");
        StringAssert.Contains(log, "shared <user-3>");
        await shared.DidNotReceive().GetAllAsync();
    }
}
