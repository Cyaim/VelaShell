using System.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Signals;
using VelaShell.Core.DirectorySync;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.PluginSdk.Protocols;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>
/// 双栏远程文档里一栏所连的那台机器:已经连上的会话、连它所用的配置,以及关文档时断开它的回调。
/// </summary>
/// <param name="Profile">连接所用的配置(登录弹窗可能改过其中的字段)。</param>
/// <param name="SessionId">已建立的会话标识。</param>
/// <param name="Session">SSH 会话;FTP 与插件协议没有长驻的会话对象,为 null。</param>
/// <param name="DisconnectAsync">关文档时断开该会话的回调。</param>
public sealed record DualSftpEndpoint(
    SessionProfile Profile,
    Guid SessionId,
    SshSession? Session,
    Func<Guid, CancellationToken, Task> DisconnectAsync)
{
    /// <summary>插件协议(S3、WebDAV…)的描述:右键动作与标签图标从这里来;内建协议为 null。</summary>
    public ProtocolDescriptor? Protocol { get; init; }

    /// <summary>
    /// 这一栏能不能接收从另一栏流式搬来的文件。内建协议恒能;插件协议看它有没有兼实现
    /// <c>IProtocolStreamUpload</c> —— 不能的那一栏仍可作为源(流式读每个插件都有)。
    /// </summary>
    public bool AcceptsStreamedUploads { get; init; } = true;

    /// <summary>执行插件协议的右键动作;协议没声明动作时为 null。</summary>
    public Func<string, string, Task>? InvokeProtocolAction { get; init; }
}

/// <summary>
/// 「远程 + 远程」双栏文件标签:两栏各连一台机器,文件经本机中转(FTP 源先完整缓存),
/// 目录比较两边都在远端。由资源管理器里 Ctrl 选中两条连接后的「在双栏 SFTP 中打开」建出。
/// </summary>
/// <remarks>
/// <para>
/// <b>两条连接都归本文档所有</b>:打开时各自新建,关闭时一起断开,与「本地 + 远程」文档的单条连接同一口径。
/// 不去复用别的标签已经开着的会话 —— 那样别的标签一关,这里的一栏就跟着失效了。
/// </para>
/// <para>
/// 同一台机器内部的复制不走这里(两栏不允许是同一条配置):那用单栏文件浏览器的「复制到」即可。
/// </para>
/// </remarks>
public sealed class DualSftpDocumentViewModel : ReactiveObject, ISftpDocumentContent, IAsyncDisposable
{
    private readonly DualSftpEndpoint _left;
    private readonly DualSftpEndpoint _right;
    private readonly SerializedSftpService _leftSftp;
    private readonly SerializedSftpService _rightSftp;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _closeSync = new();
    private Task? _closeTask;

