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
        Assert.Contains("[SSH] <user-1>@<host-1>:22 连接结束", log);
        Assert.Contains("[SSH] <user-2>@<host-2>:2222 连接结束", log);
        Assert.Contains("shared <user-3>", log);
        await shared.DidNotReceive().GetAllAsync();
    }

    [TestMethod]
    public async Task OptionalLogs_FollowTheTransferLogSettingAndOnlyTickedOnesAreExported()
    {
        // 传输记录按「文件传输 → 日志目录」去找(那里可以改到日志目录之外);插件日志按插件列出;只有勾上的进包
        string logs = Path.Combine(_root, "logs");
        string transfers = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(transfers);
        Directory.CreateDirectory(Path.Combine(logs, "plugins"));
        await File.WriteAllTextAsync(Path.Combine(logs, "velashell-20261019.log"), "[Startup] VelaShell\n");
        await File.WriteAllTextAsync(Path.Combine(logs, "transfer-20261019.log"), "stale copy in the default folder\n");
        await File.WriteAllTextAsync(Path.Combine(transfers, "transfer-20261019.log"), "UPLOAD\t/home/deployer7/a.txt\n");
        await File.WriteAllTextAsync(Path.Combine(logs, "remote-edit.log"), "[open] /home/deployer7/.env\n");
        await File.WriteAllTextAsync(Path.Combine(logs, "plugins", "velashell.ai.log"), "[Plugin:velashell.ai] ready\n");
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_ => Task.FromResult(new AppSettings()));
        var viewModel = new SettingsViewModel(settings, new ThemeService(new AppSettings().Theme));
        viewModel.Transfer.LogDirectory = transfers;

        IReadOnlyList<DiagnosticLogOption> options = await viewModel.FindDiagnosticLogOptionsAsync(logs);

        Assert.AreSequenceEqual(["transfer", "remote-edit", "plugin:velashell.ai"], [.. options.Select(option => option.Id)]);
        Assert.AreSequenceEqual([Path.Combine(transfers, "transfer-20261019.log")], options[0].Files);

        string zipPath = Path.Combine(_root, "bundle.zip");
        DiagnosticBundleResult result = await viewModel.ExportDiagnosticsAsync(zipPath, logs,
            [.. options.Where(option => option.Id != "remote-edit")]);

        Assert.AreEqual(3, result.LogFiles);
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        Assert.AreSequenceEqual(
            ["logs/velashell-20261019.log", "logs/transfer-20261019.log", "logs/plugins/velashell.ai.log", "summary.txt", "README.txt"], [.. zip.Entries.Select(entry => entry.FullName)], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        using var reader = new StreamReader(zip.GetEntry("logs/transfer-20261019.log")!.Open());
        Assert.AreEqual("UPLOAD\t/home/<user-1>/a.txt\n", await reader.ReadToEndAsync(), "收的是设置里那个目录的那一份,照样遮盖");
    }
}
