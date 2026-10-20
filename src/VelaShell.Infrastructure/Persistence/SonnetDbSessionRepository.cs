using System.Text.Json;
using SonnetDB.Documents;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Local

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 基于 SonnetDB 文档集合的会话仓储:分组存 <c>session_groups</c>、
/// 会话配置存 <c>session_profiles</c>(文档 Id 均为 Guid 字符串)。
/// 密码与私钥口令通过 <see cref="ISecretProtector" /> 以 AES-256 加密后落盘。
/// 首次运行时自动导入既有 sessions.json。
/// </summary>
public sealed class SonnetDbSessionRepository(SonnetDbEngine engine, ISecretProtector protector, string? legacySessionsFile = null) : ISessionRepository
{
    private readonly SonnetDbEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private readonly ISecretProtector _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    private bool _migrationChecked;

    /// <summary>返回全部服务器分组,按 <see cref="ServerGroup.SortOrder" /> 升序排列。</summary>
    public async Task<List<ServerGroup>> GetAllGroupsAsync()
    {
        await EnsureMigratedAsync().ConfigureAwait(false);
        List<ServerGroup?> groups = await _engine.WithCollectionAsync(SonnetDbEngine.GroupsCollection, store =>
                                        store.Scan().Select(row => SonnetDbJson.Deserialize<ServerGroup>(row.Json)).ToList()).ConfigureAwait(false);
        return [.. groups.OfType<ServerGroup>().OrderBy(static g => g.SortOrder)];
    }

    /// <summary>返回全部会话配置,密码与私钥口令已解密为明文。</summary>
    public async Task<List<SessionProfile>> GetAllSessionsAsync()
    {
        await EnsureMigratedAsync().ConfigureAwait(false);
        List<SessionProfile?> sessions = await _engine.WithCollectionAsync(SonnetDbEngine.ProfilesCollection, store =>
                                             store.Scan().Select(row => SonnetDbJson.Deserialize<SessionProfile>(row.Json)).ToList()).ConfigureAwait(false);
        return [.. sessions.OfType<SessionProfile>().Select(Unprotect)];
    }

    /// <summary>按 Id 获取单个会话配置(密码已解密);不存在时返回 <c>null</c>。</summary>
    public async Task<SessionProfile?> GetSessionAsync(Guid id)
    {
        await EnsureMigratedAsync().ConfigureAwait(false);
        SessionProfile? profile = await _engine.WithCollectionAsync(SonnetDbEngine.ProfilesCollection, store =>
        {
            DocumentRow? row = store.Get(id.ToString("D"));
            return row is null ? null : SonnetDbJson.Deserialize<SessionProfile>(row.Json);
        }).ConfigureAwait(false);
        return profile is null ? null : Unprotect(profile);
    }

    /// <summary>加密敏感字段后插入或更新会话配置(以 Id 为主键);更新时目录收藏保留库里现存的那份。</summary>
    public Task SaveSessionAsync(SessionProfile session) => UpsertAsync(session, keepStoredBookmarks: true);

    /// <summary>加密敏感字段后插入或整条覆盖会话配置(以 Id 为主键),目录收藏也取传入的。</summary>
    public Task ReplaceSessionAsync(SessionProfile session) => UpsertAsync(session, keepStoredBookmarks: false);

    /// <summary>在库里最新的那份收藏上执行修改并写回;读、改、写在同一次集合锁内完成。</summary>
    public async Task<List<RemoteDirectoryBookmark>?> UpdateDirectoryBookmarksAsync(
        Guid id, Func<List<RemoteDirectoryBookmark>, bool> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        await EnsureMigratedAsync().ConfigureAwait(false);
        return await _engine.WithCollectionAsync<List<RemoteDirectoryBookmark>?>(SonnetDbEngine.ProfilesCollection, store =>
        {
            string key = id.ToString("D");
            if (store.Get(key) is not { } row || SonnetDbJson.Deserialize<SessionProfile>(row.Json) is not { } stored)
            {
                return null;
            }
            // 文档原样读出、原样写回(机密字段还是密文),只换收藏这一项 —— 不经 Unprotect / Protect 走一圈。
            stored.DirectoryBookmarks ??= [];
            List<RemoteDirectoryBookmark> bookmarks = [.. stored.DirectoryBookmarks.Select(static b => b.Clone())];
            if (update(bookmarks))
            {
                stored.DirectoryBookmarks = bookmarks;
                store.Upsert(key, SonnetDbJson.Serialize(stored));
            }
            // 不写回时交回库里原来那份:回调改到一半的列表不算数。
            return [.. stored.DirectoryBookmarks.Select(static b => b.Clone())];
        }).ConfigureAwait(false);
    }

