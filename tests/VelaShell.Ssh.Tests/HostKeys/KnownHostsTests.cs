// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: OpenSSH sshd(8) 的 SSH_KNOWN_HOSTS 章节;velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.HostKeys;

[TestClass]
[TestCategory("HostKeys")]
public sealed class KnownHostsTests
{
    private static SshPublicKey MakeKey() =>
        SshPublicKey.Decode(TestHostKey.Create("ssh-ed25519").PublicKeyBlob);

    private static string Line(string host, SshPublicKey key) =>
        $"{host} {key.KeyType} {Convert.ToBase64String(key.Blob.Span)}";

    private static SshHostKeyContext Context(string host, int port, SshPublicKey key) => new()
    {
        Host = host,
        Port = port,
        Key = key,
        NegotiatedAlgorithm = key.KeyType,

    };

    // ------------------------------------------------------------ 解析

    [TestMethod]
    public void 注释与空行被跳过()
    {
        SshPublicKey key = MakeKey();
        string content =
            "# 这是注释\n" +
            "\n" +
            "   \n" +
            Line("example.com", key) + "\n";

        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(content);

        Assert.HasCount(1, entries);
        Assert.AreEqual(4, entries[0].LineNumber, "行号要按原文件算，注释与空行也占行");
    }

    [TestMethod]
    public void 写坏的行被跳过而不是让整个文件不可用()
    {
        SshPublicKey key = MakeKey();
        string content =
            "这行完全不对\n" +
            "host ssh-ed25519 这不是合法的base64!!!\n" +
            "只有两段 ssh-ed25519\n" +
            Line("good.example.com", key) + "\n";

        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(content);

        // 真实的 known_hosts 里什么都有。为其中一行报错，
        // 等于让用户所有已知主机一起失效。
        Assert.HasCount(1, entries);
        Assert.AreSequenceEqual(["good.example.com"], [.. entries[0].Patterns]);
    }

    [TestMethod]
    public void 逗号分隔的多个主机名()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries =
            KnownHostsFile.Parse(Line("a.example.com,b.example.com,10.0.0.1", key));

        Assert.HasCount(3, entries[0].Patterns);

