// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/09-dialing.md §7

using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Config;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Config;

/// <summary>记号展开、CertificateFile、ConnectionAttempts，以及会话级的 SetEnv / SendEnv / RemoteCommand。</summary>
[TestClass]
[TestCategory("Config")]
public sealed class SshConfigMoreKeysTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", name);

    /// <summary>补上的记号：%p %n %j %k %l %L %C；不认识的原样留着。</summary>
    [TestMethod]
    public void 路径里的记号都展开()
    {
        SshHostConfig config = SshConfigFile.Resolve(SshConfigFile.Parse("""
            Host alias
                HostName real.example
                User deploy
                Port 2200
                ProxyJump bastion
                HostKeyAlias hk
                IdentityFile /keys/%n-%h-%p-%r-%j-%k-%q
                CertificateFile /certs/%C
                IdentityFile /keys/%l/%L
            """), "alias");

        string local = System.Net.Dns.GetHostName();
        IReadOnlyList<string> identities = config.ExpandIdentityFiles();
        Assert.AreEqual("/keys/alias-real.example-2200-deploy-bastion-hk-%q", identities[0]);
        Assert.AreEqual($"/keys/{local}/{local.Split('.')[0]}", identities[1]);

#pragma warning disable CA5350 // %C 由 ssh_config(5) 规定就是 SHA-1
        string hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(local + "real.example" + "2200" + "deploy")));