    /// <summary>用两条已经连上的会话建出双栏远程文档,并开始加载两栏的目录。</summary>
    /// <param name="left">左栏的会话(资源管理器里先选中的那一条)。</param>
    /// <param name="right">右栏的会话。</param>
    /// <param name="sftpService">底层远程文件服务,两栏各包一层本文档独占的串行化视图。</param>
    /// <param name="transferOptions">设置 → 文件传输 的选项(打开时那一份;之后设置保存时宿主经 <see cref="ISftpDocumentContent.RemotePanes" /> 换上新的)。</param>
    /// <param name="transferSink">承载本文档所发起传输的浮动传输组件;为 null 时不上报进度。</param>
    /// <param name="getDefaultEditorPath">解析「默认编辑器」的回调(理由见 <see cref="SftpDocumentViewModel" />)。</param>
    /// <exception cref="ArgumentException">两栏是同一条配置或同一个会话。</exception>
    public DualSftpDocumentViewModel(
        DualSftpEndpoint left,
        DualSftpEndpoint right,
        ISftpService sftpService,
        TransferOptions transferOptions,
        FileTransferViewModel? transferSink = null,
        Func<Task<string?>>? getDefaultEditorPath = null)
    {
        _left = left ?? throw new ArgumentNullException(nameof(left));
        _right = right ?? throw new ArgumentNullException(nameof(right));
        ArgumentNullException.ThrowIfNull(sftpService);
        ArgumentNullException.ThrowIfNull(transferOptions);
        if (left.SessionId == right.SessionId || left.Profile.Id == right.Profile.Id)
        {
            throw new ArgumentException("The two panes of a dual remote document must use two different connections.", nameof(right));
        }
        _leftSftp = new SerializedSftpService(sftpService, left.SessionId);
        _rightSftp = new SerializedSftpService(sftpService, right.SessionId);
        LeftFiles = CreatePane(_leftSftp, left, transferOptions, transferSink, getDefaultEditorPath);
        RightFiles = CreatePane(_rightSftp, right, transferOptions, transferSink, getDefaultEditorPath);
        LeftFiles.DualPeer = RightFiles;
        RightFiles.DualPeer = LeftFiles;
        Title = $"{LeftFiles.ServerDisplayName} ⇄ {RightFiles.ServerDisplayName}";

        // 接收不了流式上传的那一栏(插件没实现这一面),往它那边的按钮直接置灰,不让用户点了才知道。
        CopyLeftToRightCommand = ReactiveCommand.CreateFromTask(
            () => RightFiles.ReceiveFromPeerAsync([.. LeftFiles.SelectedFiles], _lifetime.Token),
            Signal.Emit(right.AcceptsStreamedUploads));
        CopyRightToLeftCommand = ReactiveCommand.CreateFromTask(
            () => LeftFiles.ReceiveFromPeerAsync([.. RightFiles.SelectedFiles], _lifetime.Token),
            Signal.Emit(left.AcceptsStreamedUploads));
        CompareDirectoriesCommand = ReactiveCommand.CreateFromTask(CompareDirectoriesAsync);

        LeftFiles.PropertyChanged += OnPanePropertyChanged;
        RightFiles.PropertyChanged += OnPanePropertyChanged;
        left.Session?.PropertyChanged += OnSessionPropertyChanged;
        right.Session?.PropertyChanged += OnSessionPropertyChanged;
        InitialLoadTask = LoadAsync();
    }

    /// <summary>左栏(资源管理器里先选中的那条连接)。</summary>
    public FileBrowserViewModel LeftFiles { get; }

    /// <summary>右栏。</summary>
    public FileBrowserViewModel RightFiles { get; }

    /// <summary>左栏所用的连接配置。</summary>
    public SessionProfile LeftProfile => _left.Profile;

    /// <summary>右栏所用的连接配置。</summary>
    public SessionProfile RightProfile => _right.Profile;

    /// <summary>左栏若是插件协议,插件自报的图标(标签页用);内建协议或没给图标时为 null。</summary>
    public PluginSdk.PluginIcon? LeftPluginIcon => _left.Protocol?.Icon;

    /// <inheritdoc />
    public string Title { get; }

    /// <inheritdoc />
    /// <remarks>
    /// 两条连接取较差的那一条:一栏掉线了,标签页上的灯就不该还是绿的。
    /// FTP 没有长驻的会话对象可读(与单栏文档同一处置),按「已连接」计。
    /// </remarks>
    public SessionStatus Status => Worse(_left.Session?.Status ?? SessionStatus.Connected, _right.Session?.Status ?? SessionStatus.Connected);

    /// <inheritdoc />
    public IReadOnlyList<Guid> SessionIds => [_left.SessionId, _right.SessionId];

    /// <inheritdoc />
    public IReadOnlyList<FileBrowserViewModel> RemotePanes => [LeftFiles, RightFiles];

    /// <summary>把左栏选中的条目搬到右栏当前目录。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyLeftToRightCommand { get; }

    /// <summary>把右栏选中的条目搬到左栏当前目录。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyRightToLeftCommand { get; }

    /// <summary>「比较目录」:把两栏当前目录里不同的条目在各自一栏里选中(不递归)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CompareDirectoriesCommand { get; }

    /// <summary>上次「比较目录」的结论;任一栏换了目录就清掉。</summary>
    public string? CompareSummary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    internal Task InitialLoadTask { get; }

