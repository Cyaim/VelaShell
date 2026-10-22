using System.Diagnostics;
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
    public void Redactor_MasksSecretsWhoseNameContainsTheKeywordQuotedJsonBasicPemAndPrefixedTokens()
    {
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        string text = redactor.Redact(
            "env DB_PASSWORD=v-db MYSQL_ROOT_PASSWORD=v-root GITHUB_TOKEN=v-gh SECRET_KEY=v-sk AWS_SECRET_ACCESS_KEY=v-aws\n"
            + "oauth access_token=v-at refresh_token=v-rt client_secret=v-cs\n"
            + "conf password=\"v q\" passphrase='v-pp' {\"password\": \"v-json\", \"api_key\":\"v-jkey\"}\n"
            + "http Authorization: Basic dXNlcjpwYXNzd29yZA== proxy {\"Proxy-Authorization\": \"basic YWRtaW46czNjcjN0\"}\n"
            + "key -----BEGIN OPENSSH PRIVATE KEY-----\r\nb3BlbnNzaC1rZXktdjEAAAAA\r\nQyNTUxOQAAACD\r\n-----END OPENSSH PRIVATE KEY-----\n"
            + "aws AKIAIOSFODNN7EXAMPLE gh ghp_0123456789abcdefghijABCDEFGHIJ0123 slack xoxb-1234567890-abcdefghij openai sk-proj-ABCDEFGHIJKLMNOPQRST\n");

        Assert.Contains("env DB_PASSWORD=<redacted> MYSQL_ROOT_PASSWORD=<redacted> GITHUB_TOKEN=<redacted> SECRET_KEY=<redacted> AWS_SECRET_ACCESS_KEY=<redacted>\n", text);
        Assert.Contains("oauth access_token=<redacted> refresh_token=<redacted> client_secret=<redacted>\n", text);
        Assert.Contains("conf password=\"<redacted>\" passphrase='<redacted>' {\"password\": \"<redacted>\", \"api_key\":\"<redacted>\"}\n", text,
            "引号留着,JSON 还是 JSON");
        Assert.Contains("http Authorization: Basic <redacted> proxy {\"Proxy-Authorization\": \"basic <redacted>\"}\n", text);
        Assert.Contains("key -----BEGIN OPENSSH PRIVATE KEY-----\r\n<redacted>\r\n-----END OPENSSH PRIVATE KEY-----\n", text,
            "私钥正文整段换掉,首尾两行留着");
        Assert.Contains("aws <redacted> gh <redacted> slack <redacted> openai <redacted>\n", text);
        foreach (string secret in new[]
                 {
                     "v-db", "v-root", "v-gh", "v-sk", "v-aws", "v-at", "v-rt", "v-cs", "v q", "v-pp", "v-json", "v-jkey",
                     "dXNlcjpwYXNzd29yZA", "YWRtaW46czNjcjN0", "b3BlbnNzaC1rZXktdjEAAAAA", "QyNTUxOQAAACD",
                     "AKIAIOSFODNN7EXAMPLE", "ghp_", "xoxb-", "sk-proj"
                 })
        {
            Assert.DoesNotContain(secret, text);
        }
        Assert.AreEqual(19, redactor.Counts["secret"], "每处只算一次");
    }

    [TestMethod]
    public void Redactor_LeavesOrdinaryWordsAndPublicValuesAlone()
    {
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        const string text =
            "password authentication failed; Token refresh scheduled; secretary tokenizer passwordless keyboard-interactive\n"
            + "Basic authentication is disabled; basic settings saved\n"
            + "Could not load file or assembly 'Avalonia.Base, Culture=neutral, PublicKeyToken=c8d06bc3ff0f8fa3'\n"
            + "Password: \n";

        Assert.AreEqual(text, redactor.Redact(text));
        Assert.IsEmpty(redactor.Counts);
    }

    [TestMethod]
    public void Redactor_StaysLinearOnLongRunsOfKeyLikeCharacters()
    {
        // 一长串全是「名字」里允许的字符、处处是关键字、末尾却没有分隔符:回溯式的写法在这里是平方乃至立方级
        static string Run(int length) => string.Concat(Enumerable.Repeat("password_token.", (length / 15) + 1))[..length];
        static TimeSpan Fastest(string text)
        {
            TimeSpan best = TimeSpan.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                long start = Stopwatch.GetTimestamp();
                _ = new DiagnosticRedactor([], [], null, null, null).Redact(text);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
                if (elapsed < best) best = elapsed;
            }
            return best;
        }

        TimeSpan small = Fastest(Run(16 * 1024));
        TimeSpan large = Fastest(Run(64 * 1024));

        Assert.IsLessThan(TimeSpan.FromMilliseconds(500), small, $"16 KB 用了 {small}");
        Assert.IsLessThan(small * 8 + TimeSpan.FromMilliseconds(50), large, $"16 KB 用了 {small},64 KB 用了 {large}:应随长度线性增长");
    }

    [TestMethod]
    public void Redactor_MasksIpsEndingASentenceAndIPv6ButNotVersionNumbers()
    {
        var redactor = new DiagnosticRedactor([], [], null, null, null);
        string text = redactor.Redact(
            "Connection closed by 10.0.0.5. Then 10.0.0.6, done.\n"
            + "ipv6 2001:db8::1 and [fe80::1%en0]:22 again 2001:DB8:0:0:0:0:0:1 mapped ::ffff:203.0.113.7.\n"
            + "local ::1 [::1]:22 any :: clock 10:15:30 mac 00:1a:2b:3c:4d:5e\n"
            + "[Startup] VelaShell 0.0.1.0 pid=42\n"
            + "assembly 'Avalonia.Base, Version=12.1.4.0, Culture=neutral' plugin v2.1.0.3\n");

        Assert.Contains("Connection closed by <ip-1>. Then <ip-2>, done.\n", text, "句末的句号不挡住 IP");
        Assert.Contains("ipv6 <ip-4> and [<ip-5>%en0]:22 again <ip-4> mapped ::ffff:<ip-3>.\n", text,
            "同一个 IPv6 地址换种写法也是同一个占位符");
        Assert.Contains("local ::1 [::1]:22 any :: clock 10:15:30 mac 00:1a:2b:3c:4d:5e\n", text, "回环、未指定地址、时刻与 MAC 不动");
        Assert.Contains("[Startup] VelaShell 0.0.1.0 pid=42\n", text, "启动横幅里的程序集版本不是 IP");
        Assert.Contains("assembly 'Avalonia.Base, Version=12.1.4.0, Culture=neutral' plugin v2.1.0.3\n", text);
        Assert.AreEqual(6, redactor.Counts["ip"]);
    }

    [TestMethod]
    public void Redactor_CaseVariantsTheTokenTableCannotLookUpAreStillMasked()
    {
        // 正则的忽略大小写比字典宽:开尔文符号 K(U+212A)在正则里等于 k,OrdinalIgnoreCase 却不认 —— 原先这里抛 KeyNotFoundException
        var redactor = new DiagnosticRedactor(["kube-node1.example.com"], ["kevin"], null, null, null);

        string text = redactor.Redact("join \u212Aube-node1.example.com as \u212Aevin; again kube-node1.example.com as kevin");

        Assert.AreEqual("join <host-1> as <user-1>; again <host-1> as <user-1>", text);
    }

    [TestMethod]
    public async Task Bundle_LeavesSessionTranscriptsOut()
    {
        // 会话日志(session-*.log)是终端原始输出,与诊断日志同在一个目录;隐私说明里它是单独一类,不进诊断包
        await File.WriteAllTextAsync(Path.Combine(Logs, "velashell-20261019.log"), "[Startup] VelaShell\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "crash-20261019-101010-000.txt"), "UnhandledException\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "session-x.log"), "$ cat notes.txt\nprivate notes\n");
        await File.WriteAllTextAsync(Path.Combine(Logs, "session-db1-20261019-101010.log"), "$ psql\n");
        string zipPath = Path.Combine(_root, "bundle.zip");

        DiagnosticBundleResult result = await DiagnosticBundle.WriteAsync(zipPath, EmptyRequest());

        Assert.AreEqual(2, result.LogFiles);
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        CollectionAssert.AreEquivalent(
            new[] { "logs/velashell-20261019.log", "logs/crash-20261019-101010-000.txt", "summary.txt", "README.txt" },
            zip.Entries.Select(entry => entry.FullName).ToArray());
    }

    [TestMethod]
    public async Task LogFiles_SkipsWhatNoLongerFitsAndKeepsCollectingOlderSmallerOnes()
    {
        // 从新到旧收:最新的一份单独就超过上限,跳过它;放不下的跳过,更旧、更小的照收 —— 崩溃记录不能被一份大日志挤掉
        string huge = Path.Combine(Logs, "velashell-huge.log");
        string big = Path.Combine(Logs, "velashell-big.log");
        string middle = Path.Combine(Logs, "velashell-middle.log");
        string crash = Path.Combine(Logs, "crash-20261016-101010-000.txt");
        await using (FileStream file = File.Create(huge)) file.SetLength(DiagnosticBundle.MaxLogBytes + 1);
        await using (FileStream file = File.Create(big)) file.SetLength(DiagnosticBundle.MaxLogBytes - 8);
        await File.WriteAllTextAsync(middle, "0123456789");
        await File.WriteAllTextAsync(crash, "crash");
        File.SetLastWriteTimeUtc(huge, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(big, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(middle, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(crash, DateTime.UtcNow.AddDays(-3));

        Assert.AreSequenceEqual([big, crash], [.. DiagnosticBundle.LogFiles(Logs).Select(file => file.FullName)]);
    }

    [TestMethod]
    public async Task Bundle_FailureAfterTheTemporaryFileExistsLeavesNothingBehind()
    {
        await File.WriteAllTextAsync(Path.Combine(Logs, "velashell-20261019.log"), "line\n");
        // 包的位置是个已有的目录:zip 整个写完、最后一步换名时才失败 —— 那时临时文件确实已经在了
        string zipPath = Path.Combine(_root, "bundle.zip");
        Directory.CreateDirectory(zipPath);

        Exception error = await Assert.ThrowsAsync<Exception>(() => DiagnosticBundle.WriteAsync(zipPath, EmptyRequest()));

        Assert.IsTrue(error is IOException or UnauthorizedAccessException, error.ToString());
        Assert.IsEmpty(Directory.GetFiles(_root, "*.partial"), "临时文件要删掉");
        Assert.IsEmpty(Directory.GetFileSystemEntries(zipPath));

        // 收日志收到一半被取消:同样不留下任何东西
        string cancelledPath = Path.Combine(_root, "cancelled.zip");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => DiagnosticBundle.WriteAsync(cancelledPath, EmptyRequest(), cancelled.Token));

        Assert.IsFalse(File.Exists(cancelledPath));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.partial"), "临时文件要删掉");
    }

    [TestMethod]
    public async Task Bundle_LeavesAFileAtTheOldFixedTemporaryNameAlone()
    {
        await File.WriteAllTextAsync(Path.Combine(Logs, "velashell-20261019.log"), "line\n");
        string zipPath = Path.Combine(_root, "bundle.zip");
        // 临时文件名原先是固定的 <包>.partial:那里已有的文件会被截断、再被换名搬走
        string planted = zipPath + ".partial";
        await File.WriteAllTextAsync(planted, "someone else's file");

        await DiagnosticBundle.WriteAsync(zipPath, EmptyRequest());

        Assert.AreEqual("someone else's file", await File.ReadAllTextAsync(planted));
        Assert.AreSequenceEqual([planted], Directory.GetFiles(_root, "*.partial"), "自己的临时文件不留下");
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        Assert.IsNotNull(zip.GetEntry("logs/velashell-20261019.log"));
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task Bundle_DoesNotWriteThroughASymlinkPlantedAtTheOldFixedTemporaryName()
    {
        string victim = Path.Combine(_root, "victim.txt");
        await File.WriteAllTextAsync(victim, "keep me");
        string zipPath = Path.Combine(_root, "bundle.zip");
        File.CreateSymbolicLink(zipPath + ".partial", victim);

        await DiagnosticBundle.WriteAsync(zipPath, EmptyRequest());

        Assert.AreEqual("keep me", await File.ReadAllTextAsync(victim), "不能顺着链接把别的文件截断改写");
        Assert.IsNull(new FileInfo(zipPath).LinkTarget, "包是个普通文件,不是指向别处的链接");
    }

    private DiagnosticBundleRequest EmptyRequest() =>
        new("v1", Logs, [], new DiagnosticRedactor([], [], null, null, null));

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
