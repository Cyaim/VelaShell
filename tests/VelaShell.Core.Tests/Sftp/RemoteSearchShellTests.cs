using System.Diagnostics;
using System.Text;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

/// <summary>
/// 拼出来的搜索命令在<b>真的 sh</b> 上跑:像 sshd 那样把整行交给登录 shell 的 <c>-c</c>,看结果对不对、注入的东西有没有被执行。
/// </summary>
/// <remarks>
/// 本机有什么就用什么:Linux 是 GNU find / grep,macOS 是 BSD 那一套,Windows 用 Git for Windows 带的 sh(GNU);
/// 都没有时记 Inconclusive(<c>[SKIP]</c>)。BusyBox 与 bash / zsh / fish / dash / ash 五种登录 shell 在 Docker 用例里验
/// (<c>RemoteSearchDockerTests</c>)。
/// </remarks>
[TestClass]
[TestCategory("Sftp")]
public sealed class RemoteSearchShellTests
{
    /// <summary>名字里带着引号、命令替换、反引号与分隔符 —— 要被当成名字找出来,而不是被执行。</summary>
    private const string NastyName = "it's $(touch PWNED) `touch PWNED2` ;& x.txt";

    private static string? _sh;
    private static string _root = string.Empty;
    private static string _workDir = string.Empty;

