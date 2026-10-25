using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VelaShell.Core.Credentials;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 系统密钥库认证(<see cref="AuthMethod.KeyStore" />):Windows 的 CNG 密钥库里一把导不出来的钥,宿主列得出来、配置里只存引用、
/// 连接时经 SSH 库的 CNG 签名器签名登得上。
/// </summary>
/// <remarks>
/// 用例在当前用户的软件密钥库里现建一把<b>不可导出</b>的 ECDSA P-256 测试钥(名字带随机后缀),用完删掉;
/// 证书存储那一条再拿它签一张自签证书放进「个人」存储,用完同样删掉。非 Windows 记 Inconclusive。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
[DoNotParallelize]
public sealed class SystemKeyStoreTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    // ------------------------------------------------------------ 引用的写法(各平台)

    [TestMethod]
    public void 引用的文本写法往返()
    {
        SystemKeyReference user = new() { Provider = SystemKeyReference.SoftwareProvider, KeyName = "deploy/2026" };
        SystemKeyReference machine = new() { Provider = SystemKeyReference.PlatformProvider, KeyName = "tpm-key", MachineKey = true };

        Assert.AreEqual("cng:Microsoft Software Key Storage Provider/deploy/2026", user.ToText());
        Assert.IsTrue(SystemKeyReference.TryParse(user.ToText(), out SystemKeyReference? readUser));
        Assert.AreEqual(user, readUser, "钥名里的 / 不影响:按第一个 / 分开");
        Assert.IsTrue(SystemKeyReference.TryParse(machine.ToText(), out SystemKeyReference? readMachine));
        Assert.IsTrue(readMachine.MachineKey);
        Assert.IsFalse(SystemKeyReference.TryParse(@"C:\keys\id_ed25519", out _));
        Assert.IsFalse(SystemKeyReference.TryParse("cng:no-slash", out _));
        Assert.IsFalse(SystemKeyReference.TryParse("cng:/name", out _));
    }

    [TestMethod]
    public void 只有引用就算带着认证材料()
    {
        Assert.IsTrue(CredentialMaterial.HasInline(new SessionProfile
        {
            AuthMethod = AuthMethod.KeyStore,
            KeyStoreKey = new SystemKeyReference { Provider = SystemKeyReference.SoftwareProvider, KeyName = "k" },
        }));
        Assert.IsFalse(CredentialMaterial.HasInline(new SessionProfile { AuthMethod = AuthMethod.KeyStore, PrivateKeyPath = "/k" }),
            "系统密钥库看的是钥的引用,不是私钥文件");
    }

    // ------------------------------------------------------------ Windows CNG

    /// <summary>软件密钥库里现建的不可导出的钥:列得出来,公钥与钥本身的一致。</summary>
    [TestMethod]
    public async Task 列得出软件密钥库里的钥()
    {
        if (!WindowsOrSkip())
        {
            return;
        }
        using TestKey key = TestKey.Create();

        IReadOnlyList<SystemKeyInfo> keys = await new SystemKeyStore().ListKeysAsync(TestContext.CancellationToken);

        string name = key.Name;
        SystemKeyInfo found = keys.Single(k => k.Reference.KeyName == name);
        Assert.AreEqual(SystemKeyReference.SoftwareProvider, found.Reference.Provider);
        Assert.IsFalse(found.Reference.MachineKey);
        Assert.AreEqual("ecdsa-sha2-nistp256", found.KeyType);
        Assert.AreEqual(256, found.KeyBits);
        StringAssert.StartsWith(found.Reference.PublicKey, "ecdsa-sha2-nistp256 ");
        Assert.AreEqual(key.PublicKeyBlob, Convert.ToBase64String(VelaShell.Ssh.HostKeys.SshPublicKey.Parse(found.Reference.PublicKey!).Blob.Span));
    }

    /// <summary>
    /// 端到端:配置里只有引用,连接时经 CNG 签名器签名,环回上的测试服务端验过签名、放行。
    /// 钥是不可导出的 —— 能登上就说明签名是密钥库做的,而不是把私钥读进了本进程。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task 用密钥库里的钥登得上()
    {
        if (!WindowsOrSkip())
        {
            return;
        }
        using TestKey key = TestKey.Create();
        await using LoopbackSshHost host = new()
        {
            AuthPolicy = new TestAuthPolicy
            {
                RequiredMethods = ["publickey"],
                AcceptedPublicKeys = [Convert.FromBase64String(key.PublicKeyBlob)],
            },
        };

        await using VelaSshClientWrapper ssh = await ConnectAsync(KeyStoreInfo(host.Port, key.Name));

        Assert.IsTrue(ssh.IsConnected);
    }

    /// <summary>引用的钥在这台电脑上不在(别的电脑上建的、卡没插):如实说「打不开系统密钥库里的这把钥」,不去连服务器。</summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task 钥不在时如实说打不开()
    {
        if (!WindowsOrSkip())
        {
            return;
        }
        await using LoopbackSshHost host = new();

        VelaSshAuthenticationException ex = await Assert.ThrowsAsync<VelaSshAuthenticationException>(
            async () => await ConnectAsync(KeyStoreInfo(host.Port, "vela-no-such-key-" + Guid.NewGuid().ToString("N"))));

        StringAssert.Contains(ex.Message, Strings.Get("SshErr_KeyStoreUnavailable"));
        Assert.AreEqual(0, host.Accepted, "钥打不开就不去连服务器");
    }

    /// <summary>证书存储里有证书用这把钥时,列表带上证书主题(读证书记着的密钥位置,不去取私钥 —— 智能卡证书取私钥会弹框)。</summary>
    [TestMethod]
    public async Task 证书存储里的证书带上主题()
    {
        if (!WindowsOrSkip())
        {
            return;
        }
        using TestKey key = TestKey.Create();
        string subject = "VelaShell key store test " + Guid.NewGuid().ToString("N")[..8];
        using X509Certificate2 certificate = key.SelfSignedCertificate(subject);
        using X509Store store = new(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
        try
        {
            IReadOnlyList<SystemKeyInfo> keys = await new SystemKeyStore().ListKeysAsync(TestContext.CancellationToken);

            string name = key.Name;
            Assert.AreEqual(subject, keys.Single(k => k.Reference.KeyName == name).CertificateSubject);
        }
        finally
        {
            store.Remove(certificate);
        }
    }

    // ------------------------------------------------------------ 辅助

    private static ConnectionInfo KeyStoreInfo(int port, string keyName) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Username = "joe",
        AuthMethod = AuthMethod.KeyStore,
        KeyStoreKey = new SystemKeyReference { Provider = SystemKeyReference.SoftwareProvider, KeyName = keyName },
    };

    private static async Task<VelaSshClientWrapper> ConnectAsync(ConnectionInfo info)
    {
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh);
        using CancellationTokenSource cts = new(Deadline);
        try
        {
            await wrapper.ConnectAsync(cts.Token);
            return wrapper;
        }
        catch
        {
            await wrapper.DisposeAsync();
            throw;
        }
    }

    /// <summary>不是 Windows 就记 Inconclusive(全绿的报告里不能混着一行断言都没跑的用例)。</summary>
    [SupportedOSPlatformGuard("windows")]
    private static bool WindowsOrSkip()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("[SKIP] 系统密钥库(CNG)只有 Windows 有。");
        }
        return OperatingSystem.IsWindows();
    }

    /// <summary>当前用户软件密钥库里一把不可导出的 ECDSA P-256 测试钥;释放时删掉。</summary>
    [SupportedOSPlatform("windows")]
    private sealed class TestKey : IDisposable
    {
        private readonly CngKey _key;

        private TestKey(CngKey key, string name)
        {
            _key = key;
            Name = name;
            using ECDsaCng ecdsa = new(key);
            ECParameters parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            PublicKeyBlob = Convert.ToBase64String(EcdsaBlob(parameters));
        }

        public string Name { get; }

        /// <summary>SSH 公钥 blob(base64)。</summary>
        public string PublicKeyBlob { get; }

        public static TestKey Create()
        {
            string name = "vela-keystore-test-" + Guid.NewGuid().ToString("N");
            CngKey key = CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
            });
            return new TestKey(key, name);
        }

        public X509Certificate2 SelfSignedCertificate(string subject)
        {
            using ECDsaCng ecdsa = new(_key);
            CertificateRequest request = new($"CN={subject}", ecdsa, HashAlgorithmName.SHA256);
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        }

        /// <summary>RFC 5656 §3.1 的 ecdsa-sha2-nistp256 公钥 blob。</summary>
        private static byte[] EcdsaBlob(ECParameters parameters)
        {
            using MemoryStream blob = new();
            WriteString(blob, "ecdsa-sha2-nistp256"u8);
            WriteString(blob, "nistp256"u8);
            byte[] point = [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];
            WriteString(blob, point);
            return blob.ToArray();
        }

        private static void WriteString(Stream stream, ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
            stream.Write(length);
            stream.Write(value);
        }

        public void Dispose()
        {
            _key.Delete();
            _key.Dispose();
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
