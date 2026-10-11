// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH sshd(8) 的 AUTHORIZED_KEYS / SSH_KNOWN_HOSTS 章节（含 @cert-authority / @revoked）
//   OpenSSH PROTOCOL.certkeys（主机证书）
//   行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.4、§5.5

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.HostKeys;

/// <summary>读写 <c>known_hosts</c>。</summary>
/// <remarks>
/// <para>
/// 支持三种主机名写法：明文（<c>example.com</c>）、带端口（<c>[example.com]:2222</c>）、
/// 以及 <b>散列过的</b>（<c>|1|base64(salt)|base64(hmac-sha1)</c>）。
/// </para>
/// <para>
/// 散列形式是 OpenSSH 的 <c>HashKnownHosts yes</c> 产物，在很多发行版上是默认 ——
/// 不支持它等于在那些机器上完全读不到已知主机。
/// </para>
/// </remarks>
public static class KnownHostsFile
{
    /// <summary>用户默认的 <c>known_hosts</c> 路径。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    /// <summary>解析一份 <c>known_hosts</c> 文本。</summary>
    /// <remarks>
    /// <b>解析不出来的行一律跳过，不抛异常。</b>
    /// 真实的 <c>known_hosts</c> 里什么都有：手写的注释、被别的工具写坏的行、
    /// 未来才定义的密钥类型。为其中一行报错，等于让整个文件不可用。
    /// </remarks>
    public static IReadOnlyList<KnownHostEntry> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        List<KnownHostEntry> entries = [];
        string[] lines = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            KnownHostEntry? entry = ParseLine(lines[i].Trim('\r', ' ', '\t'), i + 1);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>读一份 <c>known_hosts</c> 文件；文件不存在时返回空列表。</summary>
    /// <exception cref="SshConnectException">
    /// 文件在却读不出来（没有权限、被占用、IO 错误）：<see cref="SshFailureReason.HostKeyStoreFailed"/>。
    /// </exception>
    public static async ValueTask<IReadOnlyList<KnownHostEntry>> LoadAsync(
        string? path = null, CancellationToken cancellationToken = default)
    {
        string actual = path ?? DefaultPath;

        if (!File.Exists(actual))
        {
            // 第一次用的机器上它本来就不存在。那不是错误。
            return [];
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(actual, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];   // 查过之后、读之前被删了：与不存在一样
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StoreFailed($"读不了 {actual}", ex);
        }

        return Parse(content);
    }

    /// <summary>
    /// 〔velashell-docs/zh/ssh/spec/03 §5.4〕读写 known_hosts 失败。曾经原样漏出 BCL 异常：
    /// <see cref="IOException"/> 在建连路上被归成「对端断开」、判为可重试，<see cref="UnauthorizedAccessException"/>
    /// 干脆不是 <see cref="SshException"/>。
    /// </summary>
    private static SshConnectException StoreFailed(string what, Exception inner) =>
        new(SshFailureReason.HostKeyStoreFailed, SshPhase.KeyExchange, $"{what}：{inner.Message}", inner);

    private static KnownHostEntry? ParseLine(string line, int lineNumber)
    {
        if (line.Length == 0 || line[0] == '#')
        {
            return null;
        }

        // 字段之间是空白 —— 空格或 Tab 都算（sshd(8) 的 SSH_KNOWN_HOSTS 一节）。
        // 曾经只按空格切：手工编辑、用 Tab 对齐的行被当成写坏的行静默跳过，那台主机于是一直按「没见过」处理。
        string[] fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        int index = 0;

        string marker = "";
        if (fields.Length > 0 && fields[0].StartsWith('@'))
        {
            // ⚠️ 只认 sshd(8) 定义的两个标记，**认不出的整行跳过**。曾经任何 @ 开头的都收作标记，
            //    却只在完全等于这两个时才特殊处理：管理员想吊销一把钥、把 @revoked 写成 @revoke，
            //    这一行就落进普通分支，这把钥对模式匹配到的所有主机都成了「已知」—— 与本意正好相反。
            //    将来新增的标记同理：不懂它的意思，就不能按受信行去用。
            if (fields[0] is not (KnownHostEntry.RevokedMarker or KnownHostEntry.CertificateAuthorityMarker))
            {
                return null;
            }

            marker = fields[0];
            index = 1;
        }

        // 主机 ‖ 密钥类型 ‖ base64 —— 少一样就不是一条有效记录。
        if (fields.Length < index + 3)
        {
            return null;
        }

        string hostField = fields[index];
        string keyType = fields[index + 1];
        string base64 = fields[index + 2];

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;   // 被写坏的行，跳过
        }

