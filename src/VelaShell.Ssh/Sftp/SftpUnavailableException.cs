// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/06-sftp.md §3.3、§6.2、§九

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Sftp;

/// <summary>SFTP 子系统起不来。</summary>
public sealed class SftpUnavailableException : SshException
{
    /// <summary>创建一个 SFTP 不可用异常。</summary>
    public SftpUnavailableException(string message, Exception? innerException = null)
        : base(SshFailureReason.Unsupported, SshPhase.Open, message, innerException)
    {
    }

    /// <summary>带原因码的版本（如握手超时是 <see cref="SshFailureReason.Timeout"/>）。</summary>
    internal SftpUnavailableException(SshFailureReason reason, string message, Exception? innerException = null)
        : base(reason, SshPhase.Open, message, innerException)
    {
    }

    /// <summary>
    /// sftp-server 没等 SFTP 建立就退出时（<see cref="SshFailureReason.CommandFailed"/>），服务端报来的退出码；
    /// 没报、或者不是这种失败时为 <see langword="null"/>。
    /// </summary>
    public int? ServerExitStatus { get; init; }

    /// <summary>
    /// sftp-server 没等 SFTP 建立就退出时，它在 stderr 上说的最后一段（已按对端文本清洗，换行收成一行）；
    /// 没说、或者不是这种失败时为 <see langword="null"/>。
    /// </summary>
    public string? ServerErrorOutput { get; init; }

    /// <summary>具体是哪种情形 —— 界面按它说，不解析 <see cref="Exception.Message"/> 里的中文。</summary>
    /// <remarks>库自己抛的都填；使用者自己构造、没填时是 <see cref="SftpUnavailableKind.Unknown"/>。</remarks>
    public SftpUnavailableKind FailureKind { get; init; }

    /// <summary>服务端在 <c>SSH_FXP_VERSION</c> 里回的版本（<see cref="SftpUnavailableKind.VersionTooOld"/> / <see cref="SftpUnavailableKind.VersionTooNew"/>）。</summary>
    public uint? ServerVersion { get; init; }
}
