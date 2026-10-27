using System.Diagnostics.CodeAnalysis;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;

namespace VelaShell.ViewModels;

/// <summary>一批传输为什么停在这儿。</summary>
public enum PausedBatchReason
{
    /// <summary>本次运行里连接断了(或重试用完了),剩下的没开始传。</summary>
    Interrupted,

    /// <summary>上次退出时还没传完,启动时从传输队列里还原出来的。</summary>
    Restored,
}

/// <summary>点了「继续」之后的去向。</summary>
public enum TransferResumeResult
{
    /// <summary>已经交给一条连接接着传(这一行随之撤掉)。</summary>
    Started,

    /// <summary>发起它的连接配置已经删了:没有地方可连,只能放弃。</summary>
    ProfileMissing,

    /// <summary>没连上 / 用户取消了连接:原样留着,稍后可以再点。</summary>
    NotConnected,

    /// <summary>连上了,但接着传之前就出错了。</summary>
    Failed,
}

/// <summary>「继续」的结局与(失败时)原因。</summary>
/// <param name="Result">去向。</param>
/// <param name="Message">失败原因(给用户看的那一句);其余去向为 null。</param>
public sealed record TransferResumeOutcome(TransferResumeResult Result, string? Message = null);

/// <summary>
/// 传输浮窗里的一行「已暂停的批次」:还没传完的那几个文件、来自哪台机器、停下的原因,带「继续」与「放弃」。
/// </summary>
/// <remarks>
/// 一批只占一行,而不是剩几个文件就几行:断线停下的是一整个文件夹,剩下七百个文件各占一行,
/// 面板会被撑满,真正在传的行反而被挤到下面去。
/// </remarks>
public sealed class PausedTransferBatchViewModel : ReactiveObject
{
    private bool _isResuming;
    private bool _profileMissing;

    /// <summary>以队列里的一批与停下的原因构造。</summary>
    public PausedTransferBatchViewModel(PausedTransferBatch batch, PausedBatchReason reason)
    {
        Batch = batch ?? throw new ArgumentNullException(nameof(batch));
        Reason = reason;
    }

    /// <summary>队列里的批次 Id。</summary>
    public Guid BatchId => Batch.BatchId;

    /// <summary>此刻还没传完的项。</summary>
    public PausedTransferBatch Batch { get; private set; }

    /// <summary>停下的原因。</summary>
    public PausedBatchReason Reason { get; }

    /// <summary>剩几个文件。</summary>
    public int RemainingCount => Batch.Items.Count;

    /// <summary>只剩一个文件时就是它的名字,否则是「N 个文件」。</summary>
    public string Title => Batch.Items.Count == 1
        ? Path.GetFileName(Batch.Items[0].Type == TransferType.Upload ? Batch.Items[0].LocalPath : Batch.Items[0].RemotePath)
        : Strings.Format("Transfer_PausedFiles", Batch.Items.Count);

    /// <summary>方向:全是上传 "↑",全是下载 "↓",混着的 "⇅"。</summary>
    public string Direction =>
        Batch.Items.All(static i => i.Type == TransferType.Upload) ? "↑"
        : Batch.Items.All(static i => i.Type == TransferType.Download) ? "↓"
        : "⇅";

    /// <summary>第二行:方向、服务器、此刻的状态。</summary>
    public string Detail => $"{Direction} {Batch.Origin.ServerName}  •  {StatusText}";

    /// <summary>此刻的状态说明。</summary>
    [AllowNull]
    public string StatusText
    {
        get =>
        _isResuming ? Strings.Get("Transfer_PausedResuming")
        : _profileMissing ? Strings.Get("Transfer_PausedProfileMissing")
        : field is { } error ? Strings.Format("Transfer_PausedResumeFailed", error)
        : Reason == PausedBatchReason.Restored ? Strings.Get("Transfer_PausedRestored")
        : Strings.Get("Transfer_PausedInterrupted"); private set;
    }

    /// <summary>「继续」可不可点:正在连的时候不行,配置没了也不行(那只能放弃)。</summary>
    public bool CanResume => !_isResuming && !_profileMissing;

    /// <summary>正在为它连接 / 交接。</summary>
    public bool IsResuming
    {
        get => _isResuming;
        set
        {
            this.RaiseAndSetIfChanged(ref _isResuming, value);
            if (value)
            {
                StatusText = null;
            }
            RaiseStatus();
        }
    }

    /// <summary>把「继续」的结局落到这一行上(交接成功的那种,行已经被撤掉,不会走到这里)。</summary>
    public void ApplyOutcome(TransferResumeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _isResuming = false;
        _profileMissing = outcome.Result == TransferResumeResult.ProfileMissing;
        StatusText = outcome.Result == TransferResumeResult.Failed ? outcome.Message : null;
        this.RaisePropertyChanged(nameof(IsResuming));
        RaiseStatus();
    }

    /// <summary>剩下的项变了(另一部分已经交出去之类):换成新的快照。</summary>
    public void Refresh(PausedTransferBatch batch)
    {
        Batch = batch ?? throw new ArgumentNullException(nameof(batch));
        this.RaisePropertyChanged(nameof(Batch));
        this.RaisePropertyChanged(nameof(RemainingCount));
        this.RaisePropertyChanged(nameof(Title));
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(Detail));
        this.RaisePropertyChanged(nameof(CanResume));
    }
}
