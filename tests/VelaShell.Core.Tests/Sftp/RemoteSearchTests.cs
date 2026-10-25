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
        Assert.Contains(" " + RemoteSha256.Quote("*" + Nasty + "*") + " '501' 't=$1; shift;", command,
            "模式(不含通配符时两头补 *)、上限、批处理脚本依次作为位置参数");
        Assert.EndsWith(" " + quoted, command, "文字是最后一个位置参数");
        // 脚本那一段(第一个参数)里只有 $1…$5,看不到任何用户输入。
        string script = command["sh -c '".Length..command.IndexOf("' vela-search ", StringComparison.Ordinal)];
        Assert.DoesNotContain("touch", script);
        Assert.DoesNotContain("'", script, "脚本本体整段被单引号包着,里面不能再有单引号");
        Assert.Contains("\"$1\"", script);
        Assert.Contains("\"$2\"", script);
        Assert.Contains("\"$4\"", script);
        Assert.Contains("\"$5\"", script);
        Assert.DoesNotContain("printf \"$1", script, "UTF-8 会话不转义参数,也就不该有还原那一步");
    }

    [TestMethod]
    public void BuildCommand_ByName_UsesFindOnly_AndPrunesVirtualFileSystemsOnlyFromRoot()
    {
        string fromRoot = RemoteSearch.BuildCommand(new("/", "*.conf", IgnoreCase: false), RemoteShellKind.PosixSh);
        string fromSrv = RemoteSearch.BuildCommand(new("/srv", "*.conf", IgnoreCase: true), RemoteShellKind.PosixSh);

        Assert.Contains("-path /proc", fromRoot);
        Assert.DoesNotContain("-path /proc", fromSrv, "用户指名要搜的 /proc 下面不该被跳过");
        Assert.Contains(" -name \"$2\" -exec sh -c \"$4\" vela-search {} +", fromRoot, "路径当参数交给批处理,名字里的换行不会把一条拆成两条");
        Assert.Contains(" -iname \"$2\" -exec sh -c \"$4\" vela-search {} +", fromSrv);
        Assert.Contains(" -iname \"$2\" -print", fromSrv, "裁掉了 -exec … + 的 find 上退回按行输出");
        Assert.Contains("-maxdepth 0 -exec sh -c : vela-probe {} +", fromSrv, "先在根目录上试 -exec … + 认不认");
        Assert.Contains("tr \"\\n\\000\" \"\\000\\n\" | head -n \"$3\"", fromSrv, "\\0 与换行对调之后 head 数的才是记录条数");
        Assert.DoesNotContain("grep", fromSrv);
        // 名字搜索有四个位置参数:根目录、模式、上限、批处理脚本。
        Assert.Contains(" '/srv' '*.conf' '501' 'for p in \"$@\"; do ", fromSrv);
        Assert.EndsWith("; done'", fromSrv);
    }

    [TestMethod]
    public void BuildCommand_ByContent_UsesFixedStringGrepOverRegularFiles()
    {
        string command = RemoteSearch.BuildCommand(new("/etc", "", "listen 80", IgnoreCase: true, MaxResults: 20), RemoteShellKind.Zsh);

        Assert.Contains("-type f -iname \"$2\" -exec sh -c \"$4\" vela-search \"$5\" {} +", command, "文字排在 {} 前面,作批处理的 $1");
        Assert.Contains("-type f -iname \"$2\" -exec grep -l -s -i -F -e \"$5\" {} +", command, "回退那一路照旧一批交给 grep -l");
        Assert.Contains("grep -q -s -i -F -e \"$t\" \"$p\"", command, "名字里带换行的逐个 grep -q");
        Assert.Contains("grep -l -s -i -F -e \"$t\" \"$@\"", command, "其余攒成一批交给一次 grep -l");
        Assert.Contains(RemoteSearch.NoGrepMarker, command);
        Assert.Contains(" '/etc' '*' '21' 't=$1;", command, "多要一条用来判断是否截断");
        Assert.EndsWith(" 'listen 80'", command);
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
        Assert.IsTrue(RemoteSearch.TryParseHit("d\t/srv/app", "/srv", out RemoteSearchHit dir));
        Assert.AreEqual(new RemoteSearchHit("/srv/app", true), dir);
        Assert.IsTrue(RemoteSearch.TryParseHit("f\t/srv/ends-with-cr\r", "/srv", out RemoteSearchHit cr));
        Assert.AreEqual("/srv/ends-with-cr\r", cr.Path, "记录只按换行分:行尾的 \\r 是名字的一部分");
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
