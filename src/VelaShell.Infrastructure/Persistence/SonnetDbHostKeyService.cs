using System.Text.Json;
using SonnetDB.Documents;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Local

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 基于 SonnetDB 文档集合 <c>known_hosts</c> 的主机密钥信任存储:一台主机(host:port)的每种密钥类型各记一条,
/// 文档 Id 为 <c>host:port#类型</c>。首次运行导入既有 known_hosts.json。
/// </summary>
/// <remarks>
/// 〔ssh_plan API-H4〕曾经每个 host:port 只记一把钥(文档 Id 为 <c>host:port</c>):服务端多一把别的类型的钥、
/// 或者这条连接改了主机密钥算法,谈成的类型一变就报「指纹已变更」;用户接受之后又把原来那一把覆盖掉,换回去时再报一次。
/// 那种单条的旧记录照常认,下一次信任这台主机时搬成按类型的记录。
/// </remarks>
public sealed class SonnetDbHostKeyService(SonnetDbEngine engine, string? legacyFile = null) : IHostKeyService
{
    private readonly SonnetDbEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private bool _migrationChecked;

    /// <summary>
    /// 校验主机密钥指纹:这台主机一条记录都没有返回 Unknown;任一记录的指纹与它相同返回 Trusted;否则返回 Changed。
    /// </summary>
    /// <remarks>
    /// 比的是指纹而不是类型:SHA-256 指纹覆盖整个公钥 blob(类型名就在里面),指纹相同就是同一把钥 ——
    /// 换库之前存下的记录类型是 <c>PublicKey</c> 这样的占位词,也照样认得出来。
    /// 这台主机记着钥、却没有这一种时同样报 Changed:由用户在变更弹窗里决定,接受了就按类型添一条,不覆盖别的。
    /// </remarks>
    public async Task<HostKeyVerification> VerifyHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KnownHost> known = await FindKnownHostKeysAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (known.Count == 0)
        {
            return HostKeyVerification.Unknown;
        }
        return known.Any(entry => SameFingerprint(entry.Fingerprint, fingerprint))
                   ? HostKeyVerification.Trusted
                   : HostKeyVerification.Changed;
    }

    /// <summary>取指定 host:port 最近见过的那一条记录;没有记录返回 null。</summary>
    public async Task<KnownHost?> FindKnownHostAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KnownHost> known = await FindKnownHostKeysAsync(host, port, cancellationToken).ConfigureAwait(false);
        return known.Count > 0 ? known[0] : null;
    }

    /// <summary>取指定 host:port 记着的全部密钥(每种类型一条),最近见过的在前。</summary>
    public async Task<IReadOnlyList<KnownHost>> FindKnownHostKeysAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        return await _engine.WithCollectionAsync<IReadOnlyList<KnownHost>>(SonnetDbEngine.KnownHostsCollection, store =>
            [.. RowsOf(store, host, port)
                .Select(row => SonnetDbJson.Deserialize<KnownHost>(row.Json))
                .OfType<KnownHost>()
                .OrderByDescending(entry => entry.LastSeenAt)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>信任指定主机的这把钥:按类型新增或替换那一条(别的类型的记录不动),记下最近可见时间。</summary>
    /// <remarks>
    /// 带了公钥就记下(<see cref="KnownHost.HostKey" />,导出 OpenSSH 的 <c>known_hosts</c> 要它);没带时同一把钥原来记着的照旧,
    /// 换了一把钥就清掉 —— 留着的话导出去的是上一把,与指纹对不上。
    /// </remarks>
    public async Task TrustHostKeyAsync(string host, int port, string keyType, string fingerprint, string? publicKey = null, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.KnownHostsCollection, store =>
        {
            KnownHost? sameType = Read(store, DocId(host, port, keyType));

            // 单条的旧记录(host:port)搬成按类型的记录:同一种类型、或者就是这把钥的,由这一次的新记录接替(沿用首次见到的时间);
            // 别的类型的原样搬过去 —— 那把钥照样受信。
            if (Read(store, LegacyDocId(host, port)) is { } legacy)
            {
                bool superseded = string.Equals(legacy.KeyType, keyType, StringComparison.Ordinal)
                                  || SameFingerprint(legacy.Fingerprint, fingerprint);
                if (superseded)
                {
                    sameType ??= legacy;
                }
                else if (Read(store, DocId(host, port, legacy.KeyType)) is null)
                {
                    store.Upsert(DocId(host, port, legacy.KeyType), SonnetDbJson.Serialize(legacy));
                }
                store.Delete(LegacyDocId(host, port));
            }

            KnownHost entry = sameType ?? new KnownHost { Host = host, Port = port, FirstSeenAt = DateTime.UtcNow };
            if (!string.IsNullOrEmpty(publicKey))
            {
                entry.HostKey = publicKey;
            }
            else if (!SameFingerprint(entry.Fingerprint, fingerprint))
            {
                entry.HostKey = string.Empty;
            }
            entry.KeyType = keyType;
            entry.Fingerprint = fingerprint;
            entry.LastSeenAt = DateTime.UtcNow;
            store.Upsert(DocId(host, port, keyType), SonnetDbJson.Serialize(entry));
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>返回全部已知主机密钥记录(一台主机的每种类型各一条)。</summary>
    public async Task<List<KnownHost>> GetKnownHostsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        List<KnownHost?> hosts = await _engine.WithCollectionAsync(SonnetDbEngine.KnownHostsCollection, store =>
                                         store.Scan().Select(row => SonnetDbJson.Deserialize<KnownHost>(row.Json)).ToList(),
                                     cancellationToken).ConfigureAwait(false);
        return [.. hosts.Where(h => h is not null).Cast<KnownHost>()];
    }

    /// <summary>移除指定 host:port 的已知主机记录:给了类型只移除那一种,否则全部。</summary>
    public async Task RemoveKnownHostAsync(string host, int port, string? keyType = null, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.KnownHostsCollection, store =>
        {
            foreach (DocumentRow row in RowsOf(store, host, port).ToList())
            {
                if (keyType is null
                    || string.Equals(SonnetDbJson.Deserialize<KnownHost>(row.Json)?.KeyType, keyType, StringComparison.Ordinal))
                {
                    store.Delete(row.Id);
                }
            }
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>全部受信主机 CA 与吊销记录(集合 <c>known_host_markers</c>),先加的在前。</summary>
    /// <remarks>读坏了的文档跳过;集合本身读不出来照实抛出(见接口说明,裁决要 fail-closed)。</remarks>
    public async Task<IReadOnlyList<KnownHostMarker>> GetMarkersAsync(CancellationToken cancellationToken = default) =>
        await _engine.WithCollectionAsync<IReadOnlyList<KnownHostMarker>>(SonnetDbEngine.KnownHostMarkersCollection, store =>
            [.. store.Scan()
                .Select(row => SonnetDbJson.Deserialize<KnownHostMarker>(row.Json))
                .OfType<KnownHostMarker>()
                .Where(marker => marker.HostPatterns.Length > 0 && marker.PublicKey.Length > 0)
                .OrderBy(marker => marker.AddedAt)],
            cancellationToken).ConfigureAwait(false);

    /// <summary>加一条;种类、模式与公钥都相同的已经有了就不加(重复导入同一份 known_hosts 不会越积越多)。</summary>
    public async Task<bool> AddMarkerAsync(KnownHostMarker marker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marker);
        return await _engine.WithCollectionAsync(SonnetDbEngine.KnownHostMarkersCollection, store =>
        {
            bool duplicate = store.Scan()
                .Select(row => SonnetDbJson.Deserialize<KnownHostMarker>(row.Json))
                .OfType<KnownHostMarker>()
                .Any(existing => existing.Kind == marker.Kind
                                 && string.Equals(existing.HostPatterns, marker.HostPatterns, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(existing.PublicKey, marker.PublicKey, StringComparison.Ordinal));
            if (duplicate)
            {
                return false;
            }
            store.Upsert(marker.Id, SonnetDbJson.Serialize(marker));
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 Id 删一条受信主机 CA 或吊销记录。</summary>
    public async Task RemoveMarkerAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.KnownHostMarkersCollection, store =>
        {
            store.Delete(id);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 两个 SHA-256 指纹是不是同一把钥。
    /// </summary>
    /// <remarks>
    /// 换底层库之前存下的记录是**裸 base64**(上一版库的 <c>SHA256FingerPrint</c> 不带前缀),
    /// 现在拿到的是 OpenSSH 风格的 <c>SHA256:</c> 前缀形式。逐字节比的话,换库之后每一台
    /// 已保存的主机都会被判成「指纹已变更」—— 开了「变更即阻断」的用户会一台也连不上。
    /// 所以比之前两边都去掉前缀与 base64 填充;旧记录不迁移,下次信任时自然改写成新形式。
    /// </remarks>
    internal static bool SameFingerprint(string? stored, string? current) =>
        stored is not null && current is not null
        && string.Equals(NormalizeFingerprint(stored), NormalizeFingerprint(current), StringComparison.Ordinal);

    private static string NormalizeFingerprint(string fingerprint)
    {
        string value = fingerprint.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["SHA256:".Length..];
        }
        return value.TrimEnd('=');
    }

    /// <summary>按类型的记录:<c>host:port#类型</c>。</summary>
    private static string DocId(string host, int port, string keyType) => $"{host}:{port}#{keyType}";

    /// <summary>单条的旧记录(按类型分开存之前,以及从 known_hosts.json 导入的):<c>host:port</c>。</summary>
    private static string LegacyDocId(string host, int port) => $"{host}:{port}";

    /// <summary>这台主机的全部记录:旧的单条记录与按类型的记录。</summary>
    private static IEnumerable<DocumentRow> RowsOf(DocumentCollectionStore store, string host, int port)
    {
        string legacy = LegacyDocId(host, port);
        string typed = legacy + "#";
        return store.Scan().Where(row =>
            string.Equals(row.Id, legacy, StringComparison.Ordinal) || row.Id.StartsWith(typed, StringComparison.Ordinal));
    }

    private static KnownHost? Read(DocumentCollectionStore store, string id) =>
        store.Get(id) is { } row ? SonnetDbJson.Deserialize<KnownHost>(row.Json) : null;

    private async Task EnsureMigratedAsync(CancellationToken cancellationToken)
    {
        if (_migrationChecked)
        {
            return;
        }
        await _migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_migrationChecked)
            {
                return;
            }
            if (!string.IsNullOrEmpty(legacyFile) && File.Exists(legacyFile))
            {
                bool isEmpty = await _engine.WithCollectionAsync(SonnetDbEngine.KnownHostsCollection,
                                   store => store.Count() == 0, cancellationToken).ConfigureAwait(false);
                if (isEmpty)
                {
                    await ImportLegacyAsync(legacyFile, cancellationToken).ConfigureAwait(false);
                }
            }
            _migrationChecked = true;
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    private async Task ImportLegacyAsync(string path, CancellationToken cancellationToken)
    {
        LegacyKnownHostData? data;
        try
        {
            data = SonnetDbJson.Deserialize<LegacyKnownHostData>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return;
        }
        if (data is null)
        {
            return;
        }
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.KnownHostsCollection, store =>
        {
            foreach (KnownHost hostEntry in data.Hosts)
            {
                store.Upsert(LegacyDocId(hostEntry.Host, hostEntry.Port), SonnetDbJson.Serialize(hostEntry));
            }
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class LegacyKnownHostData
    {
        // JSON 反序列化需要 setter:get-only 集合属性 System.Text.Json 默认不填充。
        public List<KnownHost> Hosts { get; set; } = [];
    }
}