    [ClassInitialize]
    public static void CreateFixture(TestContext context)
    {
        _sh = FindSh();
        if (_sh is null)
        {
            return;
        }
        _workDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"vela-search-cwd-{Guid.NewGuid():N}")).FullName;
        // 夹具由 sh 自己建,路径也就是 sh 眼里的路径(Windows 上是 Git 的 /tmp/…)。
        (string output, _) = Run(
            "sh -c 'd=$(mktemp -d) && mkdir -p \"$d/app/logs\" \"$d/.hidden\" && " +
            "printf \"listen 80\\n\" > \"$d/app/nginx.conf\" && printf \"nothing\\n\" > \"$d/app/logs/access.log\" && " +
            "printf \"x\\n\" > \"$d/$1\" && echo \"$d\"' fixture " + RemoteSha256.Quote(NastyName)).GetAwaiter().GetResult();
        _root = output.Trim();
    }

    [ClassCleanup]
    public static void RemoveFixture()
    {
        if (_sh is null)
        {
            return;
        }
        if (_root.StartsWith('/'))
        {
            Run("rm -rf " + RemoteSha256.Quote(_root)).GetAwaiter().GetResult();
        }
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // 删不掉不影响断言。
        }
    }

    [TestMethod]
    public async Task ByName_FindsFilesAndDirectories_IgnoringCase()
    {
        List<RemoteSearchHit> hits = await SearchAsync(new(_root, "NGINX"));
        Assert.AreSequenceEqual([new RemoteSearchHit(_root + "/app/nginx.conf", false)], hits);

        List<RemoteSearchHit> exact = await SearchAsync(new(_root, "NGINX", IgnoreCase: false));
        Assert.IsEmpty(exact, "区分大小写时 NGINX 找不到 nginx.conf");

        List<RemoteSearchHit> dirs = await SearchAsync(new(_root, "logs"));
        Assert.AreSequenceEqual([new RemoteSearchHit(_root + "/app/logs", true)], dirs);

        List<RemoteSearchHit> hidden = await SearchAsync(new(_root, ".hid*"));
        Assert.AreSequenceEqual([new RemoteSearchHit(_root + "/.hidden", true)], hidden, "点开头的目录照样找得到");
    }

    [TestMethod]
    public async Task ByContent_FindsOnlyFilesContainingTheText()
    {
        List<RemoteSearchHit> hits = await SearchAsync(new(_root, null, "LISTEN 80"));
        Assert.AreSequenceEqual([new RemoteSearchHit(_root + "/app/nginx.conf", false)], hits);

        List<RemoteSearchHit> narrowed = await SearchAsync(new(_root, "*.log", "listen"));
        Assert.IsEmpty(narrowed, "名字与内容两个条件同时成立才算");
    }

    [TestMethod]
    public async Task UserInput_IsNeverExecuted()
    {
        List<RemoteSearchHit> byName = await SearchAsync(new(_root, NastyName, IgnoreCase: false));
        Assert.AreSequenceEqual([new RemoteSearchHit(_root + "/" + NastyName, false)], byName);

        List<RemoteSearchHit> byContent = await SearchAsync(new(_root + "/app", "*", "$(touch PWNED3)"));
        Assert.IsEmpty(byContent);

        Assert.IsFalse(File.Exists(Path.Combine(_workDir, "PWNED")));
        Assert.IsFalse(File.Exists(Path.Combine(_workDir, "PWNED2")));
        Assert.IsFalse(File.Exists(Path.Combine(_workDir, "PWNED3")));
        (string leftovers, _) = await Run("ls -a " + RemoteSha256.Quote(_root));
        // 夹具里那个文件的名字本身就含 PWNED:逐个名字比,而不是在整段输出里找子串。
        string[] names = leftovers.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("PWNED", names);
        Assert.DoesNotContain("PWNED2", names);
    }

    /// <summary>远端多给一条(上限 + 1),服务据此知道「还有」;这里看到的正是多出来的那一条。</summary>
    [TestMethod]
    public async Task Limit_StopsAfterOneMoreThanTheMaximum()
    {
        List<RemoteSearchHit> hits = await SearchAsync(new(_root, "*", MaxResults: 1));
        Assert.HasCount(2, hits);
    }

    /// <summary>
    /// 名字里带换行(与行尾的 <c>\r</c>)的文件与目录:各是一条结果,不被拆成两条;按内容找同样。
    /// Windows 的文件系统不许名字里有这些字符,只在 Linux / macOS 上跑(BusyBox 在 Docker 用例里验)。
    /// </summary>
    [TestMethod]
    public async Task NamesWithNewlines_AreOneHitEach_ByNameAndByContent()
    {
        RequireSh();
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("[SKIP] Windows 的文件系统不许名字里有换行与 \\r。");
        }
        string root = _root + "/newline";
        (string created, int exit) = await Run(
            "sh -c 'n=$(printf \"\\n_\"); n=${n%_}; r=$(printf \"\\r\"); mkdir -p \"$1/dir${n}x\" && " +
            "printf \"needle\\n\" > \"$1/two${n}lines.txt\" && printf \"needle\\n\" > \"$1/plain.txt\" && printf \"x\\n\" > \"$1/cr${r}\"' fixture " +
            RemoteSha256.Quote(root));
        Assert.AreEqual(0, exit, created);

        List<RemoteSearchHit> byName = await SearchAsync(new(root, "*"));
        Assert.AreSequenceEqual(
        [
            new RemoteSearchHit(root + "/cr\r", false),
            new RemoteSearchHit(root + "/dir\nx", true),
            new RemoteSearchHit(root + "/plain.txt", false),
            new RemoteSearchHit(root + "/two\nlines.txt", false),
        ], byName);

        List<RemoteSearchHit> byContent = await SearchAsync(new(root, null, "NEEDLE"));
        Assert.AreSequenceEqual(
            [new RemoteSearchHit(root + "/plain.txt", false), new RemoteSearchHit(root + "/two\nlines.txt", false)], byContent,
            "带换行的名字逐个 grep,其余一批 grep -l");
    }

    /// <summary>
    /// 裁掉了 <c>find -exec … {} +</c> 的极老 BusyBox:换一个不认 <c>+</c> 的 find 顶在 PATH 前面,
    /// 按名字找退回原来的按行输出,结果照样对。
    /// </summary>
    [TestMethod]
    public async Task FindWithoutExecPlus_FallsBackToLineOutput()
    {
        RequireSh();
        string shim = _root + "/shim";
        const string Script = "#!/bin/sh\ncase \" $* \" in *\" + \"*) echo \"find: unrecognized: +\" >&2; exit 1;; esac\nexec /usr/bin/find \"$@\"";
        (string created, int exit) = await Run(
            "sh -c 'mkdir -p \"$1\" && printf \"%s\\n\" \"$2\" > \"$1/find\" && chmod +x \"$1/find\"' shim " +
            RemoteSha256.Quote(shim) + " " + RemoteSha256.Quote(Script));
        Assert.AreEqual(0, exit, created);

        List<RemoteSearchHit> hits = await SearchAsync(new(_root + "/app", "NGINX"), shim + ":");
        Assert.AreSequenceEqual([new RemoteSearchHit(_root + "/app/nginx.conf", false)], hits);
        List<RemoteSearchHit> dirs = await SearchAsync(new(_root + "/app", "logs"), shim + ":");
        Assert.AreSequenceEqual([new RemoteSearchHit(_root + "/app/logs", true)], dirs);
    }

    private static void RequireSh()
    {
        if (_sh is null)
        {
            Assert.Inconclusive("[SKIP] 本机没有 POSIX sh(Windows 上要 Git for Windows)。");
        }
    }

    private static async Task<List<RemoteSearchHit>> SearchAsync(RemoteSearchQuery query, string pathPrefix = "")
    {
        RequireSh();
        (string output, int exit) = await Run(RemoteSearch.BuildCommand(query, RemoteShellKind.PosixSh), pathPrefix);
        Assert.AreEqual(0, exit, output);
        var hits = new List<RemoteSearchHit>();
        // 与服务一样:记录只按换行分,名字里的换行在记录里是 \0。
        foreach (string line in output.Split('\n'))
        {
            Assert.IsFalse(RemoteSearch.IsToolMissingMarker(line), "本机的 sh 应当有 find / grep");
            if (RemoteSearch.TryParseHit(RemoteSearch.DecodeRecord(Encoding.UTF8.GetBytes(line), null), query.Root, out RemoteSearchHit hit))
            {
                hits.Add(hit);
            }
        }
        hits.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return hits;
    }

    /// <summary>像 sshd 那样:登录 shell <c>-c</c> 整行命令。PATH 先放 /usr/bin,Windows 上才不会撞到 System32 的 find.exe。</summary>
    private static async Task<(string Output, int ExitCode)> Run(string commandLine, string pathPrefix = "")
    {
        var start = new ProcessStartInfo(_sh!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _workDir,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("PATH=" + pathPrefix + "/usr/bin:/bin:$PATH; export PATH; " + commandLine);
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        _ = await stderr;
        return (await stdout, process.ExitCode);
    }

    private static string? FindSh()
    {
        if (!OperatingSystem.IsWindows())
        {
            return File.Exists("/bin/sh") ? "/bin/sh" : null;
        }
        // Git for Windows 装在哪都行:默认的 Program Files\Git,或者顺着 PATH 上的 git.exe / sh.exe 往上找它的 usr\bin\sh.exe。
        var roots = new List<string>();
        foreach (string? programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                 })
        {
            if (!string.IsNullOrEmpty(programFiles))
            {
                roots.Add(Path.Combine(programFiles, "Git"));
            }
        }
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (directory.Length == 0 || !(File.Exists(Path.Combine(directory, "git.exe")) || File.Exists(Path.Combine(directory, "sh.exe"))))
            {
                continue;
            }
            for (var up = new DirectoryInfo(directory); up is not null && roots.Count < 64; up = up.Parent)
            {
                roots.Add(up.FullName);
            }
        }
        return roots.Select(static root => Path.Combine(root, "usr", "bin", "sh.exe")).FirstOrDefault(File.Exists);
    }
}
