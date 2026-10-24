// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/04-authentication.md §4.6;velashell-docs/zh/ssh/spec/08-failures.md §2

using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;

namespace VelaShell.Ssh.Tests.Keys;

/// <summary>
/// 私钥文件、.ppk、证书 blob 都是外来输入：截断到任意位置、塞进随机字节，只许抛对应的公开异常
/// （<see cref="SshPrivateKeyException"/> / <see cref="SshException"/> 的派生类），不许漏出解析层的内部异常或 BCL 异常。
/// </summary>
/// <remarks>随机数用固定种子：失败时能原样复现。</remarks>
[TestClass]
[TestCategory("Keys")]
public sealed class MalformedKeyInputTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", name));

    /// <summary>只许抛 <typeparamref name="TExpected"/>（或它的派生类）；不抛也可以（截断恰好落在无关紧要的尾部）。</summary>
    private static void OnlyThrows<TExpected>(Action parse, string what)
        where TExpected : Exception
    {
        Exception? leaked = null;
        try
        {
            parse();
        }
        catch (TExpected ex)
        {
            // 外来输入的每一种坏法都要归到一个种类：界面按它说话，没归类就只剩库的中文原文。
            if (ex is SshPrivateKeyException { ProblemKind: SshKeyProblemKind.Unknown } or SshCertificateException { ProblemKind: SshKeyProblemKind.Unknown })
            {
                Assert.Fail($"{what}：{ex.GetType().Name} 没有归类（ProblemKind 是 Unknown）：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            leaked = ex;
        }
        if (leaked is not null)
        {
            Assert.Fail($"{what}：漏出了 {leaked.GetType().FullName}：{leaked.Message}");
        }
    }

    [TestMethod]
    public void OpenSSH私钥截断到任意位置只报私钥异常()
    {
        string pem = Fixture("ed25519-plain");
        byte[] body = Convert.FromBase64String(string.Concat(pem.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal))));

        for (int length = 0; length < body.Length; length++)
        {
            string truncated = Armor(body[..length]);
            OnlyThrows<SshPrivateKeyException>(() => SshPrivateKeyFile.Parse(truncated), $"截到 {length} 字节");
        }
    }

    [TestMethod]
    public void OpenSSH私钥里是随机字节时只报私钥异常()
    {
        Random random = new(20261005);
        byte[] magic = "openssh-key-v1\0"u8.ToArray();

        for (int round = 0; round < 300; round++)
        {
            byte[] noise = new byte[random.Next(0, 400)];
            random.NextBytes(noise);

            // 一半全随机（魔数就对不上），一半魔数对、后面随机（长度字段随机，最考验解析层）。
            byte[] body = round % 2 == 0 ? noise : [.. magic, .. noise];
            OnlyThrows<SshPrivateKeyException>(() => SshPrivateKeyFile.Parse(Armor(body)), $"第 {round} 轮");
        }
    }

    /// <summary>截到任意位置都只许抛 <see cref="SshPrivateKeyException" />。</summary>
    /// <remarks>
    /// **最后一个点(整份文件只差结尾那个换行)不扫**:那不算「截断」,是完整文件 —— 而它是唯一一个会真的
    /// 走到口令派生的点。实测过:493 个字符的加密 .ppk 上,只有截到 492 那一处花了 1.2 秒,其余 492 个
    /// 截断点都在 20 毫秒以内(结构不全,解析在派生之前就抛)。
    /// 派生一次是 Argon2id 8 MB × 55 passes:本机 1.2 秒、CI 上 4 秒;3 核 runner 上三套程序集并行、
    /// 内存带宽被瓜分时实测涨到 30 秒以上,撞上本套件 30 秒的全局超时 —— run 37526852105 上这条用例
    /// 就是这么红的,不是挂死。完整文件(口令对与错)由 <c>PuttyKeyTests</c> 与
    /// <c>EncryptedOpenSshKeyTests</c> 覆盖,它们各派生一次;截断扫描要的是解析层的健壮性,不必再付一次。
    /// </remarks>
    [TestMethod]
    [DataRow("putty-ed25519-v2-lo.ppk")]
    [DataRow("putty-ed25519-v3-hi-enc.ppk")]
    public void Ppk截断到任意位置只报私钥异常(string name)
    {
        string text = Fixture(name);

        for (int length = 0; length < text.Length - 1; length += 3)
        {
            string truncated = text[..length];
            OnlyThrows<SshPrivateKeyException>(
                () => SshPrivateKeyFile.Parse(truncated, "correct horse battery staple"), $"{name} 截到 {length} 个字符");
        }
    }

    [TestMethod]
    public void Ppk各段里是随机内容时只报私钥异常()
    {
        string text = Fixture("putty-ed25519-v2-lo.ppk");
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Random random = new(20261006);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=: -";

        for (int round = 0; round < 300; round++)
        {
            string[] mutated = [.. lines];
            int line = random.Next(1, mutated.Length);
            char[] noise = new char[random.Next(0, 80)];
            for (int i = 0; i < noise.Length; i++)
            {
                noise[i] = alphabet[random.Next(alphabet.Length)];
            }
            mutated[line] = new string(noise);

            OnlyThrows<SshPrivateKeyException>(() => SshPrivateKeyFile.Parse(string.Join('\n', mutated)), $"第 {round} 轮（第 {line} 行）");
        }
    }

    [TestMethod]
    [DataRow("cert-ed25519-cert.pub")]
    [DataRow("cert-rsa-cert.pub")]
    [DataRow("cert-ecdsa-cert.pub")]
    public void 证书blob截断到任意位置只报公开异常(string name)
    {
        byte[] blob = Convert.FromBase64String(Fixture(name).Split(' ')[1]);

        for (int length = 0; length < blob.Length; length++)
        {
            byte[] truncated = blob[..length];
            OnlyThrows<SshException>(() => SshPublicKey.Decode(truncated), $"{name} 截到 {length} 字节");
        }
    }

    [TestMethod]
    public void 证书类型名后面是随机字节时只报公开异常()
    {
        byte[] blob = Convert.FromBase64String(Fixture("cert-ed25519-cert.pub").Split(' ')[1]);
        int typeEnd = 4 + System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(blob);
        Random random = new(20261007);

        for (int round = 0; round < 300; round++)
        {
            byte[] noise = new byte[random.Next(0, 300)];
            random.NextBytes(noise);
            byte[] mutated = [.. blob[..typeEnd], .. noise];

            OnlyThrows<SshException>(() => SshPublicKey.Decode(mutated), $"第 {round} 轮");
        }
    }

    private static string Armor(byte[] body) =>
        "-----BEGIN OPENSSH PRIVATE KEY-----\n" + Convert.ToBase64String(body, Base64FormattingOptions.InsertLineBreaks) +
        "\n-----END OPENSSH PRIVATE KEY-----\n";
}
