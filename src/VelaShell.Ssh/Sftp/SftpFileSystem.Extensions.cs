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
    /// <summary>查 <paramref name="path"/> 所在文件系统的用量（需要 <c>statvfs@openssh.com</c>）。</summary>
    /// <param name="path">文件系统上的任意一个路径（常用目标目录本身）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SftpException">
    /// 服务端没有这个扩展（<see cref="SftpStatusCode.OperationUnsupported"/>，看 <see cref="SftpCapabilities.HasStatVfs"/>），或路径不存在之类。
    /// </exception>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/06 §7.1〕上传大文件之前预检「放得下吗」看 <see cref="SftpFileSystemInfo.AvailableBytes"/>
    /// —— 传到一半磁盘满，还会在远端留下半个文件。
    /// </remarks>
    public async ValueTask<SftpFileSystemInfo> GetFileSystemInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        if (!Capabilities.HasStatVfs)
        {
            throw new SftpException(
                SftpStatusCode.OperationUnsupported,
                serverMessage: "",
                path,
                SftpOperation.GetFileSystemInfo,
                detail: "这台服务端没有 statvfs@openssh.com")
            {
                MissingExtensions = [SftpExtensionNames.StatVfs],
            };
        }

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) =>
            {
                ArrayBufferWriter<byte> inner = new();
                SshDataWriter writer = new(inner);
                _names.Write(ref writer, path);
                SftpWire.WriteExtended(output, id, SftpExtensionNames.StatVfs, inner.WrittenSpan);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.GetFileSystemInfo, SftpMessageType.ExtendedReply);
        return SftpWire.ReadStatVfs(response.Payload);
    }

    /// <summary>
    /// 发一个<b>厂商私有</b>的扩展请求（<c>SSH_FXP_EXTENDED</c>），拿回应答载荷 —— 本库没有内置的扩展
    /// （群晖、某些 NAS 的私有扩展、插件要用的东西）走这里。
    /// </summary>
    /// <param name="extensionName">扩展名（如 <c>vendor-op@example.com</c>）。</param>
    /// <param name="payload">扩展名之后的请求内容，按那个扩展自己的约定编码（本库原样发出）。</param>
    /// <param name="cancellationToken">取消令牌。取消只是不再等，服务端那边照样会做完（SFTP 没有取消报文）。</param>
    /// <returns><c>SSH_FXP_EXTENDED_REPLY</c> 的载荷（request-id 之后的部分）；服务端回 <c>STATUS OK</c> 时为空。</returns>
    /// <exception cref="SftpException">服务端回了错误状态；不认识这个扩展是 <see cref="SftpStatusCode.OperationUnsupported"/>。</exception>
    /// <remarks>
    /// <para>
    /// 〔决策 velashell-docs/zh/ssh/spec/06 §7.3〕<b>扩展点只到「扩展名 + 载荷 → 应答」这一层</b>：请求照样走同一条流水线
    /// （request-id、在途额度、取消后的迟到应答都由本库管），管线的时序约束不交出去。要类型化，在调用方包一层即可。
    /// </para>
    /// <para>
    /// 不先查 <see cref="SftpCapabilities.RawExtensions"/>：有的服务端支持却不宣告。要不要先看一眼由调用方决定。
    /// 应答是对端给的字节，按不可信输入解析。
    /// </para>
    /// </remarks>
    public async ValueTask<byte[]> SendExtendedAsync(
        string extensionName, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(extensionName);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteExtended(output, id, extensionName, payload.Span),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response.TryGetStatus(out SftpStatusCode code, out string message))
        {
            return code == SftpStatusCode.Ok
                ? []
                : throw new SftpException(code, message, path: null, SftpOperation.Extension, detail: extensionName);
        }
        response.ExpectType(path: null, SftpOperation.Extension, SftpMessageType.ExtendedReply);
        return response.Payload.ToArray();
    }

    /// <summary>一次最多问多少个 uid / gid（两者各算）：防一个巨大的目录让请求长到被服务端拒掉。</summary>
    public const int MaxIdsPerLookup = 4096;

    /// <summary>把数字 uid / gid 翻成用户名与组名（需要 <c>users-groups-by-id@openssh.com</c>）。</summary>
    /// <param name="userIds">要翻的 uid。</param>
    /// <param name="groupIds">要翻的 gid。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与传入的 id 一一对应的名字；服务端不认识的 id 为 <see langword="null"/>。</returns>
    /// <exception cref="SftpException">服务端没有这个扩展（<see cref="SftpStatusCode.OperationUnsupported"/>，看 <see cref="SftpCapabilities.HasUsersGroupsById"/>）。</exception>
    /// <exception cref="ArgumentException">一次问的 id 超过 <see cref="MaxIdsPerLookup"/> 个。</exception>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/06 §7.1〕SFTP v3 的属性里只有数字 id，名字得另外查。有 shell 的账号可以 exec <c>getent</c>，
    /// 只开了 SFTP 的账号（chroot 的 internal-sftp）没有 exec，就靠它。名字来自服务端，是不可信文本，摆上界面前要清洗。
    /// </remarks>
    public async ValueTask<SftpIdNames> LookupUserAndGroupNamesAsync(
        IReadOnlyList<uint> userIds, IReadOnlyList<uint> groupIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        ArgumentNullException.ThrowIfNull(groupIds);
        if (userIds.Count > MaxIdsPerLookup || groupIds.Count > MaxIdsPerLookup)
        {
            throw new ArgumentException($"一次最多问 {MaxIdsPerLookup} 个 uid、{MaxIdsPerLookup} 个 gid。");
        }

        if (!Capabilities.HasUsersGroupsById)
        {
            throw new SftpException(
                SftpStatusCode.OperationUnsupported,
                serverMessage: "",
                path: null,
                SftpOperation.LookupNames,
                detail: "这台服务端没有 users-groups-by-id@openssh.com")
            {
                MissingExtensions = [SftpExtensionNames.UsersGroupsById],
            };
        }

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) =>
            {
                ArrayBufferWriter<byte> inner = new();
                SshDataWriter writer = new(inner);
                writer.WriteString(PackIds(userIds));
                writer.WriteString(PackIds(groupIds));
                SftpWire.WriteExtended(output, id, SftpExtensionNames.UsersGroupsById, inner.WrittenSpan);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path: null, SftpOperation.LookupNames, SftpMessageType.ExtendedReply);
        return SftpWire.ReadIdNames(response.Payload, userIds.Count, groupIds.Count);
    }

    private static byte[] PackIds(IReadOnlyList<uint> ids)
    {
        byte[] packed = new byte[ids.Count * 4];
        for (int i = 0; i < ids.Count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packed.AsSpan(i * 4), ids[i]);
        }
        return packed;
    }

    /// <summary>服务端内复制时每个 <c>copy-data</c> 请求最多复制多少字节。</summary>
    /// <remarks>
    /// OpenSSH 的 sftp-server 是单线程的：一个请求复制几个 GB，整条 SFTP 通道就被它堵住几分钟，
    /// 期间列目录、别的传输都等着。分段之后每段之间别的请求插得进来，也顺带有了进度、取消得了。
    /// </remarks>
    internal long CopySegmentBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>服务端内复制一个文件（需要 <c>copy-data</c>）：数据不出服务器。</summary>
    /// <param name="sourcePath">源文件。</param>
    /// <param name="destinationPath">目标文件。</param>
    /// <param name="overwrite">目标已存在时覆盖（截短再写）；<see langword="false"/> 时目标已存在就失败。</param>
    /// <param name="progress">已复制的字节数，每复制完一段报一次。</param>
    /// <param name="cancellationToken">在两段之间生效；正在服务端执行的那一段停不下来。</param>
    /// <exception cref="SftpException">
    /// 服务端没有这个扩展（<see cref="SftpStatusCode.OperationUnsupported"/>，看 <see cref="SftpCapabilities.HasCopyData"/>），
    /// 源不存在、目标已存在（不覆盖时）、没有权限、磁盘满之类。
    /// </exception>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/06 §7.1〕同一台服务器上的复制不必下载再上传：省掉双倍的网络流量与本机的临时文件，
    /// 几 GB 的文件从几分钟变成几秒。目标按源的权限位（rwx）创建，与 <c>cp</c> 一致不带 setuid 之类。
    /// 按 <see cref="CopySegmentBytes"/> 分段复制；中途失败或取消时目标留着已复制的部分（与 <c>cp</c> 一致）。
    /// </para>
    /// <para>
    /// 源的长度在开始时取一次：复制期间源还在变长，多出来的不复制。
    /// </para>
    /// </remarks>
    public async ValueTask CopyFileAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(sourcePath);
        ValidatePath(destinationPath);

        if (!Capabilities.HasCopyData)
        {
            throw new SftpException(
                SftpStatusCode.OperationUnsupported,
                serverMessage: "",
                sourcePath,
                SftpOperation.CopyData,
                detail: "这台服务端没有 copy-data")
            {
                MissingExtensions = [SftpExtensionNames.CopyData],
            };
        }

        await using SftpFileStream source = await OpenAsync(sourcePath, SftpOpenModes.Read, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        SftpFileAttributes sourceAttributes = await source.GetAttributesAsync(cancellationToken).ConfigureAwait(false);

        SftpOpenModes modes = SftpOpenModes.Write | SftpOpenModes.Create
            | (overwrite ? SftpOpenModes.Truncate : SftpOpenModes.Exclusive);
        SftpFileAttributes create = sourceAttributes.HasPermissions
            ? SftpFileAttributes.WithPermissions(sourceAttributes.PermissionBits & 0x1FF)
            : default;
        await using SftpFileStream destination = await OpenAsync(destinationPath, modes, create, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!sourceAttributes.HasSize)
        {
            // 服务端没给长度：一个请求复制到源的末尾（长度 0 的意思就是「到 EOF」），没有中间进度。
            await CopyDataAsync(source, 0, 0, destination, sourcePath, cancellationToken).ConfigureAwait(false);
            return;
        }

        ulong total = sourceAttributes.Size;
        ulong done = 0;
        while (done < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong segment = Math.Min((ulong)CopySegmentBytes, total - done);
            await CopyDataAsync(source, done, segment, destination, sourcePath, cancellationToken).ConfigureAwait(false);
            done += segment;
            progress?.Report((long)done);
        }
    }

    /// <summary>一个 <c>copy-data</c> 请求：源与目标在同一个偏移上。</summary>
    private async ValueTask CopyDataAsync(
        SftpFileStream source, ulong offset, ulong length, SftpFileStream destination, string pathForErrors,
        CancellationToken cancellationToken)
    {
        byte[] readHandle = source.Handle.ToArray();
        byte[] writeHandle = destination.Handle.ToArray();
        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) =>
            {
                ArrayBufferWriter<byte> inner = new();
                SshDataWriter writer = new(inner);
                writer.WriteString(readHandle);
                writer.WriteUInt64(offset);
                writer.WriteUInt64(length);
                writer.WriteString(writeHandle);
                writer.WriteUInt64(offset);
                SftpWire.WriteExtended(output, id, SftpExtensionNames.CopyData, inner.WrittenSpan);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(pathForErrors, SftpOperation.CopyData, SftpMessageType.Status);
    }
}
