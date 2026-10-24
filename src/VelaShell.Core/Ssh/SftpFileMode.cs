namespace VelaShell.Core.Ssh;

/// <summary>
/// 远端条目<b>自身</b>(不跟随链接,SFTP 的 LSTAT)的模式位与属主/属组,服务端没报的项为 <see langword="null" />。
/// 原子替换上传据此把被替换文件原有的权限与属主带到新文件上(见 <c>SftpService.CommitUploadAsync</c>)。
/// </summary>
/// <param name="Mode">完整的 st_mode(文件类型 + <c>07777</c> 权限位);服务端没报权限时为 null —— 那样连类型都不知道。</param>
/// <param name="UserId">属主 UID。</param>
/// <param name="GroupId">属组 GID。</param>
public sealed record SftpFileMode(uint? Mode, int? UserId, int? GroupId)
{
    /// <summary>是不是普通文件(S_IFREG)。权限没报时无从知道,按「不是」算。</summary>
    public bool IsRegularFile => Mode is { } mode && (mode & 0xF000) == 0x8000;

    /// <summary>去掉文件类型之后的权限位(<c>07777</c>,含 setuid / setgid / 粘滞位);没报时为 null。</summary>
    public uint? PermissionBits => Mode & 0xFFF;
}
