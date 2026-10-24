using System.Globalization;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Config;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Infrastructure.Import;

/// <summary>
/// OpenSSH <c>~/.ssh/config</c> 的 <see cref="ISessionImportService" /> 实现:定位配置文件、
/// 按 OpenSSH 的取值规则解析每个主机别名(<c>HostName</c> / <c>Port</c> / <c>User</c> /
/// <c>IdentityFile</c> / <c>CertificateFile</c> / <c>IdentityAgent</c> / <c>ProxyJump</c>、
/// <c>KexAlgorithms</c> / <c>HostKeyAlgorithms</c> / <c>Ciphers</c> / <c>MACs</c> / <c>Compression</c> / <c>ForwardAgent</c>、
/// <c>LocalForward</c> / <c>RemoteForward</c> / <c>DynamicForward</c>),把选中的会话写入 <see cref="ISessionRepository" />。
/// </summary>
/// <remarks>
/// <para>
/// 与 Xshell / WinSCP 两个来源不同,这里**没有密码可还原** —— OpenSSH 的配置文件从不存密码,
/// 认证要么靠 <c>IdentityFile</c> 指的私钥(旁边有 <c>-cert.pub</c> 或写了 <c>CertificateFile</c> 时是证书),
/// 要么 agent,要么交互式输入。因此每条会话的 <see cref="ImportedSession.HasEncryptedPassword" /> 恒为 <c>false</c>。
/// </para>
/// <para>
/// 只导入**字面量别名**:<c>Host *</c>、<c>Host *.internal</c> 这类通配块是给别人兜底用的模板,
/// 本身不是一台可连的机器;它们的选项会通过取值规则渗到具名别名上,这正是 OpenSSH 的语义。
/// </para>
/// <para>
/// 映射不了的设置(Unix 套接字转发、远程动态转发、指定了别的 agent 套接字、本版没实现的算法)
/// 不悄悄丢掉:写进 <see cref="ImportedSession.Notes" />,预览行里列给用户看。
/// </para>
/// </remarks>
/// <param name="repository">连接仓储。</param>
/// <param name="dataStore">通用文档存储:转发落成隧道写在这里;为 <see langword="null" /> 时不导入转发。</param>
public sealed class SshConfigImportService(ISessionRepository repository, IAppDataStore? dataStore = null) : ISessionImportService
{
    private readonly ISessionRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <summary>写入会话时打的标签,同时用作分组名兜底。</summary>
    internal const string Tag = "ssh-config";

    /// <inheritdoc />
    public string SourceKey => "SSH config";

    /// <inheritdoc />
    public ImportBrowseKind BrowseKind => ImportBrowseKind.File;

    /// <inheritdoc />
    public string? DetectDefaultSource()
    {
        string path = Path.Combine(SshPathResolver.SshDirectory, "config");
        return File.Exists(path) ? path : null;
    }

    /// <inheritdoc />
    public async Task<SessionImportScan> ScanAsync(string? source, CancellationToken cancellationToken = default)
    {
        string? path = string.IsNullOrWhiteSpace(source) ? DetectDefaultSource() : source;
        if (path is null || !File.Exists(path))
        {
            return new SessionImportScan { Source = path ?? string.Empty, Items = [] };
        }

        // Include 的相对路径以 ~/.ssh 为基准(OpenSSH 用户配置的规则),而不是配置文件自身所在目录 ——
        // 用户手动指定了别处的配置文件时,里面的 `Include conf.d/*` 仍指向 ~/.ssh/conf.d。
        // 解析、Include 展开、Match 判定都用 SSH 库的 SshConfigFile,宿主不另写一份。
        string baseDirectory = SshPathResolver.SshDirectory;
        IReadOnlyList<SshConfigBlock> blocks = await SshConfigFile
            .LoadAsync(path, includeDirectory: baseDirectory, cancellationToken).ConfigureAwait(false);

        List<SessionProfile> existing = await _repository.GetAllSessionsAsync().ConfigureAwait(false);
        var existingKeys = existing
            .Select(static s => DedupKey(s.Host, s.Port, s.Username))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<ImportedSession>();
        foreach (string alias in HostAliases(blocks))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 静态导入没有「以谁的身份、从哪台机器连」的上下文:Match 里判不了的条件
            // (user、localuser、exec)按不成立处理,那种块的选项不会渗到会话上。
            SshHostConfig options = SshConfigFile.Resolve(blocks, alias);

            // HostName 缺省即别名本身 —— `Host build01` 不写 HostName 时,ssh 直接连 build01。
            // 其中的 %h 由 SSH 库展开(SshHostConfig.HostName),宿主不另写一份。
            string host = Value(options.HostName) ?? alias;
            if (host.Length == 0)
            {
                continue;
            }

            int port = options.Port;   // SSH 库已经把配得不对的端口换成 22
            string user = Value(options.User) ?? string.Empty;
            List<string> notes = [];
            // ~ 与 %d %u %h %r 由 SSH 库展开(与连接时读私钥同一套),宿主只把相对路径落到 ~/.ssh 下。
            List<string> identities = [.. options.ExpandIdentityFiles(user)
                .Select(file => ResolveIdentityFile(file, baseDirectory)).OfType<string>()];
            (string? keyPath, string? certificatePath, AuthMethod? auth) = MapCredentials(options, user, identities, baseDirectory, notes);
            string? jump = SshConfigFile.ParseProxyJump(options.ProxyJump) is [.., SshProxyJumpHop last]
                ? last.Host
                : null;
            SshSessionOptions ssh = MapSshOptions(options, notes);
            List<TunnelConfig> tunnels = MapForwards(options, notes);

            items.Add(new ImportedSession
            {
                Name = alias,
                Host = host,
                Port = port,
                Username = user,
                ConnectionType = ConnectionType.SSH,
                Protocol = "SSH",
                IsSupported = true,
                HasEncryptedPassword = false,
                Password = null,
                PrivateKeyPath = keyPath,
                CertificatePath = certificatePath,
                AuthMethod = auth,
                Ssh = ssh.IsEmpty ? null : ssh,
                Tunnels = tunnels.Count > 0 ? tunnels : null,
                Notes = notes,
                JumpHostAlias = jump,
                AlreadyExists = existingKeys.Contains(DedupKey(host, port, user)),
                Source = path
            });
        }

