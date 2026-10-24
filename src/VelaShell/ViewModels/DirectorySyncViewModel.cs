using System.Collections.ObjectModel;
using System.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.DirectorySync;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;

namespace VelaShell.ViewModels;

/// <summary>
/// 同步对话框上次用过的选项。挂在 SFTP 文档上:同一个标签里第二次打开同步窗口时沿用,
/// 不写进设置(路径每次都取两栏当前所在的目录,选项跟着连接走才有意义)。
/// </summary>
public sealed class DirectorySyncSettings
{
    /// <summary>方向。</summary>
    public SyncDirection Direction { get; set; } = SyncDirection.ToRemote;

    /// <summary>模式。</summary>
    public SyncMode Mode { get; set; } = SyncMode.Synchronize;

    /// <summary>比修改时间。</summary>
    public bool CompareByTime { get; set; } = true;

    /// <summary>比文件大小。</summary>
    public bool CompareBySize { get; set; } = true;

    /// <summary>先按 SHA-256 比较,算不出来再回退到大小与修改时间。默认开:内容相同就不该因为时间变了而重传。</summary>
    public bool CompareByChecksum { get; set; } = true;

    /// <summary>删除多余文件。</summary>
    public bool DeleteExtraneous { get; set; }

    /// <summary>仅已存在的文件。</summary>
    public bool ExistingOnly { get; set; }

    /// <summary>文件掩码。</summary>
    public string FileMask { get; set; } = string.Empty;

    /// <summary>由三个勾选框组合出的比较依据。</summary>
    public SyncCriteria Criteria =>
        (CompareByTime ? SyncCriteria.Time : SyncCriteria.None)
        | (CompareBySize ? SyncCriteria.Size : SyncCriteria.None)
        | (CompareByChecksum ? SyncCriteria.Checksum : SyncCriteria.None);
}

/// <summary>
/// 目录同步窗口:选方向与模式 → 比较 → 在预览里勾掉不想要的 → 同步;或者「保持远端目录最新」。
/// 对标 WinSCP 的「同步」与「保持远程目录最新」两个对话框。
/// </summary>
public sealed class DirectorySyncViewModel : ReactiveObject, IDisposable
{
    /// <summary>日志最多保留的行数;再多既看不过来,也是一块只增不减的内存。</summary>
    private const int MaxLogLines = 500;

    /// <summary>预览里最多列出的错误行数,其余合成一句「还有 N 条」。</summary>
    private const int MaxErrorLines = 5;

    /// <summary>
    /// 保持最新的防抖窗口。编辑器保存一次往往连着打出删除、创建、改名、改写四五个事件,
    /// git checkout 一次上百个;一秒足够把它们并成一次同步,又不至于让人觉得"没反应"。
    /// </summary>
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromSeconds(1);

    private readonly ISftpService _sftp;
    private readonly Guid _sessionId;
    private readonly DirectorySyncRunner _runner;
    private readonly DirectorySyncSettings _settings;
    private readonly bool _inferRemotePrecision;
    private readonly ObservableCollection<SyncActionItemViewModel> _items = [];

    private CancellationTokenSource? _operationCts;

    // —— 保持远端目录最新 ——
    private readonly Lock _watchLock = new();
    private readonly Dictionary<string, bool> _pendingScopes = [with(StringComparer.Ordinal)];
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private CancellationTokenSource? _watchCts;
    private IDisposable? _watcher;
    private Timer? _watchTimer;
    private volatile bool _flushAgain;
    private SyncOptions? _watchOptions;
    private SyncFileMask _watchMask = SyncFileMask.Empty;
    private string _watchLocalRoot = string.Empty;
    private string _watchRemoteRoot = string.Empty;

    private bool _timestampsUnsupported;
    private bool _checksumFailureLogged;
    private bool _disposed;

    /// <summary>
    /// 状态文字的代次。<see cref="Progress{T}" /> 的回调是异步投递的,扫描或执行结束之后才到的那几条
    /// 「正在扫描…」「正在同步 2/3…」会把刚写上去的结论覆盖掉;每写一次结论就进一代,旧代的进度回调一律作废。
    /// </summary>
    private int _statusEpoch;

