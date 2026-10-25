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
    // ------------------------------------------------------------ 移动与链接

    /// <summary>重命名或移动。</summary>
    /// <param name="sourcePath">源。</param>
    /// <param name="destinationPath">目标。</param>
    /// <param name="overwrite">
    /// 目标已存在时是否覆盖。<b>需要服务端支持 <c>posix-rename@openssh.com</c></b>。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SftpException">
    /// 要求覆盖但服务端不支持原子重命名。
    /// </exception>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/06 §7.2〕<b>不静默降级。</b>
    /// <c>posix-rename</c> 原子覆盖，普通 <c>RENAME</c> 在目标存在时失败 ——
    /// 语义不同。悄悄换一个，上层就无从知道自己拿到的是哪一种。
    /// 用 <see cref="Capabilities"/> 事先问清楚。
    /// </remarks>
    public async ValueTask RenameAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(sourcePath);
        ValidatePath(destinationPath);

        if (overwrite && !Capabilities.HasPosixRename)
        {
            // 服务端没说话 —— 原话留空，本库的说明放进消息（ServerMessage 只装服务端的原话）。
            throw new SftpException(
                SftpStatusCode.OperationUnsupported,
                serverMessage: "",
                sourcePath,
                SftpOperation.PosixRename,
                detail: "这台服务端没有 posix-rename@openssh.com，做不到原子覆盖式重命名。" +
                        "可以先删除目标再重命名，但那不是原子的 —— 中途失败会两个都没有")
            {
                MissingExtensions = [SftpExtensionNames.PosixRename],
            };
        }

        if (overwrite)
        {
            using SftpResponse response = await _pipeline.SendAsync(
                (output, id) =>
                {
                    ArrayBufferWriter<byte> inner = new();
                    SshDataWriter writer = new(inner);
                    _names.Write(ref writer, sourcePath);
                    _names.Write(ref writer, destinationPath);
                    SftpWire.WriteExtended(output, id, SftpExtensionNames.PosixRename, inner.WrittenSpan);
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            response.ThrowIfError(sourcePath, SftpOperation.PosixRename, SftpMessageType.Status);
            return;
        }

        using SftpResponse plain = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteRename(output, id, sourcePath, destinationPath, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        plain.ThrowIfError(sourcePath, SftpOperation.Rename, SftpMessageType.Status);
    }

    /// <summary>读符号链接指向哪里。</summary>
    public async ValueTask<string> ReadSymbolicLinkAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, SftpMessageType.ReadLink, id, path, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.ReadLink, SftpMessageType.Name);

        IReadOnlyList<SftpNameEntry> entries = SftpWire.ReadName(response.Payload, _names);
        if (entries.Count != 1)
        {
            throw new SshProtocolException(
                SshPhase.Open, $"READLINK 应当返回恰好 1 项，实际返回了 {entries.Count} 项。");
        }

        return entries[0].Name;
    }

    /// <summary>建符号链接。</summary>
    /// <param name="linkPath">在哪里<b>创建</b>链接。</param>
    /// <param name="targetPath">链接<b>指向</b>哪里。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <para>
    /// 参数顺序上的坑在 <see cref="SftpWire.WriteSymLink"/> 里说明了 ——
    /// 这里按人话的顺序（先在哪建、再指向哪），发出去时按 OpenSSH 的顺序。
    /// </para>
    /// <para>
    /// 〔决策 velashell-docs/zh/ssh/spec/06 §4.5〕<b>建完回读一次</b>（自检，不是开关）：<paramref name="linkPath"/> 上读得到链接就算成了
    /// （指向的文字服务端可能规范化过，不逐字比）；读不到、而 <paramref name="targetPath"/> 上多了一条指回 <paramref name="linkPath"/> 的链接，
    /// 就是服务端按 draft 的顺序解析了 —— 删掉建错的那一条，报错。服务端不支持 <c>READLINK</c> 时不查。
    /// 曾经不查：参数顺序与服务端相反时链接建错地方，而且不报错。
    /// </para>
    /// </remarks>
    /// <exception cref="SftpException">服务端拒绝；或者链接没有建在 <paramref name="linkPath"/> 上。</exception>
    public async ValueTask CreateSymbolicLinkAsync(
        string linkPath, string targetPath, CancellationToken cancellationToken = default)
    {
        ValidatePath(linkPath);
        ValidatePath(targetPath);

        using (SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteSymLink(output, id, targetPath, linkPath, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            response.ThrowIfError(linkPath, SftpOperation.CreateSymbolicLink, SftpMessageType.Status);
        }

        await VerifySymbolicLinkAsync(linkPath, targetPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>建完回读：链接得在 <paramref name="linkPath"/> 上；建反了就删掉建错的那一条并报错（见 <see cref="CreateSymbolicLinkAsync"/>）。</summary>
    private async ValueTask VerifySymbolicLinkAsync(string linkPath, string targetPath, CancellationToken cancellationToken)
    {
        try
        {
            _ = await ReadSymbolicLinkAsync(linkPath, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (SftpException ex) when (ex.StatusCode == SftpStatusCode.OperationUnsupported)
        {
            return;   // 读不了链接的服务端：查不了，不查
        }
        catch (SftpException)
        {
            // linkPath 上没有链接：往下看是不是建反了。
        }

        bool swapped;
        try
        {
            swapped = string.Equals(
                await ReadSymbolicLinkAsync(targetPath, cancellationToken).ConfigureAwait(false), linkPath, StringComparison.Ordinal);
        }
        catch (SftpException)
        {
            swapped = false;
        }

        if (swapped)
        {
            try
            {
                await DeleteFileAsync(targetPath, cancellationToken).ConfigureAwait(false);   // REMOVE 删的是链接本身
            }
            catch (SftpException)
            {
                // 删不掉也照样报错：消息里说清楚它在哪。
            }
        }

        throw new SftpException(
            SftpStatusCode.Failure, serverMessage: "", linkPath, SftpOperation.CreateSymbolicLink,
            swapped
                ? $"服务端按与 OpenSSH 相反的参数顺序建了链接（建在了 {PeerText.Sanitize(targetPath)} 上），已删掉建错的那一条"
                : "服务端回了成功，可链接没有建在这里");
    }

    /// <summary>建硬链接（需要 <c>hardlink@openssh.com</c>）。</summary>
    public async ValueTask CreateHardLinkAsync(
        string linkPath, string targetPath, CancellationToken cancellationToken = default)
    {
        ValidatePath(linkPath);
        ValidatePath(targetPath);

        if (!Capabilities.HasHardLink)
        {
            throw new SftpException(
                SftpStatusCode.OperationUnsupported,
                serverMessage: "",
                linkPath,
                SftpOperation.CreateHardLink,
                detail: "这台服务端没有 hardlink@openssh.com")
            {
                MissingExtensions = [SftpExtensionNames.HardLink],
            };
        }

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) =>
            {
                ArrayBufferWriter<byte> inner = new();
                SshDataWriter writer = new(inner);
                _names.Write(ref writer, targetPath);
                _names.Write(ref writer, linkPath);
                SftpWire.WriteExtended(output, id, SftpExtensionNames.HardLink, inner.WrittenSpan);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(linkPath, SftpOperation.CreateHardLink, SftpMessageType.Status);
    }
}
