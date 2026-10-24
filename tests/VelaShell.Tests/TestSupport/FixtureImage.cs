using System.Collections.Concurrent;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;

namespace VelaShell.Tests.TestSupport;

/// <summary>
/// <c>docker-compose.test.yml</c> 里按 <c>tests/fixtures/&lt;靶机&gt;/Dockerfile</c> 建的靶机:本机跑着的镜像
/// 是不是按仓库里现在这份 Dockerfile 建的。不是就记 Inconclusive 并提示重建,而不是让用例红。
/// </summary>
/// <remarks>
/// <para>
/// 来由:<c>velashell-test-shells</c> 建于 Dockerfile 给 <c>vela-dash</c> 关 X11 之前时,
/// <c>X11_RefusedByServer_KeepsAgentForwardingAndWarnsOnce</c> 一直红(<c>plan.md</c> §144、§159、§167、§186);
/// 改了 Dockerfile 而本机没重建,同样的事还会再来。红的用例看上去像代码回归,查半天才发现是镜像旧了。
/// </para>
/// <para>
/// 做法:Dockerfile 末尾把自己 <c>COPY</c> 进镜像(<see cref="ImagePath" />),这里经 SSH 读出来与仓库里的比。
/// 比的是「有效内容」—— 去掉 <c>\r</c>、整行注释、空行与行尾空白(Docker 自己解析时也丢掉这些),
/// 所以只改注释不会让镜像被判旧;改了任何一条指令都会。不用手写版本号:改 Dockerfile 的人忘了抬号,
/// 正是这条要防的那种「改了却没重建」。镜像里根本没有这个文件,说明它早于这项检查,一样算旧。
/// </para>
/// <para>
/// 结论按靶机在进程内只算一次。读不了(仓库里找不到 Dockerfile、SSH 连不上)就不拦 —— 判断不了不等于旧了,
/// 连不上的情形由各用例自己的可达性检查报。
/// </para>
/// </remarks>
internal static class FixtureImage
{
    /// <summary>镜像里那份 Dockerfile 的位置(Dockerfile 末尾 <c>COPY Dockerfile</c> 到这里)。</summary>
    public const string ImagePath = "/etc/velashell-fixture/Dockerfile";

    private static readonly ConcurrentDictionary<string, string?> Verdicts = new(StringComparer.Ordinal);

    /// <summary>靶机镜像旧了就 <see cref="Assert.Inconclusive(string)" />,附上重建命令。</summary>
    /// <param name="fixture"><c>tests/fixtures/</c> 下的目录名,如 <c>ssh-shells</c>。</param>
    /// <param name="service"><c>docker-compose.test.yml</c> 里的服务名(这几台与目录名相同)。</param>
    /// <param name="host">靶机地址。</param>
    /// <param name="port">靶机端口。</param>
    /// <param name="user">任一能登录的账号。</param>
    /// <param name="password">口令。</param>
    public static void RequireCurrent(string fixture, string service, string host, int port, string user, string password)
    {
        if (!Verdicts.TryGetValue(fixture, out string? stale))
        {
            string? repo = ReadRepositoryDockerfile(fixture);
            if (repo is null)
            {
                return;
            }
            string? image;
            try
            {
                image = Task.Run(() => ReadImageDockerfileAsync(host, port, user, password)).GetAwaiter().GetResult();
            }
            catch
            {
                return;
            }
            stale = Compare(fixture, service, repo, image);
            Verdicts[fixture] = stale;
        }
        if (stale is not null)
        {
            Assert.Inconclusive(stale);
        }
    }

    /// <summary>比对两份 Dockerfile;镜像是按仓库这份建的就返回 null,否则返回跳过说明。</summary>
    /// <param name="fixture">靶机目录名。</param>
    /// <param name="service">compose 服务名。</param>
    /// <param name="repository">仓库里的 Dockerfile 全文。</param>
    /// <param name="image">镜像里读出的 Dockerfile 全文;镜像里没有这个文件时为 null 或空串。</param>
    internal static string? Compare(string fixture, string service, string repository, string? image)
    {
        string rebuild = $"重建:docker compose -f docker-compose.test.yml build {service}"
                         + $" && docker compose -f docker-compose.test.yml up -d {service}";
        if (string.IsNullOrWhiteSpace(image))
        {
            return $"[SKIP] {fixture} 靶机镜像里没有 {ImagePath},早于仓库里现在的 tests/fixtures/{fixture}/Dockerfile。{rebuild}";
        }
        IReadOnlyList<string> expected = Normalize(repository);
        IReadOnlyList<string> actual = Normalize(image);
        int firstDifference = 0;
        while (firstDifference < expected.Count && firstDifference < actual.Count
               && expected[firstDifference] == actual[firstDifference])
        {
            firstDifference++;
        }
        if (firstDifference == expected.Count && firstDifference == actual.Count)
        {
            return null;
        }
        string where = firstDifference < expected.Count
            ? $"仓库里的第 {firstDifference + 1} 条有效行「{expected[firstDifference]}」"
            : "镜像里多出的行";
        return $"[SKIP] {fixture} 靶机镜像不是按仓库里现在的 tests/fixtures/{fixture}/Dockerfile 建的(从{where}起不一致)。{rebuild}";
    }

    /// <summary>Dockerfile 的有效内容:去掉 <c>\r</c>、行尾空白、整行注释与空行。</summary>
    internal static IReadOnlyList<string> Normalize(string dockerfile) =>
    [
        .. dockerfile.Replace("\r", string.Empty, StringComparison.Ordinal)
                     .Split('\n')
                     .Select(line => line.TrimEnd())
                     .Where(line => line.Length > 0 && !line.TrimStart().StartsWith('#'))
    ];

    /// <summary>从测试输出目录往上找仓库根(有 <c>docker-compose.test.yml</c> 的那一层),读靶机的 Dockerfile。</summary>
    internal static string? ReadRepositoryDockerfile(string fixture)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docker-compose.test.yml")))
            {
                string path = Path.Combine(dir.FullName, "tests", "fixtures", fixture, "Dockerfile");
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
        }
        return null;
    }

    private static async Task<string> ReadImageDockerfileAsync(string host, int port, string user, string password)
    {
        await using VelaSshClientWrapper client = new(
            ct => SshConnection.ConnectAsync(new SshConnectionOptions(user, host, port)
            {
                Credentials = [new PasswordCredential(password)],
                // 靶机的主机键每次重建都变:无条件信任,不写 known_hosts。
                HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            }, ct),
            TimeSpan.FromSeconds(10));
        await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
        return await client.RunCommandAsync($"cat {ImagePath} 2>/dev/null").ConfigureAwait(false);
    }
}