    /// <summary>
    /// 比较两栏当前目录(一层,按大小与修改时间),选中各自一侧较新、独有或内容不同的条目,并给出一句结论。
    /// </summary>
    /// <remarks>
    /// 两边都是远端时间:任一边是 FTP(LIST 只到分钟)就按推断出的精度比,
    /// 否则一个恰好落在整分上的 SFTP 时间会被错当成「只精确到分钟」。
    /// 比较的是两栏<b>眼下看得见</b>的条目(已按各自的「显示隐藏文件」过滤),与界面所见一致。
    /// </remarks>
    public Task CompareDirectoriesAsync()
    {
        var options = new SyncOptions { Criteria = SyncCriteria.Time | SyncCriteria.Size };
        // 比较的是整个目录:任一栏的过滤框藏着行,结论就会把藏起来的那些说成「只有另一边有」。
        LeftFiles.FilterText = string.Empty;
        RightFiles.FilterText = string.Empty;
        RemoteFileInfoViewModel[] leftEntries = VisibleEntries(LeftFiles);
        RemoteFileInfoViewModel[] rightEntries = VisibleEntries(RightFiles);
        IReadOnlyList<SyncComparison> comparisons = DirectoryComparer.Compare(
            ToSyncItems(leftEntries, TimesMayBeCoarse(_left.Profile), SyncTime.ServerClockOffset(_left.Profile)),
            ToSyncItems(rightEntries, TimesMayBeCoarse(_right.Profile), SyncTime.ServerClockOffset(_right.Profile)),
            options);

        StringComparer comparer = options.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var leftMarked = new HashSet<string>(comparer);
        var rightMarked = new HashSet<string>(comparer);
        int identical = 0;
        foreach (SyncComparison comparison in comparisons)
        {
            switch (comparison.State)
            {
                case SyncComparisonState.LocalOnly or SyncComparisonState.LocalNewer:
                    leftMarked.Add(comparison.RelativePath);
                    break;
                case SyncComparisonState.RemoteOnly or SyncComparisonState.RemoteNewer:
                    rightMarked.Add(comparison.RelativePath);
                    break;
                case SyncComparisonState.Differs or SyncComparisonState.Conflict:
                    leftMarked.Add(comparison.RelativePath);
                    rightMarked.Add(comparison.RelativePath);
                    break;
                default:
                    identical++;
                    break;
            }
        }
        Select(LeftFiles, leftEntries, leftMarked);
        Select(RightFiles, rightEntries, rightMarked);
        CompareSummary = leftMarked.Count + rightMarked.Count == 0
            ? Strings.Format("Sync_CompareIdentical", identical)
            : Strings.Format("DualSftp_CompareSummary", leftMarked.Count, rightMarked.Count, identical);
        return Task.CompletedTask;
    }