    private async Task UpsertAsync(SessionProfile session, bool keepStoredBookmarks)
    {
        ArgumentNullException.ThrowIfNull(session);
        await EnsureMigratedAsync().ConfigureAwait(false);
        SessionProfile copy = Protect(session);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ProfilesCollection, store =>
        {
            string key = session.Id.ToString("D");
            // 读现存收藏与写回在同一次集合锁内:中间插进来的收藏修改不会被这次写回盖掉。
            if (keepStoredBookmarks && store.Get(key) is { } row
                && SonnetDbJson.Deserialize<SessionProfile>(row.Json) is { } stored)
            {
                copy.DirectoryBookmarks = stored.DirectoryBookmarks ?? [];
            }
            store.Upsert(key, SonnetDbJson.Serialize(copy));
            return null;
        }).ConfigureAwait(false);
    }

    /// <summary>删除指定会话配置,并从各分组的会话列表中移除对它的引用。</summary>
    public async Task DeleteSessionAsync(Guid id)
    {
        await EnsureMigratedAsync().ConfigureAwait(false);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ProfilesCollection, store =>
        {
            store.Delete(id.ToString("D"));
            return null;
        }).ConfigureAwait(false);

        // 从各分组的会话列表中移除引用。
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.GroupsCollection, store =>
        {
            foreach (DocumentRow row in store.Scan())
            {
                ServerGroup? group = SonnetDbJson.Deserialize<ServerGroup>(row.Json);
                if (group is not null && group.Sessions.Remove(id))
                {
                    store.Upsert(row.Id, SonnetDbJson.Serialize(group));
                }
            }
            return null;
        }).ConfigureAwait(false);
    }

    /// <summary>插入或更新服务器分组(以 Id 为主键)。</summary>
    public async Task SaveGroupAsync(ServerGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        await EnsureMigratedAsync().ConfigureAwait(false);
        string json = SonnetDbJson.Serialize(group);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.GroupsCollection, store =>
        {
            store.Upsert(group.Id.ToString("D"), json);
            return null;
        }).ConfigureAwait(false);
    }

    /// <summary>删除指定分组,并将归属该分组的会话重置为未分组状态。</summary>
    public async Task DeleteGroupAsync(Guid id)
    {
        await EnsureMigratedAsync().ConfigureAwait(false);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.GroupsCollection, store =>
        {
            store.Delete(id.ToString("D"));
            return null;
        }).ConfigureAwait(false);

        // 归属该分组的会话回到未分组状态。
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ProfilesCollection, store =>
        {
            foreach (DocumentRow row in store.Scan())
            {
                SessionProfile? profile = SonnetDbJson.Deserialize<SessionProfile>(row.Json);
                if (profile?.GroupId == id)
                {
                    profile.GroupId = null;
                    store.Upsert(row.Id, SonnetDbJson.Serialize(profile));
                }
            }
            return null;
        }).ConfigureAwait(false);
    }

    /// <summary>返回加密副本 —— 不得修改调用方持有的实例(其明文密码仍用于活动连接)。</summary>
    private SessionProfile Protect(SessionProfile profile)
    {
        // Clone 已经把全部字段深拷贝了(它是唯一的拷贝口径,加字段只改那一处);
        // 这里只覆盖需要加密的三项。原先这里是逐字段手写的,每加一个字段就得记得回来补一行。
        SessionProfile copy = profile.Clone();
        CredentialMaterial.ClearTailscaleCredentials(copy);
        if (copy.CredentialSource is not null)
        {
            // 引用了凭据的配置,认证材料一律不落盘(#550)。放在仓储而不是各个调用方:
            // 保存点有七八处(拖动分组、复制配置、证书信任、同步拉取、导入…),逐个防守迟早漏一个。
            // 这条不变量还撑着「内存里有材料 = 本次手输过」那条判断(CredentialMaterial.HasInline)——
            // 一旦有材料混进库里,这条配置就会永远用那份旧材料,引用形同虚设。
            CredentialMaterial.ClearInline(copy);
        }
        copy.Password = _protector.Protect(copy.Password);
        copy.PrivateKeyPassphrase = _protector.Protect(copy.PrivateKeyPassphrase);
        copy.PluginSecrets = MapSecrets(profile.PluginSecrets, _protector.Protect);
        return copy;
    }

    /// <summary>
    /// 插件协议机密字典的逐值映射(加密/解密各用一次)。
    /// <para>
    /// 之所以能"整本加密"而不必逐字段判断:机密与非机密在
    /// <see cref="SessionProfile.PluginSecrets" /> / <see cref="SessionProfile.PluginSettings" />
    /// 两个字典里就已经分开了。仓储层不认识任何协议的字段声明(那在插件里),
    /// 靠查表决定加不加密迟早会漏掉一个。
    /// </para>
    /// </summary>
    private static Dictionary<string, string>? MapSecrets(
        Dictionary<string, string>? source,
        Func<string?, string?> transform)
    {
        if (source is null)
        {
            return null;
        }
        var result = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in source)
        {
            result[entry.Key] = transform(entry.Value) ?? string.Empty;
        }
        return result;
    }

    private SessionProfile Unprotect(SessionProfile profile)
    {
        CredentialMaterial.ClearTailscaleCredentials(profile);
        profile.Password = _protector.Unprotect(profile.Password);
        profile.PrivateKeyPassphrase = _protector.Unprotect(profile.PrivateKeyPassphrase);
        profile.PluginSecrets = MapSecrets(profile.PluginSecrets, _protector.Unprotect);
        return profile;
    }

    private async Task EnsureMigratedAsync()
    {
        if (_migrationChecked)
        {
            return;
        }
        await _migrationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_migrationChecked)
            {
                return;
            }
            if (!string.IsNullOrEmpty(legacySessionsFile) && File.Exists(legacySessionsFile))
            {
                bool isEmpty = await _engine.WithCollectionAsync(SonnetDbEngine.ProfilesCollection,
                                   store => store.Count() == 0).ConfigureAwait(false) &&
                               await _engine.WithCollectionAsync(SonnetDbEngine.GroupsCollection,
                                   store => store.Count() == 0).ConfigureAwait(false);
                if (isEmpty)
                {
                    await ImportLegacyAsync(legacySessionsFile).ConfigureAwait(false);
                }
            }
            _migrationChecked = true;
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    private async Task ImportLegacyAsync(string path)
    {
        LegacySessionData? data;
        try
        {
            data = SonnetDbJson.Deserialize<LegacySessionData>(await File.ReadAllTextAsync(path).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return; // 旧文件损坏时跳过导入,不阻塞启动。
        }
        if (data is null)
        {
            return;
        }
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.GroupsCollection, store =>
        {
            foreach (ServerGroup group in data.Groups)
            {
                store.Upsert(group.Id.ToString("D"), SonnetDbJson.Serialize(group));
            }
            return null;
        }).ConfigureAwait(false);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ProfilesCollection, store =>
        {
            foreach (SessionProfile session in data.Sessions)
            {
                store.Upsert(session.Id.ToString("D"), SonnetDbJson.Serialize(Protect(session)));
            }
            return null;
        }).ConfigureAwait(false);
        TryRename(path);
    }

    private static void TryRename(string path)
    {
        try
        {
            File.Move(path, path + ".migrated.bak", true);
        }
        catch (IOException)
        {
            // 保留原文件也无碍:集合已非空,后续不会重复导入。
        }
    }

    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class LegacySessionData
    {
        // JSON 反序列化需要 setter:get-only 集合属性 System.Text.Json 默认不填充。
        public List<ServerGroup> Groups { get; set; } = [];

        public List<SessionProfile> Sessions { get; set; } = [];
    }
}
