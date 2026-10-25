using System.Diagnostics;
using System.Text;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 会话日志与录制的输出遮盖:落盘的那一份里看不到口令、令牌、私钥;不相干的输出逐字节原样,只有写到一半的值才扣下。
/// </summary>
[TestClass]
public sealed class SessionOutputMaskerTests
{
    private readonly List<byte[]> _written = [];

    private SessionOutputMasker NewMasker() => new(chunk => _written.Add(chunk));

    private string Written => Encoding.Latin1.GetString([.. _written.SelectMany(chunk => chunk)]);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>按给定的切法一段段写进去,最后 Flush;返回落盘的全部内容(按 UTF-8 还原)。</summary>
    private static string Run(IEnumerable<byte[]> chunks)
    {
        var written = new List<byte>();
        var masker = new SessionOutputMasker(written.AddRange);
        foreach (byte[] chunk in chunks)
        {
            masker.Write(chunk);
        }
        masker.Flush();
        return Encoding.UTF8.GetString([.. written]);
    }

    /// <summary>jq 1.7 在终端上的默认上色输出,<paramref name="value" /> 是 password 的值。</summary>
    private static string Jq(string value) =>
        "\u001b[1;39m{\n  \u001b[0m\u001b[1;34m\"password\"\u001b[0m\u001b[1;39m: \u001b[0m\u001b[0;32m" + value
        + "\u001b[0m\u001b[1;39m,\n  \u001b[0m\u001b[1;34m\"user\"\u001b[0m\u001b[1;39m: \u001b[0m\u001b[0;32m\"x\"\u001b[0m\u001b[1;39m\n\u001b[1;39m}\u001b[0m\n";

    /// <summary>bat 一类工具上色的 <c>.env</c> 一行,<paramref name="value" /> 是值。</summary>
    private static string Bat(string value) =>
        "\u001b[38;5;203mDB_PASSWORD\u001b[0m\u001b[38;5;231m=\u001b[0m\u001b[38;5;186m" + value + "\u001b[0m\r\n";

