using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 覆盖提交(posix-rename 原子替换)在<b>真实 OpenSSH 服务端</b>上保留被替换文件的模式。
/// 换进来的是临时文件那个 inode,它按 0644 新建:不把目标原有的模式带过去,覆盖一个 0600 的密钥文件就成了 0644,
/// 0755 的脚本丢掉执行位 —— 旧的就地覆盖写不改这些。单测里客户端是替身,测不到服务端真的怎么改。
/// 放在 Core.Tests 是因为要用 <c>VelaSshClientWrapper.InnerConnection</c>(internal,只对本工程开放)。
/// </summary>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 docker/SSH 操作不接受测试取消令牌,协作取消无法中断它们。")]
[TestClass]
public class SftpCommitModeIntegrationTests
{
    // 写 IPv4 字面量而不是 localhost:理由见 SftpSymlinkIntegrationTests。
    private const string TestHost = "127.0.0.1";
    private const int TestPort = 2222;
    private const string TestUser = "testuser";
    private const string TestPassword = "testpass";

    [TestMethod]
    [TestCategory("DockerIntegration")]
    [Timeout(60_000)]
    public async Task OverwriteCommit_KeepsTheReplacedFilesMode_AgainstRealOpenSsh()
    {
        RequireDockerAndSsh();

        string root = $"/tmp/vela-commit-mode-{Guid.NewGuid():N}";
        VelaSshClientWrapper ssh = await ConnectAsync();
        try
        {
            // setuid 位:属主就是上传者自己,改属主那一步成功,setuid 才照带(改属主时内核会清掉它,所以模式要后改)。
            (string Name, string Mode)[] files = [("secret", "600"), ("script", "755"), ("tool", "4755")];
            await ssh.RunCommandAsync($"mkdir -p {root} && cd {root} && "
                + string.Join(" && ", files.Select(f => $"printf old > {f.Name} && chmod {f.Mode} {f.Name}")));

            SshConnection inner = ssh.InnerConnection ?? throw new InvalidOperationException("SSH not connected.");
            await using var sftp = new VelaSftpClientWrapper(
                async ct => await SftpFileSystem.ConnectAsync(inner, cancellationToken: ct));
            await sftp.ConnectAsync(CancellationToken.None);
            var sessionId = Guid.NewGuid();
            ISshConnectionService connections = Substitute.For<ISshConnectionService>();
            connections.GetSession(sessionId).Returns(new SshSession
            {
                SessionId = sessionId,
                Status = SessionStatus.Connected,
                ConnectionInfo = new() { Host = TestHost, Port = TestPort, Username = TestUser, AuthMethod = AuthMethod.Password },
            });
            await using var service = new SftpService(connections, _ => sftp);

            foreach ((string name, string mode) in files)
            {
                string temporary = $"{root}/.vela-up-{name}";
                using (var content = new MemoryStream("new"u8.ToArray()))
                {
                    await sftp.UploadAsync(content, temporary);
                }
                await service.CommitUploadAsync(sessionId, temporary, $"{root}/{name}", overwrite: true);
                Assert.AreEqual(mode, (await ssh.RunCommandAsync($"stat -c %a {root}/{name}")).Trim(), name);
                Assert.AreEqual("new", await ssh.RunCommandAsync($"cat {root}/{name}"), name);
            }
        }
        finally
        {
            try
            {
                await ssh.RunCommandAsync($"rm -rf {root}");
            }
            catch
            {
                // 清理尽力而为;容器本来就是一次性的。
            }
            await ssh.DisposeAsync();
        }
    }

