using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Infrastructure.Audit;

/// <summary>
/// 给共享凭据仓储加上审计:新建、修改、删除各记一条,<b>带上此刻引用它的连接数</b>。其余操作原样转给内层。
/// </summary>
/// <remarks>
/// <para>
/// 改一条共享凭据等于一次换掉 N 条连接的登录材料(§157),而那 N 条连接自己的配置一个字都没变 ——
/// 连接配置那边的审计看不到这件事,只有在这里记。挂在仓储上,登录框里「同时更新共享凭据」、
/// 设置页的编辑框、云同步拉取、导入连接文件四条路都经过它。
/// </para>
/// <para>
/// 删除时的连接数是删除那一刻的:经设置页删除时引用它的连接已经各自拷了一份凭据(那几次改动在连接配置的审计里),
/// 到这里通常是 0,这时详情里不写连接数。
/// </para>
/// </remarks>
/// <param name="inner">真正的仓储。</param>
/// <param name="sessions">会话仓储,用来数引用它的连接。</param>
/// <param name="auditLog">审计日志;null 时只转发。</param>
public sealed class AuditedSharedCredentialRepository(
    ISharedCredentialRepository inner,
    ISessionRepository sessions,
    IAuditLogService? auditLog) : ISharedCredentialRepository
{
    private static readonly HashSet<string> Ignored = [];

    private static readonly HashSet<string> MaterialFields =
        ["username", "authMethod", "password", "privateKeyPath", "privateKeyPassphrase", "certificatePath"];

    private readonly ISharedCredentialRepository _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ISessionRepository _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    /// <inheritdoc />
    public Task<List<SharedCredential>> GetAllAsync() => _inner.GetAllAsync();

    /// <inheritdoc />
    public Task<List<SharedCredentialMetadata>> ListMetadataAsync() => _inner.ListMetadataAsync();

    /// <inheritdoc />
    public Task<string?> GetPasswordAsync(Guid id) => _inner.GetPasswordAsync(id);

    /// <inheritdoc />
    public Task<SharedCredential?> GetAsync(Guid id) => _inner.GetAsync(id);

    /// <inheritdoc />
    public async Task SaveAsync(SharedCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (auditLog is null)
        {
            await _inner.SaveAsync(credential).ConfigureAwait(false);
            return;
        }
        (bool known, SharedCredential? before) = await TryReadAsync(credential.Id).ConfigureAwait(false);
        await _inner.SaveAsync(credential).ConfigureAwait(false);
        if (!known)
        {
            return;
        }
        SharedCredential after = (await TryReadAsync(credential.Id).ConfigureAwait(false)).Credential ?? credential;
        if (before is null)
        {
            await WriteAsync(AuditActions.CredentialCreated, after.Name, null, after.Id).ConfigureAwait(false);
            return;
        }
        List<string> areas = ChangedAreas(before, after);
        if (areas.Count == 0)
        {
            return;
        }
        string name = before.Name == after.Name ? after.Name : $"{before.Name} → {after.Name}";
        await WriteAsync(AuditActions.CredentialUpdated, name, ConfigAudit.ChangedAreas(areas), after.Id).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id)
    {
        (bool known, SharedCredential? before) = auditLog is null ? (false, null) : await TryReadAsync(id).ConfigureAwait(false);
        int users = known && before is not null ? await CountUsersAsync(id).ConfigureAwait(false) : 0;
        await _inner.DeleteAsync(id).ConfigureAwait(false);
        if (known && before is not null)
        {
            await ConfigAudit.WriteAsync(auditLog!, AuditCategories.Credential, AuditActions.CredentialDeleted, null,
                ConfigAudit.Join(before.Name, Users(users))).ConfigureAwait(false);
        }
    }

    /// <summary>改了哪几块(资源键,按 名称 → 登录凭据 → 其他 的顺序);一块都没改时为空。</summary>
    internal static List<string> ChangedAreas(SharedCredential before, SharedCredential after)
    {
        HashSet<string> fields = [.. ConfigAudit.ChangedFields(before, after, Ignored)];
        List<string> areas = [];
        if (fields.Contains("name"))
        {
            areas.Add("AuditLog_AreaName");
        }
        if (fields.Overlaps(MaterialFields))
        {
            areas.Add("AuditLog_AreaCredentials");
        }
        if (fields.Any(f => f != "name" && !MaterialFields.Contains(f)))
        {
            areas.Add("AuditLog_AreaOther");
        }
        return areas;
    }

    private async Task WriteAsync(string action, string name, string? changes, Guid id)
    {
        int users = await CountUsersAsync(id).ConfigureAwait(false);
        await ConfigAudit.WriteAsync(auditLog!, AuditCategories.Credential, action, null,
            ConfigAudit.Join(name, changes, Users(users))).ConfigureAwait(false);
    }

    private static string? Users(int count) => count > 0 ? Strings.Format("AuditLog_DetailCredentialUsers", count) : null;

    /// <summary>引用这条凭据的连接有几条;数不出来按 0 算(少一段说明,记录照写)。</summary>
    private async Task<int> CountUsersAsync(Guid id)
    {
        try
        {
            List<SessionProfile> profiles = await _sessions.GetAllSessionsAsync().ConfigureAwait(false);
            return profiles.Count(p => p.CredentialSource is { } reference && reference.TryGetSharedId(out Guid used) && used == id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    private async Task<(bool Known, SharedCredential? Credential)> TryReadAsync(Guid id)
    {
        try
        {
            return (true, await _inner.GetAsync(id).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, null);
        }
    }
}