    /// <summary>分离两栏并取消生命周期令牌。</summary>
    public void Detach()
    {
        _lifetime.Cancel();
        LeftFiles.PropertyChanged -= OnPanePropertyChanged;
        RightFiles.PropertyChanged -= OnPanePropertyChanged;
        _left.Session?.PropertyChanged -= OnSessionPropertyChanged;
        _right.Session?.PropertyChanged -= OnSessionPropertyChanged;
        LeftFiles.Detach();
        RightFiles.Detach();
    }

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_closeSync)
        {
            _closeTask ??= CloseCoreAsync();
            return cancellationToken.CanBeCanceled
                ? _closeTask.WaitAsync(cancellationToken)
                : _closeTask;
        }
    }

    /// <summary>异步释放:等同于关闭文档。</summary>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private static FileBrowserViewModel CreatePane(
        SerializedSftpService sftp,
        DualSftpEndpoint endpoint,
        TransferOptions transferOptions,
        FileTransferViewModel? transferSink,
        Func<Task<string?>>? getDefaultEditorPath)
    {
        SessionProfile profile = endpoint.Profile;
        string serverName = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
        var pane = new FileBrowserViewModel(sftp, endpoint.SessionId)
        {
            ServerDisplayName = serverName,
            // 往本机的上传 / 下载记进传输队列;两栏之间的中转不记(要两条连接同时在才能接着传)。
            QueueOrigin = new(profile.Id, serverName),
            // 两栏各带自己那台机器的标识色:两边都是远端,光看路径分不清哪栏是哪台。
            AccentBrush = ConnectionAccent.BrushForProfile(profile),
            TransferSink = transferSink,
            TransferOptions = transferOptions,
            IsVisible = true,
            IsDragEnabled = true,
            GetDefaultEditorPath = getDefaultEditorPath,
            InitialRemotePath = profile.Ftp?.InitialRemotePath,
            AcceptsStreamedUploads = endpoint.AcceptsStreamedUploads,
            UseStagedRelaySource = profile.ConnectionType == ConnectionType.FTP,
        };
        // 「显示隐藏文件」与列显示按设置铺好(与单栏文档同一口径,差距分析 A8);写回设置的回调由宿主建好之后再挂。
        pane.ApplyDisplayOptions(transferOptions);
        // 插件协议的右键动作(S3 的「复制分享链接」之类),与单栏文档同一种接法。
        if (endpoint.Protocol is { Actions.Count: > 0 } protocol && endpoint.InvokeProtocolAction is { } invoke)
        {
            pane.SetProtocolActions(protocol.DisplayName, protocol.Actions);
            pane.InvokeProtocolAction = invoke;
        }
        return pane;
    }

    private async Task LoadAsync()
    {
        // 两栏同时加载:两条连接互不相干,排队只会让后一栏白等。
        await Task.WhenAll(
            LeftFiles.LoadInitialAsync(_lifetime.Token),
            RightFiles.LoadInitialAsync(_lifetime.Token)).ConfigureAwait(false);
    }

    private async Task CloseCoreAsync()
    {
        Detach();

        // 远程编辑会话先收尾(理由同单栏文档:要赶在连接被关掉之前把没传完的传掉)。
        await RemoteEditSessionManager.CloseScopeAsync(_left.SessionId).ConfigureAwait(false);
        await RemoteEditSessionManager.CloseScopeAsync(_right.SessionId).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_leftSftp.CloseAsync(), _rightSftp.CloseAsync()).ConfigureAwait(false);
            try
            {
                await InitialLoadTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // 上面的 Detach 取消了还在飞的初始加载 —— 那是关闭自己造成的,不是错误。
            }
        }
        finally
        {
            // 两条都要断:一条断开失败不能让另一条留着没人关。
            try
            {
                await _left.DisconnectAsync(_left.SessionId, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await _right.DisconnectAsync(_right.SessionId, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileBrowserViewModel.CurrentPath))
        {
            CompareSummary = null;
        }
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SshSession.Status) or null)
        {
            this.RaisePropertyChanged(nameof(Status));
        }
    }

    /// <summary>两个状态里较差的那个:错误 &gt; 断开 &gt; 连接中 &gt; 已连接。</summary>
    internal static SessionStatus Worse(SessionStatus a, SessionStatus b) =>
        Severity(a) >= Severity(b) ? a : b;

    private static int Severity(SessionStatus status) => status switch
    {
        SessionStatus.Error => 3,
        SessionStatus.Disconnected => 2,
        SessionStatus.Connecting => 1,
        _ => 0,
    };

    /// <summary>远端时间是否可能粗于秒(理由见 <see cref="SftpDocumentViewModel" /> 的同名判断)。</summary>
    private static bool TimesMayBeCoarse(SessionProfile profile) =>
        profile.ConnectionType is not (ConnectionType.SFTP or ConnectionType.SSH);

    private static RemoteFileInfoViewModel[] VisibleEntries(FileBrowserViewModel pane) =>
        [.. pane.Files.Where(static f => !f.IsParentEntry && !f.IsDirectoryLink)];

    /// <param name="entries">一栏的条目。</param>
    /// <param name="coarse">时间是否可能粗于秒。</param>
    /// <param name="clock">这一栏服务器时钟的 UTC 偏移(FTP 配了服务器时区时);null = 与本机同一时区。</param>
    private static IReadOnlyList<SyncItem> ToSyncItems(IEnumerable<RemoteFileInfoViewModel> entries, bool coarse, TimeSpan? clock) =>
        [.. entries.Select(f => new SyncItem(
            f.Name, f.FullPath, f.IsDirectory, f.IsDirectory ? 0 : f.SizeBytes, SyncTime.ToUtc(f.LastModified, clock),
            coarse ? SyncTime.InferPrecision(f.LastModified) : SyncTimePrecision.Second)
        {
            CalendarOffset = SyncTime.CalendarOffsetFor(f.LastModified, clock),
        })];

    private static void Select(FileBrowserViewModel pane, IEnumerable<RemoteFileInfoViewModel> entries, HashSet<string> names)
    {
        pane.SelectedFiles.Clear();
        foreach (RemoteFileInfoViewModel entry in entries.Where(f => names.Contains(f.Name)))
        {
            pane.SelectedFiles.Add(entry);
        }
    }
}