    /// <summary>
    /// 覆盖上传的临时文件<b>一建出来</b>就是被替换文件的权限位(0777 那几位),不是先按 0644 写完、提交前才收窄 ——
    /// 写入的那段时间里同机的其他用户读不到一个 0600 文件的新内容。setuid 位仍由提交补上(属主对上之后)。
    /// </summary>
    [TestMethod]
    [TestCategory("DockerIntegration")]
    [Timeout(60_000)]
    public async Task OverwriteUpload_TemporaryStartsWithTheReplacedFilesMode_AgainstRealOpenSsh()
    {
        RequireDockerAndSsh();

        string root = $"/tmp/vela-create-mode-{Guid.NewGuid():N}";
        VelaSshClientWrapper ssh = await ConnectAsync();
        try
        {
            (string Name, string Mode, string Temporary)[] files = [("secret", "600", "600"), ("script", "755", "755"), ("tool", "4755", "755")];
            await ssh.RunCommandAsync($"mkdir -p {root} && cd {root} && "
                + string.Join(" && ", files.Select(f => $"printf old > {f.Name} && chmod {f.Mode} {f.Name}")));

            SshConnection inner = ssh.InnerConnection ?? throw new InvalidOperationException("SSH not connected.");
            await using var sftp = new VelaSftpClientWrapper(
                async ct => await SftpFileSystem.ConnectAsync(inner, cancellationToken: ct));
            await sftp.ConnectAsync(CancellationToken.None);
            var sessionId = Guid.NewGuid();
            ISshConnectionService connections = Substitute.For<ISshConnectionService>();
            connections.GetSession(sessionId).Returns(new SshSession
            {
                SessionId = sessionId,
                Status = SessionStatus.Connected,
                ConnectionInfo = new() { Host = TestHost, Port = TestPort, Username = TestUser, AuthMethod = AuthMethod.Password },
            });
            await using var service = new SftpService(connections, _ => sftp);

            Assert.IsNull(await service.GetUploadCreateModeAsync(sessionId, $"{root}/absent"), "新文件按服务端默认建");
            foreach ((string name, string mode, string expectedTemporary) in files)
            {
                uint? createMode = await service.GetUploadCreateModeAsync(sessionId, $"{root}/{name}");
                string temporary = $"{root}/.vela-up-{name}";
                using (var content = new MemoryStream("new"u8.ToArray()))
                {
                    await service.UploadStreamAsync(sessionId, content, temporary, 3, createMode: createMode);
                }
                Assert.AreEqual(expectedTemporary, (await ssh.RunCommandAsync($"stat -c %a {temporary}")).Trim(), $"{name}:临时文件写完、提交之前的模式");

                await service.CommitUploadAsync(sessionId, temporary, $"{root}/{name}", overwrite: true);
                Assert.AreEqual(mode, (await ssh.RunCommandAsync($"stat -c %a {root}/{name}")).Trim(), name);
            }
        }
        finally
        {
            try
            {
                await ssh.RunCommandAsync($"rm -rf {root}");
            }
            catch
            {
                // 清理尽力而为;容器本来就是一次性的。
            }
            await ssh.DisposeAsync();
        }
    }

    /// <summary>跳过记 Inconclusive 而不是"通过":全绿的报告里不能混着一行断言都没跑的用例。</summary>
    private static void RequireDockerAndSsh()
    {
        if (!IsDockerAvailable())
        {
            Assert.Inconclusive("Docker 不可用。运行 'docker compose -f docker-compose.test.yml up -d' 以启用。");
        }
        try
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync(TestHost, TestPort).Wait(TimeSpan.FromSeconds(3)))
            {
                Assert.Inconclusive($"SSH 测试服务器 {TestHost}:{TestPort} 不可达。");
            }
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            Assert.Inconclusive($"SSH 测试服务器 {TestHost}:{TestPort} 不可达:{ex.Message}");
        }
    }

    private static bool IsDockerAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null)
            {
                return false;
            }
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<VelaSshClientWrapper> ConnectAsync()
    {
        VelaSshClientWrapper client = new(
            ct => SshConnection.ConnectAsync(new SshConnectionOptions(TestUser, TestHost, TestPort)
            {
                Credentials = [new PasswordCredential(TestPassword)],
                // 测试容器的主机键每次重建都变:无条件信任,不写 known_hosts。
                HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            }, ct),
            TimeSpan.FromSeconds(10));

        await client.ConnectAsync(CancellationToken.None);
        return client;
    }
}
