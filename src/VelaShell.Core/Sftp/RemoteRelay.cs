using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 两个远端会话之间的文件中转:源端顺序读,目标端边收边写,中间只经过本机内存。
/// </summary>
/// <remarks>
/// <para>
/// SFTP / FTP 协议本身都没有「服务器到服务器」的复制原语(FTP 的 FXP 各家服务器几乎都禁了),
/// 所以数据一定要过本机。这里做到的是<b>不落盘</b>:不像同会话复制那样先整份下载到临时文件再上传,
/// 两段是同时进行的,耗时约等于较慢那一段,而不是两段之和。
/// </para>
/// <para>
/// 源与目标可以是不同的协议(SFTP、FTP、插件的文件协议两两组合),两端都只经 <see cref="ISftpService" />。
/// </para>
/// <para>
/// 支持断点续传,口径与上传相同:目标上前一次留下的半截,核实尾部与源一致后从那里接着写。
/// 核实要在源里来回定位,所以只有源可 Seek(SFTP)时才续;源回不了头时核实不了,按「对不上」交回同名冲突策略,
/// 而不是悄悄整份重传(那会不经询问覆盖一个可能与此无关的同名文件)。
/// </para>
/// </remarks>
public static class RemoteRelay
{
    /// <summary>把源会话上的一个文件搬到目标会话的给定路径(覆盖)。</summary>
    /// <param name="source">源端文件服务。</param>
    /// <param name="sourceSessionId">源会话。</param>
    /// <param name="sourcePath">源文件的远端路径。</param>
    /// <param name="target">目标端文件服务。</param>
    /// <param name="targetSessionId">目标会话。</param>
    /// <param name="targetPath">目标文件的远端路径。</param>
    /// <param name="progress">进度回报(以目标端写出的字节计)。</param>
    /// <param name="writing">
    /// 源端已经打开、即将开始写目标时回调一次。调用方据此区分「源都没打开就失败了」
    /// (目标原封未动,绝不能当半截文件清掉)与「写到一半失败了」。
    /// </param>
    /// <param name="resumeOffset">
    /// &gt; 0 表示目标已有前一次留下的半截,试着从那里接着传(真正的起点由目标端按此刻的状态核实,
    /// 见 <see cref="ISftpService.UploadStreamAsync" />)。源流不可 Seek(FTP 的数据流、多数插件协议)时核实不了,
    /// 抛 <see cref="VelaSftpResumeMismatchException" />,目标未被碰过。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task CopyFileAsync(
        ISftpService source,
        Guid sourceSessionId,
        string sourcePath,
        ISftpService target,
        Guid targetSessionId,
        string targetPath,
        IProgress<TransferProgress>? progress = null,
        Action? writing = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        // 先 stat:进度要一个分母,「保留时间戳」要源文件的修改时间;顺带把"源已经不在了"
        // 挡在打开流之前,报出来的是那条路径找不到,而不是一条读到一半断掉的流。
        RemoteFileInfo info = await source.GetFileInfoAsync(sourceSessionId, sourcePath, cancellationToken).ConfigureAwait(false);
        await using Stream stream = await source.OpenReadAsync(sourceSessionId, sourcePath, cancellationToken).ConfigureAwait(false);
        if (resumeOffset > 0 && !stream.CanSeek)
        {
            // 续传点是按「目标比源短」探出来的,那个短文件是不是上一次的半截,得回到源里比对尾部才知道 ——
            // 源回不了头(FTP 的数据连接、多数插件协议)就核实不了。悄悄整份重传等于不经询问覆盖一个
            // 可能与此无关的同名文件,所以按「对不上」报:调用方会把它交回同名冲突策略。
            // 此时目标还没被碰过(writing 没有回调)。
            throw new VelaSftpResumeMismatchException(Strings.Format("SftpSvc_ResumeUnverifiable", targetPath));
        }
        writing?.Invoke();
        await target.UploadStreamAsync(
            targetSessionId,
            stream,
            targetPath,
            Math.Max(0, info.Size),
            info.LastModified == default ? null : info.LastModified,
            progress,
            resumeOffset,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