#pragma warning restore CA5350
        Assert.AreSequenceEqual([$"/certs/{hash}"], [.. config.ExpandCertificateFiles()]);
    }

    /// <summary>
    /// 钥旁边的 -cert.pub 自动配上（ssh 的默认行为），证书排在裸钥前面；CertificateFile 写了一张证的不是这些钥的，报给 IdentityFileSkipped。
    /// 样本是真 ssh-keygen 生成与签发的。
    /// </summary>
    [TestMethod]
    public async Task 证书与私钥自动配对_证书排在前面()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse($"""
            Host cert
                IdentityFile "{Fixture("cert-ed25519")}"
                CertificateFile "{Fixture("cert-rsa-cert.pub")}"
            """);
        List<string> skipped = [];

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "cert", new SshConfigConnectOptions
        {
            IdentityFileSkipped = (path, _) => skipped.Add(Path.GetFileName(path)),
        });

        PublicKeyCredential[] keys = [.. options.Credentials.OfType<PublicKeyCredential>()];
        Assert.HasCount(2, keys);
        Assert.IsInstanceOfType<SshCertificateSigner>(keys[0].Signer, "证书排在前面");
        Assert.IsInstanceOfType<InMemorySshSigner>(keys[1].Signer);
        Assert.AreSequenceEqual(["cert-rsa-cert.pub"], [.. skipped], "这张证书证的钥没读出来，报给调用方");
    }

    /// <summary>ConnectionAttempts：拨号器包一层，失败了隔一会儿再试，第三次连上；只有一次时不包。</summary>
    [TestMethod]
    public async Task ConnectionAttempts拨号失败时再试()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host flaky
                ConnectionAttempts 3
            Host once
                HostName once.example
            """);
        SshConnectionOptions flaky = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "flaky");
        Assert.AreEqual(3, ((RetryingDialer)flaky.Dialer).Attempts);
        Assert.IsNotInstanceOfType<RetryingDialer>((await SshConfigFile.CreateConnectionOptionsAsync(blocks, "once")).Dialer);

        int calls = 0;
        RetryingDialer dialer = new(new FlakyDialer(() => ++calls), 3);
        TimeSpan interval = RetryingDialer.Interval;
        RetryingDialer.Interval = TimeSpan.FromMilliseconds(10);
        try
        {
            await using Stream stream = await dialer.DialAsync(SshDialTarget.Direct("h", 22));
            Assert.AreEqual(3, calls);

            calls = -10;
            await Assert.ThrowsAsync<SshConnectException>(async () => await dialer.DialAsync(SshDialTarget.Direct("h", 22)));
            Assert.AreEqual(-7, calls, "试满三次就照实失败");
        }
        finally
        {
            RetryingDialer.Interval = interval;
        }
    }

    private sealed class FlakyDialer(Func<int> attempt) : ISshTransportDialer
    {
        public ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default) =>
            attempt() == 3
                ? ValueTask.FromResult<Stream>(new MemoryStream())
                : throw new SshConnectException(SshFailureReason.TcpRefused, SshPhase.Dialing, "连不上");
    }

    /// <summary>SetEnv（多个、先出现的赢）+ SendEnv 选中的本机变量；RemoteCommand 成了伪终端里的命令；模板里显式给的不动。</summary>
    /// <summary>
    /// 〔F22〕AddressFamily / BindAddress 落到直连的 TCP 拨号器上；HostKeyAlias 包住主机密钥策略；GlobalKnownHostsFile 交给 known_hosts 策略；
    /// IdentityAgent 的几种写法；BindInterface 写了本机没有的网卡报配置错误。
    /// </summary>
    [TestMethod]
    public async Task 地址族_本机地址_主机密钥别名_全局known_hosts与IdentityAgent()
    {
        string global = Path.Combine(Path.GetTempPath(), $"vela-global-kh-{Guid.NewGuid():N}");
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse($"""
            Host a
                HostName 127.0.0.1
                AddressFamily inet
                BindAddress 127.0.0.2
                HostKeyAlias shared-box
                GlobalKnownHostsFile {global}
                IdentityAgent none
            Host b
                IdentityAgent ~/agent.sock
            Host c
                BindInterface no-such-interface-velashell
            Host d
                BindAddress not-an-address
            """);

        SshHostConfig a = SshConfigFile.Resolve(blocks, "a");
        Assert.AreEqual(System.Net.Sockets.AddressFamily.InterNetwork, a.AddressFamily);
        Assert.AreEqual("shared-box", a.HostKeyAlias);
        Assert.AreSequenceEqual([global], [.. a.GlobalKnownHostsFiles]);
        Assert.IsFalse(a.TryGetIdentityAgent(out _), "IdentityAgent none：不用 agent");
        Assert.IsTrue(SshConfigFile.Resolve(blocks, "b").TryGetIdentityAgent(out string? agent));
        Assert.IsTrue(agent!.EndsWith("agent.sock", StringComparison.Ordinal) && !agent.StartsWith('~'), agent);
        Assert.IsTrue(SshConfigFile.Resolve(blocks, "zzz").TryGetIdentityAgent(out string? defaultAgent));
        Assert.IsNull(defaultAgent, "没写：默认的那个 agent");

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "a");
        var dialer = (TcpTransportDialer)options.Dialer;
        Assert.AreEqual(System.Net.Sockets.AddressFamily.InterNetwork, dialer.AddressFamily);
        Assert.AreSequenceEqual([System.Net.IPAddress.Parse("127.0.0.2")], [.. dialer.LocalAddresses]);
        var alias = (HostKeyAliasPolicy)options.HostKeyPolicy;
        Assert.AreEqual("shared-box", alias.Alias);
        Assert.AreSequenceEqual([global], [.. ((KnownHostsPolicy)alias.Inner).GlobalKnownHostsFiles]);

        Assert.AreEqual(SshFailureReason.InvalidConfiguration, (await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConfigFile.CreateConnectionOptionsAsync(blocks, "c"))).Reason);
        Assert.AreEqual(SshFailureReason.InvalidConfiguration, (await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConfigFile.CreateConnectionOptionsAsync(blocks, "d"))).Reason);
    }

    /// <summary>
    /// 〔F22〕HostKeyAlias：查、记主机密钥用别名、端口不带（OpenSSH 10.5 黑盒核对过：连 2222 端口，记下的是别名本身）。
    /// GlobalKnownHostsFile：只读 —— 查的时候认，记的时候只写自己的那份。
    /// </summary>
    [TestMethod]
    public async Task 主机密钥别名按别名查记_全局known_hosts只读()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vela-kh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var hostKey = InMemorySshSigner.GenerateEd25519();
            using var other = InMemorySshSigner.GenerateEd25519();
            string own = Path.Combine(dir, "known_hosts");
            string global = Path.Combine(dir, "global");
            await File.WriteAllTextAsync(global, $"shared-box {hostKey.PublicKey.ToOpenSshFormat()}\n");

            KnownHostsPolicy known = new(own) { UnknownHost = UnknownHostBehavior.AcceptAndPersist, GlobalKnownHostsFiles = [global] };
            HostKeyAliasPolicy policy = new(known, "shared-box");

            SshHostKeyContext context = new()
            {
                Host = "10.0.0.5",
                Port = 2222,
                Key = hostKey.PublicKey,
                NegotiatedAlgorithm = hostKey.PublicKey.KeyType,
            };
            Assert.AreEqual(SshHostKeyVerdict.Accept, await policy.EvaluateAsync(context), "全局那份里按别名记着它");
            Assert.IsFalse(File.Exists(own), "认得的钥不写自己的那份");

            SshHostKeyContext newcomer = new()
            {
                Host = "10.0.0.6",
                Port = 2200,
                Key = other.PublicKey,
                NegotiatedAlgorithm = other.PublicKey.KeyType,
            };
            HostKeyAliasPolicy second = new(new KnownHostsPolicy(own) { UnknownHost = UnknownHostBehavior.AcceptAndPersist }, "other-alias");
            Assert.AreEqual(SshHostKeyVerdict.AcceptAndPersist, await second.EvaluateAsync(newcomer));
            await second.PersistAsync(newcomer);
            string written = await File.ReadAllTextAsync(own);
            Assert.StartsWith("other-alias ", written, "按别名记、不带端口");
            Assert.DoesNotContain("10.0.0.6", written);
            Assert.DoesNotContain("other-alias", await File.ReadAllTextAsync(global), "全局那份只读，没被写过");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void 会话的环境变量与远端命令()
    {
        string variable = $"VELA_SENDENV_{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        Environment.SetEnvironmentVariable(variable, "from-local");
        try
        {
            SshHostConfig config = SshConfigFile.Resolve(SshConfigFile.Parse($"""
                Host h
                    SetEnv LANG=C.UTF-8 EDITOR=vim
                    SetEnv LANG=ignored
                    SendEnv VELA_SENDENV_* -LC_*
                    RemoteCommand tmux new -A -s main
                """), "h");

            SshShellOptions shell = config.ApplyToShell();
            Assert.AreEqual("C.UTF-8", shell.Environment["LANG"], "先出现的赢");
            Assert.AreEqual("vim", shell.Environment["EDITOR"]);
            Assert.AreEqual("from-local", shell.Environment[variable]);
            Assert.AreEqual("tmux new -A -s main", shell.Command);

            SshShellOptions explicitTemplate = config.ApplyToShell(new SshShellOptions
            {
                Environment = new Dictionary<string, string> { ["ONLY"] = "me" },
                Command = "htop",
            });
            Assert.AreSequenceEqual(["ONLY"], [.. explicitTemplate.Environment.Keys]);
            Assert.AreEqual("htop", explicitTemplate.Command);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
