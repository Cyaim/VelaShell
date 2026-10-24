using VelaShell.Core.Layouts;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Docking.Model;

namespace VelaShell.Services.Layouts;

/// <summary>
/// 一个停靠文档在布局模板里记成什么(<see cref="LayoutDocument" />),以及它算不算「此刻在线」。
/// </summary>
/// <remarks>
/// <para>
/// 记的是「怎么重新打开它」:配置 Id 加上打开方式。同一条 SSH 配置开的终端与开的独立 SFTP 标签
/// 要分得开(<see cref="LayoutDocumentKind.Terminal" /> 与 <see cref="LayoutDocumentKind.Files" />),
/// 插件协议则只记类型,由打开时的注册表决定它是终端、文件还是工作台 —— 宿主不认识具体协议。
/// </para>
/// <para>
/// 记不下来的返回 null:插件用 <c>PanelDisplayMode.Document</c> 开出来的面板(AI 聊天等)是插件
/// 自己建的控件,宿主没有「重新打开它」的契约;不认识的文档类型同理。配置是不是还在库里
/// (快速连接、外部拉起的那份临时配置并不入库)由调用方另查。
/// </para>
/// </remarks>
public static class LayoutDocumentDescriber
{
    /// <summary>描述一个文档;没法重新打开的返回 null。</summary>
    /// <param name="document">停靠文档。</param>
    /// <param name="isWorkspaceProfile">
    /// 一条插件配置是不是工作台(Redis…)。只有「连接中」占位标签要问:真文档的类型本身就说明了。
    /// </param>
    /// <returns>模板里的标签;固定状态、是否活动与标题由 <see cref="DockLayoutSnapshot.Capture" /> 补。</returns>
    public static LayoutDocument? Describe(DockDocument document, Func<SessionProfile, bool> isWorkspaceProfile)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(isWorkspaceProfile);
        return document switch
        {
            TerminalDocument { Terminal.LocalShell: { } shell } => new()
            {
                Kind = LayoutDocumentKind.LocalShell,
                ShellId = shell.Id,
            },
            TerminalDocument { Terminal.Profile: { } profile } => ForProfile(LayoutDocumentKind.Terminal, profile),
            SftpDocument sftp => ForProfile(LayoutDocumentKind.Files, sftp.ViewModel.Profile),
            DualSftpDocument dual => DualFiles(dual.ViewModel.LeftProfile, dual.ViewModel.RightProfile),
            PluginWorkspaceDocument workspace => ForProfile(LayoutDocumentKind.Workspace, workspace.Profile),
            // 「连接中」与失败卡片:按它连上之后会变成的那种标签记(终端标签不经过这种占位)。
            ConnectingDocument { SecondaryProfile: { } right } connecting => DualFiles(connecting.Profile, right),
            ConnectingDocument connecting => ForProfile(
                connecting.Profile.ConnectionType == ConnectionType.Plugin && isWorkspaceProfile(connecting.Profile)
                    ? LayoutDocumentKind.Workspace
                    : LayoutDocumentKind.Files,
                connecting.Profile),
            _ => null,
        };
    }

    /// <summary>
    /// 这个文档此刻算不算「在线」—— 退出时记布局只记在线的,与「恢复会话」记
    /// <c>LastOpenProfileIds</c> 同一口径:断开着的、还在连的、失败卡片都不记,
    /// 免得下次启动替用户去连一台他退出时就没连着的机器。
    /// </summary>
    /// <param name="document">停靠文档。</param>
    /// <returns>在线返回 <see langword="true" />。</returns>
    public static bool IsLive(DockDocument document) => document switch
    {
        TerminalDocument terminal => terminal.Terminal.IsConnected,
        // 文件标签只在连上之后才建出来(之前是「连接中」占位),断线也不会自己关 —— 与恢复会话一样按在场算。
        SftpDocumentBase => true,
        PluginWorkspaceDocument workspace => workspace.Status == SessionStatus.Connected,
        _ => false,
    };

    private static LayoutDocument ForProfile(string kind, SessionProfile profile) =>
        new() { Kind = kind, ProfileId = profile.Id };

    private static LayoutDocument DualFiles(SessionProfile left, SessionProfile right) =>
        new() { Kind = LayoutDocumentKind.DualFiles, ProfileId = left.Id, SecondProfileId = right.Id };
}
