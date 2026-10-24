using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Infrastructure.Audit;

/// <summary>
/// 配置类审计记录(连接配置、设置、共享凭据的改动)共用的小工具:比较、拼详情、写记录。
/// </summary>
/// <remarks>
/// <para>
/// 审计挂在仓储 / 服务这一层(<see cref="AuditedSessionRepository" />、<see cref="AuditedSettingsService" />、
/// <see cref="AuditedSharedCredentialRepository" />),不挂在界面上:保存点有编辑框、拖动分组、证书信任、
/// 云同步拉取、导入……挂在界面上迟早漏一处,而这一层每次落盘都要经过。
/// </para>
/// <para>
/// <b>只记哪一块变了,不记值。</b>详情按写入时的界面语言写成一句话(与安全告警的详情同一口径),
/// 机密字段只会以「登录凭据」这样的块名出现。
/// </para>
/// </remarks>
internal static class ConfigAudit
{
    /// <summary>详情里各段之间的分隔。</summary>
    internal const string Separator = " · ";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>写一条记录;写不进去只留痕,不打断保存。</summary>
    internal static async Task WriteAsync(IAuditLogService log, string category, string action, Guid? profileId, string detail)
    {
        try
        {
            await log.WriteAsync(new AuditEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Category = category,
                Action = action,
                ProfileId = profileId,
                Detail = detail
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 审计写不进去不能让保存失败:用户的改动已经落盘了。
            Trace.WriteLine($"[Audit] {category}/{action} could not be written: {ex.Message}");
        }
    }

    /// <summary>
    /// 两个对象按 JSON 逐个顶层字段比较,交回变了的字段名(camelCase);<paramref name="ignored" /> 里的不算。
    /// </summary>
    internal static IEnumerable<string> ChangedFields<T>(T before, T after, IReadOnlySet<string> ignored)
    {
        JsonObject a = JsonSerializer.SerializeToNode(before, Options)!.AsObject();
        JsonObject b = JsonSerializer.SerializeToNode(after, Options)!.AsObject();
        return a.Select(p => p.Key)
                .Union(b.Select(p => p.Key))
                .Where(key => !ignored.Contains(key) && !JsonNode.DeepEquals(a[key], b[key]));
    }

    /// <summary>「改动:名称、登录凭据」—— 块名按给定顺序,资源键见 <c>AuditLog_Area*</c>。</summary>
    internal static string ChangedAreas(IEnumerable<string> areaKeys) =>
        Strings.Format("AuditLog_DetailChanged",
            string.Join(Strings.Get("SetTransfer_ListSeparator"), areaKeys.Select(Strings.Get)));

    /// <summary>把非空的几段用 <see cref="Separator" /> 连起来。</summary>
    internal static string Join(params IEnumerable<string?> parts) =>
        string.Join(Separator, parts.Where(p => !string.IsNullOrEmpty(p)));
}
