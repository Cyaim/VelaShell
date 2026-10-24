using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

/// <summary>远端搜索的命令拼装与输出解析(纯函数)。真 shell 上的行为见 <see cref="RemoteSearchShellTests" /> 与 Docker 用例。</summary>
[TestClass]
[TestCategory("Sftp")]
public sealed class RemoteSearchTests
{
    private const string Nasty = "it's $(touch /tmp/pwned); `id` \"x\" \\ ;|&";

    [TestMethod]
    [DataRow(null, "*")]
    [DataRow("", "*")]
    [DataRow("  ", "*")]
    [DataRow("nginx", "*nginx*")]
    [DataRow(" nginx ", "*nginx*")]
    [DataRow("*.log", "*.log")]
    [DataRow("access.?", "access.?")]
    [DataRow("[ab]*.conf", "[ab]*.conf")]
    public void EffectiveNamePattern_WrapsPlainTextAsContains(string? input, string expected) =>
        Assert.AreEqual(expected, RemoteSearch.EffectiveNamePattern(input));

    [TestMethod]
    [DataRow("/", "/")]
    [DataRow("//", "/")]
    [DataRow("/var/log/", "/var/log")]
    [DataRow("/var//log", "/var/log")]
    [DataRow(" /srv ", "/srv")]
    public void NormalizeRoot_TrimsAndCollapsesSlashes(string input, string expected) =>
        Assert.AreEqual(expected, RemoteSearch.NormalizeRoot(input));

    /// <summary>
    /// 用户填的东西一个字节都不进脚本:脚本是常量,目录、模式、文字都作为单引号包好的位置参数跟在后面。
    /// </summary>
    [TestMethod]
    public void BuildCommand_PosixShell_PassesUserInputOnlyAsQuotedArguments()
    {
        var query = new RemoteSearchQuery("/srv/" + Nasty, Nasty, Nasty);

        string command = RemoteSearch.BuildCommand(query, RemoteShellKind.Bash);

        Assert.StartsWith("sh -c '", command);
        string quoted = RemoteSha256.Quote(Nasty);
        Assert.Contains(" vela-search " + RemoteSha256.Quote("/srv/" + Nasty) + " ", command);
        Assert.Contains(" " + RemoteSha256.Quote("*" + Nasty + "*") + " '501' " + quoted, command, "模式(不含通配符时两头补 *)、上限、文字依次作为位置参数");
        // 脚本那一段(第一个参数)里只有 $1…$4,看不到任何用户输入。
        string script = command["sh -c '".Length..command.IndexOf("' vela-search ", StringComparison.Ordinal)];
        Assert.DoesNotContain("touch", script);
        Assert.DoesNotContain("'", script, "脚本本体整段被单引号包着,里面不能再有单引号");
        Assert.Contains("\"$1\"", script);
        Assert.Contains("\"$2\"", script);
        Assert.Contains("\"$4\"", script);
    }

    [TestMethod]
    public void BuildCommand_ByName_UsesFindOnly_AndPrunesVirtualFileSystemsOnlyFromRoot()
    {
        string fromRoot = RemoteSearch.BuildCommand(new("/", "*.conf", IgnoreCase: false), RemoteShellKind.PosixSh);
        string fromSrv = RemoteSearch.BuildCommand(new("/srv", "*.conf", IgnoreCase: true), RemoteShellKind.PosixSh);

        Assert.Contains("-path /proc", fromRoot);
        Assert.DoesNotContain("-path /proc", fromSrv, "用户指名要搜的 /proc 下面不该被跳过");
        Assert.Contains(" -name \"$2\" -print", fromRoot);
        Assert.Contains(" -iname \"$2\" -print", fromSrv);
        Assert.DoesNotContain("grep", fromSrv);
        // 名字搜索只有三个位置参数。
        Assert.EndsWith(" '/srv' '*.conf' '501'", fromSrv);
    }

    [TestMethod]
    public void BuildCommand_ByContent_UsesFixedStringGrepOverRegularFiles()
    {
        string command = RemoteSearch.BuildCommand(new("/etc", "", "listen 80", IgnoreCase: true, MaxResults: 20), RemoteShellKind.Zsh);

        Assert.Contains("-type f -iname \"$2\" -exec grep -l -s -i -F -e \"$4\" {} +", command);
        Assert.Contains(RemoteSearch.NoGrepMarker, command);
        Assert.EndsWith(" '/etc' '*' '21' 'listen 80'", command, "多要一条用来判断是否截断");
    }

    /// <summary>fish 的单引号里 <c>\\</c> 与 <c>\'</c> 是转义:反斜杠要翻倍、引号要写成 <c>\'</c>,POSIX 那套 <c>'\''</c> 也照样认。</summary>
    [TestMethod]
    public void QuoteFor_Fish_EscapesBackslashAndQuote()
    {
        Assert.AreEqual(@"'a\\b\'c'", RemoteSearch.QuoteFor(RemoteShellKind.Fish, @"a\b'c"));
        Assert.AreEqual(@"'a\b'\''c'", RemoteSearch.QuoteFor(RemoteShellKind.Bash, @"a\b'c"));

        string command = RemoteSearch.BuildCommand(new("/srv", @"a\b"), RemoteShellKind.Fish);
        Assert.Contains(@"'*a\\b*'", command);
        Assert.Contains(@"printf ""d\\t%s\\n""", command, "脚本里的反斜杠在 fish 里同样要翻倍");
    }

    [TestMethod]
    [DataRow(RemoteShellKind.NonPosix)]
    [DataRow(RemoteShellKind.Unknown)]
    public void BuildCommand_RefusesNonPosixShells(RemoteShellKind shell) =>
        Assert.ThrowsExactly<NotSupportedException>(() => RemoteSearch.BuildCommand(new("/srv", "x"), shell));

    [TestMethod]
    public void BuildCommand_RejectsRelativeRootAndNonPositiveLimit()
    {
        Assert.ThrowsExactly<ArgumentException>(() => RemoteSearch.BuildCommand(new("srv", "x"), RemoteShellKind.Bash));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RemoteSearch.BuildCommand(new("/srv", "x", MaxResults: 0), RemoteShellKind.Bash));
    }

    [TestMethod]
    public void TryParseHit_ReadsTypedLines_SkipsNoiseAndTheRootItself()
    {
        Assert.IsTrue(RemoteSearch.TryParseHit("d\t/srv/app\r", "/srv", out RemoteSearchHit dir));
        Assert.AreEqual(new RemoteSearchHit("/srv/app", true), dir);
        Assert.IsTrue(RemoteSearch.TryParseHit("f\t/srv//app/a b.txt", "/srv/", out RemoteSearchHit file));
        Assert.AreEqual(new RemoteSearchHit("/srv/app/a b.txt", false), file);

        Assert.IsFalse(RemoteSearch.TryParseHit("d\t/srv", "/srv", out _), "根目录名字匹配时 find 会列出它,不算结果");
        Assert.IsFalse(RemoteSearch.TryParseHit("Welcome to Ubuntu", "/srv", out _));
        Assert.IsFalse(RemoteSearch.TryParseHit("x\t/srv/a", "/srv", out _));
        Assert.IsFalse(RemoteSearch.TryParseHit("f\trelative", "/srv", out _));
        Assert.IsTrue(RemoteSearch.IsToolMissingMarker(RemoteSearch.NoFindMarker + "\r"));
        Assert.IsTrue(RemoteSearch.IsToolMissingMarker(RemoteSearch.NoGrepMarker));
        Assert.IsFalse(RemoteSearch.IsToolMissingMarker("f\t/srv/VELA-SEARCH-NO-FIND"));
    }
}