        foreach (string host in new[] { "a.example.com", "b.example.com", "10.0.0.1" })
        {
            Assert.AreEqual(
                KnownHostStatus.Known,
                KnownHostsFile.Lookup(entries, host, 22, key).Status, host);
        }
    }

    /// <summary>字段之间用 Tab 分隔的行照样认（曾经只按空格切，这样的行被静默跳过，那台主机一直按「没见过」处理）。</summary>
    [TestMethod]
    public void Tab分隔的行照样认出来()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries =
            KnownHostsFile.Parse($"@cert-authority\t*.corp\t{key.KeyType} {Convert.ToBase64String(key.Blob.Span)}\n" +
                                 $"server.example\t{key.KeyType}\t \t{Convert.ToBase64String(key.Blob.Span)}");

        Assert.HasCount(2, entries);
        Assert.IsTrue(entries[0].IsCertificateAuthority);
        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "server.example", 22, key).Status);
    }

    [TestMethod]
    public void 标记行被认出来()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(
            "@revoked " + Line("bad.example.com", key) + "\n" +
            "@cert-authority " + Line("*.example.com", key) + "\n");

        Assert.IsTrue(entries[0].IsRevoked);
        Assert.IsTrue(entries[1].IsCertificateAuthority);
    }

    /// <summary>
    /// 认不出的标记整行跳过。曾经它落进普通分支：把 @revoked 写成 @revoke 想吊销一把钥，
    /// 结果这把钥对模式匹配到的所有主机都成了「已知」—— 与本意正好相反。
    /// </summary>
    [TestMethod]
    [DataRow("@revoke", DisplayName = "拼错的吊销")]
    [DataRow("@REVOKED", DisplayName = "大小写不对")]
    [DataRow("@future-marker", DisplayName = "将来新增的标记")]
    public void 认不出的标记整行跳过而不是当成受信行(string marker)
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse($"{marker} " + Line("*", key) + "\n");

        Assert.IsEmpty(entries);
        Assert.AreEqual(KnownHostStatus.Unknown, KnownHostsFile.Lookup(entries, "victim.example.com", 22, key).Status);
    }

    // ------------------------------------------------------------ 查询

    [TestMethod]
    public void 主机与密钥都对上就是已知()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("example.com", key));

        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "example.com", 22, key).Status);
    }

    [TestMethod]
    public void 没见过的主机是未知不是变了()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("other.example.com", key));

        // 「没见过」与「变了」是两件完全不同的事 —— 前者可以问，后者该拒。
        Assert.AreEqual(KnownHostStatus.Unknown, KnownHostsFile.Lookup(entries, "example.com", 22, key).Status);
    }

    [TestMethod]
    public void 主机对上但密钥不同就是变了并指出行号()
    {
        SshPublicKey known = MakeKey();
        SshPublicKey different = MakeKey();

        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(
            "# 头一行是注释\n" + Line("example.com", known) + "\n");

        KnownHostLookup lookup = KnownHostsFile.Lookup(entries, "example.com", 22, different);

        Assert.AreEqual(KnownHostStatus.Changed, lookup.Status);
        Assert.HasCount(1, lookup.ConflictingEntries);

        // 行号要能指出来 —— 否则用户不知道该去删哪一行。
        Assert.AreEqual(2, lookup.ConflictingEntries[0].LineNumber);
    }

    [TestMethod]
    public void 同一台主机可以有多把不同类型的密钥()
    {
        var ed25519 = SshPublicKey.Decode(TestHostKey.Create("ssh-ed25519").PublicKeyBlob);
        var rsa = SshPublicKey.Decode(TestHostKey.Create("ssh-rsa").PublicKeyBlob);

        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(
            Line("example.com", ed25519) + "\n" + Line("example.com", rsa) + "\n");

        // 两把都该算「已知」。只按「主机对上、密钥不同」就报「变了」的实现，
        // 会在服务器同时提供 ed25519 与 rsa 时随机报警。
        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "example.com", 22, ed25519).Status);
        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "example.com", 22, rsa).Status);
    }

    [TestMethod]
    public void 吊销压过一切()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(
            Line("example.com", key) + "\n" +
            "@revoked " + Line("example.com", key) + "\n");

        Assert.AreEqual(
            KnownHostStatus.Revoked,
            KnownHostsFile.Lookup(entries, "example.com", 22, key).Status,
            "哪怕别处还有一条「已知」，吊销也要赢");
    }

    [TestMethod]
    public void 非默认端口按方括号形式匹配()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("[example.com]:2222", key));

        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "example.com", 2222, key).Status);
        Assert.AreEqual(
            KnownHostStatus.Unknown,
            KnownHostsFile.Lookup(entries, "example.com", 22, key).Status,
            "端口不同就是不同的条目 —— 22 端口上的那台机器可能完全是另一台");
    }

    /// <summary>
    /// IPv6 字面量配非 22 端口：读写都是 <c>[::1]:2222</c> 这种方括号形式（端口前的冒号与地址里的冒号分得开）；
    /// 22 端口写成不带括号的地址本身。十六进制大小写不同也是同一台。散列行同样按方括号形式算。
    /// </summary>
    [TestMethod]
    public void IPv6配非默认端口按方括号形式读写()
    {
        SshPublicKey key = MakeKey();

        IReadOnlyList<KnownHostEntry> parsed = KnownHostsFile.Parse(Line("[::1]:2222", key));
        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(parsed, "::1", 2222, key).Status);
        Assert.AreEqual(KnownHostStatus.Unknown, KnownHostsFile.Lookup(parsed, "::1", 22, key).Status, "端口不同就是不同的条目");

        Assert.StartsWith("[::1]:2222 ", KnownHostsFile.FormatEntry("::1", 2222, key));
        Assert.StartsWith("::1 ", KnownHostsFile.FormatEntry("::1", 22, key));

        string written = KnownHostsFile.FormatEntry("2001:DB8::5", 2222, key);
        Assert.StartsWith("[2001:db8::5]:2222 ", written);
        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(KnownHostsFile.Parse(written), "2001:db8::5", 2222, key).Status);

        string hashed = KnownHostsFile.FormatEntry("::1", 2222, key, hashHostName: true);
        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(KnownHostsFile.Parse(hashed), "::1", 2222, key).Status);
        Assert.AreEqual(KnownHostStatus.Unknown, KnownHostsFile.Lookup(KnownHostsFile.Parse(hashed), "::1", 22, key).Status);
    }

    [TestMethod]
    public void 通配模式()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("*.example.com", key));

        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "a.example.com", 22, key).Status);
        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "b.c.example.com", 22, key).Status);
        Assert.AreEqual(KnownHostStatus.Unknown, KnownHostsFile.Lookup(entries, "example.org", 22, key).Status);
    }

    [TestMethod]
    public void 散列过的主机名也能匹配()
    {
        // OpenSSH 的 HashKnownHosts yes 在很多发行版上是默认 ——
        // 不支持它等于在那些机器上完全读不到已知主机。
        SshPublicKey key = MakeKey();
        byte[] salt = RandomNumberGenerator.GetBytes(20);

#pragma warning disable CA5350 // 格式由 OpenSSH 规定就是 HMAC-SHA1
        byte[] hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes("secret.example.com"));