    /// <summary>创建同步窗口的视图模型。</summary>
    /// <param name="sftp">远端文件服务(文档独占的串行化视图)。</param>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="remotePane">远端文件面板:传输走它的管道。</param>
    /// <param name="settings">上次用过的选项,关闭时写回。</param>
    /// <param name="localPath">初始本地目录(本地栏当前目录)。</param>
    /// <param name="remotePath">初始远端目录(远端栏当前目录)。</param>
    /// <param name="inferRemotePrecision">远端时间是否可能粗于秒(FTP / 插件协议为 true)。</param>
    /// <param name="serverName">窗口标题里的服务器名。</param>
    public DirectorySyncViewModel(
        ISftpService sftp,
        Guid sessionId,
        FileBrowserViewModel remotePane,
        DirectorySyncSettings settings,
        string localPath,
        string remotePath,
        bool inferRemotePrecision,
        string serverName)
    {
        _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));
        _sessionId = sessionId;
        _runner = new(sftp, sessionId, remotePane ?? throw new ArgumentNullException(nameof(remotePane)));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _inferRemotePrecision = inferRemotePrecision;
        ServerName = serverName;
        Items = new(_items);
        WatchLog = [];
        LocalPath = localPath;
        RemotePath = remotePath;
        Direction = settings.Direction;
        Mode = settings.Mode;
        CompareByTime = settings.CompareByTime;
        CompareBySize = settings.CompareBySize;
        CompareByChecksum = settings.CompareByChecksum;
        DeleteExtraneous = settings.DeleteExtraneous;
        ExistingOnly = settings.ExistingOnly;
        FileMask = settings.FileMask;

        SetDirectionCommand = ReactiveCommand.Create<string>(value =>
        {
            if (CanEditOptions && Enum.TryParse(value, out SyncDirection direction))
            {
                Direction = direction;
            }
        });
        SetModeCommand = ReactiveCommand.Create<string>(value =>
        {
            if (CanEditOptions && CanChooseMode && Enum.TryParse(value, out SyncMode mode))
            {
                Mode = mode;
            }
        });
        CompareCommand = ReactiveCommand.CreateFromTask(CompareAsync);
        SynchronizeCommand = ReactiveCommand.CreateFromTask(SynchronizeAsync);
        CancelCommand = ReactiveCommand.Create(() => _operationCts?.Cancel());
        CheckAllCommand = ReactiveCommand.Create(() => SetAllChecked(true));
        UncheckAllCommand = ReactiveCommand.Create(() => SetAllChecked(false));
        BrowseLocalCommand = ReactiveCommand.CreateFromTask(BrowseLocalAsync);
        ToggleWatchCommand = ReactiveCommand.CreateFromTask(ToggleWatchAsync);
    }

    /// <summary>窗口关闭或文档关闭导致本对象被释放时触发;窗口据此自行关闭。</summary>
    public event EventHandler? Disposed;

    /// <summary>服务器名(窗口标题)。</summary>
    public string ServerName { get; }

    /// <summary>
    /// 服务器时钟的 UTC 偏移(FTP 连接配置里的「服务器时区」);null = 与本机同一时区。
    /// 扫远端时按它换算 LIST 的时间,见 <see cref="SyncTime.ServerClockOffset" />。来自连接配置,不是同步选项 —— 同步选项刻意不持久化。
    /// </summary>
    public TimeSpan? RemoteClockOffset { get; init; }

    /// <summary>预览行。</summary>
    public ReadOnlyObservableCollection<SyncActionItemViewModel> Items { get; }

    /// <summary>「保持远端目录最新」的日志,最新的在最上面。</summary>
    public ObservableCollection<string> WatchLog { get; }

    // ———————————————————————— 选项 ————————————————————————

    /// <summary>本地目录。</summary>
    public string LocalPath
    {
        get;
        set => SetOption(ref field, value ?? string.Empty);
    } = string.Empty;

    /// <summary>远端目录。</summary>
    public string RemotePath
    {
        get;
        set => SetOption(ref field, value ?? string.Empty);
    } = string.Empty;

    /// <summary>方向。切到双向时模式回到「同步文件」:双向没有源,镜像与时间戳无从谈起。</summary>
    public SyncDirection Direction
    {
        get;
        set
        {
            if (!SetOption(ref field, value))
            {
                return;
            }
            if (value == SyncDirection.Both)
            {
                Mode = SyncMode.Synchronize;
            }
            this.RaisePropertyChanged(nameof(IsDirectionBoth));
            this.RaisePropertyChanged(nameof(IsDirectionToRemote));
            this.RaisePropertyChanged(nameof(IsDirectionToLocal));
            this.RaisePropertyChanged(nameof(CanChooseMode));
            this.RaisePropertyChanged(nameof(CanDeleteExtraneous));
        }
    }

    /// <summary>模式。</summary>
    public SyncMode Mode
    {
        get;
        set
        {
            if (!SetOption(ref field, value))
            {
                return;
            }
            this.RaisePropertyChanged(nameof(IsModeSynchronize));
            this.RaisePropertyChanged(nameof(IsModeMirror));
            this.RaisePropertyChanged(nameof(IsModeTimestamps));
            this.RaisePropertyChanged(nameof(CanDeleteExtraneous));
            this.RaisePropertyChanged(nameof(CanChooseCriteria));
        }
    }

    /// <summary>比修改时间。</summary>
    public bool CompareByTime
    {
        get;
        set => SetOption(ref field, value);
    }

    /// <summary>比文件大小。</summary>
    public bool CompareBySize
    {
        get;
        set => SetOption(ref field, value);
    }

    /// <summary>先按 SHA-256 比较(不支持或出错时回退到大小与修改时间)。</summary>
    public bool CompareByChecksum
    {
        get;
        set => SetOption(ref field, value);
    }

    /// <summary>
    /// 摘要缓存。由文档传入,与「比较目录」共用:同步完的自动复查、保持最新的每一轮,没变过的文件不再整份重读。
    /// </summary>
    internal SyncChecksumCache ChecksumCache { get; init; } = new();

    /// <summary>删除多余文件。</summary>
    public bool DeleteExtraneous
    {
        get;
        set => SetOption(ref field, value);
    }

    /// <summary>仅已存在的文件。</summary>
    public bool ExistingOnly
    {
        get;
        set => SetOption(ref field, value);
    }

    /// <summary>文件掩码。</summary>
    public string FileMask
    {
        get;
        set => SetOption(ref field, value ?? string.Empty);
    } = string.Empty;

    /// <summary>方向为双向。</summary>
    public bool IsDirectionBoth => Direction == SyncDirection.Both;

    /// <summary>方向为本地 → 远端。</summary>
    public bool IsDirectionToRemote => Direction == SyncDirection.ToRemote;

    /// <summary>方向为远端 → 本地。</summary>
    public bool IsDirectionToLocal => Direction == SyncDirection.ToLocal;

    /// <summary>模式为同步文件。</summary>
    public bool IsModeSynchronize => Mode == SyncMode.Synchronize;

    /// <summary>模式为镜像文件。</summary>
    public bool IsModeMirror => Mode == SyncMode.Mirror;

    /// <summary>模式为仅同步时间戳。</summary>
    public bool IsModeTimestamps => Mode == SyncMode.Timestamps;

    /// <summary>模式可选(双向时不可选)。</summary>
    public bool CanChooseMode => !IsDirectionBoth;

    /// <summary>「删除多余文件」可选(双向与时间戳模式不删除)。</summary>
    public bool CanDeleteExtraneous => !IsDirectionBoth && !IsModeTimestamps;

    /// <summary>比较依据可选(时间戳模式只看时间)。</summary>
    public bool CanChooseCriteria => !IsModeTimestamps;

    // ———————————————————————— 状态 ————————————————————————

    /// <summary>正在扫描两边。</summary>
    public bool IsScanning
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseStateChanged();
        }
    }

    /// <summary>正在执行同步。</summary>
    public bool IsRunning
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseStateChanged();
        }
    }

    /// <summary>正在保持远端目录最新。</summary>
    public bool IsWatching
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseStateChanged();
        }
    }

    /// <summary>扫描或执行中。</summary>
    public bool IsBusy => IsScanning || IsRunning;

    /// <summary>选项可编辑。</summary>
    public bool CanEditOptions => !IsBusy && !IsWatching;

    /// <summary>「比较」可点。</summary>
    public bool CanCompare => CanEditOptions;

    /// <summary>「同步」可点:有勾选的步骤且空闲。</summary>
    public bool CanSynchronize => CanEditOptions && _items.Any(static i => i.IsChecked);

    /// <summary>有预览行。</summary>
    public bool HasItems => _items.Count > 0;

    /// <summary>显示日志而不是预览。</summary>
    public bool ShowWatchLog => IsWatching || (WatchLog.Count > 0 && !HasItems);

    /// <summary>显示「先点比较」的空状态提示。</summary>
    public bool ShowEmptyHint => !HasItems && !ShowWatchLog && !IsBusy;

    /// <summary>底部状态文字。</summary>
    public string? StatusText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>错误文字。</summary>
    public string? ErrorText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>附注:未处理的冲突、没进入的目录链接、服务器设不了修改时间。</summary>
    public string? NoteText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>已勾选步骤的汇总。</summary>
    public string? SummaryText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    // ———————————————————————— 命令与视图注入 ————————————————————————

    /// <summary>选方向(参数为 <see cref="SyncDirection" /> 的名字)。</summary>
    public ReactiveCommand<string, RxVoid> SetDirectionCommand { get; }

    /// <summary>选模式(参数为 <see cref="SyncMode" /> 的名字)。</summary>
    public ReactiveCommand<string, RxVoid> SetModeCommand { get; }

    /// <summary>比较两边并生成预览。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CompareCommand { get; }

    /// <summary>执行勾选的步骤。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SynchronizeCommand { get; }

    /// <summary>取消正在进行的比较或同步。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    /// <summary>全部勾选。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CheckAllCommand { get; }

    /// <summary>全部取消勾选。</summary>
    public ReactiveCommand<RxVoid, RxVoid> UncheckAllCommand { get; }

    /// <summary>选择本地目录。</summary>
    public ReactiveCommand<RxVoid, RxVoid> BrowseLocalCommand { get; }

    /// <summary>开始 / 停止保持远端目录最新。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleWatchCommand { get; }

    /// <summary>由视图设置:选择本地文件夹,返回路径或 null。</summary>
    public Func<Task<string?>>? PickLocalFolder { get; set; }

    /// <summary>由视图设置:危险操作的二次确认。</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>由文档设置:同步落定后刷新两栏。</summary>
    public Func<Task>? RefreshPanesAsync { get; set; }

    /// <summary>本地目录监视的工厂(测试替换):回调参数为变化的完整路径,以及它是否是新出现的条目。</summary>
    internal Func<string, Action<string, bool>, IDisposable?> WatchFactory { get; set; } = WatchLocalTree;

    /// <summary>停掉监视、取消在飞的操作;窗口随之关闭。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopWatching();
        _operationCts?.Cancel();
        Disposed?.Invoke(this, EventArgs.Empty);
    }

    // ———————————————————————— 比较与同步 ————————————————————————

    private async Task CompareAsync()
    {
        if (!CanCompare || !TryBuildOptions(forWatch: false, out Request request))
        {
            return;
        }
        CancellationTokenSource cts = BeginOperation();
        IsScanning = true;
        try
        {
            int count = await CompareCoreAsync(request, cts.Token);
            SetStatus(count == 0 ? Strings.Get("Sync_NothingToDo") : Strings.Format("Sync_PreviewReady", count));
        }
        catch (OperationCanceledException)
        {
            SetStatus(Strings.Get("Sync_Cancelled"));
        }
        catch (DirectoryNotFoundException)
        {
            SetStatus(null);
            ErrorText = Strings.Format("Sync_LocalMissing", request.LocalRoot);
        }
        catch (Exception ex)
        {
            SetStatus(null);
            ErrorText = ex.Message;
        }
        finally
        {
            IsScanning = false;
            EndOperation(cts);
        }
    }

    /// <summary>扫两边、比较、出计划,把计划铺进预览;返回步数。</summary>
    private async Task<int> CompareCoreAsync(Request request, CancellationToken ct)
    {
        ClearItems();
        NoteText = null;
        int localDirectories = 0;
        int remoteDirectories = 0;
        int epoch = Interlocked.Increment(ref _statusEpoch);
        void ShowProgress()
        {
            if (epoch == Volatile.Read(ref _statusEpoch))
            {
                StatusText = Strings.Format("Sync_ScanningProgress", localDirectories, remoteDirectories);
            }
        }
        ShowProgress();
        var localProgress = new Progress<int>(n =>
        {
            localDirectories = n;
            ShowProgress();
        });
        var remoteProgress = new Progress<int>(n =>
        {
            remoteDirectories = n;
            ShowProgress();
        });

        // 两边并行扫:本地在线程池上,远端走网络,谁也不等谁。
        Task<SyncTree> localScan = DirectoryTreeScanner.ScanLocalAsync(
            request.LocalRoot, request.Mask, progress: localProgress, cancellationToken: ct);
        SyncTree remoteTree;
        try
        {
            remoteTree = await DirectoryTreeScanner.ScanRemoteAsync(
                _sftp, _sessionId, request.RemoteRoot, request.Mask, _inferRemotePrecision,
                serverClockOffset: RemoteClockOffset,
                progress: remoteProgress, cancellationToken: ct);
        }
        catch
        {
            // 远端先失败了也要把本地那一路收掉,免得它的异常没人观察。
            try
            {
                await localScan;
            }
            catch
            {
                // 以远端的错误为准。
            }
            throw;
        }
        SyncTree localTree = await localScan;

        var hashingProgress = new Progress<SyncChecksumProgress>(p =>
        {
            if (epoch == Volatile.Read(ref _statusEpoch))
            {
                StatusText = Strings.Format("Sync_HashingProgress", p.Done, p.Total);
            }
        });
        SyncChecksumOutcome checksums = await SyncChecksums.ApplyAsync(
            localTree.Items, remoteTree.Items, request.Options, _sftp, _sessionId, ChecksumCache, hashingProgress, ct);

        IReadOnlyList<SyncComparison> comparisons = DirectoryComparer.Compare(checksums.Local, checksums.Remote, request.Options);
        IReadOnlyList<SyncAction> plan = SyncPlanner.Plan(comparisons, request.Options);
        foreach (SyncAction action in plan)
        {
            var item = new SyncActionItemViewModel(action);
            item.PropertyChanged += OnItemPropertyChanged;
            _items.Add(item);
        }
        NoteText = BuildNote(
            comparisons.Count(static c => c.State == SyncComparisonState.Conflict),
            localTree.SkippedLinks + remoteTree.SkippedLinks,
            checksums);
        UpdateSummary();
        RaiseStateChanged();
        return plan.Count;
    }

    private async Task SynchronizeAsync()
    {
        SyncAction[] actions = [.. _items.Where(static i => i.IsChecked).Select(static i => i.Action)];
        if (!CanEditOptions || actions.Length == 0 || !TryBuildOptions(forWatch: false, out Request request))
        {
            return;
        }
        int deletions = actions.Count(static a => a.Kind is SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote);
        if (deletions > 0 && ConfirmAsync is not null && !await ConfirmAsync(Strings.Format("Sync_ConfirmDelete", deletions)))
        {
            return;
        }

        CancellationTokenSource cts = BeginOperation();
        IsRunning = true;
        try
        {
            int epoch = Interlocked.Increment(ref _statusEpoch);
            var progress = new Progress<SyncRunProgress>(p =>
            {
                if (epoch == Volatile.Read(ref _statusEpoch))
                {
                    StatusText = Strings.Format("Sync_Running", p.Done, p.Total, p.Current);
                }
            });
            DirectorySyncResult result = await _runner.ExecuteAsync(request.LocalRoot, request.RemoteRoot, actions, progress, cts.Token);
            _timestampsUnsupported |= result.TimestampsUnsupported;
            ErrorText = FormatErrors(result.Errors);
            await RefreshPanesQuietlyAsync();
            if (result.Cancelled)
            {
                SetStatus(Strings.Get("Sync_Cancelled"));
                return;
            }

            // 同步完立刻复查一遍:剩下的差异要么是失败项,要么说明这台服务器记不住时间 ——
            // 两种都该让用户当场看见,而不是下次打开才发现"怎么又要传一遍"。
            IsRunning = false;
            IsScanning = true;
            int remaining = await CompareCoreAsync(request, cts.Token);
            SetStatus(remaining == 0
                ? Strings.Format("Sync_DoneVerified", result.Succeeded, result.Failed)
                : Strings.Format("Sync_DoneRemaining", result.Succeeded, result.Failed, remaining));
        }
        catch (OperationCanceledException)
        {
            SetStatus(Strings.Get("Sync_Cancelled"));
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsRunning = false;
            IsScanning = false;
            EndOperation(cts);
        }
    }

    // ———————————————————————— 保持远端目录最新 ————————————————————————

    private async Task ToggleWatchAsync()
    {
        if (IsWatching)
        {
            StopWatching();
            return;
        }
        if (IsBusy || !TryBuildOptions(forWatch: true, out Request request))
        {
            return;
        }
        if (request.Options.EffectiveDeleteExtraneous
            && ConfirmAsync is not null
            && !await ConfirmAsync(Strings.Get("Sync_ConfirmWatchDelete")))
        {
            return;
        }

        ClearItems();
        WatchLog.Clear();
        NoteText = null;
        _watchOptions = request.Options;
        _watchMask = request.Mask;
        _watchLocalRoot = request.LocalRoot;
        _watchRemoteRoot = request.RemoteRoot;
        _watchCts = new();
        IDisposable? watcher = WatchFactory(request.LocalRoot, OnLocalChanged);
        if (watcher is null)
        {
            _watchCts = null;
            ErrorText = Strings.Get("Sync_WatchUnavailable");
            return;
        }
        _watcher = watcher;
        IsWatching = true;
        string started = Strings.Format("Sync_WatchStarted", request.LocalRoot, request.RemoteRoot);
        AppendLog(started);
        SetStatus(started);

        // 开始时先完整对齐一次(WinSCP 的「启动时同步」):否则只有开始之后的改动会上去,
        // 之前就不一样的文件会一直不一样,用户却以为"已经在保持最新了"。
        lock (_watchLock)
        {
            _pendingScopes[string.Empty] = true;
        }
        await FlushPendingAsync();
    }

    private void StopWatching()
    {
        if (!IsWatching)
        {
            return;
        }
        lock (_watchLock)
        {
            _watcher?.Dispose();
            _watcher = null;
            _watchTimer?.Dispose();
            _watchTimer = null;
            _pendingScopes.Clear();
        }
        // 只取消不释放:在飞的那次同步手里还拿着这个令牌。
        _watchCts?.Cancel();
        _watchCts = null;
        IsWatching = false;
        string stopped = Strings.Get("Sync_WatchStopped");
        AppendLog(stopped);
        SetStatus(stopped);
    }

    /// <summary>写一条结论性的状态文字,并让之前还在路上的进度回调作废。</summary>
    private void SetStatus(string? text)
    {
        Interlocked.Increment(ref _statusEpoch);
        StatusText = text;
    }

    /// <summary>
    /// 本地有变化(可能在任意线程上)。只记下「哪个目录要重新对一遍」,由防抖计时器统一处理。
    /// </summary>
    /// <remarks>
    /// 记的是<b>目录</b>而不是文件:文件的删除只能从父目录的列举里看出来(远端多了一个),
    /// 改名也是一删一增。新出现的目录记成「递归」(里面可能已经有一整棵树,比如解压、git clone),
    /// 其余只对父目录做一层比较 —— 改一个文件不该触发整棵树的重新扫描。
    /// </remarks>
    /// <param name="fullPath">变化的完整路径。</param>
    /// <param name="isNewEntry">是否是新出现的条目(创建、改名后的新名字、监视溢出后的整体重扫)。</param>
    internal void OnLocalChanged(string fullPath, bool isNewEntry)
    {
        string root = _watchLocalRoot;
        if (!IsWatching || root.Length == 0)
        {
            return;
        }
        string relative;
        try
        {
            relative = Path.GetRelativePath(root, fullPath);
        }
        catch (ArgumentException)
        {
            return;
        }
        if (relative == ".")
        {
            relative = string.Empty;
        }
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return;
        }
        relative = relative.Replace(Path.DirectorySeparatorChar, '/');
        lock (_watchLock)
        {
            if (_watcher is null)
            {
                return;
            }
            if (relative.Length == 0)
            {
                _pendingScopes[string.Empty] = isNewEntry || _pendingScopes.GetValueOrDefault(string.Empty);
            }
            else
            {
                if (isNewEntry && Directory.Exists(fullPath))
                {
                    _pendingScopes[relative] = true;
                }
                int slash = relative.LastIndexOf('/');
                _pendingScopes.TryAdd(slash < 0 ? string.Empty : relative[..slash], false);
            }
            _watchTimer ??= new(_ => ScheduleFlush(), null, Timeout.Infinite, Timeout.Infinite);
            _watchTimer.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void ScheduleFlush() =>
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            if (IsWatching)
            {
                _ = FlushPendingAsync();
            }
        });

    /// <summary>把攒下的目录逐个对一遍。同一时刻只有一轮在跑;跑的途中又有变化,跑完接着再来一轮。</summary>
    internal async Task FlushPendingAsync()
    {
        if (!await _flushGate.WaitAsync(0))
        {
            _flushAgain = true;
            return;
        }
        try
        {
            do
            {
                _flushAgain = false;
                KeyValuePair<string, bool>[] batch;
                lock (_watchLock)
                {
                    batch = [.. _pendingScopes];
                    _pendingScopes.Clear();
                }
                CancellationTokenSource? cts = _watchCts;
                SyncOptions? options = _watchOptions;
                if (batch.Length == 0 || cts is null || options is null || cts.IsCancellationRequested)
                {
                    return;
                }
                string[] deepScopes = [.. batch.Where(static s => s.Value).Select(static s => s.Key)];
                foreach ((string scope, bool recursive) in batch.OrderBy(static s => s.Key.Length))
                {
                    // 已被某个递归范围包住的,不必再单独对一遍。
                    if (deepScopes.Any(deep => deep != scope && Covers(deep, scope)))
                    {
                        continue;
                    }
                    await SyncScopeAsync(scope, recursive, options, cts.Token);
                }
            }
            while (_flushAgain);
        }
        catch (OperationCanceledException)
        {
            // 停止监视。
        }
        catch (Exception ex)
        {
            AppendLog(Strings.Format("Sync_WatchError", ex.Message));
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task SyncScopeAsync(string scope, bool recursive, SyncOptions options, CancellationToken ct)
    {
        string localDirectory = scope.Length == 0
            ? _watchLocalRoot
            : Path.Combine(_watchLocalRoot, scope.Replace('/', Path.DirectorySeparatorChar));
        // 目录已经没了:它的删除由父目录那一层去对(父目录一定也在待办里)。
        if (!Directory.Exists(localDirectory) || !IsScopeIncluded(scope))
        {
            return;
        }
        SyncTree local = await DirectoryTreeScanner.ScanLocalAsync(localDirectory, _watchMask, recursive, scope, cancellationToken: ct);
        SyncTree remote;
        try
        {
            remote = await DirectoryTreeScanner.ScanRemoteAsync(
                _sftp, _sessionId, DirectoryTreeScanner.CombineRemote(_watchRemoteRoot, scope), _watchMask,
                _inferRemotePrecision, recursive, scope, RemoteClockOffset, cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 远端还没有这个目录(本地刚建的):当作空的,执行时会逐级建出来。
            remote = new([], 0);
        }
        SyncChecksumOutcome checksums = await SyncChecksums.ApplyAsync(
            local.Items, remote.Items, options, _sftp, _sessionId, ChecksumCache, cancellationToken: ct);
        if (checksums.RemoteFailure is { } reason && !_checksumFailureLogged)
        {
            _checksumFailureLogged = true;
            AppendLog(Strings.Format("Sync_NoteChecksumUnsupported", reason));
        }
        IReadOnlyList<SyncAction> plan = SyncPlanner.Plan(DirectoryComparer.Compare(checksums.Local, checksums.Remote, options), options);
        if (plan.Count == 0)
        {
            return;
        }
        DirectorySyncResult result = await _runner.ExecuteAsync(_watchLocalRoot, _watchRemoteRoot, plan, null, ct);
        string time = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        foreach (SyncAction action in plan)
        {
            AppendLog($"{time}  {SyncActionItemViewModel.DescribeKind(action.Kind)}  {action.RelativePath}");
        }
        foreach (string error in result.Errors)
        {
            AppendLog(Strings.Format("Sync_WatchError", error));
        }
        if (result.TimestampsUnsupported && !_timestampsUnsupported)
        {
            _timestampsUnsupported = true;
            AppendLog(Strings.Get("Sync_TimestampsUnsupported"));
        }
        await RefreshPanesQuietlyAsync();
    }

    /// <summary>范围本身及其每一级上级目录都没被掩码排除(被排除目录里的变化不该被同步上去)。</summary>
    private bool IsScopeIncluded(string scope)
    {
        for (int slash = scope.IndexOf('/'); slash > 0; slash = scope.IndexOf('/', slash + 1))
        {
            if (!_watchMask.Includes(scope[..slash], isDirectory: true))
            {
                return false;
            }
        }
        return scope.Length == 0 || _watchMask.Includes(scope, isDirectory: true);
    }

    private static bool Covers(string ancestor, string path) =>
        ancestor.Length == 0 || path == ancestor || path.StartsWith(ancestor + "/", StringComparison.Ordinal);

    /// <summary>
    /// 默认的本地目录树监视。创建与改名后的新名字报「新条目」;修改、删除与改名前的旧名字只报变化;
    /// 缓冲区溢出(短时间变化太多,事件被丢)时整棵树重对一遍 —— 丢了哪些不知道,只能全对。
    /// </summary>
    private static IDisposable? WatchLocalTree(string root, Action<string, bool> onChanged)
    {
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += (_, e) => onChanged(e.FullPath, true);
            watcher.Changed += (_, e) => onChanged(e.FullPath, false);
            watcher.Deleted += (_, e) => onChanged(e.FullPath, false);
            watcher.Renamed += (_, e) =>
            {
                onChanged(e.OldFullPath, false);
                onChanged(e.FullPath, true);
            };
            watcher.Error += (_, _) => onChanged(root, true);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // ———————————————————————— 杂项 ————————————————————————

    private async Task BrowseLocalAsync()
    {
        if (PickLocalFolder is null || !CanEditOptions)
        {
            return;
        }
        if (await PickLocalFolder() is { Length: > 0 } picked)
        {
            LocalPath = picked;
        }
    }

    /// <summary>一次比较或同步用到的全部输入,在开始时一次定下来。</summary>
    private readonly record struct Request(SyncOptions Options, SyncFileMask Mask, string LocalRoot, string RemoteRoot);

    private bool TryBuildOptions(bool forWatch, out Request request)
    {
        request = default;
        ErrorText = null;
        if (string.IsNullOrWhiteSpace(LocalPath))
        {
            ErrorText = Strings.Get("Sync_LocalRequired");
            return false;
        }
        if (string.IsNullOrWhiteSpace(RemotePath))
        {
            ErrorText = Strings.Get("Sync_RemoteRequired");
            return false;
        }
        string local;
        try
        {
            local = Path.GetFullPath(LocalPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ErrorText = Strings.Format("Sync_LocalMissing", LocalPath);
            return false;
        }
        if (!Directory.Exists(local))
        {
            ErrorText = Strings.Format("Sync_LocalMissing", local);
            return false;
        }
        string remote = RemotePath.Trim();
        if (remote.Length > 1)
        {
            remote = remote.TrimEnd('/');
        }
        if (!SyncFileMask.TryParse(FileMask, out SyncFileMask mask, out _))
        {
            ErrorText = Strings.Get("Sync_InvalidMask");
            return false;
        }
        SaveSettings();
        request = new(
            new()
            {
                // 保持最新恒为本地 → 远端;时间戳模式对"刚改的文件"没有意义,按同步处理。
                Direction = forWatch ? SyncDirection.ToRemote : Direction,
                Mode = forWatch && Mode == SyncMode.Timestamps ? SyncMode.Synchronize : Mode,
                Criteria = _settings.Criteria,
                DeleteExtraneous = DeleteExtraneous,
                ExistingOnly = ExistingOnly,
            },
            mask,
            local,
            remote);
        return true;
    }

    private void SaveSettings()
    {
        _settings.Direction = Direction;
        _settings.Mode = Mode;
        _settings.CompareByTime = CompareByTime;
        _settings.CompareBySize = CompareBySize;
        _settings.CompareByChecksum = CompareByChecksum;
        _settings.DeleteExtraneous = DeleteExtraneous;
        _settings.ExistingOnly = ExistingOnly;
        _settings.FileMask = FileMask;
    }

    /// <summary>选项一变,预览就作废:按旧选项算出来的计划不能拿新选项的名义去执行。</summary>
    private bool SetOption<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        this.RaiseAndSetIfChanged(ref field, value);
        if (HasItems)
        {
            ClearItems();
            NoteText = null;
            SetStatus(null);
        }
        return true;
    }

    private CancellationTokenSource BeginOperation()
    {
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        ErrorText = null;
        return cts;
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_operationCts, cts))
        {
            _operationCts = null;
        }
        cts.Dispose();
    }

    private async Task RefreshPanesQuietlyAsync()
    {
        if (RefreshPanesAsync is null)
        {
            return;
        }
        try
        {
            await RefreshPanesAsync();
        }
        catch
        {
            // 刷新失败不影响同步结果;两栏各自有错误提示。
        }
    }

    private void ClearItems()
    {
        foreach (SyncActionItemViewModel item in _items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
        _items.Clear();
        UpdateSummary();
        RaiseStateChanged();
    }

    private void SetAllChecked(bool value)
    {
        foreach (SyncActionItemViewModel item in _items)
        {
            item.IsChecked = value;
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SyncActionItemViewModel.IsChecked))
        {
            UpdateSummary();
            this.RaisePropertyChanged(nameof(CanSynchronize));
        }
    }

    private void UpdateSummary()
    {
        if (_items.Count == 0)
        {
            SummaryText = null;
            return;
        }
        int uploads = 0, downloads = 0, deletions = 0, others = 0;
        long uploadBytes = 0, downloadBytes = 0;
        foreach (SyncActionItemViewModel item in _items.Where(static i => i.IsChecked))
        {
            switch (item.Action.Kind)
            {
                case SyncActionKind.Upload:
                    uploads++;
                    uploadBytes += item.Action.Bytes;
                    break;
                case SyncActionKind.Download:
                    downloads++;
                    downloadBytes += item.Action.Bytes;
                    break;
                case SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote:
                    deletions++;
                    break;
                default:
                    others++;
                    break;
            }
        }
        SummaryText = Strings.Format("Sync_Summary",
            uploads, LocalFileEntry.FormatSize(uploadBytes),
            downloads, LocalFileEntry.FormatSize(downloadBytes),
            deletions, others);
    }

    private string? BuildNote(int conflicts, int skippedLinks, SyncChecksumOutcome checksums)
    {
        var parts = new List<string>(4);
        if (checksums.RemoteFailure is { } reason)
        {
            parts.Add(Strings.Format("Sync_NoteChecksumUnsupported", reason));
        }
        else if (checksums.FellBack > 0)
        {
            parts.Add(Strings.Format("Sync_NoteChecksumPartial", checksums.FellBack, checksums.Candidates));
        }
        if (conflicts > 0)
        {
            parts.Add(Strings.Format("Sync_NoteConflicts", conflicts));
        }
        if (skippedLinks > 0)
        {
            parts.Add(Strings.Format("Sync_NoteSkippedLinks", skippedLinks));
        }
        if (_timestampsUnsupported)
        {
            parts.Add(Strings.Get("Sync_TimestampsUnsupported"));
        }
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string? FormatErrors(IReadOnlyList<string> errors)
    {
        if (errors.Count == 0)
        {
            return null;
        }
        string shown = string.Join("\n", errors.Take(MaxErrorLines));
        return errors.Count <= MaxErrorLines
            ? shown
            : shown + "\n" + Strings.Format("Sync_MoreErrors", errors.Count - MaxErrorLines);
    }

    private void AppendLog(string line)
    {
        WatchLog.Insert(0, line);
        while (WatchLog.Count > MaxLogLines)
        {
            WatchLog.RemoveAt(WatchLog.Count - 1);
        }
        this.RaisePropertyChanged(nameof(ShowWatchLog));
        this.RaisePropertyChanged(nameof(ShowEmptyHint));
    }

    private void RaiseStateChanged()
    {
        this.RaisePropertyChanged(nameof(IsBusy));
        this.RaisePropertyChanged(nameof(CanEditOptions));
        this.RaisePropertyChanged(nameof(CanCompare));
        this.RaisePropertyChanged(nameof(CanSynchronize));
        this.RaisePropertyChanged(nameof(HasItems));
        this.RaisePropertyChanged(nameof(ShowWatchLog));
        this.RaisePropertyChanged(nameof(ShowEmptyHint));
    }
}
