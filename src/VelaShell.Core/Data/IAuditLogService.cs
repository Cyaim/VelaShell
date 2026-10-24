using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>审计日志(时序数据)。为设置中的“安全审计”页与合规追踪提供存取。</summary>
public interface IAuditLogService
{
    /// <summary>写入一条审计日志条目。</summary>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>按时间倒序查询;<paramref name="category" /> 为空时返回全部类别。</summary>
    /// <param name="limit">最多几条。</param>
    /// <param name="category">只要这一类;空为全部。</param>
    /// <param name="from">只要这个时刻(含)之后的;空为不限。</param>
    /// <param name="to">只要这个时刻(不含)之前的;空为不限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<List<AuditEntry>> QueryAsync(int limit, string? category = null, DateTimeOffset? from = null, DateTimeOffset? to = null,
        CancellationToken cancellationToken = default);

    /// <summary>删掉早于 <paramref name="cutoff" /> 的记录(保留策略,见 <see cref="AuditRetention" />)。</summary>
    Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}
