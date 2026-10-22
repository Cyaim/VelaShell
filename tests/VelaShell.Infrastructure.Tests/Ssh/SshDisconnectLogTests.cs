using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 连接结束时写进诊断日志的那一行:分得清掉线与本端关闭,带上对端的原因码与原话,且永远只占一行。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class SshDisconnectLogTests
{
    [TestMethod]
    public void LocalClose_IsLoggedAsSuchWithoutTheLibraryMessage()
    {
        var reason = new SshConnectionClosedException(SshFailureReason.Aborted, SshPhase.Closed, "连接已释放");
        string line = SshDisconnectLog.Describe("root@db1:22", reason, TimeSpan.FromMinutes(5));
        Assert.AreEqual("[SSH] root@db1:22 closed after 00:05:00: closed locally", line);
    }

    [TestMethod]
    public void PeerDisconnect_CarriesCodeAndSanitizedDescriptionOnOneLine()
    {
        var reason = new SshConnectionClosedException(SshFailureReason.ClosedByPeer, SshPhase.Open, "对端断开了连接")
        {
            DisconnectReason = SshDisconnectReason.ByApplication,
            // 对端原话是不可信文本:换行与控制字符不能在日志里伪造出第二行
            PeerDescription = "idle timeout\r\n[SSH] fake line\u001b[31m"
        };
        string line = SshDisconnectLog.Describe("ops@[2001:db8::1]:2222", reason, TimeSpan.FromHours(26.5));
        StringAssert.StartsWith(line, "[SSH] ops@[2001:db8::1]:2222 closed after 1.02:30:00: ClosedByPeer(Open)");
        StringAssert.Contains(line, "peer DISCONNECT ByApplication(11)");
        StringAssert.Contains(line, "\"idle timeout [SSH] fake line?[31m\"", "换行收成一个空格,其余控制字符照库的规矩换成 ?");
        StringAssert.Contains(line, " - 对端断开了连接");
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.DoesNotContain("\u001b", line);
    }

    [TestMethod]
    public void PeerDescription_BidiControlsAndUnicodeLineBreaksCannotReshapeTheLine()
    {
        var reason = new SshConnectionClosedException(SshFailureReason.ClosedByPeer, SshPhase.Open, "对端断开了连接")
        {
            DisconnectReason = SshDisconnectReason.ByApplication,
            // U+2028 / U+2029 在不少查看器里就是换行;双向控制符能把显示顺序倒过来,伪造出另一段文字
            PeerDescription = "bye\u2028[SSH] fake line\u2029\u202Eevil\u202C \u2066x\u2069"
        };
        string line = SshDisconnectLog.Describe("u@h:22", reason, TimeSpan.FromSeconds(1));
        StringAssert.Contains(line, "\"bye [SSH] fake line ?evil? ?x?\"");
        foreach (char c in "\u2028\u2029\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069")
        {
            Assert.DoesNotContain(c.ToString(), line);
        }
    }

    [TestMethod]
    public void TransportFailure_NamesTheInnerCauseAndCapsLongText()
    {
        var reason = new SshConnectionClosedException(SshFailureReason.KeepAliveTimeout, SshPhase.Open, "保活超时",
            new IOException(new string('x', 1000)));
        string line = SshDisconnectLog.Describe("u@h:22", reason, TimeSpan.FromSeconds(42));
        StringAssert.StartsWith(line, "[SSH] u@h:22 closed after 00:00:42: KeepAliveTimeout(Open) - 保活超时 <- IOException: ");
        StringAssert.EndsWith(line, "…");
        Assert.IsLessThan(400, line.Length);
    }
}
