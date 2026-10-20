using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>
/// 会话与分组的持久化仓储接口,提供服务器分组与会话配置的读取、保存与删除操作。
/// </summary>
public interface ISessionRepository
{
    /// <summary>
    /// 异步获取全部服务器分组。
    /// </summary>
    Task<List<ServerGroup>> GetAllGroupsAsync();

    /// <summary>
    /// 异步获取全部会话配置。
    /// </summary>
    Task<List<SessionProfile>> GetAllSessionsAsync();

    /// <summary>
    /// 异步按标识获取单个会话配置,未找到时返回 <c>null</c>。
    /// </summary>
    Task<SessionProfile?> GetSessionAsync(Guid id);

    /// <summary>
    /// 异步保存会话配置(新增或更新)。
    /// </summary>
    /// <remarks>
    /// 更新时 <see cref="SessionProfile.DirectoryBookmarks" /> 保留库里现存的那份,不取传入的:收藏只经
    /// <see cref="UpdateDirectoryBookmarksAsync" /> 修改。整条写回的调用方(连接成功、置顶、拖动分组、编辑配置、
    /// 证书信任……)手里多半是早先读出来的副本,按它写收藏会把别处刚加的收藏抹掉。
    /// 要连收藏一起覆盖的(同步拉取、导入备份)用 <see cref="ReplaceSessionAsync" />。
    /// </remarks>
    Task SaveSessionAsync(SessionProfile session);

    /// <summary>
    /// 异步整条覆盖会话配置,目录收藏也取传入的(新增或更新)。
    /// </summary>
    Task ReplaceSessionAsync(SessionProfile session);

    /// <summary>
    /// 异步只改一条会话配置的目录收藏:在库里最新的那份收藏上执行 <paramref name="update" />,其余字段原样不动。
    /// </summary>
    /// <param name="id">会话配置的标识。</param>
    /// <param name="update">就地修改收藏列表;返回 <c>false</c> 表示不写回。</param>
    /// <returns>执行后库里的收藏列表(副本);配置不在库里时返回 <c>null</c>,且不调用 <paramref name="update" />。</returns>
    Task<List<RemoteDirectoryBookmark>?> UpdateDirectoryBookmarksAsync(
        Guid id, Func<List<RemoteDirectoryBookmark>, bool> update);

    /// <summary>
    /// 异步按标识删除会话配置。
    /// </summary>
    Task DeleteSessionAsync(Guid id);

    /// <summary>
    /// 异步保存服务器分组(新增或更新)。
    /// </summary>
    Task SaveGroupAsync(ServerGroup group);

    /// <summary>
    /// 异步按标识删除服务器分组。
    /// </summary>
    Task DeleteGroupAsync(Guid id);
}