        bool hashed = hostField.StartsWith("|1|", StringComparison.Ordinal);

        return new KnownHostEntry(
            hashed ? [hostField] : [.. hostField.Split(',', StringSplitOptions.RemoveEmptyEntries)],
            hashed,
            marker,
            keyType,
            blob,
            lineNumber);
    }

    /// <summary>查一台主机的一把密钥（主机证书按此刻的时间验有效期）。</summary>
    /// <param name="entries">已解析的条目。</param>
    /// <param name="host">主机名或地址。</param>
    /// <param name="port">端口。<b>非 22 时要按 <c>[host]:port</c> 匹配。</b></param>
    /// <param name="key">服务端出示的公钥（普通公钥或主机证书）。</param>
    public static KnownHostLookup Lookup(
        IReadOnlyList<KnownHostEntry> entries, string host, int port, SshPublicKey key) =>
        Lookup(entries, host, port, key, DateTimeOffset.UtcNow);

    /// <summary>查一台主机的一把密钥。</summary>
    /// <param name="entries">已解析的条目。</param>
    /// <param name="host">主机名或地址。</param>
    /// <param name="port">端口。<b>非 22 时要按 <c>[host]:port</c> 匹配。</b></param>
    /// <param name="key">服务端出示的公钥（普通公钥或主机证书）。</param>
    /// <param name="now">验主机证书有效期用的时刻。</param>
    /// <remarks>
    /// 主机证书的裁决顺序见 velashell-docs/zh/ssh/spec/03 §5.5：吊销 → 证书里那把钥单独记着 →
    /// 有对上的 CA 就验证书 → 否则把证书里那把钥当普通钥。<b>比对与记录用的都是证书里那把钥</b>
    /// （<see cref="SshPublicKey.PlainKey"/>），不是证书 blob —— 证书每次重签 blob 都会变。
    /// </remarks>
    public static KnownHostLookup Lookup(
        IReadOnlyList<KnownHostEntry> entries, string host, int port, SshPublicKey key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(key);

        string plain = FormatHostPattern(host, port);
        SshPublicKey presented = key.PlainKey;
        OpenSshCertificate? certificate = key.Certificate;
        SshPublicKey? authorityKey = certificate?.SignatureKey;

        List<KnownHostEntry> conflicts = [];
        List<KnownHostEntry> otherTypes = [];
        KnownHostEntry? trusted = null;
        KnownHostEntry? authority = null;

        // ⚠️ **必须把整份表扫完才能下结论。**
        //
        // 吊销要压过信任，而 @revoked 那一行通常是**追加**在后面的
        // （撤销一把密钥最自然的动作就是往文件末尾加一行）。
        // 一碰到「已知」就 return 的话，吊销会被前面那条旧的信任行盖掉 ——
        // 那是一个实打实的安全漏洞：撤销了等于没撤销。
        foreach (KnownHostEntry entry in entries)
        {
            if (!MatchesHost(entry, host, port, plain))
            {
                continue;
            }

            bool sameKey = entry.KeyBlob.Span.SequenceEqual(presented.Blob.Span);

            if (entry.IsRevoked)
            {
                // 吊销的可以是那把钥、整张证书，或者签发它的 CA（吊销一个 CA 就作废它签过的全部证书）。
                if (sameKey
                    || (certificate is not null && entry.KeyBlob.Span.SequenceEqual(key.Blob.Span))
                    || (authorityKey is not null && entry.KeyBlob.Span.SequenceEqual(authorityKey.Blob.Span)))
                {
                    // 吊销赢，立刻返回 —— 后面再有什么都不重要了。
                    return new KnownHostLookup(KnownHostStatus.Revoked, entry, []);
                }
                continue;
            }

            if (entry.IsCertificateAuthority)
            {
                // CA 只为它签的证书担保；不把 CA 公钥当成这台主机的普通密钥来比
                // （否则会把「出示了一张证书」误报成「密钥变了」）。
                if (authorityKey is not null && entry.KeyBlob.Span.SequenceEqual(authorityKey.Blob.Span))
                {
                    authority ??= entry;
                }
                else
                {
                    // 这台主机由（别的）CA 管：出示的钥若没有单独记着，不能当成没见过（见 OtherKeyTypesKnown）。
                    otherTypes.Add(entry);
                }
                continue;
            }

            if (sameKey)
            {
                trusted ??= entry;
                continue;
            }

            // 主机对上、密钥不对。**先记下来继续找** ——
            // 同一台主机可以有多把不同类型的密钥（ed25519 与 rsa 各一条），
            // 只有当没有任何一条对上时，「变了」才成立。
            if (entry.KeyType == presented.KeyType)
            {
                conflicts.Add(entry);
            }
            else
            {
                otherTypes.Add(entry);
            }
        }

        // 明确记下的钥优先：它已经被信任，证书不必再看。
        if (trusted is not null)
        {
            return new KnownHostLookup(KnownHostStatus.Known, trusted, []);
        }

        if (authority is not null)
        {
            string? problem = certificate!.CheckHostCertificate(host, now);
            return problem is null
                ? new KnownHostLookup(KnownHostStatus.Known, authority, [])
                : new KnownHostLookup(KnownHostStatus.CertificateInvalid, authority, []) { CertificateProblem = problem };
        }

        if (conflicts.Count > 0)
        {
            return new KnownHostLookup(KnownHostStatus.Changed, null, conflicts);
        }

        // 只记着别的类型：**不是「没见过」**（见 OtherKeyTypesKnown）。
        return otherTypes.Count > 0
            ? new KnownHostLookup(KnownHostStatus.OtherKeyTypesKnown, null, otherTypes)
            : new KnownHostLookup(KnownHostStatus.Unknown, null, []);
    }

    /// <summary>这台主机在 <c>known_hosts</c> 里记着哪些类型的密钥（不含吊销行）。</summary>
    /// <remarks>
    /// 对上这台主机的 <c>@cert-authority</c> 行报出全部证书类型 —— CA 能为任何类型的主机密钥签证书，
    /// 这样证书算法会被排到前面，服务端才会出示证书（velashell-docs/zh/ssh/spec/03 §5.5）。
    /// </remarks>
    public static IReadOnlyList<string> KnownKeyTypes(IReadOnlyList<KnownHostEntry> entries, string host, int port) =>
        KnownKeyTypesCore(entries, host, port);

    /// <summary>这台主机记着的普通主机密钥（不含 <c>@cert-authority</c> 与 <c>@revoked</c>；解析不了的跳过）。</summary>
    public static IReadOnlyList<SshPublicKey> KnownHostKeys(IReadOnlyList<KnownHostEntry> entries, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(host);

        string plain = FormatHostPattern(host, port);
        List<SshPublicKey> keys = [];
        foreach (KnownHostEntry entry in entries)
        {
            if (entry.IsRevoked || entry.IsCertificateAuthority || !MatchesHost(entry, host, port, plain))
            {
                continue;
            }
            try
            {
                var key = SshPublicKey.Decode(entry.KeyBlob);
                if (!keys.Any(k => k.Blob.Span.SequenceEqual(key.Blob.Span)))
                {
                    keys.Add(key);
                }
            }
            catch (SshPublicKeyException)
            {
                // 这一行的钥本库认不得：不算进去。
            }
        }
        return keys;
    }

    private static List<string> KnownKeyTypesCore(IReadOnlyList<KnownHostEntry> entries, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(host);

        string plain = FormatHostPattern(host, port);
        List<string> types = [];
        foreach (KnownHostEntry entry in entries)
        {
            if (entry.IsRevoked || !MatchesHost(entry, host, port, plain))
            {
                continue;
            }

            foreach (string type in entry.IsCertificateAuthority ? CertificateKeyTypes : [entry.KeyType])
            {
                if (!types.Contains(type, StringComparer.Ordinal))
                {
                    types.Add(type);
                }
            }
        }
        return types;
    }

    /// <summary>本库验得了的主机证书类型（blob 里的类型串）。</summary>
    private static readonly string[] CertificateKeyTypes =
    [
        SshAlgorithmNames.SshEd25519CertV01,
        SshAlgorithmNames.EcdsaSha2Nistp256CertV01,
        SshAlgorithmNames.EcdsaSha2Nistp384CertV01,
        SshAlgorithmNames.EcdsaSha2Nistp521CertV01,
        SshAlgorithmNames.SshRsaCertV01,
    ];

    /// <summary>非 22 端口要按 <c>[host]:port</c> 匹配；主机名一律小写。</summary>
    /// <remarks>
    /// OpenSSH 写 known_hosts 之前先把主机名小写化，散列行算的就是小写名字的 HMAC。
    /// 曾经原样拿去算：用户填的是 <c>Server.Example.COM</c> 时散列行一条都对不上，结论是「没见过」——
    /// 有中间人时，本该报「密钥变了」的连接成了「新主机，要信任吗」，类型偏好的保护也一并失效；
    /// 反过来本库写出的散列行没小写化，OpenSSH 读不到。明文行一直不分大小写，两条分支口径不一致。
    /// </remarks>
    private static string FormatHostPattern(string host, int port) =>
        port == 22 ? host.ToLowerInvariant() : $"[{host.ToLowerInvariant()}]:{port}";

    /// <summary>这一行是不是这台主机的：与 <see cref="Lookup(IReadOnlyList{KnownHostEntry}, string, int, SshPublicKey)"/> 同一套规则（不看标记）。</summary>
    internal static bool MatchesHost(KnownHostEntry entry, string host, int port) =>
        MatchesHost(entry, host, port, FormatHostPattern(host, port));

    private static bool MatchesHost(KnownHostEntry entry, string host, int port, string plain)
    {
        if (entry.IsHashed)
        {
            return MatchesHashed(entry.Patterns[0], plain);
        }

        // 取反模式（!pattern）对上了，这一整行就不算这台主机 —— 哪怕别的模式也对上了。
        // 曾经把它当成「这一个模式不匹配」：`*.corp,!untrusted.corp` 那一行照样经 *.corp
        // 把密钥信给了 untrusted.corp，与写配置的人的本意正好相反。
        bool matched = false;
        foreach (string pattern in entry.Patterns)
        {
            bool negated = pattern.StartsWith('!');
            string body = negated ? pattern[1..] : pattern;

            if (MatchesPattern(body, plain) || (port == 22 && MatchesPattern(body, host)))
            {
                if (negated)
                {
                    return false;
                }
                matched = true;
            }
        }

        return matched;
    }

    /// <summary>匹配 <c>|1|salt|hash</c> 形式。</summary>
    /// <remarks>
    /// HMAC-SHA1(key = salt, data = 主机名)。
    /// SHA-1 在这里不是当安全散列用的 —— 它只是 OpenSSH 定下的、
    /// 用来避免明文列出主机名的混淆手段，换算法就与 OpenSSH 的文件不兼容了。
    /// </remarks>
    private static bool MatchesHashed(string pattern, string host)
    {
        string[] parts = pattern.Split('|');
        if (parts.Length != 4 || parts[1] != "1")
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);

            // Verify 只收长度恰好是 HMAC-SHA1 输出的散列，别的长度它抛 ArgumentException —— 这样的行就是对不上。
            if (expected.Length != HMACSHA1.HashSizeInBytes)
            {
                return false;
            }

