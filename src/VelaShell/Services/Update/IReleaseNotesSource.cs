namespace VelaShell.Services.Update;

/// <summary>读取最新正式版或指定安装版本清单中的说明,不查询匿名 Release API。</summary>
public interface IReleaseNotesSource
{
    /// <summary>只读取正式版;没有正式版或清单附件时返回 null,旧清单缺少日志字段时返回空正文。</summary>
    Task<ReleaseNotes?> GetLatestReleaseNotesAsync(CancellationToken cancellationToken = default);

    /// <summary>读取指定版本(可为预发布版)的说明;只对该版本的有限标签变体做 404 回退。</summary>
    Task<ReleaseNotes?> GetReleaseNotesForVersionAsync(string version, CancellationToken cancellationToken = default);
}

/// <summary>一个 Release 的日志与同一响应中可用于当前平台的可选安装清单快照。</summary>
public sealed record ReleaseNotes(string Tag, string Body, DateTimeOffset? PublishedAt = null, string? HtmlUrl = null,
    UpdateManifest? Manifest = null);
