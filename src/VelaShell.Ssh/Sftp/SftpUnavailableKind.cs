// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §4(INIT / VERSION 与版本的取法)
//   RFC 4254 §6.5(subsystem 请求)
//   行为规格: velashell-docs/zh/ssh/spec/06-sftp.md §一、§九;velashell-docs/zh/ssh/spec/08-failures.md §一

namespace VelaShell.Ssh.Sftp;

/// <summary>SFTP 起不来的具体情形（<see cref="SftpUnavailableException.FailureKind"/>）—— 界面据此分清「服务端没开 SFTP」「版本不对」「本端已经收工」。</summary>
/// <remarks>
/// 曾经只能看 <see cref="Diagnostics.SshException.Reason"/>：「版本太低」与「流水线已释放」都是 <see cref="Diagnostics.SshFailureReason.Unsupported"/>，
/// 界面一律说成「服务端没有开 SFTP」。零值 <see cref="Unknown"/> 是「没归类」：库自己抛的都会归类。
/// </remarks>
public enum SftpUnavailableKind
{
    /// <summary>没归类（使用者自己构造的）。</summary>
    Unknown = 0,

    /// <summary>服务端拒绝了 <c>subsystem sftp</c> 请求：sshd_config 里缺少或注释掉了「Subsystem sftp …」那一行。</summary>
    SubsystemRejected,

    /// <summary>SFTP 握手在时限内没有完成：sftp-server 没有回应（常见原因是服务端登录 shell 的启动文件卡住了）。</summary>
    HandshakeTimeout,

    /// <summary>sftp-server 没等 SFTP 建立就退出了（退出码与 stderr 在 <see cref="SftpUnavailableException.ServerExitStatus"/> / <see cref="SftpUnavailableException.ServerErrorOutput"/>）。</summary>
    ServerExited,

    /// <summary>服务端只支持 SFTP v<see cref="SftpUnavailableException.ServerVersion"/>，低于本库要求的 v3。</summary>
    VersionTooOld,

    /// <summary>服务端回的版本 v<see cref="SftpUnavailableException.ServerVersion"/> 比我们发的 v3 高（draft-02 §4 要求回较小的那个）：按 v3 解析会读错属性，不连。</summary>
    VersionTooNew,

    /// <summary>
    /// SFTP 流水线已经收工或释放（本端关闭了文件系统、连接释放）：那是本端的事，不是服务端不支持 SFTP。
    /// 原因码是 <see cref="Diagnostics.SshFailureReason.Aborted"/>（曾经释放那一条路是 <see cref="Diagnostics.SshFailureReason.Unsupported"/>）。
    /// </summary>
    SessionClosed,
}