    /// <summary>
    /// 带密钥的输出样本:名称、原文、遮完的样子(带前缀的令牌被切在前缀里时前缀已经放出去了,结果因切法而异,记为 null)、
    /// 不许出现在落盘内容里的密钥(空串:这一条只看不误伤)。
    /// </summary>
    private static IEnumerable<object?[]> SecretLines =>
    [
        ["env", "DB_PASSWORD=hunter2\r\n", "DB_PASSWORD=***\r\n", "hunter2"],
        ["yaml", "data:\n  password: cGFzcw==\n", "data:\n  password: ***\n", "cGFzcw=="],
        ["json", "{\"api_key\": \"k-12345\", \"user\": \"x\"}\n", "{\"api_key\": \"***\", \"user\": \"x\"}\n", "k-12345"],
        ["quoted-with-spaces", "\"client_secret\": \"abc def ghi\"\n", "\"client_secret\": \"***\"\n", "abc def ghi"],
        ["grep-color",
            "\u001b[01;31m\u001b[KGITHUB_TOKEN\u001b[m\u001b[K=plain-value-1\n",
            "\u001b[01;31m\u001b[KGITHUB_TOKEN\u001b[m\u001b[K=***\n", "plain-value-1"],
        // GNU grep 的高亮连分隔符一起框住(grep 'PASSWORD='):分隔符后面跟的是 ESC[m ESC[K,不只是颜色序列
        ["grep-color-to-separator",
            "DB_\u001b[01;31m\u001b[KPASSWORD=\u001b[m\u001b[Khunter2\n",
            "DB_\u001b[01;31m\u001b[KPASSWORD=\u001b[m\u001b[K***\n", "hunter2"],
        ["jq", Jq("\"hunter2\""), Jq("\"***\""), "hunter2"],
        ["bat", Bat("hunter2"), Bat("***"), "hunter2"],
        ["bearer", "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig\r\n", "Authorization: Bearer ***\r\n", "eyJhbGciOiJIUzI1NiJ9.payload.sig"],
        ["export-token",
            "$ export GITHUB_TOKEN=ghp_0123456789abcdefghijABCDEFGHIJ0123\r\n",
            "$ export GITHUB_TOKEN=***\r\n", "0123456789abcdefghijABCDEFGHIJ0123"],
        ["pem-block",
            "$ cat id\r\n-----BEGIN OPENSSH PRIVATE KEY-----\r\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQ\r\n-----END OPENSSH PRIVATE KEY-----\r\n$ ",
            "$ cat id\r\n-----BEGIN OPENSSH PRIVATE KEY-----\r\n***\r\n-----END OPENSSH PRIVATE KEY-----\r\n$ ",
            "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQ"],
        ["gcp-json-pem",
            "  \"private_key\": \"-----BEGIN PRIVATE KEY-----\\nMIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQC7\\n-----END PRIVATE KEY-----\\n\",\r\n",
            "  \"private_key\": \"***\",\r\n", "MIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQC7"],
        ["one-line-pem",
            "  \"pem\": \"-----BEGIN PRIVATE KEY-----\\nMIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQC7\\n-----END PRIVATE KEY-----\\n\",\r\n",
            "  \"pem\": \"-----BEGIN PRIVATE KEY-----***-----END PRIVATE KEY-----\\n\",\r\n",
            "MIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQC7"],
        ["aws-secret-string",
            "    \"SecretString\": \"{\\\"username\\\":\\\"admin\\\",\\\"password\\\":\\\"hunter2\\\"}\",\n",
            "    \"SecretString\": \"***\",\n", "hunter2"],
        ["escaped-json-key",
            "\"body\": \"{\\\"password\\\":\\\"hunter2\\\"}\"\n",
            "\"body\": \"{\\\"password\\\":\\\"***\\\"}\"\n", "hunter2"],
        // 上下文截在一个长值的中间时,值里的「Password:"」不能被当成新的名字、把后面真正的名字吞掉
        ["long-value-then-key",
            "\"password\": \"" + new string('Z', 600) + " Password:\"api_key\"=hunter2 more\r\n",
            "\"password\": \"***\"api_key\"=*** more\r\n", "hunter2"],
        // 上下文截在一串很长的标识符中间:后半截照样当名字认;截在 xBearer 的 x 之后也不能凭空多出一个凭据
        ["long-key-run", new string('Z', 600) + "password=hunter2\r\n", new string('Z', 600) + "password=***\r\n", "hunter2"],
        ["bearer-inside-a-word", new string('Z', 600) + "Bearer abcdefghij\r\n", new string('Z', 600) + "Bearer abcdefghij\r\n", ""],
        ["bare-aws-key", "aws AKIAIOSFODNN7EXAMPLE\n", null, "IOSFODNN7EXAMPLE"],
        ["token-after-cjk", "令牌为sk-proj-ABCDEFGHIJKLMNOPQRST\n", null, "proj-ABCDEFGHIJKLMNOPQRST"],
        // 令牌与凭据紧跟在颜色序列后面:序列的收尾字节 m、K 是字母,不能因此当成「前面挨着字母」
        ["token-after-sgr", "\u001b[38;5;231mghp_0123456789abcdefghijABCDEFGHIJ0123\u001b[0m\n", null, "0123456789abcdefghijABCDEFGHIJ0123"],
        ["openai-key-after-sgr", "\u001b[32msk-proj-ABCDEFGHIJKLMNOPQRSTUV\u001b[0m\n", null, "proj-ABCDEFGHIJKLMNOPQRSTUV"],
        ["aws-key-after-reset", "\u001b[0mAKIAIOSFODNN7EXAMPLE\n", null, "IOSFODNN7EXAMPLE"],
        ["bearer-after-sgr",
            "\u001b[37mBearer eyJhbGciOiJIUzI1NiJ9.payload.sig\u001b[39m\n",
            "\u001b[37mBearer ***\u001b[39m\n", "eyJhbGciOiJIUzI1NiJ9.payload.sig"],
        ["grep-highlighted-token", "\u001b[01;31m\u001b[Kghp_0123456789abcdefghijABCDEFGHIJ0123\u001b[m\u001b[K\n", null, "0123456789abcdefghijABCDEFGHIJ0123"],
        // 多行私钥块里 END 前面同一行还有正文
        ["pem-body-before-end",
            "-----BEGIN RSA PRIVATE KEY-----\nAAAA\nBBBBsecret2-----END RSA PRIVATE KEY-----\n",
            "-----BEGIN RSA PRIVATE KEY-----\n***\n***-----END RSA PRIVATE KEY-----\n", "BBBBsecret2"],
        // 到行尾都没收尾的引号串:扣着的值不能等到换行就原文放出去
        ["unclosed-double-quote", "password=\"abc def\r\n$ ", "password=\"***\r\n$ ", "abc def"],
        ["unclosed-single-quote", "export TOKEN='abc def\r\n> ", "export TOKEN='***\r\n> ", "abc def"],
        ["unclosed-escaped-json",
            "\"body\": \"{\\\"password\\\":\\\"abc def\n",
            "\"body\": \"{\\\"password\\\":\\\"***\n", "abc def"],
        // 冒号后跟空白、不加引号的值遮到行尾;同一行后面的下一个键留着(紧挨着的逗号也留给它)
        ["yaml-passphrase-to-eol", "passphrase: correct horse battery staple\r\n", "passphrase: ***\r\n", "correct horse battery staple"],
        ["yaml-value-before-next-key", "password: abc def, user: bob\n", "password: ***, user: bob\n", "abc def"],
        ["yaml-value-before-comment", "  password: abc def  # rotated monthly\n", "  password: ***  # rotated monthly\n", "abc def"],
        // 不带口令字样、值却必是机密的键
        ["k8s-tls-key", "  tls.key: LS0tLS1CRUdJTiBQUklWQVRFIEtFWS0tLS0t\n", "  tls.key: ***\n", "LS0tLS1CRUdJTiBQUklWQVRFIEtFWS0tLS0t"],
        ["kubeconfig-client-key-data",
            "    client-key-data: LS0tLS1CRUdJTiBSU0EgUFJJVkFURSBLRVk=\n", "    client-key-data: ***\n", "LS0tLS1CRUdJTiBSU0EgUFJJVkFURSBLRVk="],
        ["dockerconfigjson",
            "  .dockerconfigjson: eyJhdXRocyI6eyJyZWdpc3RyeSI6e319fQ==\n", "  .dockerconfigjson: ***\n", "eyJhdXRocyI6eyJyZWdpc3RyeSI6e319fQ=="],
        ["docker-config-auth",
            "{\"auths\":{\"registry.example.com\":{\"auth\":\"b3BzOmh1bnRlcjI=\"}}}\n",
            "{\"auths\":{\"registry.example.com\":{\"auth\":\"***\"}}}\n", "b3BzOmh1bnRlcjI="],
        ["netrc-one-line", "machine api.example.com login ops password hunter2\n", "machine api.example.com login ops password ***\n", "hunter2"],
        ["netrc-multi-line",
            "machine api.example.com\r\n  login ops\r\n  password hunter2\r\n",
            "machine api.example.com\r\n  login ops\r\n  password ***\r\n", "hunter2"],
        ["github-fine-grained-pat",
            "GH github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456\n", null,
            "11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456"],
        ["pgp-private-key-block",
            "-----BEGIN PGP PRIVATE KEY BLOCK-----\n\nlQOYBGVxQ0EBCADabcdefghijklmnop\n=AbCd\n-----END PGP PRIVATE KEY BLOCK-----\n",
            "-----BEGIN PGP PRIVATE KEY BLOCK-----\n\n***\n***\n-----END PGP PRIVATE KEY BLOCK-----\n", "lQOYBGVxQ0EBCADabcdefghijklmnop"],
    ];

