using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Media;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Services;
using VelaShell.Views;

namespace VelaShell.Docking;

/// <summary>
/// 文档型连接(独立 SFTP、FTP、S3/WebDAV 等插件协议、Redis 等插件工作台)在
/// **握手完成之前**占位的标签页。
/// <para>
/// 为什么需要它:这四条路径原先都是先把会话连上、拿到 <c>sessionId</c> 才
/// <c>Layout.AddDocument</c>。链路一慢,用户点完之后屏幕上**什么都不会发生** ——
/// 没有标签、没有圆点、右下角也没有转圈,看起来就是「点了没反应」(#385 反馈)。
/// 终端标签早就是"先建标签、后握手"(见 <c>CreateConnectingTab</c>),这里把文档型
/// 标签也拉齐到同一条纪律上:点击那一刻标签就在,连上之后由
/// <see cref="DockWorkspace.ReplaceDocument" /> 原位换成真文档。
/// </para>
/// </summary>
public sealed class ConnectingDocument : DockDocument, IDockViewProvider
{
    /// <summary>建一个「连接中」占位标签。</summary>
    /// <param name="profile">正在连接的配置。</param>
    /// <param name="typeLabel">连接类型的展示名(SFTP / FTP / S3 / Redis…),写进副标题与提示。</param>
    /// <param name="displayName">
    /// 标签与覆盖层上的名称;不给则取配置的显示名。双栏远程文档一次连两台,写成「A ⇄ B」。
    /// </param>
    public ConnectingDocument(SessionProfile profile, string typeLabel, string? displayName = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        TypeLabel = typeLabel;
        DisplayName = displayName;
        // 占位标签的 Id 不能取会话 id —— 那玩意儿正是还没有的东西。用一个进程内唯一值,
        // 与真文档换手之后它就随占位一起消失了。
        Id = $"connecting-{Guid.NewGuid():N}";
        Title = DisplayName;
        IsSessionDocument = true;
    }

    /// <summary>正在连接的配置。</summary>
    public SessionProfile Profile { get; }

    /// <summary>
    /// 「远程 + 远程」双栏一次连两台:右栏那条配置(<see cref="Profile" /> 是左栏);其余连接为 null。
    /// </summary>
    /// <remarks>
    /// 只给布局模板用:用户在「连接中」或失败卡片还挂着的时候存模板,这个标签也该按它将来的样子
    /// (一个双栏文件标签)记下来,而不是只剩左栏那一台。
    /// </remarks>
    public SessionProfile? SecondaryProfile { get; init; }

    /// <summary>
    /// 连接类型的展示名(SFTP / FTP / S3 / Redis…)。
    /// </summary>
    /// <remarks>
    /// 可写:插件协议要先把插件惰性激活起来才问得到这个名字,而占位标签必须在那之前
    /// 就出现(激活本身就可能是这条路上最慢的一步)。先挂协议 id,问到了再换成展示名。
    /// </remarks>
    public string TypeLabel
    {
        get;
        set
        {
            SetField(ref field, value);
            OnPropertyChanged(nameof(ConnectionTooltip));
        }
    }

    /// <summary>
    /// 标签与覆盖层里指代这条连接的名称:建标签时给了就用它,否则优先用配置的显示名,没起名才退回主机。
    /// </summary>
    [AllowNull]
    public string DisplayName =>
        field ?? (string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Host : Profile.Name);

    /// <summary>
    /// 标签上的状态圆点。与终端 / SFTP / 工作台标签共用一套 <see cref="SessionStatus" />,
    /// 于是四种标签的黄(连接中)与红(失败)是同一种语言。
    /// </summary>
    public SessionStatus Status
    {
        get;
        private set
        {
            SetField(ref field, value);
            OnPropertyChanged(nameof(IsConnecting));
            OnPropertyChanged(nameof(HasError));
        }
    } = SessionStatus.Connecting;

    /// <summary>是否仍在连接(决定覆盖层显示转圈还是失败卡片)。</summary>
    public bool IsConnecting => Status == SessionStatus.Connecting;

    /// <summary>是否已失败。</summary>
    public bool HasError => Status == SessionStatus.Error;