        return new SessionImportScan
        {
            Source = path,
            Items = items,
            MasterPasswordEnabled = false
        };
    }

    /// <inheritdoc />
    public async Task<SessionImportOutcome> ImportAsync(IReadOnlyList<ImportedSession> items, string groupName, CancellationToken cancellationToken = default) =>
        await SessionImportWriter.WriteAsync(_repository, items, groupName, Tag, cancellationToken, dataStore).ConfigureAwait(false);

    /// <summary>去掉首尾空白;空串当作没有。</summary>
    private static string? Value(string? value) => value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>
    /// 认证:<c>CertificateFile</c>(或 <c>IdentityFile</c> 旁边存在的 <c>-cert.pub</c>)→ 证书;<c>IdentityFile</c> → 私钥;
    /// 两样都没有而 <c>IdentityAgent</c> 指着默认的 agent → SSH Agent;都不沾 → 交给写入器按密码处理。
    /// </summary>
    /// <remarks>
    /// <para>
    /// OpenSSH 自己会去读 <c>&lt;IdentityFile&gt;-cert.pub</c>(ssh_config(5) 的 <c>CertificateFile</c> 一节),
    /// 所以那个文件在本机存在时同样按证书认证导入;不存在的不猜(私钥可能还没拷过来,证书就更说不准)。
    /// </para>
    /// <para>
    /// 证书配哪把私钥:<c>IdentityFile</c> 里与证书同名(去掉 <c>-cert.pub</c>)的那把优先,否则第一把;
    /// 一把都没写时按 ssh-keygen 的命名约定由证书路径反推。VelaShell 一条连接只用一种认证方式,
    /// 写了多把私钥的只取一把,其余的如实列进预览。
    /// </para>
    /// </remarks>
    private static (string? KeyPath, string? CertificatePath, AuthMethod? Auth) MapCredentials(
        SshHostConfig options, string user, List<string> identities, string baseDirectory, List<string> notes)
    {
        string? certificate = options.ExpandCertificateFiles(user)
            .Select(file => ResolveIdentityFile(file, baseDirectory)).OfType<string>().FirstOrDefault();
        certificate ??= identities
            .Select(identity => identity + OpenSshCertificatePaths.CertificateSuffix)
            .FirstOrDefault(FileExists);

        if (certificate is not null)
        {
            string? inferred = OpenSshCertificatePaths.InferPrivateKeyPath(certificate, static _ => true);
            string? key = identities.FirstOrDefault(identity => string.Equals(identity, inferred, StringComparison.OrdinalIgnoreCase))
                          ?? identities.FirstOrDefault()
                          ?? inferred;
            NoteExtraIdentities(identities, key, notes);
            if (key is null)
            {
                notes.Add(Strings.Get("SshCfgImport_NoteCertificateNoKey"));
            }
            return (key, certificate, AuthMethod.Certificate);
        }

        if (identities.Count > 0)
        {
            NoteExtraIdentities(identities, identities[0], notes);
            return (identities[0], null, null);
        }

        if (Value(options.First("IdentityAgent")) is { } agentSetting)
        {
            if (!options.TryGetIdentityAgent(out string? endpoint))
            {
                return (null, null, null);   // IdentityAgent none:不用 agent。
            }
            if (endpoint is null || IsDefaultAgent(endpoint))
            {
                return (null, null, AuthMethod.Agent);
            }
            notes.Add(Strings.Format("SshCfgImport_NoteIdentityAgentPath", agentSetting));
        }
        return (null, null, null);
    }

    private static void NoteExtraIdentities(List<string> identities, string? used, List<string> notes)
    {
        List<string> unused = [.. identities.Where(identity => !string.Equals(identity, used, StringComparison.OrdinalIgnoreCase))];
        if (unused.Count > 0)
        {
            notes.Add(Strings.Format("SshCfgImport_NoteExtraIdentities", string.Join(", ", unused.Select(Path.GetFileName))));
        }
    }

    /// <summary>
    /// 协议层选项:<c>Compression</c>、<c>ForwardAgent</c>,以及四个算法清单(§134 的自定义清单,写法本来就是 ssh_config 的
    /// <c>+</c> / <c>-</c> / <c>^</c>,原样带过来;本版没实现的名字去掉,拼错的整条不导,都进预览)。
    /// </summary>
    private static SshSessionOptions MapSshOptions(SshHostConfig options, List<string> notes)
    {
        var ssh = new SshSessionOptions
        {
            Compression = options.Compression,
            KexAlgorithms = Algorithms(SshAlgorithmKind.KeyExchange, "KexAlgorithms", options.KexAlgorithms, notes),
            HostKeyAlgorithms = Algorithms(SshAlgorithmKind.HostKey, "HostKeyAlgorithms", options.HostKeyAlgorithms, notes),
            Ciphers = Algorithms(SshAlgorithmKind.Cipher, "Ciphers", options.Ciphers, notes),
            Macs = Algorithms(SshAlgorithmKind.Mac, "MACs", options.Macs, notes),
        };

        // ForwardAgent:yes 转发默认的 agent;写的是别的 agent 套接字时不导 —— VelaShell 只转发默认的那个,
        // 而配置作者特意给这台机器指了一个(多半是只装了几把钥的)agent,换成默认的等于多交出去几把钥。
        string? forward = Value(options.First("ForwardAgent"));
        if (forward is not null && !string.Equals(forward, "no", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(forward, "yes", StringComparison.OrdinalIgnoreCase) || IsDefaultAgent(ResolveAgentValue(forward)))
            {
                ssh.AgentForwarding = true;
            }
            else
            {
                notes.Add(Strings.Format("SshCfgImport_NoteForwardAgentPath", forward));
            }
        }
        return ssh;
    }

    private static string? Algorithms(SshAlgorithmKind kind, string keyword, string? spec, List<string> notes)
    {
        string? usable = SshAlgorithmPreferences.SanitizeImported(kind, spec, out IReadOnlyList<string> dropped, out string? error);
        if (error is not null)
        {
            notes.Add(Strings.Format("SshCfgImport_NoteAlgoSkipped", keyword, error));
        }
        else if (dropped.Count > 0)
        {
            notes.Add(Strings.Format("SshCfgImport_NoteAlgoDropped", keyword, string.Join(", ", dropped)));
        }
        return usable;
    }

    /// <summary>
    /// 转发 → 隧道(停止状态,在隧道面板里启动):<c>LocalForward</c> → 本地转发、<c>RemoteForward</c> → 远程转发、
    /// <c>DynamicForward</c> → 动态转发(SOCKS)。现有的隧道类型装不下的(Unix 套接字、远程动态转发)如实跳过,进预览。
    /// </summary>
    /// <remarks>
    /// 没写监听地址时本地一头绑环回(<c>GatewayPorts yes</c> 时全部网卡)、远程一头交给服务端定(环回),与 ssh 的默认一致;
    /// <c>*</c> 是全部网卡。<c>ClearAllForwardings yes</c> 时一条都不导。逐行解析:一行写错不连累别的行。
    /// </remarks>
    private static List<TunnelConfig> MapForwards(SshHostConfig options, List<string> notes)
    {
        List<TunnelConfig> tunnels = [];
        if (options.ClearAllForwardings)
        {
            return tunnels;
        }

        string localDefault = options.GatewayPorts ? "0.0.0.0" : "127.0.0.1";
        foreach ((SshConfigForwardKind kind, string keyword) in new[]
                 {
                     (SshConfigForwardKind.Local, "LocalForward"),
                     (SshConfigForwardKind.Remote, "RemoteForward"),
                     (SshConfigForwardKind.Dynamic, "DynamicForward"),
                 })
        {
            foreach (string value in options.All(keyword))
            {
                SshConfigForward forward;
                try
                {
                    forward = SshConfigForward.Parse(kind, value);
                }
                catch (SshConnectException)
                {
                    notes.Add(Strings.Format("SshCfgImport_NoteForwardInvalid", $"{keyword} {value}"));
                    continue;
                }

                if (forward.ListenSocketPath is not null || forward.TargetSocketPath is not null || forward.IsRemoteDynamic
                    || forward.ListenPort == 0)
                {
                    notes.Add(Strings.Format("SshCfgImport_NoteForwardSkipped", $"{keyword} {value}"));
                    continue;
                }

                tunnels.Add(kind switch
                {
                    SshConfigForwardKind.Local => new TunnelConfig
                    {
                        Type = TunnelType.LocalForward,
                        Name = string.Empty,
                        LocalHost = ListenHost(forward.ListenAddress, localDefault),
                        LocalPort = (uint)forward.ListenPort,
                        RemoteHost = HostAddress.Normalize(forward.TargetHost!),
                        RemotePort = (uint)forward.TargetPort,
                    },
                    SshConfigForwardKind.Remote => new TunnelConfig
                    {
                        Type = TunnelType.RemoteForward,
                        Name = string.Empty,
                        LocalHost = HostAddress.Normalize(forward.TargetHost!),
                        LocalPort = (uint)forward.TargetPort,
                        RemoteHost = ListenHost(forward.ListenAddress, "127.0.0.1"),
                        RemotePort = (uint)forward.ListenPort,
                    },
                    _ => new TunnelConfig
                    {
                        Type = TunnelType.DynamicForward,
                        Name = string.Empty,
                        LocalHost = ListenHost(forward.ListenAddress, localDefault),
                        LocalPort = (uint)forward.ListenPort,
                    },
                });
            }
        }

        if (tunnels.Count > 0)
        {
            notes.Add(Strings.Format("SshCfgImport_NoteForwardsImported", tunnels.Count.ToString(CultureInfo.InvariantCulture)));
        }
        return tunnels;
    }

    /// <summary>监听地址:没写用默认,<c>*</c> 是全部网卡,其余原样(IPv6 去掉方括号)。</summary>
    private static string ListenHost(string? address, string fallback) => address switch
    {
        null or "" => fallback,
        "*" => "0.0.0.0",
        "localhost" => "127.0.0.1",
        _ => HostAddress.Normalize(address),
    };

    /// <summary><c>$变量</c> 取环境变量的值,其余原样(路径)。</summary>
    private static string? ResolveAgentValue(string value) =>
        value.StartsWith('$') ? Environment.GetEnvironmentVariable(value[1..]) : value;

    /// <summary>这个 agent 端点是不是 VelaShell 连的那个默认 agent(<c>SSH_AUTH_SOCK</c> 指着的)。</summary>
    private static bool IsDefaultAgent(string? endpoint) =>
        endpoint is { Length: > 0 }
        && Environment.GetEnvironmentVariable("SSH_AUTH_SOCK") is { Length: > 0 } socket
        && string.Equals(endpoint.Trim(), socket.Trim(), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 可作为会话导入的主机别名:<c>Host</c> 行上不含通配与取反的模式,按首次出现去重。
    /// </summary>
    /// <remarks><c>Match</c> 块没有 <c>Host</c> 模式,自然不产生别名。</remarks>
    private static List<string> HostAliases(IReadOnlyList<SshConfigBlock> blocks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new List<string>();
        foreach (SshConfigBlock block in blocks)
        {
            if (block.Match is not null)
            {
                continue;
            }
            foreach (string pattern in block.Patterns)
            {
                if (IsLiteralAlias(pattern) && seen.Add(pattern))
                {
                    aliases.Add(pattern);
                }
            }
        }
        return aliases;
    }

    /// <summary>别名是否是「字面量」:不含 <c>*</c> / <c>?</c> 通配,也不是取反模式。</summary>
    private static bool IsLiteralAlias(string pattern) =>
        pattern.Length > 0 && !pattern.StartsWith('!') && pattern.AsSpan().IndexOfAny('*', '?') < 0;

    /// <summary>
    /// 把 SSH 库展开好的 <c>IdentityFile</c> 落成本机绝对路径(相对路径按 <c>~/.ssh</c>)。文件不存在也照样带上 ——
    /// 密钥可能在另一台机器上,或者用户正打算补进来;把路径留在配置里比悄悄丢掉更有用。
    /// </summary>
    /// <remarks>
    /// <c>~</c> / <c>%d</c> / <c>%h</c> 的展开曾经在这里另写了一份(而且不认 <c>%h</c> / <c>%r</c>);
    /// <c>IdentityFile none</c> 由库排除。
    /// </remarks>
    private static string? ResolveIdentityFile(string? expanded, string baseDirectory)
    {
        if (expanded is null)
        {
            return null;
        }
        string path = SshPathResolver.ToAbsolute(expanded, baseDirectory);
        return path.Length > 0 ? path : null;
    }

    private static string DedupKey(string host, int port, string user) => $"{host.Trim()}|{port}|{user.Trim()}";
}