#pragma warning disable CA5350 // known_hosts 的散列形式由 OpenSSH 规定就是 HMAC-SHA1，换不得
            return HMACSHA1.Verify(salt, Encoding.UTF8.GetBytes(host), expected);
#pragma warning restore CA5350
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>匹配主机模式，支持 <c>*</c> 与 <c>?</c> 通配（<c>!</c> 取反由调用方处理）。</summary>
    private static bool MatchesPattern(string pattern, string host)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal)
            && !pattern.Contains('?', StringComparison.Ordinal))
        {
            return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
        }

        return HostPatterns.Matches(pattern, host);
    }

    /// <summary>拼一条可以直接追加进 <c>known_hosts</c> 的行。</summary>
    /// <param name="host">主机。</param>
    /// <param name="port">端口。</param>
    /// <param name="key">公钥。是证书时记下的是<b>证书里那把钥</b>（证书每次重签 blob 都会变）。</param>
    /// <param name="hashHostName">要不要把主机名散列掉（对应 <c>HashKnownHosts yes</c>）。</param>
    /// <exception cref="ArgumentException">主机名里有 <c>known_hosts</c> 里另有含义的字符（见 <see cref="IsRecordableHost"/>）。</exception>
    public static string FormatEntry(string host, int port, SshPublicKey key, bool hashHostName = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(key);

        if (!IsRecordableHost(host))
        {
            throw new ArgumentException(
                $"主机名 {PeerText.Sanitize(host)} 里有 known_hosts 里另有含义的字符（, * ? ! [ ] # 空白、控制字符，或开头的 @ |），" +
                "写进去这一行的意思就变了。", nameof(host));
        }

        string name = FormatHostPattern(host, port);

        if (hashHostName)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(20);

#pragma warning disable CA5350 // 同上：格式由 OpenSSH 规定
            byte[] hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(name));
