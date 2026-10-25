// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §6、§7
//   OpenSSH PROTOCOL              SFTP 扩展章节
//   行为规格:                     velashell-docs/zh/ssh/spec/06-sftp.md 全部

using System.Buffers;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Sftp;

public sealed partial class SftpFileSystem
{
    // ------------------------------------------------------------ 属性

    /// <summary>取属性，<b>跟随</b>符号链接。</summary>
    public ValueTask<SftpFileAttributes> GetAttributesAsync(
        string path, CancellationToken cancellationToken = default) =>
        StatAsync(path, SftpMessageType.Stat, cancellationToken);

    /// <summary>取属性，<b>不跟随</b>符号链接 —— 描述链接本身。</summary>
    public ValueTask<SftpFileAttributes> GetLinkAttributesAsync(
        string path, CancellationToken cancellationToken = default) =>
        StatAsync(path, SftpMessageType.LStat, cancellationToken);

    private async ValueTask<SftpFileAttributes> StatAsync(
        string path, SftpMessageType type, CancellationToken cancellationToken)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, type, id, path, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.GetAttributes, SftpMessageType.Attrs);
        return SftpWire.ReadAttrs(response.Payload);
    }

    /// <summary>文件或目录存在吗。</summary>
    public async ValueTask<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await GetLinkAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SftpException ex) when (ex.IsNotFound)
        {
            return false;
        }
    }

    /// <summary>设属性。</summary>
    public async ValueTask SetAttributesAsync(
        string path, SftpFileAttributes attributes, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteSetStat(output, id, path, attributes, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.SetAttributes, SftpMessageType.Status);
    }

    /// <summary>设属性，<b>不跟随</b>符号链接 —— 改链接自身（需要 <c>lsetstat@openssh.com</c>）。</summary>
    /// <exception cref="SftpException">服务端没有这个扩展（<see cref="SftpStatusCode.OperationUnsupported"/>，看 <see cref="SftpCapabilities.HasLSetStat"/>）。</exception>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/06 §7.1〕相当于 <c>touch -h</c> / <c>chown -h</c>：同步目录时保留符号链接自身的时间戳。
    /// <c>SETSTAT</c> 跟随链接，改到的是链接的目标；没有扩展时不退化成它（那等于换了语义）。
    /// </remarks>
    public async ValueTask SetLinkAttributesAsync(
        string path, SftpFileAttributes attributes, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        if (!Capabilities.HasLSetStat)
        {
            throw new SftpException(
                SftpStatusCode.OperationUnsupported,
                serverMessage: "",
                path,
                SftpOperation.SetAttributes,
                detail: "这台服务端没有 lsetstat@openssh.com")
            {
                MissingExtensions = [SftpExtensionNames.LSetStat],
            };
        }

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) =>
            {
                ArrayBufferWriter<byte> inner = new();
                SshDataWriter writer = new(inner);
                _names.Write(ref writer, path);
                attributes.Write(ref writer);
                SftpWire.WriteExtended(output, id, SftpExtensionNames.LSetStat, inner.WrittenSpan);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.SetAttributes, SftpMessageType.Status);
    }

    /// <summary>改权限。</summary>
    public ValueTask SetPermissionsAsync(
        string path, uint permissions, CancellationToken cancellationToken = default) =>
        SetAttributesAsync(path, SftpFileAttributes.WithPermissions(permissions), cancellationToken);

    /// <summary>改最后修改时间。</summary>
    /// <remarks>
    /// <c>atime</c> 与 <c>mtime</c> <b>共用一个标志位</b>，只给一个会把另一个抹成 1970 年。
    /// 所以这里<b>先把当前的 atime 取回来</b>再一并写回。
    /// </remarks>
    public async ValueTask SetLastWriteTimeAsync(
        string path, DateTimeOffset modifyTime, CancellationToken cancellationToken = default)
    {
        SftpFileAttributes current = await GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);

        DateTimeOffset accessTime = current.HasTimes
            ? current.LastAccessTime
            : modifyTime;

        await SetAttributesAsync(
            path, SftpFileAttributes.WithTimes(accessTime, modifyTime), cancellationToken).ConfigureAwait(false);
    }
}
