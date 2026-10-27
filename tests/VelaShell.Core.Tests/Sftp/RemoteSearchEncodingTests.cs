using System.Text;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

/// <summary>
/// 远端搜索按会话编码:输出按会话的文件名编码解(与 SFTP 同一个来源),命令里的目录、模式、文字编成会话编码的字节,
/// 以八进制转义交给远端还原;名字里的换行在记录里是 <c>\0</c>,解析时换回来。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
public sealed class RemoteSearchEncodingTests
{
    private const string Chinese = "中文报告";

    [ClassInitialize]
    public static void RegisterCodePages(TestContext _) =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [TestMethod]
    public void DecodeRecord_UsesTheSessionEncoding_AndRestoresNewlines()
    {
        var gbk = Encoding.GetEncoding("GBK");
        byte[] record = [.. "f\t/srv/"u8.ToArray(), .. gbk.GetBytes(Chinese), 0, .. "x.txt"u8.ToArray()];

        Assert.AreEqual("f\t/srv/" + Chinese + "\nx.txt", RemoteSearch.DecodeRecord(record, gbk));
        Assert.AreNotEqual("f\t/srv/" + Chinese + "\nx.txt", RemoteSearch.DecodeRecord(record, null),
            "UTF-8 会话照旧按 UTF-8 解(GBK 字节解不出中文)");
        Assert.AreEqual("d\t/srv/" + Chinese, RemoteSearch.DecodeRecord(Encoding.UTF8.GetBytes("d\t/srv/" + Chinese), null));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("UTF-8")]
    [DataRow("utf8")]
    [DataRow("no-such-charset")]
    public void Resolve_Utf8BlankOrUnknown_MeansDefault(string? name) =>
        Assert.IsNull(RemoteFileNameEncoding.Resolve(name));

    [TestMethod]
    public void Resolve_LegacyCharset_IsTheSessionEncoding() =>
        Assert.AreEqual(936, RemoteFileNameEncoding.Resolve(" GBK ")?.CodePage);

    /// <summary>GBK 会话:三项用户输入都编成 GBK 字节、写成纯 ASCII 的八进制转义,脚本开头用 printf 还原。</summary>
    [TestMethod]
    public void BuildCommand_LegacyEncoding_PassesUserInputAsOctalEscapesOfTheSessionBytes()
    {
        var gbk = Encoding.GetEncoding("GBK");
        string command = RemoteSearch.BuildCommand(new("/srv/" + Chinese, Chinese, Chinese + "'$(id)"), RemoteShellKind.Bash, gbk);

        Assert.DoesNotContain(Chinese, command, "中文不能原样进命令(exec 请求只能是 UTF-8,远端要的是 GBK 字节)");
        Assert.Contains(" vela-search " + RemoteSha256.Quote(Escape(gbk, "/srv/" + Chinese)) + " ", command);
        Assert.Contains(" " + RemoteSha256.Quote(Escape(gbk, "*" + Chinese + "*")) + " '501' ", command);
        Assert.EndsWith(" " + RemoteSha256.Quote(Escape(gbk, Chinese + "'$(id)")), command);
        Assert.DoesNotContain("$(id)", command, "转义之后用户输入一个字节都不以原样出现");
        Assert.Contains("t=$(printf \"$5_\")", command, "脚本开头把参数还原成字节");
        Assert.IsTrue(Escape(gbk, "/srv").All(static c => c is '\\' or (>= '0' and <= '7')), "转义串只由 \\ddd 组成,拿它当 printf 格式串不会解释出转换");
    }

    /// <summary>会话编码里没有的字符(GBK 里的表情符号):整次退回原样的 UTF-8 参数,而不是把它换成「?」通配符。</summary>
    [TestMethod]
    public void BuildCommand_InputTheSessionEncodingCannotHold_FallsBackToPlainArguments()
    {
        var gbk = Encoding.GetEncoding("GBK");
        string command = RemoteSearch.BuildCommand(new("/srv", "😀"), RemoteShellKind.PosixSh, gbk);

        Assert.Contains(" '*😀*' ", command);
        Assert.Contains(" '/srv' ", command);
        Assert.DoesNotContain("printf \"$1_\"", command);
    }

    private static string Escape(Encoding encoding, string value) =>
        string.Concat(encoding.GetBytes(value).Select(static b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
}
