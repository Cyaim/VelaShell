using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SonnetDB.Documents;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 基于 SonnetDB 文档集合 <c>app_config</c> 的设置服务:
/// settings 与 state 各为一个固定 Id 的 JSON 文档。首次运行导入既有 settings.json / state.json。
/// </summary>
/// <remarks>
/// <para>
/// 设置里的代理密码(<see cref="ProxyOptions.Password" />)落盘前经 <see cref="ISecretProtector" /> 加密,
/// 与连接配置里的密码同一强度;读出来交给调用方的是明文,内存里的缓存与快照也是明文 —— 加密只管「数据目录里躺着什么」。
/// 存量的明文照常读得出来,下一次保存时换成密文(不为它单独写一次库)。
/// </para>
/// <para>
/// 设置导出(<c>SettingsTransfer.Export</c>)与没设端到端口令的云同步本来就不带这个字段,不受影响。
/// 没传保护器(测试)时原样落盘。
/// </para>
/// </remarks>
public sealed class SonnetDbSettingsService(
    SonnetDbEngine engine,
    IReadOnlyList<string>? legacyDirectories = null,
    ISecretProtector? protector = null) : ISettingsService
{
    private const string SettingsDocId = "settings";
    private const string StateDocId = "state";

    // 设置文档里代理密码的位置(SonnetDbJson 的 camelCase 名;与 SettingsCategories.SecretFields 里那一项是同一个字段)。
    private const string ProxyBlock = "proxy";
    private const string PasswordField = "password";

    private readonly SonnetDbEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly IReadOnlyList<string> _legacyDirectories = legacyDirectories ?? [];

    /// <summary>
    /// 这次运行里解不开的那份代理密码(库里的密文原样)与它所属的那台代理。交给调用方的是空串 ——
    /// 把密文当密码交给代理只会换来一次失败的认证,随加密的云同步带到别的设备还会顶掉那边的好密码。
    /// 保存时只要还是同一台代理、密码也没重填,就把密文原样写回:系统密钥链这次没打开、退出时顺手存一下窗口尺寸,
    /// 不能就此把密码抹掉,密钥链回来那次启动照常解开。
    /// </summary>
    private volatile UnreadableProxyPassword? _unreadableProxyPassword;

    /// <param name="Ciphertext">库里存着的密文。</param>
    /// <param name="Proxy">它所属的代理(只用类型、主机、端口、用户名)。</param>
    private sealed record UnreadableProxyPassword(string Ciphertext, ProxyOptions Proxy);

    /// <summary>
    /// settings 文档的 JSON 缓存:设置是读热点(每次连接/每个传输文件都读),
    /// 而引擎全局锁串行所有集合操作。缓存序列化文本、按次反序列化,调用方仍各拿独立
    /// 实例(可安全修改后再保存),读路径却不再进锁/碰盘。保存时同步刷新。
    /// </summary>
    private volatile string? _settingsJsonCache;

    /// <summary>
    /// 只读共享快照:与 <see cref="_settingsJsonCache" /> 同源,但省掉每次调用的反序列化。
    /// 只读调用方(传输调优、代理解析、连接工厂)走 <c>GetSnapshotAsync()</c> 拿它;
    /// 需要改写的调用方仍走 <see cref="GetSettingsAsync" /> 拿独立实例。
    /// 保存时整体替换为**新反序列化的**实例(不直接引用调用方传进来的对象,
    /// 否则调用方后续继续改那个对象会把共享快照一起改掉)。
    /// </summary>
    private volatile AppSettings? _snapshot;

    /// <inheritdoc />
    public AppSettings? CurrentSnapshot => _snapshot;

    /// <summary>设置保存成功后触发,携带刚持久化的设置快照,供订阅方刷新缓存或界面。</summary>
    public event Action<AppSettings>? SettingsSaved;

    /// <summary>读取应用设置:优先命中 JSON 缓存,未命中则从文档集合读取或首次导入并归一化。</summary>
    public async Task<AppSettings> GetSettingsAsync()
    {
        if (_settingsJsonCache is { } cached && SonnetDbJson.Deserialize<AppSettings>(cached) is { } fromCache)
        {
            fromCache.Normalize();
            return fromCache;
        }
        AppSettings settings = await GetOrImportAsync<AppSettings>(SettingsDocId, "settings.json", ToStoredSettingsJson).ConfigureAwait(false);
        settings.Normalize();
        // 在 Normalize 之后解:记下的「那台代理」要与调用方拿到、之后存回来的那份对得上(规整会迁移代理类型)。
        // 这里已在引擎锁之外、线程池上(InLockAsync 用 Task.Run),等密钥解析也不压 UI 线程。
        RevealProxyPassword(settings);
        string serialized = SonnetDbJson.Serialize(settings);
        _settingsJsonCache = serialized;
        _snapshot = DeserializeNormalized(serialized);
        return settings;
    }

    /// <summary>持久化应用设置,同步刷新 JSON 缓存与只读快照,并触发 <see cref="SettingsSaved"/>。</summary>
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string json = SonnetDbJson.Serialize(settings);
        await UpsertJsonAsync(SettingsDocId, ToStoredSettingsJson(settings)).ConfigureAwait(false);
        _settingsJsonCache = json;
        _snapshot = DeserializeNormalized(json);
        MirrorRenderMode(settings);
        StartupAppearance.Mirror(settings, new VelaShellStoragePaths().StartupAppearanceFile);
        SettingsSaved?.Invoke(settings);
    }

    /// <summary>从 JSON 反序列化出一份归一化后的独立实例;失败时返回 null(退回按需加载)。</summary>
    private static AppSettings? DeserializeNormalized(string json)
    {
        if (SonnetDbJson.Deserialize<AppSettings>(json) is not { } settings)
        {
            return null;
        }
        settings.Normalize();
        return settings;
    }

    /// <summary>
    /// 设置写成库里那份 JSON:代理密码换成密文,其余与 <see cref="SonnetDbJson.Serialize{T}" /> 一字不差。
    /// 改的是序列化出来的副本,调用方手里那份对象不动(它之后还要拿明文去连代理)。
    /// </summary>
    private string ToStoredSettingsJson(AppSettings settings)
    {
        string json = SonnetDbJson.Serialize(settings);
        if (protector is null)
        {
            return json;
        }
        string stored = StoredProxyPassword(settings.Proxy);
        if (stored.Length == 0)
        {
            return json;
        }
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        if (root[ProxyBlock] is JsonObject proxy)
        {
            proxy[PasswordField] = stored;
        }
        return root.ToJsonString();
    }

    /// <summary>代理密码在库里该存成什么:填着的加密;空着的,若是这次没解开的那一份(同一台代理)就写回原密文,否则就是空。</summary>
    private string StoredProxyPassword(ProxyOptions proxy)
    {
        if (proxy.Password.Length > 0)
        {
            // 重填了密码:解不开的那份作废,存这一份(用当前的本机密钥,这台设备解得开)。
            _unreadableProxyPassword = null;
            return protector!.Protect(proxy.Password) ?? string.Empty;
        }
        if (_unreadableProxyPassword is { } kept)
        {
            if (SettingsTransfer.SameProxy(kept.Proxy, proxy))
            {
                return kept.Ciphertext;
            }
            // 换了一台代理:旧密码不属于它,与导入、同步拉取「换了代理不留密码」同一口径。
            _unreadableProxyPassword = null;
        }
        return string.Empty;
    }

    /// <summary>
    /// 库里读出来的代理密码解成明文交给调用方。存量的明文原样交出;是密文却解不开(本机加密密钥换过、
    /// 系统密钥链这次没打开)时交出空串,密文记在 <see cref="_unreadableProxyPassword" /> 里等保存时写回。
    /// </summary>
    private void RevealProxyPassword(AppSettings settings)
    {
        _unreadableProxyPassword = null;
        if (protector is null || settings.Proxy.Password.Length == 0)
        {
            return;
        }
        string stored = settings.Proxy.Password;
        if (protector.TryUnprotect(stored, out string? plaintext))
        {
            settings.Proxy.Password = plaintext ?? string.Empty;
            return;
        }
        ProxyOptions owner = new()
        {
            Type = settings.Proxy.Type,
            Host = settings.Proxy.Host,
            Port = settings.Proxy.Port,
            Username = settings.Proxy.Username
        };
        _unreadableProxyPassword = new(stored, owner);
        settings.Proxy.Password = string.Empty;
        Trace.WriteLine("[Settings] The saved proxy password could not be decrypted with this device's key; it is kept as is and treated as empty.");
    }

    /// <summary>读取应用运行状态:从文档集合读取,不存在时首次导入既有 state.json 或返回新实例。</summary>
    public async Task<AppState> GetStateAsync() =>
        await GetOrImportAsync<AppState>(StateDocId, "state.json", SonnetDbJson.Serialize).ConfigureAwait(false);

    /// <summary>持久化应用运行状态到固定 Id 的状态文档。</summary>
    public async Task SaveStateAsync(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        await UpsertJsonAsync(StateDocId, SonnetDbJson.Serialize(state)).ConfigureAwait(false);
    }

    /// <summary>读一份文档;库里还没有就从旧版本的 JSON 文件导入,按 <paramref name="toStoredJson" /> 写进库里再交出。</summary>
    private async Task<T> GetOrImportAsync<T>(string docId, string legacyFileName, Func<T, string> toStoredJson) where T : class, new()
    {
        T? existing = await _engine.WithCollectionAsync(SonnetDbEngine.ConfigCollection, store =>
        {
            DocumentRow? row = store.Get(docId);
            return row is null ? null : SonnetDbJson.Deserialize<T>(row.Json);
        }).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }
        T? imported = await TryImportLegacyAsync<T>(legacyFileName).ConfigureAwait(false);
        if (imported is null)
        {
            return new();
        }
        await UpsertJsonAsync(docId, toStoredJson(imported)).ConfigureAwait(false);
        return imported;
    }

    private async Task UpsertJsonAsync(string docId, string json)
    {
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ConfigCollection, store =>
        {
            store.Upsert(docId, json);
            return null;
        }).ConfigureAwait(false);
    }

    private async Task<T?> TryImportLegacyAsync<T>(string fileName) where T : class
    {
        foreach (string directory in _legacyDirectories)
        {
            string path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                T? value = SonnetDbJson.Deserialize<T>(await File.ReadAllTextAsync(path).ConfigureAwait(false));
                if (value is not null)
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 旧文件损坏时跳过。
            }
        }
        return null;
    }

    /// <summary>
    /// 把渲染模式镜像成一个单行文件。渲染后端要在 Avalonia 初始化前定下,那时数据库还没起来,
    /// 启动路径读不了这份设置 —— 见 <see cref="VelaShellStoragePaths.RenderModeFile" />。
    /// 写失败只意味着下次启动沿用上一次的模式,不值得打断保存。
    /// </summary>
    private static void MirrorRenderMode(AppSettings settings)
    {
        try
        {
            var paths = new VelaShellStoragePaths();
            Directory.CreateDirectory(paths.RootDirectory);
            File.WriteAllText(
                paths.RenderModeFile,
                settings.Appearance.HardwareAcceleration ? "gpu" : "software"
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力而为。
        }
    }
}