    /// <summary>
    /// 新加的几条规则「不该遮的没遮」:证书与公钥、作为值出现的文件名、不是字符串的 <c>auth</c>、帮助文本与句子里的 <c>password</c>、
    /// 不够长的 <c>github_pat_</c>。逐字节原样。
    /// </summary>
    [TestMethod]
    [DataRow("  tls.crt: LS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0t\n")]
    [DataRow("    client-certificate-data: LS0tLS1CRUdJTiBDRVJU\n    certificate-authority-data: LS0tLS1CRUdJTiBDRVJU\n")]
    [DataRow("    ssl_certificate_key /etc/nginx/tls.key;\n")]
    [DataRow("{\"auth\": {\"type\": \"oauth\"}, \"oauth\": \"x\", \"author\": \"ops\"}\n")]
    [DataRow("auth: true\n")]
    [DataRow("  password  Set the account password\n")]
    [DataRow("password reset link sent to ops@example.com\n")]
    [DataRow("Password for 'https://ops@github.com': ")]
    [DataRow("the machine room password policy is strict\n")]
    [DataRow("PasswordAuthentication yes\n")]
    [DataRow("see github_pat_example in the docs\n")]
    [DataRow("-----BEGIN PGP PUBLIC KEY BLOCK-----\n\nmQINBGVxQ0EBEADabcdefghijklmnop\n-----END PGP PUBLIC KEY BLOCK-----\n")]
    [DataRow("ops@vault-secrets-01: ~$ ")]
    [DataRow("[ops@token-box: ~/deploy]$ ")]
    public void NewRules_LeaveLookalikesAlone(string text)
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes(text));
        Assert.AreEqual(text, Written, "原样、当场放出");
    }

    /// <summary>
    /// 冒号后跟空白、不加引号的值遮到行尾:值里可以有空格。同一行后面别的键不能吃掉 —— 判据是空白之后紧跟着「键」
    /// (标识符紧跟 <c>=</c>,或紧跟 <c>:</c> 而冒号后是空白),或者 YAML 注释的 <c>#</c>;<c>10:30</c>、<c>C:\</c>、<c>http://</c> 不算键。
    /// <c>名字=值</c> 仍到空白为止(<c>.env</c> 与 <c>k=v k=v</c> 日志里空格就是边界);冒号后面没有空白的也照旧。
    /// </summary>
    [TestMethod]
    public void UnquotedColonValues_AreMaskedToTheEndOfTheLine_WithoutEatingTheNextKey()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("passphrase: correct horse battery staple\n"));
        masker.Write(Bytes("secret: meet at 10:30 near C:\\vault, see http://intranet\n"));
        masker.Write(Bytes("password: abc def user: bob\n"));
        masker.Write(Bytes("password: abc def, user: bob; token: x y\n"));
        masker.Write(Bytes("token: abc def host=10.0.0.1 port=22\n"));
        masker.Write(Bytes("password: a b \"user\": \"x\"\n"));
        masker.Write(Bytes("password: it's \"quoted\" inside  \n"));
        masker.Write(Bytes("DB_PASSWORD=abc def\n"));
        masker.Write(Bytes("password:abc def\n"));
        masker.Write(Bytes("\"auth\": {\"password\": \"p4ss\"}\n"));
        Assert.AreEqual(
            "passphrase: ***\n"
            + "secret: ***\n"
            + "password: *** user: bob\n"
            + "password: ***, user: bob; token: ***\n"
            + "token: *** host=10.0.0.1 port=22\n"
            + "password: *** \"user\": \"x\"\n"
            + "password: ***  \n"
            + "DB_PASSWORD=*** def\n"
            + "password:*** def\n"
            + "\"auth\": {\"password\": \"***\"}\n",
            Written, "不是字符串的 \"auth\" 不配,也不能把里面的 password 一起跳过");
    }

    /// <summary>
    /// 到行尾的值在行还没完时扣着:停在值里、停在值后面的空白上、停在看不出是不是下一个键的半截上,都等下一段;
    /// 下一段里看出是键了,值就停在键前面。超过扣下的上限照样吞到同一个终点。
    /// </summary>
    [TestMethod]
    public void UnquotedColonValue_IsHeldUntilItsEndIsKnown()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("passphrase: correct "));
        Assert.AreEqual("passphrase: ", Written, "值后面的空白之后可能还有词:扣着");
        masker.Write(Bytes("horse user"));
        Assert.AreEqual("passphrase: ", Written, "user 后面还看不出是不是键");
        masker.Write(Bytes(": bob\n"));
        Assert.AreEqual("passphrase: *** user: bob\n", Written);
        _written.Clear();

        string value = string.Concat(Enumerable.Repeat("word ", 1200));
        masker.Write(Bytes("password: " + value[..3000]));
        masker.Write(Bytes(value[3000..] + "us"));
        masker.Write(Bytes("er: bob # note\n"));
        Assert.AreEqual("password: *** user: bob # note\n", Written, "吞的时候同一个判据:停在下一个键前面");
    }

    /// <summary><c>.netrc</c> 分行写的 <c>password</c> 只认行首的单独一行;后面再来一个词就不算(扣着的那一点原样放出)。</summary>
    [TestMethod]
    public void NetrcPasswordLine_OnlyCountsWhenItIsTheWholeLine()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("  password hunt"));
        Assert.AreEqual("  password ", Written, "可能是 netrc 的口令行:扣着等行尾");
        masker.Write(Bytes("er2\r\n"));
        Assert.AreEqual("  password ***\r\n", Written);
        _written.Clear();

        masker.Write(Bytes("password changed"));
        masker.Write(Bytes(" for ops\r\n"));
        masker.Write(Bytes("password changed "));
        Assert.AreEqual("password changed for ops\r\npassword ", Written, "停在词后面的空白上也还不知道后面有没有词:接着扣");
        masker.Write(Bytes("for ops\r\n"));
        Assert.AreEqual("password changed for ops\r\npassword changed for ops\r\n", Written, "后面还有词:不是 netrc,原样");
        _written.Clear();

        masker.Write(Bytes("$ grep -r x .\r\n./a: password hunter2\r\n"));
        Assert.AreEqual("$ grep -r x .\r\n./a: password hunter2\r\n", Written, "不在行首的不算");
    }

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

    /// <summary>名字与分隔符当场放出,只扣值,扣到值结束(这里是行尾)为止。</summary>
    [TestMethod]
    public void ValueSplitAcrossChunks_OnlyTheValueIsHeld()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("export TOKEN=ab"));
        Assert.AreEqual("export TOKEN=", Written, "正写到值的中间:名字与分隔符放出,值扣下");
        masker.Write(Bytes("cdef\nnext "));
        masker.Write(Bytes("SECRET="));
        Assert.AreEqual("export TOKEN=***\nnext SECRET=", Written, "值还没开始:没有可扣的");
        masker.Write(Bytes("xyz\n"));
        Assert.AreEqual("export TOKEN=***\nnext SECRET=***\n", Written);
        Assert.DoesNotContain("ab", Written.Replace("next", ""));
    }

    /// <summary>
    /// 到行尾都没收尾的引号串(shell 的续行、截断的 JSON):扣着的值等到换行不能原文放出去,从引号之后遮到行尾;
    /// 不跨行,开头的引号照放,也不凭空补一个收尾的引号。行中间的 <c>\r</c>(进度条)同样算行尾,与双引号串的吞法一致。
    /// </summary>
    [TestMethod]
    public void UnclosedQuotedValue_IsMaskedToTheEndOfTheLine()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("password=\"abc def"));
        Assert.AreEqual("password=", Written, "引号串还没收尾:扣下");
        masker.Write(Bytes("\r\n$ "));
        Assert.AreEqual("password=\"***\r\n$ ", Written, "到行尾都没收尾:遮到行尾,换行与提示符照放");
        _written.Clear();

        masker.Write(Bytes("export TOKEN='abc def\r\n> rest'\r\n"));
        masker.Write(Bytes("\"body\": \"{\\\"password\\\":\\\"abc def\n"));
        masker.Write(Bytes("token=\"abc\rprogress 50%\r\n"));
        masker.Write(Bytes("password=\"\nPassword: \"\n"));
        Assert.AreEqual(
            "export TOKEN='***\r\n> rest'\r\n"
            + "\"body\": \"{\\\"password\\\":\\\"***\n"
            + "token=\"***\rprogress 50%\r\n"
            + "password=\"\nPassword: \"\n",
            Written, "只遮到行尾,下一行照常;引号后面什么都没有的不动");
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

    /// <summary>
    /// 等输入的提示一个字节也不扣、也不改:值还没开始,没有可扣的。
    /// 尤其是 macOS 上 sudo / su 那种不带尾随空格的 <c>Password:</c> —— 扣下的话,它要等到敲完回车才进记录。
    /// </summary>
    [TestMethod]
    [DataRow("Password:")]
    [DataRow("Password: ")]
    [DataRow("[sudo] password for ops: ")]
    [DataRow("ops@10.0.0.5's password: ")]
    [DataRow("Enter passphrase for key '/home/ops/.ssh/id_ed25519': ")]
    [DataRow("Vault password: ")]
    [DataRow("Token:")]
    [DataRow("ops@web-01:~$ ")]
    [DataRow("[ops@web-01 deploy]$ ")]
    [DataRow("root@db-01:/var/lib/mysql# ")]
    [DataRow("ops@MacBook-Pro ~ % ")]
    [DataRow("PS C:\\Users\\ops> ")]
    [DataRow("postgres=# ")]
    public void WaitingPrompts_GoOutAtOnceAndUnchanged(string prompt)
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes(prompt));
        Assert.AreEqual(prompt, Written, "提示当场原样放出,不等下一段输出");
    }

    /// <summary>不相干的输出逐字节原样:目录列表、带关键词却没有值的文件名、日志行、带标题的提示符。</summary>
    [TestMethod]
    public void OrdinaryOutput_IsUntouched()
    {
        const string text = "total 48\r\ndrwxr-xr-x  2 ops ops 4096 Oct 22 10:00 .\r\n-rw-------  1 ops ops  411 Oct 22 09:58 .bash_history\r\n"
            + "tokenizer.json  secrets.md  api_keys/  credentials.example\r\n"
            + "2026-10-22 10:00:01 INFO  [worker-3] request GET /api/v1/items?id=42 200 12ms\r\n"
            + "\u001b[?2004h\u001b]0;ops@web-01: ~/deploy\u0007ops@web-01:~/deploy$ ";
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes(text));
        Assert.AreEqual(text, Written);
    }

    [TestMethod]
    public void GrepColorBetweenNameAndValue_StillMasks()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("\u001b[01;31m\u001b[KGITHUB_TOKEN\u001b[m\u001b[K=plain-value-1\n"));
        Assert.AreEqual("\u001b[01;31m\u001b[KGITHUB_TOKEN\u001b[m\u001b[K=***\n", Written);
    }

    /// <summary>值本身上了色也照遮:jq、bat 一类工具在分隔符与值之间插颜色序列(这里是它们的原样字节)。</summary>
    [TestMethod]
    public void ColouredValues_AreMasked()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("\u001b[1;34m\"password\"\u001b[0m\u001b[1;39m: \u001b[0m\u001b[0;32m\"hunter2\"\u001b[0m"));
        masker.Write(Bytes("\r\n\u001b[38;5;203mDB_PASSWORD\u001b[0m\u001b[38;5;231m=\u001b[0m\u001b[38;5;186mhunter2\u001b[0m"));
        masker.Flush();
        Assert.AreEqual(
            "\u001b[1;34m\"password\"\u001b[0m\u001b[1;39m: \u001b[0m\u001b[0;32m\"***\"\u001b[0m"
            + "\r\n\u001b[38;5;203mDB_PASSWORD\u001b[0m\u001b[38;5;231m=\u001b[0m\u001b[38;5;186m***\u001b[0m",
            Written);
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

    /// <summary>
    /// 多行私钥块的最后一行正文与 END 写在同一行(<c>…Qy==-----END …</c>,手工拼接或截断的输出):END 前面的正文换成 <c>***</c>,END 照放;
    /// 只有缩进的(YAML 里的块)照原样,END 后面的照常跑别的规则。
    /// </summary>
    [TestMethod]
    public void PrivateKeyBlock_BodyBeforeEndOnTheSameLineIsMasked()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("-----BEGIN RSA PRIVATE KEY-----\nAAAA\nBBBBsecret2-----END RSA PRIVATE KEY-----\n$ "));
        masker.Write(Bytes("key: |\n  -----BEGIN EC PRIVATE KEY-----\n  MHcCAQEE\n  -----END EC PRIVATE KEY-----\nnext: 1\n"));
        masker.Write(Bytes("-----BEGIN OPENSSH PRIVATE KEY-----\r\nb3BlbnNz\r\nQyNT"));
        masker.Write(Bytes("UxOQ-----END OPENSSH PRIVATE KEY----- password=hunter2\r\n"));
        Assert.AreEqual(
            "-----BEGIN RSA PRIVATE KEY-----\n***\n***-----END RSA PRIVATE KEY-----\n$ "
            + "key: |\n  -----BEGIN EC PRIVATE KEY-----\n***\n  -----END EC PRIVATE KEY-----\nnext: 1\n"
            + "-----BEGIN OPENSSH PRIVATE KEY-----\r\n***\r\n***-----END OPENSSH PRIVATE KEY----- password=***\r\n",
            Written);
    }

    /// <summary>
    /// 私钥写在一行里(JSON 与 <c>.env</c> 里 <c>\n</c> 是字面量):BEGIN 与 END 之间的正文遮掉,没有 END 就遮到行尾、下一行起照旧按私钥块整行遮;
    /// 同一行里的其他规则照跑。
    /// </summary>
    [TestMethod]
    public void PrivateKeyOnOneLine_BodyIsMaskedAndTheOtherRulesStillRun()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("  \"private_key_id\": \"0123abcd\",\r\n"
            + "  \"private_key\": \"-----BEGIN PRIVATE KEY-----\\nMIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQC7\\n-----END PRIVATE KEY-----\\n\",\r\n"));
        masker.Write(Bytes("cert: \"-----BEGIN EC PRIVATE KEY-----\\nMHcCAQEEIBkeybody\\n-----END EC PRIVATE KEY-----\\n\" password=hunter2\n"));
        masker.Write(Bytes("$ head -c 64 key.json\r\n{\"k\": \"-----BEGIN RSA PRIVATE KEY-----\\nMIIEpAIBAAKCAQEA\r\n"));
        masker.Write(Bytes("-----END RSA PRIVATE KEY-----\r\n$ "));
        Assert.AreEqual(
            "  \"private_key_id\": \"***\",\r\n  \"private_key\": \"***\",\r\n"
            + "cert: \"-----BEGIN EC PRIVATE KEY-----***-----END EC PRIVATE KEY-----\\n\" password=***\n"
            + "$ head -c 64 key.json\r\n{\"k\": \"-----BEGIN RSA PRIVATE KEY-----***\r\n"
            + "-----END RSA PRIVATE KEY-----\r\n$ ",
            Written);
    }

    /// <summary>
    /// 收了尾的引号串恰好停在已收到内容的末尾,里面却有一段还没完的私钥正文:收尾时整段遮掉,照私钥正文的结束条件吞
    /// (照引号的结束条件吞,会在那个收尾引号上原地打转,直到栈溢出把读循环连同整个程序带走)。
    /// </summary>
    [TestMethod]
    public void ClosedQuoteAroundAnUnfinishedKeyBody_IsMaskedOnFlush()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("PRIVATE_KEY=\"x -----BEGIN RSA PRIVATE KEY-----Bearer abc\""));
        Assert.AreEqual("PRIVATE_KEY=", Written, "私钥正文还可能接着长:整段扣下");
        masker.Flush();
        Assert.AreEqual("PRIVATE_KEY=***", Written);
    }

    [TestMethod]
    public void BearerAndPrefixedTokens_AreMasked()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("Authorization: Bearer eyJhbGciOi.payload.sig\n"));
        masker.Write(Bytes("token: Bearer abcdefgh123\n"));
        masker.Write(Bytes("aws AKIAIOSFODNN7EXAMPLE gh ghp_0123456789abcdefghijABCDEFGHIJ0123 sk-proj-ABCDEFGHIJKLMNOPQRST\n"));
        string text = Written;
        // 「token: Bearer …」冒号后跟空白、不加引号:值到行尾,与里面的 Bearer 凭据并成一段
        Assert.AreEqual("Authorization: Bearer ***\ntoken: ***\naws *** gh *** ***\n", text);
    }

    /// <summary>
    /// 令牌紧挨着汉字也认得出:按 Latin-1 映射之后,UTF-8 的 0xAA、0xB5、0xBA 与 0xC0–0xFF 都会被当成字母,
    /// 「为」(E4 B8 BA)、「个」(E4 B8 AA)后面、「是」(E6 …)前面就都不算词边界了 —— 边界只能按 ASCII 算。
    /// </summary>
    [TestMethod]
    public void TokensNextToNonAsciiText_AreMasked()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("令牌为sk-proj-ABCDEFGHIJKLMNOPQRST\n"));
        masker.Write(Bytes("新的一个ghp_0123456789abcdefghijABCDEFGHIJ0123是新的\n"));
        masker.Write(Bytes("改为Bearer abcdefgh123\n"));
        masker.Write([0xB5, .. "AKIAIOSFODNN7EXAMPLE"u8, 0xFF, (byte)'\n']);
        Assert.AreEqual("令牌为***\n", Encoding.UTF8.GetString(_written[0]));
        Assert.AreEqual("新的一个***是新的\n", Encoding.UTF8.GetString(_written[1]));
        Assert.AreEqual("改为Bearer ***\n", Encoding.UTF8.GetString(_written[2]));
        byte[] latin1Neighbours = [0xB5, .. "***"u8, 0xFF, (byte)'\n'];
        CollectionAssert.AreEqual(latin1Neighbours, _written[3]);
    }

    /// <summary>
    /// 令牌与凭据紧跟在颜色序列后面也认得出(bat、grep 高亮、带色的日志):<c>ESC[0m</c>、<c>ESC[K</c> 的收尾字节是字母,
    /// 只看前一个字符就当成了「前面挨着字母」。序列原样留着;序列后面接着的是一个词的中间时照旧不算。
    /// </summary>
    [TestMethod]
    public void TokensRightAfterAControlSequence_AreMasked()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("\u001b[38;5;231mghp_0123456789abcdefghijABCDEFGHIJ0123\u001b[0m\n"));
        masker.Write(Bytes("\u001b[32msk-proj-ABCDEFGHIJKLMNOPQRSTUV\u001b[0m\n"));
        masker.Write(Bytes("\u001b[0mAKIAIOSFODNN7EXAMPLE\n"));
        masker.Write(Bytes("\u001b[36mAuthorization\u001b[39m: \u001b[37mBearer eyJhbGciOiJIUzI1NiJ9.payload.sig\u001b[39m\n"));
        masker.Write(Bytes("\u001b[01;31m\u001b[Kghp_0123456789abcdefghijABCDEFGHIJ0123\u001b[m\u001b[K\n"));
        masker.Write(Bytes("\u001b[0mxBearer abcdefghij \u001b[0mmy_ghp_0123456789abcdefghijABCDEFGHIJ0123\n"));
        Assert.AreEqual(
            "\u001b[38;5;231m***\u001b[0m\n"
            + "\u001b[32m***\u001b[0m\n"
            + "\u001b[0m***\n"
            + "\u001b[36mAuthorization\u001b[39m: \u001b[37mBearer ***\u001b[39m\n"
            + "\u001b[01;31m\u001b[K***\u001b[m\u001b[K\n"
            + "\u001b[0mxBearer abcdefghij \u001b[0mmy_ghp_0123456789abcdefghijABCDEFGHIJ0123\n",
            Written);
    }

    /// <summary>引号串认反斜杠转义;JSON 里转义过的名字(<c>\"password\":</c>,如 aws secretsmanager 的 SecretString)也认得出。</summary>
    [TestMethod]
    public void EscapedQuotesAndEscapedJsonKeys_AreHonoured()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("{\"client_secret\": \"a\\\"b c\", \"user\": \"x\"}\n"));
        masker.Write(Bytes("    \"SecretString\": \"{\\\"username\\\":\\\"admin\\\",\\\"password\\\":\\\"hunter2\\\"}\",\n"));
        masker.Write(Bytes("\"last-applied\": \"{\\\"data\\\":{\\\"password\\\":\\\"aHVudGVyMg==\\\"}}\"\n"));
        Assert.AreEqual(
            "{\"client_secret\": \"***\", \"user\": \"x\"}\n"
            + "    \"SecretString\": \"***\",\n"
            + "\"last-applied\": \"{\\\"data\\\":{\\\"password\\\":\\\"***\\\"}}\"\n",
            Written);
    }

    /// <summary>值不吞控制字符:OSC 标题收尾的 BEL 留在原处(否则回放时标题一直不结束,把提示符也吃进去),DEL、退格同理。</summary>
    [TestMethod]
    public void ControlCharacters_EndAValue()
    {
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("password=abc\u0007rest token=xyz\u007f secret=q\u0008\u0008\n"));
        Assert.AreEqual("password=***\u0007rest token=***\u007f secret=***\u0008\u0008\n", Written);
        _written.Clear();
        masker.Write(Bytes("\u001b]0;ops@vault-secrets-01: ~\u0007ops@vault-secrets-01:~$ "));
        string replay = Written;
        Assert.AreEqual(1, replay.Count(c => c == '\u0007'), "标题收尾的 BEL 还在");
        Assert.StartsWith("ops@vault-secrets-01:", replay[(replay.IndexOf('\u0007') + 1)..], "BEL 之后紧接着就是提示符,标题在原处结束");
    }

    /// <summary>
    /// 任意一处切开、以及打字回显(一个字节一段)都不漏:名字、颜色序列、<c>Bea|rer</c>、<c>-----BEGI|N</c> 切在半截照样认得出。
    /// 除了带前缀的令牌被切在前缀里(那几个字节已经放出去了),结果与一次写进来逐字节相同。
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(SecretLines))]
    public void AnySplitOrByteByByteEcho_LeaksNothing(string name, string line, string? expected, string secret)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        var runs = new List<(string How, string Output)> { ("一次写入", Run([bytes])) };
        for (int at = 1; at < bytes.Length; at++)
        {
            runs.Add(($"切在第 {at} 字节", Run([bytes[..at], bytes[at..]])));
        }
        runs.Add(("逐字节回显", Run(bytes.Select(b => new[] { b }))));

        foreach ((string how, string output) in runs)
        {
            if (expected is not null)
            {
                Assert.AreEqual(expected, output, $"{name}:{how}");
            }
            for (int i = 0; i + 4 <= secret.Length; i++)
            {
                Assert.DoesNotContain(secret.Substring(i, 4), output, $"{name}:{how}漏出了密钥的一段");
            }
        }
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
        Assert.AreEqual("TOKEN=***", Written, "超过上限不再扣,先放一个 ***");
        masker.Write(Bytes(new string('a', 1000)));
        Assert.AreEqual("TOKEN=***", Written, "值还没完:剩下的接着吞,尾巴不漏");
        _written.Clear();
        masker.Write(Bytes(" PASSWORD=half"));
        Assert.AreEqual(" PASSWORD=", Written, "值结束之后照常处理,新的值扣下");
        masker.Flush();
        Assert.AreEqual(" PASSWORD=***", Written, "停止记录时扣着的那一点也遮完放出");
    }

    /// <summary>
    /// 值超过扣下的上限:先放一个 <c>***</c>,剩下的部分接着吞到值结束为止,尾巴一个字节也不漏。
    /// 引号串里切在转义中间的反斜杠照样算数;写在一行里的私钥正文吞到 END 为止,之后不会误进私钥块。
    /// </summary>
    [TestMethod]
    public void ValueOverTheHoldCap_IsMaskedToItsEnd()
    {
        string value = new('Q', 6000);
        SessionOutputMasker masker = NewMasker();
        masker.Write(Bytes("token: " + value[..3000]));
        masker.Write(Bytes(value[3000..5000]));
        masker.Write(Bytes(value[5000..] + " next=1\r\n"));
        masker.Write(Bytes("{\"password\": \"" + value[..3000]));
        masker.Write(Bytes(value[3000..5000] + "\\"));
        masker.Write(Bytes("\"" + value[5000..] + "\", \"user\": \"x\"}\r\n"));
        masker.Write(Bytes("\"pem\": \"-----BEGIN PRIVATE KEY-----\\n" + value[..3000]));
        masker.Write(Bytes(value[3000..]));
        masker.Write(Bytes("\\n-----END PRIVATE KEY-----\\n\"\r\n$ ls\r\n"));
        Assert.AreEqual(
            "token: *** next=1\r\n{\"password\": \"***\", \"user\": \"x\"}\r\n"
            + "\"pem\": \"-----BEGIN PRIVATE KEY-----***-----END PRIVATE KEY-----\\n\"\r\n$ ls\r\n",
            Written);
    }

    /// <summary>
    /// 规则都是线性的:16 KB 的关键词连串、超长标识符、一长串令牌前缀……写进去再 Flush,远不到一秒。
    /// 遮盖跑在桥的读循环上,Flush 在 UI 线程上 —— 旧的 <c>名字=值</c> 规则在 4 KB 的 <c>tokentoken…</c> 上要跑半分钟。
    /// </summary>
    [TestMethod]
    public void PathologicalInput_StaysLinear()
    {
        const int size = 16 * 1024;
        static string Repeat(string unit) => string.Concat(Enumerable.Repeat(unit, (size / unit.Length) + 1))[..size];
        (string Name, string Text)[] inputs =
        [
            ("关键词连串", Repeat("token")),
            ("关键词连串 + 换行", Repeat("token") + "\r\n"),
            ("关键词连串 + 分隔符", Repeat("token") + " password="),
            ("关键词连串 + 扣着的值", "password: \"" + Repeat("token")[..4000]),
            ("长标识符", Repeat("a") + "\r\n"),
            ("令牌前缀连串", Repeat("sk-") + "!"),
            ("断开的令牌", Repeat("ghp_abc-")),
            ("Basic 连串", Repeat("basic ")),
            ("引号连串", Repeat("password=\"x ")),
            ("私钥开头连串", Repeat("-----BEGIN ")),
            ("颜色序列连串", Repeat("token\u001b[1m")),
            ("颜色序列后的令牌前缀连串", Repeat("\u001b[1;2mghp_")),
            ("超长的控制序列参数", "\u001b[" + Repeat("1;") + "mBearer abcdefghij\r\n"),
            ("没收尾的引号串里满是名字", "password=\"" + Repeat("token=x ") + "\r\n"),
            ("到行尾的值里满是词", "password: " + Repeat("a ") + "\r\n"),
            ("到行尾的值里满是像键的词", "password: " + Repeat("user:x ") + "\r\n"),
            ("到行尾的值后一长串空白", "password: a" + Repeat(" ") + "b\r\n"),
            ("到行尾的值连串", Repeat("token: a ")),
            ("扣着的到行尾的值", "passphrase: " + Repeat("ab ")[..4000]),
            ("netrc 连串", Repeat("machine a login b ") + "\r\n"),
            ("netrc 口令行", "password " + Repeat("a")),
        ];
        foreach ((string name, string text) in inputs)
        {
            var masker = new SessionOutputMasker(_ => { });
            var watch = Stopwatch.StartNew();
            Task work = Task.Run(() =>
            {
                masker.Write(Encoding.Latin1.GetBytes(text));
                masker.Flush();
            });
            Assert.IsTrue(work.Wait(TimeSpan.FromSeconds(10)), $"{name}:10 秒都没跑完");
            Assert.IsLessThan(1000L, watch.ElapsedMilliseconds, $"{name}:{watch.ElapsedMilliseconds} ms");
        }
    }
}