#pragma warning restore CA5350

        string hashedHost = string.Create(
            CultureInfo.InvariantCulture,
            $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}");

        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line(hashedHost, key));

        Assert.IsTrue(entries[0].IsHashed);
        Assert.AreEqual(
            KnownHostStatus.Known,
            KnownHostsFile.Lookup(entries, "secret.example.com", 22, key).Status);
        Assert.AreEqual(
            KnownHostStatus.Unknown,
            KnownHostsFile.Lookup(entries, "other.example.com", 22, key).Status);
    }

    /// <summary>
    /// 散列不是 20 字节（截断、手改过）的散列行就是对不上，不抛 ——
    /// <see cref="HMACSHA1.Verify(byte[], byte[], byte[])"/> 遇到长度不对的散列会抛 <see cref="ArgumentException"/>，
    /// 一行坏掉的记录不能让整个查找失败。
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(19)]
    [DataRow(21)]
    [DataRow(32)]
    public void 散列长度不对的散列行当成对不上(int hashLength)
    {
        SshPublicKey key = MakeKey();
        byte[] salt = RandomNumberGenerator.GetBytes(20);

#pragma warning disable CA5350 // 格式由 OpenSSH 规定就是 HMAC-SHA1
        byte[] full = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes("secret.example.com"));
#pragma warning restore CA5350
        byte[] hash = new byte[hashLength];
        full.AsSpan(0, Math.Min(hashLength, full.Length)).CopyTo(hash);

        string hashedHost = string.Create(
            CultureInfo.InvariantCulture,
            $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}");
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line(hashedHost, key));

        Assert.AreEqual(KnownHostStatus.Unknown, KnownHostsFile.Lookup(entries, "secret.example.com", 22, key).Status);
    }

    /// <summary>
    /// OpenSSH 先把主机名小写化再散列。用户填的是大写时也要对得上 ——
    /// 曾经对不上就是「没见过」：有中间人时，本该报「密钥变了」的连接成了「新主机，要信任吗」。
    /// </summary>
    [TestMethod]
    public void 散列行按小写主机名比对_大小写不同也对得上()
    {
        SshPublicKey known = MakeKey();
        SshPublicKey different = MakeKey();
        byte[] salt = RandomNumberGenerator.GetBytes(20);

#pragma warning disable CA5350 // 格式由 OpenSSH 规定就是 HMAC-SHA1
        byte[] hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes("[server.example.com]:2222"));