#pragma warning restore CA5350

            name = string.Create(
                CultureInfo.InvariantCulture,
                $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}");
        }

        SshPublicKey recorded = key.PlainKey;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name} {recorded.KeyType} {Convert.ToBase64String(recorded.Blob.Span)}");
    }

    /// <summary>这个主机名能不能原样写进 <c>known_hosts</c>。</summary>
    /// <remarks>
    /// <para>
    /// 主机名那一栏本身是一张模式表：<c>,</c> 分隔多个模式，<c>*</c> <c>?</c> 是通配，<c>!</c> 取反，<c>[ ]</c> 包着非 22 端口，
    /// 空白结束这一栏，开头的 <c>@</c> 是标记、<c>|</c> 是散列行，<c>#</c> 开头是注释。主机名带着它们写进去，
    /// 这一行的意思就变了 —— <c>x,*</c> 让这把钥对<b>所有主机</b>生效。主机名可能来自外部启动链接、
    /// <c>ssh_config</c> 的 <c>HostName</c>，不一定是使用者亲手敲的。
    /// </para>
    /// <para>合法的主机名、IP（含 IPv6 与区域标识 <c>%</c>）与国际化域名都用不到这些字符。散列行也一样拒绝：这样的名字本来就不是一台主机。</para>
    /// </remarks>
    public static bool IsRecordableHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (host.Length == 0 || host[0] is '@' or '|')
        {
            return false;
        }

        foreach (char c in host)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is ',' or '*' or '?' or '!' or '[' or ']' or '#')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>把一台主机追加进 <c>known_hosts</c>。</summary>
    /// <exception cref="ArgumentException">主机名不能原样写进 <c>known_hosts</c>（见 <see cref="IsRecordableHost"/>）。</exception>
    /// <exception cref="SshConnectException">
    /// 写不进去（没有权限、被占用、磁盘满）：<see cref="SshFailureReason.HostKeyStoreFailed"/>。
    /// </exception>
    /// <remarks>
    /// <b>只追加，不改写已有的行。</b>改写意味着要把整个文件读进来再写回去，
    /// 而那会在并发写时丢掉别的进程刚加的记录 —— OpenSSH 自己也是追加。
    /// 要删记录的那两个场合另走 <see cref="RemoveHostKeysAsync(string, int, IReadOnlyCollection{string}?, string?, CancellationToken)"/>。
    /// </remarks>
    public static async ValueTask AppendAsync(
        string host,
        int port,
        SshPublicKey key,
        string? path = null,
        bool hashHostName = false,
        CancellationToken cancellationToken = default)
    {
        string actual = path ?? DefaultPath;
        string line = FormatEntry(host, port, key, hashHostName) + Environment.NewLine;

        try
        {
            string? directory = Path.GetDirectoryName(actual);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 文件最后一行没有换行（手工编辑过的文件很常见）的话，直接追加会把新记录接在那一行后面，
            // 两条一起坏掉 —— 那台主机从此每次都按「没见过」处理。先补一个换行。
            if (!await EndsWithNewlineAsync(actual, cancellationToken).ConfigureAwait(false))
            {
                line = Environment.NewLine + line;
            }

            await File.AppendAllTextAsync(actual, line, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StoreFailed($"写不进 {actual}", ex);
        }
    }

    /// <summary>文件不存在、为空，或者最后一个字节是 <c>\n</c>。</summary>
    private static async ValueTask<bool> EndsWithNewlineAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: true);
        if (stream.Length == 0)
        {
            return true;
        }

        stream.Seek(-1, SeekOrigin.End);
        byte[] last = new byte[1];
        return await stream.ReadAsync(last, cancellationToken).ConfigureAwait(false) == 1 && last[0] == (byte)'\n';
    }

    /// <summary>改写时文件被别的进程改了，最多重来几次。</summary>
    private const int RewriteAttempts = 5;

    /// <summary>从 <c>known_hosts</c> 里删掉这台主机记着的普通主机密钥。</summary>
    /// <param name="host">主机。</param>
    /// <param name="port">端口。</param>
    /// <param name="fingerprints">
    /// 只删这几把（SHA-256 指纹；带不带 <c>=</c> 填充、有没有 <c>SHA256:</c> 前缀都认）；<see langword="null"/> 删这台主机的全部普通钥。
    /// </param>
    /// <param name="path"><c>known_hosts</c> 路径；<see langword="null"/> 为默认路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删掉的钥的指纹（<see cref="SshPublicKey.Sha256Fingerprint"/> 的样子，不重复）。没有可删的为空，文件一个字节都不动。</returns>
    /// <exception cref="SshConnectException">
    /// 读不了、写不进，或者一直在被别的进程改动：<see cref="SshFailureReason.HostKeyStoreFailed"/>。
    /// </exception>
    /// <remarks>
    /// <para>
    /// 〔Q4，velashell-docs/zh/ssh/spec/03 §5.4〕平时照旧只追加（<see cref="AppendAsync"/>），只有两个场合改写：
    /// 「密钥变了」确认是重装之后一键删掉旧的记录 —— 报错文案让人手工去做的那件事（<see cref="KnownHostsPolicy.RemoveHostKeysAsync"/>）；
    /// 主机密钥轮换时删掉服务端不再出示的旧钥（<see cref="IHostKeyRotationPolicy.ForgetHostKeysAsync"/>）。
    /// </para>
    /// <para>
    /// <b>只动专属于这台主机的记录。</b>散列行（一行只代表一个名字）对上了整行删；明文行把这台主机的名字拿掉 ——
    /// 一行记着几个名字（<c>host,10.0.0.5</c>）时别的名字照旧受信，名字拿光了才整行删。
    /// <c>@revoked</c>、<c>@cert-authority</c>、带通配或取反的行（<c>*.corp</c>）不动：它们管的不止这一台。
    /// 别的行（注释、空行、写坏的行）连同各自的换行符原样保留。
    /// </para>
    /// <para>
    /// <b>临时文件 + 原子替换 + 冲突重试。</b>新内容先写进同一目录下的临时文件；替换之前再读一次原文件，
    /// 与改写所依据的内容不一样（多半是别的进程刚追加了一条）就按新内容重来，最多 5 次；一样才原子地换上去（Unix 上权限照旧）。
    /// 中途失败原文件不受影响。比较与替换之间仍有一个极短的窗口，那时追加进来的一条会丢 —— 所以只在上面两个场合改写。
    /// </para>
    /// </remarks>
    public static ValueTask<IReadOnlyList<string>> RemoveHostKeysAsync(
        string host,
        int port,
        IReadOnlyCollection<string>? fingerprints = null,
        string? path = null,
        CancellationToken cancellationToken = default) =>
        RemoveHostKeysAsync(host, port, fingerprints, path, beforeReplace: null, cancellationToken);

    /// <param name="host">主机。</param>
    /// <param name="port">端口。</param>
    /// <param name="fingerprints">只删这几把；<see langword="null"/> 全删。</param>
    /// <param name="path">路径。</param>
    /// <param name="beforeReplace">（测试用）每一轮比较之前调一次，参数是第几轮。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    internal static async ValueTask<IReadOnlyList<string>> RemoveHostKeysAsync(
        string host,
        int port,
        IReadOnlyCollection<string>? fingerprints,
        string? path,
        Func<int, ValueTask>? beforeReplace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        string actual = path ?? DefaultPath;
        HashSet<string>? wanted = fingerprints is null
            ? null
            : new(fingerprints.Select(SshPublicKey.NormalizeFingerprint), StringComparer.Ordinal);
        if (wanted is { Count: 0 })
        {
            return [];
        }

        for (int attempt = 1; ; attempt++)
        {
            byte[]? original = await ReadBytesAsync(actual, cancellationToken).ConfigureAwait(false);
            if (original is null)
            {
                return [];   // 文件不存在：没什么可删的
            }

            List<string> removed = [];
            byte[] rewritten = RemoveHostLines(original, host, port, wanted, removed);
            if (removed.Count == 0)
            {
                return [];
            }

            if (beforeReplace is not null)
            {
                await beforeReplace(attempt).ConfigureAwait(false);
            }
            if (await TryReplaceAsync(actual, original, rewritten, cancellationToken).ConfigureAwait(false))
            {
                return removed;
            }
            if (attempt == RewriteAttempts)
            {
                throw new SshConnectException(SshFailureReason.HostKeyStoreFailed, SshPhase.KeyExchange,
                    $"改写不了 {actual}：试了 {RewriteAttempts} 次，它一直在被别的进程改动。");
            }
            await Task.Delay(20 * attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>按 <see cref="RemoveHostKeysAsync(string, int, IReadOnlyCollection{string}?, string?, CancellationToken)"/> 的规则改写；<paramref name="removed"/> 收删掉的钥的指纹。</summary>
    private static byte[] RemoveHostLines(byte[] content, string host, int port, HashSet<string>? wanted, List<string> removed)
    {
        string plain = FormatHostPattern(host, port);
        using MemoryStream output = new(content.Length);
        int start = 0;
        for (int lineNumber = 1; start < content.Length; lineNumber++)
        {
            int newline = Array.IndexOf(content, (byte)'\n', start);
            int end = newline < 0 ? content.Length : newline + 1;   // 带着这一行自己的换行
            ReadOnlySpan<byte> raw = content.AsSpan(start, end - start);
            start = end;

            string text = Encoding.UTF8.GetString(raw);
            KnownHostEntry? entry = ParseLine(text.Trim('\uFEFF', '\r', '\n', ' ', '\t'), lineNumber);
            if (entry is null || entry.IsRevoked || entry.IsCertificateAuthority
                || (!entry.IsHashed && entry.Patterns.Any(static p => p.StartsWith('!') || p.Contains('*') || p.Contains('?')))
                || !MatchesHost(entry, host, port, plain))
            {
                output.Write(raw);
                continue;
            }

            string fingerprint = FingerprintOf(entry.KeyBlob.Span);
            if (wanted is not null && !wanted.Contains(fingerprint))
            {
                output.Write(raw);
                continue;
            }

            if (!removed.Contains(fingerprint))
            {
                removed.Add(fingerprint);
            }

            string[] others = entry.IsHashed
                ? []
                : [.. entry.Patterns.Where(p => !string.Equals(p, plain, StringComparison.OrdinalIgnoreCase))];
            if (others.Length > 0)
            {
                // 主机那一栏换成剩下的名字，其余（类型、公钥、注释、换行）原样。
                int lead = text.Length - text.TrimStart('\uFEFF', ' ', '\t').Length;
                int fieldEnd = text.IndexOfAny([' ', '\t'], lead);
                output.Write(Encoding.UTF8.GetBytes(text[..lead] + string.Join(',', others) + text[fieldEnd..]));
            }
        }
        return output.ToArray();
    }

    /// <summary>记录里那把钥的指纹：本库认得的按 <see cref="SshPublicKey.Sha256Fingerprint"/>（证书算证书里那把钥），认不得的按原样的 blob。</summary>
    private static string FingerprintOf(ReadOnlySpan<byte> blob)
    {
        try
        {
            return SshPublicKey.Decode(blob.ToArray()).Sha256Fingerprint;
        }
        catch (SshPublicKeyException)
        {
            return SshPublicKey.Sha256FingerprintOf(blob);
        }
    }

    /// <summary>写临时文件，确认原文件还是 <paramref name="original"/> 之后原子地换上去；原文件已经变了（或者正被别的进程开着）返回 <see langword="false"/>。</summary>
    private static async ValueTask<bool> TryReplaceAsync(string path, byte[] original, byte[] rewritten, CancellationToken cancellationToken)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";   // 同一个目录：替换是同一个卷上的改名
        try
        {
            await File.WriteAllBytesAsync(temporary, rewritten, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            }

            // 改写所依据的已经不是文件现在的内容（多半是别的进程刚追加了一条）：直接换上去就把那一条丢了。
            byte[]? current = await ReadBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (current is null || !current.AsSpan().SequenceEqual(original))
            {
                return false;
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            return false;   // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION：别的进程正开着它，等一下重来
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StoreFailed($"改写不了 {path}", ex);
        }
        finally
        {
            try
            {
                File.Delete(temporary);   // 换上去了就已经不在
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉的临时文件不影响结果。
            }
        }
    }

    /// <summary>读整个文件（不挡别的进程同时读写）；不存在为 <see langword="null"/>。</summary>
    private static async ValueTask<byte[]?> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: true);
            byte[] content = new byte[stream.Length];
            await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
            return content;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StoreFailed($"读不了 {path}", ex);
        }
    }
}
