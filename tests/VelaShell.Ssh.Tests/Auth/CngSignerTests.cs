// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/04-authentication.md §4.8

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Auth;

/// <summary>
/// Windows CNG 密钥库里<b>不可导出</b>的钥做签名器：在当前用户的软件密钥库里现建一把、用完删掉。
/// 签出来的名用公钥验得过 —— 私钥一个字节都没离开密钥库。
/// </summary>
[TestClass]
[TestCategory("Keys")]
[SupportedOSPlatform("windows")]
[OSCondition(OperatingSystems.Windows, IgnoreMessage = "CNG 只在 Windows 上。")]
public sealed class CngSignerTests
{
    [SupportedOSPlatform("windows")]
    internal static string CreateKey(CngAlgorithm algorithm, int? rsaBits = null)
    {
        string name = $"vela-test-{Guid.NewGuid():N}";
        CngKeyCreationParameters parameters = new()
        {
            ExportPolicy = CngExportPolicies.None,
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
        };
        if (rsaBits is { } bits)
        {
            parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(bits), CngPropertyOptions.None));
        }
        CngKey.Create(algorithm, name, parameters).Dispose();
        return name;
    }

    [SupportedOSPlatform("windows")]
    internal static void DeleteKey(string name)
    {
        using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        key.Delete();
    }

    /// <summary>RSA 与 ECDSA P-256 两种：签名算法名对、签出来的名公钥验得过、不当成「本地便宜」的钥。</summary>
    [TestMethod]
    public async Task 不可导出的钥照样签得出能验过的名()
    {
        byte[] data = Encoding.UTF8.GetBytes("交给密钥库签的数据");
        foreach ((CngAlgorithm algorithm, int? bits, string expected) in new[]
                 {
                     (CngAlgorithm.Rsa, (int?)2048, SshAlgorithmNames.RsaSha512),
                     (CngAlgorithm.ECDsaP256, null, SshAlgorithmNames.EcdsaSha2Nistp256),
                 })
        {
            string name = CreateKey(algorithm, bits);
            try
            {
                using var signer = CngSshSigner.Open(name);
                Assert.AreEqual(expected, signer.SignatureAlgorithms[0]);
                Assert.IsFalse(signer.IsLocalAndCheap);
                Assert.AreEqual(name, signer.KeyName);

                byte[] signature = await signer.SignAsync(data, expected);
                Assert.IsTrue(signer.PublicKey.VerifySignature(signature, data, expected), $"{algorithm.Algorithm}：签名验不过");

                using var raw = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                Assert.AreEqual(CngExportPolicies.None, raw.ExportPolicy, "这把钥确实导不出来");
            }
            finally
            {
                DeleteKey(name);
            }
        }
    }

    /// <summary>找不到的钥报 KeyFileUnreadable；不能签名的钥（ECDH）报 Unsupported。</summary>
    [TestMethod]
    public void 找不到或不能签名的钥当场报()
    {
        Assert.AreEqual(SshFailureReason.KeyFileUnreadable,
            Assert.ThrowsExactly<SshPrivateKeyException>(() => CngSshSigner.Open($"vela-missing-{Guid.NewGuid():N}")).Reason);

        string ecdh = CreateKey(CngAlgorithm.ECDiffieHellmanP256);
        try
        {
            Assert.AreEqual(SshFailureReason.Unsupported,
                Assert.ThrowsExactly<SshPrivateKeyException>(() => CngSshSigner.Open(ecdh)).Reason);
        }
        finally
        {
            DeleteKey(ecdh);
        }
    }
}
