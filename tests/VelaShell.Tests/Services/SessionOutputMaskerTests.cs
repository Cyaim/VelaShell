using System.Text;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 会话日志与录制的输出遮盖:落盘的那一份里看不到口令、令牌、私钥;不相干的输出逐字节原样,不完整的行只在可能藏着值时才扣下。
/// </summary>
[TestClass]
public sealed class SessionOutputMaskerTests
{
    private readonly List<byte[]> _written = [];

    private SessionOutputMasker NewMasker() => new(chunk => _written.Add(chunk));

    private string Written => Encoding.Latin1.GetString([.. _written.SelectMany(chunk => chunk)]);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [TestMethod]
    public void EnvFileYamlAndJson_OnlyTheSecretValuesAreMasked()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("DB_HOST=db1\r\nDB_PASSWORD=hunter2\r\nAPI_KEY=abc123\r\nPWD=/home/ops\r\n"));
        masker.Write(Bytes("data:\n  password: cGFzcw==\n  username: YWRtaW4=\n"));
        masker.Write(Bytes("{\"api_key\": \"k-123\", \"user\": \"x\", \"client_secret\":'s e c'}\n"));

        Assert.AreEqual(
            "DB_HOST=db1\r\nDB_PASSWORD=***\r\nAPI_KEY=***\r\nPWD=/home/ops\r\n"
            + "data:\n  password: ***\n  username: YWRtaW4=\n"
            + "{\"api_key\": \"***\", \"user\": \"x\", \"client_secret\":'***'}\n",
            Written, "PWD 是当前目录不是口令;引号保留,行尾保留");
    }

    [TestMethod]
    public void ValueSplitAcrossChunks_IsHeldUntilTheLineCompletes()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("export TOKEN=ab"));
        Assert.IsEmpty(_written, "正写到值的中间:扣下");
        masker.Write(Bytes("cdef\nnext "));
        masker.Write(Bytes("SECRET="));
        masker.Write(Bytes("xyz\n"));
        Assert.AreEqual("export TOKEN=***\nnext SECRET=***\n", Written);
        Assert.DoesNotContain("ab", Written.Replace("next", ""));
    }

    [TestMethod]
    public void PromptsAndScreenUpdates_AreNotHeld()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("[sudo] password for ops: "));
        Assert.AreEqual("[sudo] password for ops: ", Written, "等输入的提示当场放出,不等下一段输出");
        _written.Clear();
        masker.Write(Bytes("\u001b[2J\u001b[H  top - 10:00:01 up 3 days"));
        Assert.AreEqual("\u001b[2J\u001b[H  top - 10:00:01 up 3 days", Written, "全屏程序不换行的输出当场放出");
    }

    [TestMethod]
    public void GrepColorBetweenNameAndValue_StillMasks()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("\u001b[01;31m\u001b[KGITHUB_TOKEN\u001b[m\u001b[K=plain-value-1\n"));
        Assert.AreEqual("\u001b[01;31m\u001b[KGITHUB_TOKEN\u001b[m\u001b[K=***\n", Written);
    }

    [TestMethod]
    public void PrivateKeyBlock_BodyIsMaskedEvenWhenSplit()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("$ cat id_ed25519\r\n-----BEGIN OPENSSH PRIVATE KEY-----\r\nb3BlbnNzaC1rZXktdjEAAAAA"));
        masker.Write(Bytes("BG5vbmUAAAAEbm9uZQ\r\nQyNTUxOQAAACD\r\n-----END OPENSSH PRIVATE KEY-----\r\n$ "));
        masker.Flush();
        Assert.AreEqual(
            "$ cat id_ed25519\r\n-----BEGIN OPENSSH PRIVATE KEY-----\r\n***\r\n***\r\n-----END OPENSSH PRIVATE KEY-----\r\n$ ",
            Written);
    }

    [TestMethod]
    public void BearerAndPrefixedTokens_AreMasked()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("Authorization: Bearer eyJhbGciOi.payload.sig\n"));
        masker.Write(Bytes("token: Bearer abcdefgh123\n"));
        masker.Write(Bytes("aws AKIAIOSFODNN7EXAMPLE gh ghp_0123456789abcdefghijABCDEFGHIJ0123 sk-proj-ABCDEFGHIJKLMNOPQRST\n"));
        string text = Written;
        Assert.AreEqual("Authorization: Bearer ***\ntoken: *** ***\naws *** gh *** ***\n", text);
    }

    [TestMethod]
    public void NonAsciiBytes_RoundTripUntouched()
    {
        SessionOutputMasker masker = NewMasker();
        byte[] gbk = [0xC3, 0xDC, 0xC2, 0xEB, (byte)'\n'];   // GBK「密码」
        byte[] utf8 = Bytes("部署完成 ✓ password=秘密\n");
        masker.Write(gbk);
        masker.Write(utf8);
        CollectionAssert.AreEqual(gbk, _written[0], "GBK 字节原样往返");
        Assert.AreEqual("部署完成 ✓ password=***\n", Encoding.UTF8.GetString(_written[1]), "值是非 ASCII 也整段遮掉,别处的多字节字符不受影响");
    }

    [TestMethod]
    public void HeldTail_IsCappedAndFlushedOnStop()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("TOKEN=" + new string('a', SessionOutputMasker.MaxHeldBytes)));
        Assert.AreEqual("TOKEN=***", Written, "超过上限不再扣,照现有内容遮完放出");
        _written.Clear();
        masker.Write(Bytes("PASSWORD=half"));
        Assert.IsEmpty(_written);
        masker.Flush();
        Assert.AreEqual("PASSWORD=***", Written, "停止记录时扣着的那一点也遮完放出");
    }
}