#pragma warning restore CA5350

        string hashedHost = string.Create(
            CultureInfo.InvariantCulture,
            $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}");
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line(hashedHost, known));

        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "Server.Example.COM", 2222, known).Status);
        Assert.AreEqual(KnownHostStatus.Changed, KnownHostsFile.Lookup(entries, "Server.Example.COM", 2222, different).Status,
            "大小写不同就当成没见过的话，中间人换一把钥只会换来一句「要信任吗」");
        Assert.AreSequenceEqual([known.KeyType], [.. KnownHostsFile.KnownKeyTypes(entries, "SERVER.example.com", 2222)]);
    }

    [TestMethod]
    public void 写出的散列行按小写主机名算_OpenSSH读得到()
    {
        SshPublicKey key = MakeKey();
        string line = KnownHostsFile.FormatEntry("Server.Example.COM", 22, key, hashHostName: true);
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(line);

        // 拆开散列行，按 OpenSSH 的口径（小写名字的 HMAC）自己验一遍。
        string[] parts = entries[0].Patterns[0].Split('|');
#pragma warning disable CA5350
        byte[] expected = HMACSHA1.HashData(Convert.FromBase64String(parts[2]), Encoding.UTF8.GetBytes("server.example.com"));
#pragma warning restore CA5350
        Assert.AreEqual(Convert.ToBase64String(expected), parts[3]);
    }

    [TestMethod]
    public void 写出来的行能被自己读回去()
    {
        SshPublicKey key = MakeKey();

        foreach (bool hashed in new[] { false, true })
        {
            string line = KnownHostsFile.FormatEntry("example.com", 2222, key, hashed);
            IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(line);

            Assert.AreEqual(hashed, entries[0].IsHashed);
            Assert.AreEqual(
                KnownHostStatus.Known,
                KnownHostsFile.Lookup(entries, "example.com", 2222, key).Status,
                $"hashed={hashed}");
        }
    }

    // ------------------------------------------------------------ 策略

    [TestMethod]
    public async Task 已知主机直接通过()
    {
        SshPublicKey key = MakeKey();
        string path = await WriteTempAsync(Line("example.com", key));

        try
        {
            KnownHostsPolicy policy = new(path);
            SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context("example.com", 22, key));
            Assert.AreEqual(SshHostKeyDecision.Accept, verdict.Decision);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task 密钥变了时拒绝并指出该去删哪一行()
    {
        SshPublicKey known = MakeKey();
        SshPublicKey different = MakeKey();
        string path = await WriteTempAsync(Line("example.com", known));

        try
        {
            KnownHostsPolicy policy = new(path);
            SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context("example.com", 22, different));

            Assert.AreEqual(SshHostKeyDecision.Reject, verdict.Decision);
            Assert.AreEqual(SshFailureReason.HostKeyChanged, verdict.Reason, "「变了」要与「不信任」分开报，界面上给的提示完全不同。");

            // 消息里必须把三件事说清楚：变了、可能是什么、下一步怎么办。
            Assert.Contains("变了", verdict.Message!);
            Assert.Contains("中间人", verdict.Message!);
            Assert.Contains("第 1 行", verdict.Message!);
            Assert.Contains(different.Sha256Fingerprint, verdict.Message!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task 首次连接可以问并记下来()
    {
        SshPublicKey key = MakeKey();
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");

        try
        {
            int asked = 0;
            KnownHostsPolicy policy = new(path, (_, _) =>
            {
                asked++;
                return ValueTask.FromResult(true);
            });

            SshHostKeyContext context = Context("new.example.com", 22, key);
            SshHostKeyVerdict verdict = await policy.EvaluateAsync(context);

            Assert.AreEqual(1, asked);
            Assert.AreEqual(SshHostKeyDecision.AcceptAndPersist, verdict.Decision);

            await policy.PersistAsync(context);

            // 记下来之后，下一次就该直接通过 —— 而且不再问。
            SshHostKeyVerdict second = await policy.EvaluateAsync(context);
            Assert.AreEqual(SshHostKeyDecision.Accept, second.Decision);
            Assert.AreEqual(1, asked, "已经记下来的主机不该再问一遍");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task 严格模式下未知主机直接拒绝()
    {
        SshPublicKey key = MakeKey();
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");

        KnownHostsPolicy policy = new(path) { UnknownHost = UnknownHostBehavior.Reject };
        SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context("new.example.com", 22, key));

        Assert.AreEqual(SshHostKeyDecision.Reject, verdict.Decision);
        Assert.Contains(key.Sha256Fingerprint, verdict.Message!);
    }

    [TestMethod]
    public async Task 没配询问回调时不会悄悄放行()
    {
        SshPublicKey key = MakeKey();
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");

        KnownHostsPolicy policy = new(path);   // UnknownHost = Ask，但没给回调
        SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context("new.example.com", 22, key));

        // 问不了就该拒，不该默认放行 —— 默认放行等于关掉中间人防护。
        Assert.AreEqual(SshHostKeyDecision.Reject, verdict.Decision);
    }

    [TestMethod]
    public async Task 吊销的密钥被拒并说明原因()
    {
        SshPublicKey key = MakeKey();
        string path = await WriteTempAsync("@revoked " + Line("example.com", key));

        try
        {
            KnownHostsPolicy policy = new(path);
            SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context("example.com", 22, key));

            Assert.AreEqual(SshHostKeyDecision.Reject, verdict.Decision);
            Assert.Contains("@revoked", verdict.Message!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteTempAsync(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, content + "\n");
        return path;
    }

    // ------------------------------------------------------------ 换一种类型

    [TestMethod]
    public void 只记着别的类型时不是没见过()
    {
        var ed25519 = SshPublicKey.Decode(TestHostKey.Create("ssh-ed25519").PublicKeyBlob);
        var ecdsa = SshPublicKey.Decode(TestHostKey.Create("ecdsa-sha2-nistp256").PublicKeyBlob);
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("example.com", ed25519));

        KnownHostLookup lookup = KnownHostsFile.Lookup(entries, "example.com", 22, ecdsa);

        // 中间人只要出示一种没记过的类型：当成「没见过」的话，「变了」的检查就被绕过去了。
        Assert.AreEqual(KnownHostStatus.OtherKeyTypesKnown, lookup.Status);
        Assert.HasCount(1, lookup.ConflictingEntries);
        Assert.AreSequenceEqual(
            ["ssh-ed25519"], [.. KnownHostsFile.KnownKeyTypes(entries, "example.com", 22)]);
    }

    [TestMethod]
    public async Task 只记着别的类型时连接受新主机的策略也拒绝且不写入()
    {
        var ed25519 = SshPublicKey.Decode(TestHostKey.Create("ssh-ed25519").PublicKeyBlob);
        var ecdsa = SshPublicKey.Decode(TestHostKey.Create("ecdsa-sha2-nistp256").PublicKeyBlob);
        string path = await WriteTempAsync(Line("example.com", ed25519));

        try
        {
            // accept-new：没见过的主机悄悄记下来 —— 正是这个模式最怕被换类型绕过去。
            KnownHostsPolicy policy = new(path) { UnknownHost = UnknownHostBehavior.AcceptAndPersist };
            SshHostKeyVerdict verdict = await policy.EvaluateAsync(Context("example.com", 22, ecdsa));

            Assert.AreEqual(SshHostKeyDecision.Reject, verdict.Decision);
            Assert.Contains("ssh-ed25519", verdict.Message!);
            Assert.HasCount(1, KnownHostsFile.Parse(await File.ReadAllTextAsync(path)), "不该写进任何东西");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void 已记下的类型排到主机密钥算法的最前面()
    {
        SshAlgorithmSet preferred = SshAlgorithmSet.Default.PreferHostKeyTypes(["ssh-rsa"]);

        // 协商以客户端顺序为准：已知类型排在前面，正常的服务端就谈成它。
        Assert.AreSequenceEqual(
            [Ssh.Protocol.SshAlgorithmNames.RsaSha512, Ssh.Protocol.SshAlgorithmNames.RsaSha256], [.. preferred.HostKey.Take(2)], SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual([.. SshAlgorithmSet.Default.HostKey], [.. preferred.HostKey], SequenceOrder.InAnyOrder, "只调顺序，不增删");
    }

    // ------------------------------------------------------------ 取反与追加

    [TestMethod]
    public void 取反模式对上时整行都不算这台主机()
    {
        SshPublicKey key = MakeKey();
        IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(Line("*.corp,!untrusted.corp", key));

        Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "a.corp", 22, key).Status);
        Assert.AreEqual(
            KnownHostStatus.Unknown, KnownHostsFile.Lookup(entries, "untrusted.corp", 22, key).Status,
            "写配置的人明确排除了它 —— 不能再经 *.corp 把密钥信给它");
    }

    [TestMethod]
    [DataRow("x,*", DisplayName = "逗号接通配:对所有主机生效")]
    [DataRow("*", DisplayName = "通配")]
    [DataRow("host?", DisplayName = "问号")]
    [DataRow("!x", DisplayName = "取反")]
    [DataRow("a b", DisplayName = "空格")]
    [DataRow("a\tb", DisplayName = "Tab")]
    [DataRow("x\ny ssh-ed25519 AAAA", DisplayName = "换行")]
    [DataRow("@cert-authority", DisplayName = "开头的 @")]
    [DataRow("|1|abc|def", DisplayName = "开头的 |")]
    [DataRow("[x]", DisplayName = "方括号")]
    [DataRow("#x", DisplayName = "注释")]
    [DataRow("", DisplayName = "空串")]
    public void 主机名里有known_hosts另有含义的字符时不写(string host)
    {
        // `x,*` 写进去，这把钥就对所有主机生效了。主机名可能来自外部启动链接、ssh_config 的 HostName。
        SshPublicKey key = MakeKey();

        Assert.IsFalse(KnownHostsFile.IsRecordableHost(host));
        Assert.ThrowsExactly<ArgumentException>(() => KnownHostsFile.FormatEntry(host, 22, key));
        Assert.ThrowsExactly<ArgumentException>(() => KnownHostsFile.FormatEntry(host, 22, key, hashHostName: true));
    }

    [TestMethod]
    [DataRow("server.example.com")]
    [DataRow("host_name-1.corp")]
    [DataRow("10.0.0.9")]
    [DataRow("fe80::1%eth0")]
    [DataRow("例子.测试")]
    public void 正常的主机名照常写(string host) =>
        Assert.IsTrue(KnownHostsFile.IsRecordableHost(host));

    [TestMethod]
    public async Task 记不下来的主机名连接不放行()
    {
        // 使用者说的是「信任并记住」：记不下来就不该悄悄当成「只信这一次」。
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");
        KnownHostsPolicy policy = new(path, (_, _) => ValueTask.FromResult(true));

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await policy.PersistAsync(Context("x,*", 22, MakeKey())));

        Assert.AreEqual(SshFailureReason.InvalidConfiguration, error.Reason);
        Assert.IsFalse(File.Exists(path), "一行都不该写");
    }

    /// <summary>
    /// 读不出来（被别的进程独占）报带原因码的 <see cref="SshConnectException"/>。
    /// 曾经漏出 BCL 异常：<see cref="IOException"/> 在建连路上被归成「对端断开」，<see cref="UnauthorizedAccessException"/> 干脆接不住。
    /// </summary>
    [TestMethod]
    public async Task 读不出来时报存储失败()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");
        File.WriteAllText(path, "");

        try
        {
            SshConnectException error;
            await using (FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                error = await Assert.ThrowsExactlyAsync<SshConnectException>(
                    async () => await KnownHostsFile.LoadAsync(path));
            }

            Assert.AreEqual(SshFailureReason.HostKeyStoreFailed, error.Reason);
            Assert.IsInstanceOfType<IOException>(error.InnerException);
            Assert.Contains(path, error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task 写不进去时报存储失败()
    {
        // 拿一个目录当 known_hosts：追加必然失败（Windows 上是 UnauthorizedAccessException）。
        DirectoryInfo directory = Directory.CreateTempSubdirectory("kh-");
        try
        {
            SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
                async () => await KnownHostsFile.AppendAsync("a.example.com", 22, MakeKey(), directory.FullName));

            Assert.AreEqual(SshFailureReason.HostKeyStoreFailed, error.Reason);
            Assert.IsTrue(error.InnerException is IOException or UnauthorizedAccessException, error.InnerException?.GetType().Name);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // ------------------------------------------------------------ 删记录（Q4）

    /// <summary>
    /// 〔Q4〕只动专属于这台主机的记录：散列行整行删，几个名字的行只拿掉这台主机的名字；
    /// 吊销行、通配行、别的主机与别的端口、注释与写坏的行连同换行符原样留着。
    /// </summary>
    [TestMethod]
    public async Task 删记录只动专属于这台主机的那几行()
    {
        SshPublicKey k1 = MakeKey(), k2 = MakeKey(), k3 = MakeKey(), k4 = MakeKey(), k5 = MakeKey(), k6 = MakeKey(), k7 = MakeKey();
        string content =
            "# 注释\r\n" +
            Line("example.com", k1) + "\r\n" +
            Line("example.com,10.0.0.5", k2) + "\n" +
            KnownHostsFile.FormatEntry("example.com", 22, k3, hashHostName: true) + "\n" +
            Line("*.com", k4) + "\n" +
            "@revoked " + Line("example.com", k5) + "\n" +
            Line("other.example", k1) + "\n" +
            Line("[example.com]:2222", k6) + "\n" +
            "写坏的行\n" +
            Line("Example.COM", k7);
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, content);

        try
        {
            IReadOnlyList<string> removed = await KnownHostsFile.RemoveHostKeysAsync("example.com", 22, path: path);

            Assert.AreSequenceEqual(
                [.. new[] { k1, k2, k3, k7 }.Select(k => k.Sha256Fingerprint)], [.. removed]);
            Assert.AreEqual(
                "# 注释\r\n" +
                Line("10.0.0.5", k2) + "\n" +
                Line("*.com", k4) + "\n" +
                "@revoked " + Line("example.com", k5) + "\n" +
                Line("other.example", k1) + "\n" +
                Line("[example.com]:2222", k6) + "\n" +
                "写坏的行\n",
                await File.ReadAllTextAsync(path));
            Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"), "临时文件没留下");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>〔Q4〕给了指纹只删那几把（带不带填充、有没有前缀都认）；非 22 端口按 [host]:port 认。</summary>
    [TestMethod]
    public async Task 删记录给了指纹只删那几把()
    {
        SshPublicKey k1 = MakeKey(), k2 = MakeKey();
        string path = await WriteTempAsync(
            Line("[example.com]:2222", k1) + "\n" + Line("[example.com]:2222", k2) + "\n" + Line("example.com", k1));

        try
        {
            string bare = k1.Sha256Fingerprint["SHA256:".Length..] + "=";
            IReadOnlyList<string> removed = await KnownHostsFile.RemoveHostKeysAsync("example.com", 2222, [bare], path);

            Assert.AreSequenceEqual([k1.Sha256Fingerprint], [.. removed]);
            Assert.AreEqual(Line("[example.com]:2222", k2) + "\n" + Line("example.com", k1) + "\n", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>〔Q4〕没有可删的：文件一个字节都不动（连写入时间都不变）；文件不存在不是错。</summary>
    [TestMethod]
    public async Task 删记录没有可删的时文件不动()
    {
        SshPublicKey key = MakeKey();
        string path = await WriteTempAsync(Line("other.example", key));

        try
        {
            DateTime written = File.GetLastWriteTimeUtc(path);
            Assert.IsEmpty(await KnownHostsFile.RemoveHostKeysAsync("example.com", 22, path: path));
            Assert.IsEmpty(await KnownHostsFile.RemoveHostKeysAsync("other.example", 22, [MakeKey().Sha256Fingerprint], path));
            Assert.AreEqual(written, File.GetLastWriteTimeUtc(path));
            Assert.AreEqual(Line("other.example", key) + "\n", await File.ReadAllTextAsync(path));

            Assert.IsEmpty(await KnownHostsFile.RemoveHostKeysAsync("example.com", 22, path: path + ".none"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 〔Q4〕冲突重试：改写之后、替换之前别的进程追加了一条 —— 不能把它丢掉，按新内容重来一轮。
    /// </summary>
    [TestMethod]
    public async Task 删记录时别的进程刚追加的那一条不丢()
    {
        SshPublicKey old = MakeKey(), late = MakeKey();
        string path = await WriteTempAsync(Line("example.com", old));

        try
        {
            List<int> attempts = [];
            IReadOnlyList<string> removed = await KnownHostsFile.RemoveHostKeysAsync(
                "example.com", 22, fingerprints: null, path,
                beforeReplace: async attempt =>
                {
                    attempts.Add(attempt);
                    if (attempt == 1)
                    {
                        await KnownHostsFile.AppendAsync("late.example", 22, late, path);
                    }
                },
                CancellationToken.None);

            Assert.AreSequenceEqual([1, 2], [.. attempts]);
            Assert.AreSequenceEqual([old.Sha256Fingerprint], [.. removed]);
            IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "late.example", 22, late).Status);
            Assert.AreEqual(KnownHostStatus.Unknown, KnownHostsFile.Lookup(entries, "example.com", 22, old).Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>〔Q4〕「密钥变了」、确认是重装之后一键删掉旧记录：下次按「没见过」处理（策略的缓存也作废了）。</summary>
    [TestMethod]
    public async Task 密钥变了确认之后一键删掉旧记录()
    {
        SshPublicKey old = MakeKey(), current = MakeKey();
        string path = await WriteTempAsync(Line("example.com", old));

        try
        {
            KnownHostsPolicy policy = new(path);
            Assert.AreEqual(SshFailureReason.HostKeyChanged, (await policy.EvaluateAsync(Context("example.com", 22, current))).Reason);

            Assert.AreSequenceEqual([old.Sha256Fingerprint], [.. (await policy.RemoveHostKeysAsync("example.com", 22))]);

            SshHostKeyVerdict after = await policy.EvaluateAsync(Context("example.com", 22, current));
            Assert.AreNotEqual(SshFailureReason.HostKeyChanged, after.Reason);
            Assert.Contains("没见过", after.Message!);
            Assert.IsEmpty(await KnownHostsPolicy.WithoutFile().RemoveHostKeysAsync("example.com", 22));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task 文件末尾没有换行时追加不会把两条记录粘在一起()
    {
        SshPublicKey first = MakeKey();
        SshPublicKey second = MakeKey();
        string path = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");

        try
        {
            // 手工编辑过的文件常常最后一行没有换行。
            await File.WriteAllTextAsync(path, Line("a.example.com", first));
            await KnownHostsFile.AppendAsync("b.example.com", 22, second, path);

            IReadOnlyList<KnownHostEntry> entries = KnownHostsFile.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "a.example.com", 22, first).Status);
            Assert.AreEqual(KnownHostStatus.Known, KnownHostsFile.Lookup(entries, "b.example.com", 22, second).Status);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
