// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: OpenSSH PROTOCOL.key;velashell-docs/zh/ssh/spec/04-authentication.md §4.6

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Keys;

namespace VelaShell.Ssh.Tests.Keys;

/// <summary>
/// 把私钥写成 OpenSSH 私钥文件（可以带口令）。对不对只认真工具：本机的 <c>ssh-keygen</c> 读得出本库写的文件、
/// 导出的公钥与本库的逐字节一致 —— 不靠本库自己读自己来自证。
/// </summary>
[TestClass]
[TestCategory("Keys")]
public sealed class PrivateKeyWriteTests
{
    private const string Passphrase = "correct horse battery staple";

    public TestContext TestContext { get; set; } = null!;

    private static IEnumerable<(string Name, Func<InMemorySshSigner> Generate)> Keys()
    {
        yield return ("ed25519", InMemorySshSigner.GenerateEd25519);
        yield return ("ecdsa-256", () => InMemorySshSigner.GenerateEcdsa(256));
        yield return ("ecdsa-384", () => InMemorySshSigner.GenerateEcdsa(384));
        yield return ("ecdsa-521", () => InMemorySshSigner.GenerateEcdsa(521));
        yield return ("rsa-2048", () => InMemorySshSigner.GenerateRsa(2048));
    }

    [TestMethod]
    public async Task 写出的文件本库读得回来_口令不对时报口令不对()
    {
        foreach ((string name, Func<InMemorySshSigner> generate) in Keys())
        {
            using InMemorySshSigner key = generate();

            string plain = SshPrivateKeyFile.Format(key, comment: name);
            string sealedText = SshPrivateKeyFile.Format(key, Passphrase, name, kdfRounds: 4);

            Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----\n", plain, name);
            Assert.IsTrue(sealedText.Split('\n').All(line => line.Length <= 70), $"{name}：正文按 70 列折行");

            using (InMemorySshSigner reread = SshPrivateKeyFile.Parse(plain))
            {
                Assert.AreSequenceEqual(key.PublicKey.Blob.ToArray(), reread.PublicKey.Blob.ToArray(), name);

                // 读回来的私钥签的名，原来那把的公钥要验得过：结构合法、材料错位的私钥照样读得进来
                // （例如 Ed25519 只写了 32 字节种子），要到真去连服务器才以验签失败告终。
                byte[] data = "velashell key self-check"u8.ToArray();
                string algorithm = reread.SignatureAlgorithms[0];
                byte[] signature = await reread.SignAsync(data, algorithm);
                Assert.IsTrue(key.PublicKey.VerifySignature(signature, data, algorithm), $"{name}：读回来的私钥签的名验不过 —— 密钥材料写错位了");
            }
            using (InMemorySshSigner reread = SshPrivateKeyFile.Parse(sealedText, Passphrase))
            {
                Assert.AreSequenceEqual(key.PublicKey.Blob.ToArray(), reread.PublicKey.Blob.ToArray(), name);
            }

            SshPrivateKeyException wrong = Assert.ThrowsExactly<SshPrivateKeyException>(() => SshPrivateKeyFile.Parse(sealedText, "wrong"));
            Assert.AreEqual(SshFailureReason.KeyPassphraseIncorrect, wrong.Reason, name);
            SshPrivateKeyException missing = Assert.ThrowsExactly<SshPrivateKeyException>(() => SshPrivateKeyFile.Parse(sealedText));
            Assert.AreEqual(SshFailureReason.KeyPassphraseRequired, missing.Reason, name);
        }
    }

    /// <summary>真 <c>ssh-keygen -y</c> 读得出本库写的文件（带口令与不带），导出的公钥与本库的逐字节一致、注释原样。</summary>
    [TestMethod]
    public async Task 真ssh_keygen读得出本库写的私钥()
    {
        string? keygen = FindSshKeygen();
        if (keygen is null)
        {
            Assert.Inconclusive("本机没有 ssh-keygen：跳过与真工具的比对。");
        }

        string directory = Path.Combine(Path.GetTempPath(), $"vela-keywrite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach ((string name, Func<InMemorySshSigner> generate) in Keys())
            {
                using InMemorySshSigner key = generate();
                string expected = Convert.ToBase64String(key.PublicKey.Blob.Span);

                foreach (bool withPassphrase in new[] { false, true })
                {
                    string path = Path.Combine(directory, $"{name}-{(withPassphrase ? "enc" : "plain")}");
                    await File.WriteAllTextAsync(
                        path, SshPrivateKeyFile.Format(key, withPassphrase ? Passphrase : default, $"vela-{name}", kdfRounds: 8),
                        TestContext.CancellationToken);
                    if (OperatingSystem.IsWindows())
                    {
                        RestrictToCurrentUser(path);
                    }
                    else
                    {
                        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }

                    string output = await RunAsync(keygen, ["-y", "-P", withPassphrase ? Passphrase : "", "-f", path]);
                    string[] fields = output.Trim().Split(' ');
                    Assert.IsGreaterThanOrEqualTo(2, fields.Length, $"{name}：ssh-keygen 的输出：{output}");
                    Assert.AreEqual(key.PublicKey.KeyType, fields[0], name);
                    Assert.AreEqual(expected, fields[1], $"{name}{(withPassphrase ? "（带口令）" : "")}：ssh-keygen 导出的公钥与本库的不一致");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Windows 上的 OpenSSH 拒读别人也能读的私钥(UNPROTECTED PRIVATE KEY FILE)。文件从 %TEMP% 继承 ACL,
    /// 而 %TEMP% 上可能挂着别的用户组(plan.md §197、§199、§201、§210 都撞过):断掉继承,只给自己完全控制 ——
    /// 与 Unix 上的 0600 是同一件事。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUser(string path)
    {
        SecurityIdentifier self = WindowsIdentity.GetCurrent().User!;
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(self);
        security.AddAccessRule(new FileSystemAccessRule(self, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static string? FindSshKeygen()
    {
        if (OperatingSystem.IsWindows())
        {
            string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keygen.exe");
            return File.Exists(system) ? system : null;
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(directory, "ssh-keygen");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private async Task<string> RunAsync(string program, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken);
        Assert.AreEqual(0, process.ExitCode, $"ssh-keygen 失败：{await stderr}");
        return await stdout;
    }
}
