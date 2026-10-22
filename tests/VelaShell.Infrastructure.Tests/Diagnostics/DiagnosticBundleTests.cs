using System.IO.Compression;
using System.Text;
using VelaShell.Infrastructure.Diagnostics;

namespace VelaShell.Infrastructure.Tests.Diagnostics;

/// <summary>
/// 诊断包:收齐日志与概况,落盘前脱敏;主机、用户名、本机路径、IP、邮箱与口令不出现在包里。
/// </summary>
[TestClass]
public sealed class DiagnosticBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"velashell_diagbundle_{Guid.NewGuid():N}");
    private string Logs => Path.Combine(_root, "logs");

    public DiagnosticBundleTests() => Directory.CreateDirectory(Logs);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public void Redactor_ReplacesKnownHostsUsersAndPathsConsistentlyAndRespectsWordBoundaries()
    {
        var redactor = new DiagnosticRedactor(
            hosts: ["db1.prod.example.com", "10.20.30.40", "[2001:db8::7]", "localhost"],
            userNames: ["deploy", "root", "ab"],
            homeDirectory: @"C:\Users\Alice", localUserName: "Alice", machineName: "ALICE-PC");

        string text = redactor.Redact(
            "[SSH] deploy@db1.prod.example.com:22 连接结束\n"
            + "[SSH] deploy@10.20.30.40:22 again db1.prod.example.com\n"
            + @"open C:\Users\Alice\.velashell\logs and C:/Users/Alice/x on ALICE-PC as alice" + "\n"
            + "ipv6 2001:db8::7 root ab deployer redeploy 10.20.30.400 db1.prod.example.community\n");

        Assert.Contains("[SSH] <user-1>@<host-1>:22 连接结束", text);
        Assert.Contains("[SSH] <user-1>@<host-2>:22 again <host-1>", text, "同一个值换成同一个占位符");
        Assert.Contains(@"open ~\.velashell\logs and ~/x on <machine> as <local-user>", text);
        Assert.Contains("ipv6 <host-3> root ab deployer redeploy", text,
            "常见登录名与两个字符以内的值不换;词的一部分不换");
        Assert.DoesNotContain("10.20.30.40:", text);
        Assert.Contains("10.20.30.400", text, "不是合法 IPv4 的数字串不动");
        Assert.Contains("db1.prod.example.community", text, "更长的域名不是同一台机器");
        Assert.AreEqual(4, redactor.Counts["host"], "四处主机,其中一处是 IPv6");
    }

    [TestMethod]
    public void Redactor_ScrubsUnknownIpsEmailsAndSecretsButKeepsLoopback()
    {
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        string text = redactor.Redact(
            "connect 203.0.113.9 then 203.0.113.9 and 198.51.100.1, local 127.0.0.1 any 0.0.0.0\n"
            + "mail ops.team+alerts@corp.example.org\n"
            + "proxy password=hunter2; token: abc.def Authorization: Bearer sk-live-123== apikey=xyz\n");
        Assert.Contains("connect <ip-1> then <ip-1> and <ip-2>, local 127.0.0.1 any 0.0.0.0", text);
        Assert.Contains("mail <email>", text);
        Assert.Contains("password=<redacted>; token: <redacted>", text);
        Assert.Contains("Bearer <redacted>", text);
        Assert.Contains("apikey=<redacted>", text);
        foreach (string secret in new[] { "hunter2", "abc.def", "sk-live-123", "xyz", "corp.example.org" })
        {
            Assert.DoesNotContain(secret, text);
        }
    }

    [TestMethod]
    public async Task Bundle_CollectsRedactedLogsCrashesAndSummaryWithoutLeavingPartialFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(Logs, "velashell-20261019.log"), "[SSH] deploy@db1.example.net:22 连接结束\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "crash-20261019-101010-000.txt"), "at C:\\Users\\Alice\\app\nIP 203.0.113.9\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "notes.md"), "not a log");
        // 日志正被监听器追加写入时也要读得到
        await using var held = new FileStream(Path.Combine(Logs, "velashell-20261020.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await held.WriteAsync(Encoding.UTF8.GetBytes("live line from deploy\n"));
        await held.FlushAsync();

        var redactor = new DiagnosticRedactor(["db1.example.net"], ["deploy"], @"C:\Users\Alice", "Alice", "ALICE-PC");
        var request = new DiagnosticBundleRequest("v1.7.2", Logs,
            [new DiagnosticPluginInfo("velashell.ai", "2.1.0", "Running", "Isolated", false, @"failed at C:\Users\Alice\plugins")],
            redactor);
        string zipPath = Path.Combine(_root, "bundle.zip");

        DiagnosticBundleResult result = await DiagnosticBundle.WriteAsync(zipPath, request);

        Assert.AreEqual(3, result.LogFiles);
        Assert.IsFalse(File.Exists(zipPath + ".partial"));
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        CollectionAssert.AreEquivalent(
            new[] { "logs/velashell-20261019.log", "logs/velashell-20261020.log", "logs/crash-20261019-101010-000.txt", "summary.txt", "README.txt" },
            zip.Entries.Select(entry => entry.FullName).ToArray());
        string all = string.Join('\n', zip.Entries.Select(Read));
        foreach (string secret in new[] { "deploy", "db1.example.net", "Alice", "203.0.113.9" })
        {
            Assert.DoesNotContain(secret, all, $"包里不能出现 {secret}");
        }
        string summary = Read(zip.GetEntry("summary.txt")!);
        Assert.Contains("VelaShell: v1.7.2", summary);
        Assert.Contains("velashell.ai 2.1.0 state=Running mode=Isolated error=failed at ~\\plugins", summary);
        string readMe = Read(zip.GetEntry("README.txt")!);
        Assert.Contains("logs/: 3 file(s)", readMe);
        Assert.Contains("host: 1", readMe);
        Assert.Contains("ip: 1", readMe);
    }

    [TestMethod]
    public async Task Bundle_StopsCollectingOlderLogsPastTheSizeCapAndFailsCleanly()
    {
        string newest = Path.Combine(Logs, "velashell-new.log");
        string oldest = Path.Combine(Logs, "velashell-old.log");
        await File.WriteAllTextAsync(oldest, "old");
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-2));
        await using (FileStream big = File.Create(newest))
        {
            big.SetLength(DiagnosticBundle.MaxLogBytes - 1);
        }
        Assert.AreSequenceEqual([newest], [.. DiagnosticBundle.LogFiles(Logs).Select(file => file.FullName)],
            "从新到旧收,超过总量上限的旧文件不收");

        string zipPath = Path.Combine(_root, "missing-dir", "bundle.zip");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => DiagnosticBundle.WriteAsync(zipPath,
            new DiagnosticBundleRequest("v1", Logs, [], new DiagnosticRedactor([], [], null, null, null))));
        Assert.IsFalse(File.Exists(zipPath + ".partial"));
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
