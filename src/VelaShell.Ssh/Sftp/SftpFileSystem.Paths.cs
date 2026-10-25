// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §6、§7
//   OpenSSH PROTOCOL              SFTP 扩展章节
//   行为规格:                     velashell-docs/zh/ssh/spec/06-sftp.md 全部

using System.Buffers;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Sftp;

public sealed partial class SftpFileSystem
{
    // ------------------------------------------------------------ 路径

    /// <summary>把路径规范化成绝对路径。</summary>
    public async ValueTask<string> GetRealPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, SftpMessageType.RealPath, id, path, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.RealPath, SftpMessageType.Name);

        IReadOnlyList<SftpNameEntry> entries = SftpWire.ReadName(response.Payload, _names);
        if (entries.Count != 1)
        {
            throw new SshProtocolException(
                SshPhase.Open, $"REALPATH 应当返回恰好 1 项，实际返回了 {entries.Count} 项。");
        }

        return entries[0].Name;
    }

    /// <summary>
    /// 展开 <c>~</c> 并规范化成绝对路径：<c>~</c>、<c>~/projects</c>、<c>~alice</c>、<c>~alice/shared</c>；
    /// 不以 <c>~</c> 开头的路径等同 <see cref="GetRealPathAsync"/>。
    /// </summary>
    /// <exception cref="SftpException">
    /// <c>~用户名</c> 而服务端既没有 <c>expand-path@openssh.com</c> 也没有 <c>home-directory</c>
    /// （<see cref="SftpStatusCode.OperationUnsupported"/>）；用户不存在、路径不存在之类。
    /// </exception>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/06 §7.1〕<c>REALPATH</c> 不展开 <c>~</c> —— <c>~/projects</c> 会被当成当前目录下一个名叫 <c>~</c> 的目录。
    /// 有 <c>expand-path@openssh.com</c> 时整条交给服务端（它认得 <c>~用户名</c>，顺带规范化）；
    /// 没有时，<c>~</c> 与 <c>~/…</c> 用登录时的工作目录（<c>REALPATH "."</c>，SFTP 里就是家目录）拼出来，
    /// <c>~用户名</c> 用 <c>home-directory</c> 扩展取那个用户的家目录，拼好之后再 <c>REALPATH</c> 一次。
    /// </para>
    /// </remarks>
    public async ValueTask<string> ExpandPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        if (!path.StartsWith('~'))
        {
            return await GetRealPathAsync(path, cancellationToken).ConfigureAwait(false);
        }

        if (Capabilities.HasExpandPath)
        {
            return await SingleNameRequestAsync(SftpExtensionNames.ExpandPath, path, path, cancellationToken).ConfigureAwait(false);
        }

        int slash = path.IndexOf('/', StringComparison.Ordinal);
        string user = slash < 0 ? path[1..] : path[1..slash];
        string rest = slash < 0 ? "" : path[(slash + 1)..];

        string home;
        if (user.Length == 0)
        {
            home = await GetRealPathAsync(".", cancellationToken).ConfigureAwait(false);
        }
        else if (Capabilities.HasHomeDirectory)
        {
            home = await SingleNameRequestAsync(SftpExtensionNames.HomeDirectory, user, path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new SftpException(
                SftpStatusCode.OperationUnsupported,
                serverMessage: "",
                path,
                SftpOperation.ExpandPath,
                detail: "这台服务端既没有 expand-path@openssh.com 也没有 home-directory，展开不了 ~用户名")
            {
                MissingExtensions = [SftpExtensionNames.ExpandPath, SftpExtensionNames.HomeDirectory],
            };
        }

        return rest.Length == 0
            ? home
            : await GetRealPathAsync(home.TrimEnd('/') + "/" + rest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发一个只带一个字符串参数、回 <c>SSH_FXP_NAME</c>（恰好一项）的扩展请求。</summary>
    private async ValueTask<string> SingleNameRequestAsync(
        string extension, string argument, string pathForErrors, CancellationToken cancellationToken)
    {
        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) =>
            {
                ArrayBufferWriter<byte> inner = new();
                SshDataWriter writer = new(inner);
                _names.Write(ref writer, argument);
                SftpWire.WriteExtended(output, id, extension, inner.WrittenSpan);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(pathForErrors, SftpOperation.ExpandPath, SftpMessageType.Name);

        IReadOnlyList<SftpNameEntry> entries = SftpWire.ReadName(response.Payload, _names);
        if (entries.Count != 1)
        {
            throw new SshProtocolException(
                SshPhase.Open, $"{extension} 应当返回恰好 1 项，实际返回了 {entries.Count} 项。");
        }
        return entries[0].Name;
    }
}
