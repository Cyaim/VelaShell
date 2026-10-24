using VelaShell.Core.Layouts;
using VelaShell.Core.Models;

namespace VelaShell.Services.Layouts;

/// <summary>模板里的一个标签解析之后,要怎么打开它。</summary>
/// <param name="Kind">标签类型(<see cref="LayoutDocumentKind" />)。</param>
/// <param name="Profile">配置(本地终端为 null);从库里新读的那份,不是保存模板时的旧副本。</param>
/// <param name="SecondProfile">双栏的右栏配置。</param>
/// <param name="Shell">本地终端的 shell。</param>
public sealed record LayoutOpenRequest(
    string Kind,
    SessionProfile? Profile = null,
    SessionProfile? SecondProfile = null,
    LocalShellInfo? Shell = null);

/// <summary>还原布局模板时查配置、开标签的那一面(宿主的连接流程)。</summary>
public interface ILayoutDocumentOpener
{
    /// <summary>按 Id 从库里取配置;不在库里(已删除)返回 null。</summary>
    /// <param name="profileId">配置 Id。</param>
    /// <returns>配置。</returns>
    Task<SessionProfile?> FindProfileAsync(Guid profileId);

    /// <summary>按标识找本机的 shell;本机没有这个 shell 返回 null。</summary>
    /// <param name="shellId">shell 标识。</param>
    /// <returns>shell。</returns>
    LocalShellInfo? FindLocalShell(string shellId);

    /// <summary>打开一个标签。走宿主既有的连接流程,新标签经 <c>DockWorkspace.AddDocument</c> 放进工作区。</summary>
    /// <param name="request">要打开什么。</param>
    /// <returns>连接流程结束(连上、失败或取消)时完成。</returns>
    Task OpenAsync(LayoutOpenRequest request);
}

/// <summary>宿主那几条打开路径(由主窗口视图模型交进来)。</summary>
/// <param name="Connect">按配置的默认方式打开(资源管理器双击那一条):SSH 开终端,文件协议开文件标签,插件按注册表。</param>
/// <param name="OpenSftp">SSH / SFTP 配置开独立的 SFTP 文件标签。</param>
/// <param name="OpenFtp">FTP 配置开文件标签。</param>
/// <param name="OpenPluginFiles">插件文件协议(S3 …)开文件标签。</param>
/// <param name="OpenWorkspace">插件工作台(Redis …)。</param>
/// <param name="OpenDualFiles">「远程 + 远程」双栏。</param>
/// <param name="OpenLocalShell">本地终端。</param>
public sealed record LayoutOpenActions(
    Func<SessionProfile, Task> Connect,
    Func<SessionProfile, Task> OpenSftp,
    Func<SessionProfile, Task> OpenFtp,
    Func<SessionProfile, Task> OpenPluginFiles,
    Func<SessionProfile, Task> OpenWorkspace,
    Func<SessionProfile, SessionProfile, Task> OpenDualFiles,
    Func<LocalShellInfo, Task> OpenLocalShell);

/// <summary>
/// <see cref="ILayoutDocumentOpener" /> 的宿主实现:把模板里的标签类型映射到宿主的打开路径。
/// </summary>
/// <remarks>
/// 映射按「标签类型 + 配置此刻的连接类型」定:保存模板之后配置的类型可能被改过(SFTP 改成了 SSH),
/// 照旧按保存时的路径开只会换来一张失败卡片。认不准的一律退回 <see cref="LayoutOpenActions.Connect" /> ——
/// 那是资源管理器双击的同一条路,至少能按配置现在的样子开出一个能用的标签。
/// </remarks>
/// <param name="findProfile">按 Id 从库里取配置。</param>
/// <param name="findShell">按标识找本机的 shell。</param>
/// <param name="actions">宿主的打开路径。</param>
public sealed class LayoutDocumentOpener(
    Func<Guid, Task<SessionProfile?>> findProfile,
    Func<string, LocalShellInfo?> findShell,
    LayoutOpenActions actions) : ILayoutDocumentOpener
{
    /// <inheritdoc />
    public Task<SessionProfile?> FindProfileAsync(Guid profileId) => findProfile(profileId);

    /// <inheritdoc />
    public LocalShellInfo? FindLocalShell(string shellId) => findShell(shellId);

    /// <inheritdoc />
    public Task OpenAsync(LayoutOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == LayoutDocumentKind.LocalShell)
        {
            return request.Shell is { } shell ? actions.OpenLocalShell(shell) : Task.CompletedTask;
        }
        if (request.Profile is not { } profile)
        {
            return Task.CompletedTask;
        }
        return request.Kind switch
        {
            LayoutDocumentKind.Files => profile.ConnectionType switch
            {
                ConnectionType.SSH or ConnectionType.SFTP => actions.OpenSftp(profile),
                ConnectionType.FTP => actions.OpenFtp(profile),
                ConnectionType.Plugin => actions.OpenPluginFiles(profile),
                _ => actions.Connect(profile),
            },
            LayoutDocumentKind.DualFiles when request.SecondProfile is { } right => actions.OpenDualFiles(profile, right),
            LayoutDocumentKind.Workspace when profile.ConnectionType == ConnectionType.Plugin => actions.OpenWorkspace(profile),
            _ => actions.Connect(profile),
        };
    }
}
