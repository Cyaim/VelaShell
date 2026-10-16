namespace VelaShell.Services.Update;

/// <summary>读取最新已发布版本的说明,不依赖安装版本或更新通道。</summary>
public interface IReleaseNotesSource
{
    /// <summary>包含预发布版、排除草稿,没有发布时返回 null。</summary>
    Task<ReleaseNotes?> GetLatestReleaseNotesAsync(CancellationToken cancellationToken = default);
}

/// <summary>一个 Release 的版本标签与完整更新正文。</summary>
public sealed record ReleaseNotes(string Tag, string Body, DateTimeOffset? PublishedAt = null);
