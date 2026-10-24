using NSubstitute;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Import;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Config;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 与 OpenSSH 互通的 <c>~/.ssh/config</c> 两个方向:导入补上的映射(算法清单、压缩、agent、转发、证书),
/// 以及反向的导出(<see cref="SshConfigExporter" />,导出的文件再用 SSH 库的解析器读回来核对)。
/// </summary>
[TestClass]
public sealed class SshConfigInteropTests
{
    private readonly List<string> _temp = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string path in _temp)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string WriteConfig(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sshconfig-interop-{Guid.NewGuid():N}");
        File.WriteAllText(path, content);
        _temp.Add(path);
        return path;
    }

    private string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sshconfig-interop-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _temp.Add(path);
        return path;
    }

    private static async Task<ImportedSession> ScanOneAsync(string configPath, string alias)
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        SessionImportScan scan = await new SshConfigImportService(repository).ScanAsync(configPath);
        return scan.Items.Single(i => i.Name == alias);
    }

    // ------------------------------------------------------------ 导入:算法清单

    /// <summary>
    /// §134 的自定义清单写法本来就是 ssh_config 的,原样带过来;本版没实现的名字(CBC、group1)去掉并说一声,
    /// 去掉之后一个不剩、或者拼错了的整条不导。
    /// </summary>
    [TestMethod]
    public async Task 算法清单原样带过来_没实现的去掉_拼错的整条不导()
    {
        string config = WriteConfig(
            """
            Host legacy
                Ciphers aes128-cbc,aes256-ctr
                KexAlgorithms +diffie-hellman-group1-sha1
                MACs -hmac-sha1
                HostKeyAlgorithms ^ssh-ed25519,rsa-sha2-512
            Host typo
                Ciphers aes256-ctrr
            """);

        ImportedSession legacy = await ScanOneAsync(config, "legacy");
        Assert.AreEqual("aes256-ctr", legacy.Ssh?.Ciphers);
        Assert.IsNull(legacy.Ssh?.KexAlgorithms, "只写了没实现的:留个空前缀等于什么都没写");
        Assert.AreEqual("-hmac-sha1", legacy.Ssh?.Macs);
        Assert.AreEqual("^ssh-ed25519,rsa-sha2-512", legacy.Ssh?.HostKeyAlgorithms);
        Assert.IsTrue(legacy.Notes.Any(n => n.Contains("Ciphers") && n.Contains("aes128-cbc")));
        Assert.IsTrue(legacy.Notes.Any(n => n.Contains("KexAlgorithms") && n.Contains("diffie-hellman-group1-sha1")));

        ImportedSession typo = await ScanOneAsync(config, "typo");
        Assert.IsNull(typo.Ssh?.Ciphers);
        Assert.IsTrue(typo.Notes.Any(n => n.Contains("Ciphers") && n.Contains("aes256-ctrr")));
    }

    [TestMethod]
    public async Task 压缩与agent转发()
    {
        string config = WriteConfig(
            """
            Host a
                Compression yes
                ForwardAgent yes
            Host b
                ForwardAgent /tmp/some-other-agent.sock
            Host c
                User nobody
            """);

        ImportedSession a = await ScanOneAsync(config, "a");
        Assert.IsTrue(a.Ssh?.Compression);
        Assert.IsTrue(a.Ssh?.AgentForwarding);

        ImportedSession b = await ScanOneAsync(config, "b");
        Assert.IsFalse(b.Ssh?.AgentForwarding ?? false, "指定了别的 agent:VelaShell 只转发默认的,换成默认的等于多交出几把钥");
        Assert.IsTrue(b.Notes.Any(n => n.Contains("/tmp/some-other-agent.sock")));

        Assert.IsNull((await ScanOneAsync(config, "c")).Ssh, "一项都没开:存 null");
    }

    // ------------------------------------------------------------ 导入:转发

    [TestMethod]
    public async Task 三种转发落成现有的隧道类型_装不下的如实跳过()
    {
        string config = WriteConfig(
            """
            Host fw
                LocalForward 8080 db.internal:5432
                LocalForward [::1]:9000 [2001:db8::5]:22
                LocalForward /tmp/local.sock /var/run/docker.sock
                LocalForward 8443
                RemoteForward 9090 localhost:80
                RemoteForward *:9091 10.0.0.9:443
                RemoteForward 1080
                DynamicForward 1081
                DynamicForward *:1082
            """);

        ImportedSession fw = await ScanOneAsync(config, "fw");
        List<TunnelConfig> tunnels = [.. fw.Tunnels!];

        Assert.HasCount(6, tunnels);
        TunnelConfig local = tunnels[0];
        Assert.AreEqual(TunnelType.LocalForward, local.Type);
        Assert.AreEqual(("127.0.0.1", 8080u, "db.internal", 5432u), (local.LocalHost, local.LocalPort, local.RemoteHost, local.RemotePort));
        Assert.AreEqual(("::1", 9000u, "2001:db8::5", 22u), (tunnels[1].LocalHost, tunnels[1].LocalPort, tunnels[1].RemoteHost, tunnels[1].RemotePort));
        TunnelConfig remote = tunnels[2];
        Assert.AreEqual(TunnelType.RemoteForward, remote.Type);
        Assert.AreEqual(("127.0.0.1", 9090u, "localhost", 80u), (remote.RemoteHost, remote.RemotePort, remote.LocalHost, remote.LocalPort));
        Assert.AreEqual("0.0.0.0", tunnels[3].RemoteHost, "* 是全部网卡");
        Assert.AreEqual((TunnelType.DynamicForward, "127.0.0.1", 1081u), (tunnels[4].Type, tunnels[4].LocalHost, tunnels[4].LocalPort));
        Assert.AreEqual("0.0.0.0", tunnels[5].LocalHost);

        Assert.IsTrue(fw.Notes.Any(n => n.Contains("/tmp/local.sock")), "Unix 套接字:现有的隧道类型装不下");
        Assert.IsTrue(fw.Notes.Any(n => n.Contains("RemoteForward 1080")), "远程动态转发:现有的隧道类型装不下");
        Assert.IsTrue(fw.Notes.Any(n => n.Contains("LocalForward 8443")), "写错的那一行不连累别的行");
    }

    [TestMethod]
    public async Task GatewayPorts与ClearAllForwardings()
    {
        string config = WriteConfig(
            """
            Host gw
                GatewayPorts yes
                LocalForward 8080 db:5432
            Host none
                ClearAllForwardings yes
                LocalForward 8080 db:5432
            """);

        Assert.AreEqual("0.0.0.0", (await ScanOneAsync(config, "gw")).Tunnels!.Single().LocalHost);
        Assert.IsNull((await ScanOneAsync(config, "none")).Tunnels);
    }

    // ------------------------------------------------------------ 导入:认证

    [TestMethod]
    public async Task CertificateFile映射到证书认证_私钥取同名的那把()
    {
        string config = WriteConfig(
            """
            Host cert
                IdentityFile /keys/other
                IdentityFile /keys/deploy
                CertificateFile /keys/deploy-cert.pub
            Host certonly
                CertificateFile /keys/ops-cert.pub
            """);

        ImportedSession cert = await ScanOneAsync(config, "cert");
        Assert.AreEqual(AuthMethod.Certificate, cert.AuthMethod);
        Assert.AreEqual(Path.GetFullPath("/keys/deploy-cert.pub"), cert.CertificatePath);
        Assert.AreEqual(Path.GetFullPath("/keys/deploy"), cert.PrivateKeyPath, "与证书同名的那把,不是第一把");
        Assert.IsTrue(cert.Notes.Any(n => n.Contains("other")), "另一把私钥没导入,说一声");

        ImportedSession certOnly = await ScanOneAsync(config, "certonly");
        Assert.AreEqual(AuthMethod.Certificate, certOnly.AuthMethod);
        Assert.AreEqual(Path.GetFullPath("/keys/ops"), certOnly.PrivateKeyPath, "按 ssh-keygen 的命名约定反推");
    }

    /// <summary>OpenSSH 自己会读 <c>&lt;IdentityFile&gt;-cert.pub</c>:那个文件在本机存在时同样按证书认证导入,不存在的不猜。</summary>
    [TestMethod]
    public async Task IdentityFile旁边有证书时按证书认证()
    {
        string directory = NewDirectory();
        string withCert = Path.Combine(directory, "id_signed");
        File.WriteAllText(withCert, "");
        File.WriteAllText(withCert + "-cert.pub", "");
        string config = WriteConfig(
            $"""
            Host signed
                IdentityFile {withCert.Replace('\\', '/')}
            Host plain
                IdentityFile {Path.Combine(directory, "id_plain").Replace('\\', '/')}
            """);

        ImportedSession signed = await ScanOneAsync(config, "signed");
        Assert.AreEqual(AuthMethod.Certificate, signed.AuthMethod);
        Assert.AreEqual(Path.GetFullPath(withCert + "-cert.pub"), signed.CertificatePath);

        ImportedSession plain = await ScanOneAsync(config, "plain");
        Assert.IsNull(plain.AuthMethod, "没有证书:仍按私钥(写入器推断)");
        Assert.IsNull(plain.CertificatePath);
    }

    [TestMethod]
    public async Task IdentityAgent默认的agent映射到Agent认证()
    {
        string config = WriteConfig(
            """
            Host agent
                IdentityAgent SSH_AUTH_SOCK
            Host noagent
                IdentityAgent none
            Host otheragent
                IdentityAgent /run/user/1000/some-unusual-agent.sock
            Host keyed
                IdentityAgent SSH_AUTH_SOCK
                IdentityFile /keys/id
            """);

        Assert.AreEqual(AuthMethod.Agent, (await ScanOneAsync(config, "agent")).AuthMethod);
        Assert.IsNull((await ScanOneAsync(config, "noagent")).AuthMethod);
        ImportedSession other = await ScanOneAsync(config, "otheragent");
        Assert.IsNull(other.AuthMethod);
        Assert.IsTrue(other.Notes.Any(n => n.Contains("some-unusual-agent.sock")));
        Assert.IsNull((await ScanOneAsync(config, "keyed")).AuthMethod, "写了私钥就按私钥:VelaShell 一条连接只用一种认证");
    }

    // ------------------------------------------------------------ 导入:写入

    [TestMethod]
    public async Task 写入时带上证书_协议层选项与隧道()
    {
        string config = WriteConfig(
            """
            Host full
                HostName 10.0.0.8
                IdentityFile /keys/deploy
                CertificateFile /keys/deploy-cert.pub
                Compression yes
                Ciphers aes256-gcm@openssh.com
                LocalForward 8080 db:5432
            """);
        ImportedSession item = await ScanOneAsync(config, "full");

        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        List<SessionProfile> saved = [];
        repository.SaveSessionAsync(Arg.Do<SessionProfile>(saved.Add)).Returns(Task.CompletedTask);
        IAppDataStore store = Substitute.For<IAppDataStore>();

        await new SshConfigImportService(repository, store).ImportAsync([item], "SSH config");

        SessionProfile profile = saved.Single();
        Assert.AreEqual(AuthMethod.Certificate, profile.AuthMethod);
        Assert.AreEqual(Path.GetFullPath("/keys/deploy-cert.pub"), profile.CertificatePath);
        Assert.AreEqual(Path.GetFullPath("/keys/deploy"), profile.PrivateKeyPath);
        Assert.IsTrue(profile.Ssh?.Compression);
        Assert.AreEqual("aes256-gcm@openssh.com", profile.Ssh?.Ciphers);
        await store.Received(1).UpsertAsync("tunnels", profile.Id.ToString("D"),
            Arg.Is<List<TunnelConfig>>(list => list.Count == 1 && list[0].RemoteHost == "db" && list[0].RemotePort == 5432),
            Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------ 导出

    private static SessionArchive Archive(params SessionProfile[] sessions) => new()
    {
        Application = "VelaShell test",
        ExportedAtUtc = new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc),
        Sessions = [.. sessions],
    };

    private static SshHostConfig ReadBack(SshConfigExport export, string alias) =>
        SshConfigFile.Resolve(SshConfigFile.Parse(export.Content), alias);

    /// <summary>导出的文件拿 SSH 库的解析器读回来(导入用的同一个),每个字段都对得上。</summary>
    [TestMethod]
    public void 导出的ssh_config读得回来()
    {
        string home = Path.Combine(Path.GetTempPath(), "home-for-export");
        SessionProfile bastion = new() { Name = "bastion", Host = "203.0.113.10", Port = 2222, Username = "jump" };
        SessionProfile web = new()
        {
            Name = "web 01",
            Host = "2001:db8::10",
            Username = "deploy",
            AuthMethod = AuthMethod.Certificate,
            PrivateKeyPath = Path.Combine(home, ".ssh", "id_ed25519"),
            CertificatePath = Path.Combine(home, ".ssh", "id_ed25519-cert.pub"),
            JumpHostProfileId = bastion.Id,
            Ssh = new SshSessionOptions { Compression = true, AgentForwarding = true, Ciphers = "aes256-gcm@openssh.com" },
        };
        SessionProfile ftp = new() { Name = "files", Host = "ftp.example", ConnectionType = ConnectionType.FTP, Port = 21 };
        SessionArchive archive = Archive(bastion, web, ftp);
        archive.Tunnels = new()
        {
            [web.Id] =
            [
                new TunnelConfig { Type = TunnelType.LocalForward, Name = "", LocalHost = "127.0.0.1", LocalPort = 8080, RemoteHost = "db", RemotePort = 5432 },
                new TunnelConfig { Type = TunnelType.RemoteForward, Name = "", LocalHost = "::1", LocalPort = 80, RemoteHost = "127.0.0.1", RemotePort = 9090 },
                new TunnelConfig { Type = TunnelType.DynamicForward, Name = "", LocalHost = "127.0.0.1", LocalPort = 1080 },
            ],
        };

        SshConfigExport export = SshConfigExporter.Write(archive, home);

        Assert.AreEqual(2, export.Exported);
        Assert.AreEqual(1, export.Skipped, "FTP 写不进 ssh_config");
        Assert.DoesNotContain("ftp.example", export.Content);

        SshHostConfig jump = ReadBack(export, "bastion");
        Assert.AreEqual(("203.0.113.10", 2222, "jump"), (jump.HostName, jump.Port, jump.User));

        SshHostConfig host = ReadBack(export, "web-01");
        Assert.AreEqual("2001:db8::10", host.HostName);
        Assert.AreEqual(22, host.Port);
        Assert.AreEqual("deploy", host.User);
        Assert.AreEqual("bastion", host.ProxyJump);
        Assert.AreSequenceEqual(["~/.ssh/id_ed25519"], [.. host.IdentityFiles], "主目录下的路径写成 ~/…,换台机器也找得到");
        Assert.AreEqual("~/.ssh/id_ed25519-cert.pub", host.First("CertificateFile"));
        Assert.IsTrue(host.Compression);
        Assert.IsTrue(host.ForwardAgent);
        Assert.AreEqual("aes256-gcm@openssh.com", host.Ciphers);
        List<SshConfigForward> forwards = [.. host.GetForwards()];
        Assert.HasCount(3, forwards);
        Assert.AreEqual((SshConfigForwardKind.Local, 8080, "db", 5432), (forwards[0].Kind, forwards[0].ListenPort, forwards[0].TargetHost, forwards[0].TargetPort));
        Assert.AreEqual((SshConfigForwardKind.Remote, 9090, "::1", 80), (forwards[1].Kind, forwards[1].ListenPort, forwards[1].TargetHost, forwards[1].TargetPort));
        Assert.AreEqual((SshConfigForwardKind.Dynamic, 1080), (forwards[2].Kind, forwards[2].ListenPort));
        Assert.DoesNotContain("Password", export.Content.Replace("# Passwords and passphrases are never written.", ""));
    }

    [TestMethod]
    public void 别名去掉特殊字符_重名加序号()
    {
        SshConfigExport export = SshConfigExporter.Write(Archive(
            new SessionProfile { Name = "prod*", Host = "10.0.0.1" },
            new SessionProfile { Name = "prod", Host = "10.0.0.2" },
            new SessionProfile { Name = "  ", Host = "10.0.0.3" }));

        List<string> aliases = [.. SshConfigFile.Parse(export.Content).SelectMany(b => b.Patterns)];
        Assert.AreSequenceEqual(["prod", "prod-2", "10.0.0.3"], aliases);
    }

    /// <summary>引用共享凭据的连接:认证材料取自凭据,用户名填了的以连接为准。</summary>
    [TestMethod]
    public void 共享凭据的私钥与用户名()
    {
        SharedCredential credential = new() { Name = "ops", Username = "ops", AuthMethod = AuthMethod.PrivateKey, PrivateKeyPath = "/keys/ops" };
        SessionProfile session = new() { Name = "s", Host = "h", CredentialSource = CredentialReference.ForShared(credential.Id) };
        SessionArchive archive = Archive(session);
        archive.SharedCredentials = [credential];

        SshHostConfig host = ReadBack(SshConfigExporter.Write(archive), "s");

        Assert.AreEqual("ops", host.User);
        Assert.AreSequenceEqual(["/keys/ops"], [.. host.IdentityFiles]);
    }

    /// <summary>「放开老算法」在 ssh_config 里没有开关:没写清单的写成 +老算法,写了 + 的并成一条,写了 - / ^ 的写完整清单。</summary>
    [TestMethod]
    public void 放开老算法换算成ssh_config的写法()
    {
        Assert.IsNull(SshConfigExporter.AlgorithmLine(SshAlgorithmKind.Cipher, null, legacy: false));
        Assert.AreEqual("-hmac-sha1", SshConfigExporter.AlgorithmLine(SshAlgorithmKind.Mac, "-hmac-sha1", legacy: false));

        string? kex = SshConfigExporter.AlgorithmLine(SshAlgorithmKind.KeyExchange, null, legacy: true);
        Assert.AreEqual("+diffie-hellman-group14-sha1", kex);
        Assert.AreEqual("+diffie-hellman-group14-sha1,curve25519-sha256",
            SshConfigExporter.AlgorithmLine(SshAlgorithmKind.KeyExchange, "+curve25519-sha256", legacy: true));
        string? removed = SshConfigExporter.AlgorithmLine(SshAlgorithmKind.KeyExchange, "-curve25519-sha256", legacy: true);
        Assert.IsNotNull(removed);
        Assert.IsFalse(removed.StartsWith('-'), "OpenSSH 一条里只能有一种前缀:写算出来的完整清单");
        Assert.Contains("diffie-hellman-group14-sha1", removed);
        Assert.DoesNotContain("curve25519-sha256,", removed + ",");
        Assert.AreEqual("aes256-ctr", SshConfigExporter.AlgorithmLine(SshAlgorithmKind.Cipher, "aes256-ctr", legacy: true), "整个替换的与默认无关");
    }
}
