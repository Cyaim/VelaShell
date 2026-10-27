using VelaShell.Core.Layouts;
using VelaShell.Core.Models;
using VelaShell.Services;
using VelaShell.Services.Layouts;

namespace VelaShell.Tests.Services;

/// <summary>
/// 还原器的映射:模板里的标签类型 + 配置此刻的连接类型 → 宿主的哪一条打开路径。
/// </summary>
[TestClass]
[TestCategory("Docking")]
public sealed class LayoutDocumentOpenerTests
{
    private readonly List<string> _calls = [];

    private LayoutDocumentOpener Opener() => new(
        _ => Task.FromResult<SessionProfile?>(null),
        _ => null,
        new LayoutOpenActions(
            p => Record("connect", p),
            p => Record("sftp", p),
            p => Record("ftp", p),
            p => Record("pluginFiles", p),
            p => Record("workspace", p),
            (l, r) => Record($"dual:{r.Name}", l),
            shell => Record($"shell:{shell.Id}", null)));

    private Task Record(string path, SessionProfile? profile)
    {
        _calls.Add(profile is null ? path : $"{path}:{profile.Name}");
        return Task.CompletedTask;
    }

    private static SessionProfile Profile(string name, ConnectionType type) =>
        new() { Name = name, Host = $"{name}.example", ConnectionType = type };

    [TestMethod]
    [DataRow(LayoutDocumentKind.Terminal, ConnectionType.SSH, "connect:p")]
    [DataRow(LayoutDocumentKind.Terminal, ConnectionType.Plugin, "connect:p")]
    [DataRow(LayoutDocumentKind.Files, ConnectionType.SSH, "sftp:p")]
    [DataRow(LayoutDocumentKind.Files, ConnectionType.SFTP, "sftp:p")]
    [DataRow(LayoutDocumentKind.Files, ConnectionType.FTP, "ftp:p")]
    [DataRow(LayoutDocumentKind.Files, ConnectionType.Plugin, "pluginFiles:p")]
    [DataRow(LayoutDocumentKind.Workspace, ConnectionType.Plugin, "workspace:p")]
    // 保存之后配置的类型被改过:按配置现在的样子走默认打开,而不是硬按旧路径开出一张失败卡片。
    [DataRow(LayoutDocumentKind.Workspace, ConnectionType.SSH, "connect:p")]
    [DataRow("hologram", ConnectionType.SSH, "connect:p")]
    public async Task Open_MapsKindAndConnectionTypeToTheHostPath(string kind, ConnectionType type, string expected)
    {
        await Opener().OpenAsync(new(kind, Profile("p", type)));

        Assert.AreSequenceEqual([expected], [.. _calls]);
    }

    [TestMethod]
    public async Task Open_DualFilesAndLocalShell()
    {
        LayoutDocumentOpener opener = Opener();

        await opener.OpenAsync(new(LayoutDocumentKind.DualFiles, Profile("l", ConnectionType.SFTP), Profile("r", ConnectionType.FTP)));
        await opener.OpenAsync(new(LayoutDocumentKind.LocalShell, Shell: new LocalShellInfo("wsl", "WSL", "wsl.exe")));
        // 缺了要用的那一半:什么都不开(解析那一步本该已经把它当缺失跳过)。
        await opener.OpenAsync(new(LayoutDocumentKind.LocalShell));
        await opener.OpenAsync(new(LayoutDocumentKind.Terminal));

        Assert.AreSequenceEqual(["dual:r:l", "shell:wsl"], [.. _calls]);
    }
}