    /// <summary>失败原因;<see cref="HasError" /> 为真时才有意义。</summary>
    public string? ErrorMessage
    {
        get;
        private set => SetField(ref field, value);
    }

    /// <summary>覆盖层标题:连接中为「正在连接 X」,失败后为「连接失败」。</summary>
    public string OverlayTitle =>
        HasError
            ? Strings.Get("Msg_ConnectionFailedTitle")
            : Strings.Format("Msg_ConnectingToTitle", DisplayName);

    /// <summary>覆盖层详情:连接中给一句"正在建立会话",失败后给具体原因。</summary>
    public string OverlayDetail =>
        HasError ? ErrorMessage ?? string.Empty : Strings.Get("Msg_ConnectingDetail");

    /// <summary>从连接配置派生的强调色画刷,与真文档标签上的那条色带同源。</summary>
    public IBrush ConnectionAccentBrush => ConnectionAccent.BrushForProfile(Profile);

    /// <summary>标签悬停提示。</summary>
    public string ConnectionTooltip => $"{DisplayName} · {TypeLabel} · {Strings.Connecting}";

    /// <summary>
    /// 用户点了「取消」:撤销这次连接。由宿主接上(取消令牌 + 撤标签),
    /// 视图层只管把意图发出来。
    /// </summary>
    public Action? CancelRequested { get; set; }

    /// <summary>用户点了「重试」:重新走一遍这条连接流程。由宿主接上。</summary>
    public Action? RetryRequested { get; set; }

    /// <summary>取消这次连接(覆盖层的「取消」按钮)。</summary>
    public void Cancel() => CancelRequested?.Invoke();

    /// <summary>重试这次连接(失败卡片的「重试」按钮)。</summary>
    public void Retry() => RetryRequested?.Invoke();

    /// <summary>
    /// 上一次败在算法协商上、而对端有本版实现了只是没放开的算法:失败卡片上的「放开这几个算法并重连」按它来。
    /// 每次转入失败态时一并换掉(没有就是 <see langword="null" />),重试时清掉。
    /// </summary>
    public SshAlgorithmRemedy? AlgorithmRemedy
    {
        get;
        private set
        {
            SetField(ref field, value);
            OnPropertyChanged(nameof(HasAlgorithmRemedy));
            OnPropertyChanged(nameof(AlgorithmRemedyTip));
        }
    }

    /// <summary>失败卡片上要不要出「放开这几个算法并重连」。</summary>
    public bool HasAlgorithmRemedy => AlgorithmRemedy is not null;

    /// <summary>那颗按钮的悬停说明:往哪一类清单里加哪几个。</summary>
    public string AlgorithmRemedyTip => AlgorithmRemedy is { } remedy
        ? SshAlgorithmRemedyFlow.ButtonTip(remedy, SshAlgorithmRemedyFollowUp.Reconnect)
        : string.Empty;

    /// <summary>用户点了「放开这几个算法并重连」:由宿主确认、改配置、重试。</summary>
    public Action? AllowAlgorithmsRequested { get; set; }

    /// <summary>放开这几个算法并重连(失败卡片上的那颗按钮)。</summary>
    public void AllowAlgorithms() => AllowAlgorithmsRequested?.Invoke();

    /// <summary>转入失败态:标签圆点变红,覆盖层换成失败卡片。</summary>
    /// <param name="message">面向用户的失败原因。</param>
    /// <param name="remedy">算法协商失败、且能一键放开对端那几个算法时给;失败卡片据此多一颗按钮。</param>
    public void MarkFailed(string message, SshAlgorithmRemedy? remedy = null)
    {
        ErrorMessage = message;
        AlgorithmRemedy = remedy;
        Status = SessionStatus.Error;
        RaiseOverlayChanged();
    }

    /// <summary>转回连接中(重试时)。</summary>
    public void MarkConnecting()
    {
        ErrorMessage = null;
        AlgorithmRemedy = null;
        Status = SessionStatus.Connecting;
        RaiseOverlayChanged();
    }

    /// <inheritdoc />
    public Control CreateView() => new ConnectingDocumentView { DataContext = this };

    private void RaiseOverlayChanged()
    {
        OnPropertyChanged(nameof(OverlayTitle));
        OnPropertyChanged(nameof(OverlayDetail));
    }
}
