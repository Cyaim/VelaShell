using System.ComponentModel;
using VelaShell.Core.Models;

namespace VelaShell.ViewModels;

/// <summary>
/// 独立文件标签(本地 + 远程的双栏 SFTP,或两台远程并排的双栏)的视图模型共有的那一面:
/// 标签页要画的标题与状态灯,以及关标签时宿主要做的收尾。
/// </summary>
/// <remarks>
/// 停靠层、标签页与退出流程只认这一面,于是「本地 + 远程」与「远程 + 远程」两种文档
/// 共用同一条关闭收口与同一个标签页外观,而不必在宿主里按类型各写一遍。
/// </remarks>
public interface ISftpDocumentContent : INotifyPropertyChanged
{
    /// <summary>标签页标题。</summary>
    string Title { get; }

    /// <summary>标签页状态灯要画的连接状态;多条连接时取最差的那一条。</summary>
    SessionStatus Status { get; }

    /// <summary>
    /// 本文档持有的全部会话。关标签后宿主逐个把它们从资源管理器的状态册子上摘掉 ——
    /// 漏一个,那条配置在树上的圆点就会一直亮着。
    /// </summary>
    IReadOnlyList<Guid> SessionIds { get; }

    /// <summary>
    /// 本文档里的远端文件栏(单栏文档一栏,双栏远程文档两栏)。设置保存后宿主要把新的传输选项铺到它们身上 ——
    /// 漏了的话,改了冲突策略、并发、重试次数,开着的文件标签还按打开那一刻的设置走。
    /// </summary>
    IReadOnlyList<FileBrowserViewModel> RemotePanes { get; }

    /// <summary>关闭文档:停掉在途工作、断开它持有的全部连接;只执行一次。</summary>
    /// <param name="cancellationToken">只取消调用方的等待,不中断关闭本身。</param>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
