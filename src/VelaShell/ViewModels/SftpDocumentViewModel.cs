using System.Collections.ObjectModel;
using System.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.DirectorySync;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Presentation.Services;

namespace VelaShell.ViewModels;

/// <summary>持有一个独立 SFTP 文档的全部状态与生命周期资源。</summary>
public sealed class SftpDocumentViewModel : ReactiveObject, ISftpDocumentContent, IAsyncDisposable
{
    private readonly Func<Guid, CancellationToken, Task> _disconnectAsync;
    private readonly SerializedSftpService _serializedSftp;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _closeTask;
    private readonly Lock _closeSync = new();
    private readonly Func<Guid, Func<List<RemoteDirectoryBookmark>, bool>, Task<List<RemoteDirectoryBookmark>?>>? _updateBookmarksAsync;
    private readonly SemaphoreSlim _bookmarkGate = new(1, 1);

    /// <summary>初始化 SFTP 文档视图模型,并开始加载本地/远程文件树。</summary>
    /// <param name="profile">用于建立连接的会话配置。</param>
    /// <param name="session">已建立的 SSH 会话。</param>
    /// <param name="workflow">连接工作流服务,关闭文档时用它断开会话。</param>
    /// <param name="sftpService">底层 SFTP 服务,构造时会被包成本文档独占的串行化视图。</param>
    /// <param name="transferOptions">设置 → 文件传输 的选项(打开时那一份;之后设置保存时宿主经 <see cref="ISftpDocumentContent.RemotePanes" /> 换上新的)。</param>
    /// <param name="transferSink">承载本文档所发起传输的浮动传输组件;为 null 时不上报进度。</param>
    /// <param name="getDefaultEditorPath">
    /// 解析「设置 → 文件传输 → 默认编辑器」的回调。独立 SFTP 标签与终端侧边栏的文件浏览器
    /// 是两个各自 new 出来的 <see cref="FileBrowserViewModel" />,宿主回调不会自动继承——
    /// 漏传这一个会让右键「使用默认编辑器打开」误报“未配置”(明明已配置)。
    /// </param>
    /// <param name="updateBookmarksAsync">目录收藏的落盘回调,见另一个构造函数的同名参数。</param>
    public SftpDocumentViewModel(
        SessionProfile profile,
        SshSession session,
        IConnectionWorkflowService workflow,
        ISftpService sftpService,
        TransferOptions transferOptions,
        FileTransferViewModel? transferSink = null,
        Func<Task<string?>>? getDefaultEditorPath = null,
        Func<Guid, Func<List<RemoteDirectoryBookmark>, bool>, Task<List<RemoteDirectoryBookmark>?>>? updateBookmarksAsync = null)
        : this(profile,
               (session ?? throw new ArgumentNullException(nameof(session))).SessionId,
               (workflow ?? throw new ArgumentNullException(nameof(workflow))).DisconnectAsync,
               sftpService,
               transferOptions,
               transferSink,
               getDefaultEditorPath,
               updateBookmarksAsync)
    {
        Session = session;
        // 标签页状态灯读的是 Status,而它的真值住在 SshSession 上 —— 转发一次属性变更,
        // 灯才会跟着会话断开/重连改色。FTP 走另一个构造函数,没有这条线。
        session.PropertyChanged += OnSessionPropertyChanged;
    }

    /// <summary>
    /// 以「一个会话标识 + 一个断开回调」构造文档,不绑定具体协议。
    /// FTP 与 S3 会话都走这条:它们没有 <see cref="SshSession" />,也不经
    /// <see cref="IConnectionWorkflowService" />(那是 SSH 握手),但文件面板与传输栈完全共用
    /// —— 它们只认 <see cref="ISftpService" /> + 会话标识。
    /// </summary>
    /// <param name="profile">用于建立连接的会话配置。</param>
    /// <param name="sessionId">已建立的会话标识。</param>
    /// <param name="disconnectAsync">关闭文档时断开该会话的回调。</param>
    /// <param name="sftpService">底层远程文件服务,构造时会被包成本文档独占的串行化视图。</param>
    /// <param name="transferOptions">设置 → 文件传输 的选项(打开时那一份;之后设置保存时宿主经 <see cref="ISftpDocumentContent.RemotePanes" /> 换上新的)。</param>
    /// <param name="transferSink">承载本文档所发起传输的浮动传输组件;为 null 时不上报进度。</param>
    /// <param name="getDefaultEditorPath">解析「默认编辑器」的回调。</param>
    /// <param name="updateBookmarksAsync">
    /// 目录收藏的落盘回调:按配置 id 在库里最新的那份收藏上执行修改(返回 false 不写),交回改后的列表;
    /// 配置没保存过时交回 null。只改收藏这一项 —— 本文档手里的配置可能早已过时,不能整条写回。
    /// 临时连接可不提供。
    /// </param>
    public SftpDocumentViewModel(
        SessionProfile profile,
        Guid sessionId,
        Func<Guid, CancellationToken, Task> disconnectAsync,
        ISftpService sftpService,
        TransferOptions transferOptions,
        FileTransferViewModel? transferSink = null,
        Func<Task<string?>>? getDefaultEditorPath = null,
        Func<Guid, Func<List<RemoteDirectoryBookmark>, bool>, Task<List<RemoteDirectoryBookmark>?>>? updateBookmarksAsync = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _updateBookmarksAsync = updateBookmarksAsync;
        Profile.DirectoryBookmarks ??= [];
        RebuildBookmarks();
        _disconnectAsync = disconnectAsync ?? throw new ArgumentNullException(nameof(disconnectAsync));
        _serializedSftp = new SerializedSftpService(sftpService ?? throw new ArgumentNullException(nameof(sftpService)), sessionId);
        SessionId = sessionId;
        Title = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
        RemoteFiles = new FileBrowserViewModel(_serializedSftp, SessionId)
        {
            ServerDisplayName = Title,
            // 上传 / 下载批次记进传输队列:断线或退出时没传完的,按这条配置找回连接接着传。
            QueueOrigin = new(profile.Id, Title),
            TransferSink = transferSink,
            TransferOptions = transferOptions ?? throw new ArgumentNullException(nameof(transferOptions)),
            IsVisible = true,
            IsDragEnabled = true,
            GetDefaultEditorPath = getDefaultEditorPath,
            // 连接配置里的「默认打开路径」(FTP / FTPS 的高级选项)。其余协议没有这块设置,
            // 取到 null 即照旧从登录工作目录起步。
            InitialRemotePath = profile.Ftp?.InitialRemotePath,
        };
        // 「显示隐藏文件」与列显示按设置铺好,与终端侧栏的面板同一个来源(差距分析 A8);写回设置的回调由宿主建好之后再挂。
        RemoteFiles.ApplyDisplayOptions(transferOptions);
        LocalFiles = new LocalFilePaneViewModel(transferOptions)
        {
            UploadSelectedAsync = () => UploadSelectedAsync()
        };
        RemoteFiles.PickFolderForDownload = () => Task.FromResult<string?>(LocalFiles.CurrentPath);
        UploadSelectedCommand = ReactiveCommand.CreateFromTask(UploadSelectedAsync);
        DownloadSelectedCommand = ReactiveCommand.CreateFromTask(DownloadSelectedAsync);
        DeleteLocalSelectedCommand = LocalFiles.DeleteSelectedCommand;
        LocalFiles.ConfirmDelete = message => ConfirmLocalDelete?.Invoke(message) ?? Task.FromResult(false);
        CompareDirectoriesCommand = ReactiveCommand.CreateFromTask(CompareDirectoriesAsync);
        OpenSyncCommand = ReactiveCommand.CreateFromTask(OpenSyncAsync);
        AddBookmarkCommand = ReactiveCommand.CreateFromTask(AddBookmarkAsync);
        ApplyBookmarkCommand = ReactiveCommand.CreateFromTask<DirectoryBookmarkViewModel>(ApplyBookmarkAsync);
        EditBookmarkCommand = ReactiveCommand.CreateFromTask<DirectoryBookmarkViewModel>(item => EditBookmarkAsync(item));
        DeleteBookmarkCommand = ReactiveCommand.CreateFromTask<DirectoryBookmarkViewModel>(item => DeleteBookmarkAsync(item));
        LocalFiles.PropertyChanged += OnPanePropertyChanged;
        RemoteFiles.PropertyChanged += OnPanePropertyChanged;
        InitialLoadTask = LoadAsync();
    }

    /// <summary>「比较目录」:把两栏当前目录里不同的条目在各自一栏里选中(不递归,WinSCP 同名命令的口径)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CompareDirectoriesCommand { get; }

    /// <summary>打开目录同步窗口(同一文档只开一个,再点就把它提到前面)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenSyncCommand { get; }

    /// <summary>保存当前本地与远程目录配对。</summary>
    public ReactiveCommand<RxVoid, RxVoid> AddBookmarkCommand { get; }

    /// <summary>应用一条目录收藏,同时导航本地与远程两栏。</summary>
    public ReactiveCommand<DirectoryBookmarkViewModel, RxVoid> ApplyBookmarkCommand { get; }

    /// <summary>编辑一条目录收藏。</summary>
    public ReactiveCommand<DirectoryBookmarkViewModel, RxVoid> EditBookmarkCommand { get; }

    /// <summary>删除一条目录收藏。</summary>
    public ReactiveCommand<DirectoryBookmarkViewModel, RxVoid> DeleteBookmarkCommand { get; }

    /// <summary>本连接下的目录收藏,按用户保存顺序排列。</summary>
    public ObservableCollection<DirectoryBookmarkViewModel> Bookmarks { get; } = [];

    /// <summary>是否至少有一条收藏。</summary>
    public bool HasBookmarks => Bookmarks.Count > 0;

    /// <summary>收藏操作失败时的错误;文档工具条与收藏管理窗口都显示它。</summary>
    public string? BookmarkErrorMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>由视图显示收藏编辑器,取消时返回 null。</summary>
    public Func<RemoteDirectoryBookmark, Task<RemoteDirectoryBookmark?>>? EditBookmarkDialog { get; set; }

    /// <summary>由视图显示收藏管理窗口。</summary>
    public Func<Task>? ShowBookmarkManager { get; set; }

    /// <summary>由视图确认收藏删除。</summary>
    public Func<string, Task<bool>>? ConfirmBookmarkDelete { get; set; }

    /// <summary>文档分离(关闭)时触发:收藏管理窗口据此跟着关掉,不留一个对着已关文档的窗口。</summary>
    public event EventHandler? Detached;

    /// <summary>上次「比较目录」的结论;任一栏换了目录就清掉,免得它描述的已经不是眼前这两个目录。</summary>
    public string? CompareSummary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>本文档里上次用过的同步选项。</summary>
    public DirectorySyncSettings SyncSettings { get; } = new();

    /// <summary>当前开着的同步窗口的视图模型;没开时为 null。</summary>
    public DirectorySyncViewModel? ActiveSync { get; private set; }

    /// <summary>由视图设置:显示(或激活)同步窗口。</summary>
    public Func<DirectorySyncViewModel, Task>? ShowSyncWindow { get; set; }

    /// <summary>
    /// 远端时间是否可能粗于秒。FTP 的 LIST 只到分钟、插件协议没有保证;SFTP 的 mtime 恒为秒级,
    /// 不能被一个恰好落在整分上的时间降级成「只精确到分钟」。
    /// </summary>
    internal bool RemoteTimesMayBeCoarse => Profile.ConnectionType is not (ConnectionType.SFTP or ConnectionType.SSH);

    /// <summary>「比较目录」与同步窗口共用的摘要缓存。</summary>
    private readonly SyncChecksumCache _checksumCache = new();

    /// <summary>
    /// 比较两栏当前目录(一层),选中各自一侧较新或独有的条目,并给出一句结论。
    /// 勾着 SHA-256 时(默认)大小相同的文件先比摘要,算不出来的回退到大小与修改时间。
    /// </summary>
    public async Task CompareDirectoriesAsync()
    {
        var options = new SyncOptions
        {
            Criteria = SyncSettings.Criteria == SyncCriteria.None ? SyncCriteria.Time | SyncCriteria.Size : SyncSettings.Criteria,
        };
        string localPath = LocalFiles.CurrentPath;
        string remotePath = RemoteFiles.CurrentPath;
        // 比较的是整个目录:任一栏的过滤框藏着行,结论就会把藏起来的那些说成「只有另一边有」。
        LocalFiles.FilterText = string.Empty;
        RemoteFiles.FilterText = string.Empty;
        // 远端栏隐藏了点文件时,本地栏的点文件也不参与 —— 否则每个 .git、.env 都会被标成「只有本地有」。
        bool includeHidden = RemoteFiles.ShowHiddenFiles;
        LocalFileEntry[] localEntries = [.. LocalFiles.Entries.Where(e => !e.IsParentEntry && (includeHidden || !e.Name.StartsWith('.')))];
        RemoteFileInfoViewModel[] remoteEntries = [.. RemoteFiles.Files.Where(static f => !f.IsParentEntry && !f.IsDirectoryLink)];
        bool coarse = RemoteTimesMayBeCoarse;
        // FTP 配了服务器时区时,LIST 的时间按它换算(与同步窗口同一口径,见 SyncTime.ServerClockOffset)。
        TimeSpan? clock = SyncTime.ServerClockOffset(Profile);
        IReadOnlyList<SyncItem> localItems = [.. localEntries.Select(static e => new SyncItem(
            e.Name, e.FullPath, e.IsDirectory, e.IsDirectory ? 0 : e.SizeBytes, SyncTime.ToUtc(e.LastModified)))];
        IReadOnlyList<SyncItem> remoteItems = [.. remoteEntries.Select(f => new SyncItem(
            f.Name, f.FullPath, f.IsDirectory, f.IsDirectory ? 0 : f.SizeBytes, SyncTime.ToUtc(f.LastModified, clock),
            coarse ? SyncTime.InferPrecision(f.LastModified) : SyncTimePrecision.Second)
        {
            CalendarOffset = SyncTime.CalendarOffsetFor(f.LastModified, clock),
        })];

        string? fellBack = null;
        if (options.Criteria.HasFlag(SyncCriteria.Checksum))
        {
            CompareSummary = Strings.Get("Sync_CompareHashing");
            SyncChecksumOutcome outcome;
            try
            {
                outcome = await SyncChecksums.ApplyAsync(
                    localItems, remoteItems, options, _serializedSftp, SessionId, _checksumCache, cancellationToken: _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                CompareSummary = null;
                return;
            }
            // 算摘要期间用户换了目录:结论描述的已经不是眼前这两个目录,选中也会落到错的行上。
            if (!string.Equals(localPath, LocalFiles.CurrentPath, StringComparison.Ordinal)
                || !string.Equals(remotePath, RemoteFiles.CurrentPath, StringComparison.Ordinal))
            {
                CompareSummary = null;
                return;
            }
            localItems = outcome.Local;
            remoteItems = outcome.Remote;
            if (outcome.RemoteFailure is not null || outcome.FellBack > 0)
            {
                fellBack = Strings.Get("Sync_CompareFellBack");
            }
        }
        IReadOnlyList<SyncComparison> comparisons = DirectoryComparer.Compare(localItems, remoteItems, options);

        StringComparer comparer = options.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var localMarked = new HashSet<string>(comparer);
        var remoteMarked = new HashSet<string>(comparer);
        int identical = 0;
        foreach (SyncComparison comparison in comparisons)
        {
            switch (comparison.State)
            {
                case SyncComparisonState.LocalOnly or SyncComparisonState.LocalNewer:
                    localMarked.Add(comparison.RelativePath);
                    break;
                case SyncComparisonState.RemoteOnly or SyncComparisonState.RemoteNewer:
                    remoteMarked.Add(comparison.RelativePath);
                    break;
                case SyncComparisonState.Differs or SyncComparisonState.Conflict:
                    localMarked.Add(comparison.RelativePath);
                    remoteMarked.Add(comparison.RelativePath);
                    break;
                default:
                    identical++;
                    break;
            }
        }

        LocalFiles.SelectedEntries.Clear();
        foreach (LocalFileEntry entry in localEntries.Where(e => localMarked.Contains(e.Name)))
        {
            LocalFiles.SelectedEntries.Add(entry);
        }
        RemoteFiles.SelectedFiles.Clear();
        foreach (RemoteFileInfoViewModel entry in remoteEntries.Where(f => remoteMarked.Contains(f.Name)))
        {
            RemoteFiles.SelectedFiles.Add(entry);
        }
        string summary = localMarked.Count + remoteMarked.Count == 0
            ? Strings.Format("Sync_CompareIdentical", identical)
            : Strings.Format("Sync_CompareSummary", localMarked.Count, remoteMarked.Count, identical);
        CompareSummary = fellBack is null ? summary : summary + " " + fellBack;
    }

    private async Task OpenSyncAsync()
    {
        if (ShowSyncWindow is null)
        {
            return;
        }
        if (ActiveSync is null)
        {
            var sync = new DirectorySyncViewModel(
                _serializedSftp,
                SessionId,
                RemoteFiles,
                SyncSettings,
                LocalFiles.CurrentPath,
                RemoteFiles.CurrentPath,
                RemoteTimesMayBeCoarse,
                Title)
            {
                RefreshPanesAsync = RefreshPanesAsync,
                ChecksumCache = _checksumCache,
                RemoteClockOffset = SyncTime.ServerClockOffset(Profile),
            };
            sync.Disposed += OnSyncDisposed;
            ActiveSync = sync;
        }
        await ShowSyncWindow(ActiveSync);
    }

    private void OnSyncDisposed(object? sender, EventArgs e)
    {
        if (sender is DirectorySyncViewModel sync)
        {
            sync.Disposed -= OnSyncDisposed;
            if (ReferenceEquals(ActiveSync, sync))
            {
                ActiveSync = null;
            }
        }
    }

    private async Task RefreshPanesAsync()
    {
        await LocalFiles.RefreshAsync(_lifetime.Token);
        await RemoteFiles.RefreshSilentlyAsync();
    }

    /// <summary>打开新增收藏编辑器并保存用户确认的结果。</summary>
    public Task AddBookmarkAsync() => AddBookmarkAsync(EditBookmarkDialog);

    /// <summary>
    /// 用指定的编辑器新增收藏。收藏管理窗口传它自己的编辑器:<see cref="EditBookmarkDialog" /> 挂在文档视图上,
    /// 文档不是当前标签时那个视图已经离开窗口树,弹不出框,操作就会悄无声息地什么也不做。
    /// </summary>
    /// <param name="editor">收藏编辑器,取消时返回 null;为 null 时什么也不做。</param>
    public async Task AddBookmarkAsync(Func<RemoteDirectoryBookmark, Task<RemoteDirectoryBookmark?>>? editor)
    {
        if (editor is null)
        {
            return;
        }

        await _bookmarkGate.WaitAsync(_lifetime.Token);
        try
        {
            BookmarkErrorMessage = null;
            RemoteDirectoryBookmark seed = new()
            {
                Id = Guid.Empty,
                Name = DefaultBookmarkName(RemoteFiles.CurrentPath),
                LocalPath = LocalFiles.CurrentPath,
                RemotePath = RemoteFiles.CurrentPath,
            };
            RemoteDirectoryBookmark? edited = await editor(seed);
            if (edited is not null)
            {
                await AddOrUpdateBookmarkAsync(edited, existingId: null);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _bookmarkGate.Release();
        }
    }

    /// <summary>同时导航两栏到一条收藏;两侧失败互不回滚。</summary>
    public async Task ApplyBookmarkAsync(DirectoryBookmarkViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        BookmarkErrorMessage = null;
        if (await NormalizeBookmarkAsync(item.Snapshot) is not { } bookmark)
        {
            return;
        }

        try
        {
            await Task.WhenAll(
                LocalFiles.NavigateToAsync(bookmark.LocalPath, _lifetime.Token),
                RemoteFiles.NavigateToAsync(bookmark.RemotePath, _lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // 两个面板通常会把列举错误写入各自的 ErrorMessage;这里只兜底处理
            // 路径格式或本地权限在导航入口就抛出的情况。
            BookmarkErrorMessage = ex.Message;
        }
    }

    /// <summary>打开编辑器,成功后替换原收藏并持久化。</summary>
    public Task EditBookmarkAsync(DirectoryBookmarkViewModel? item) => EditBookmarkAsync(item, EditBookmarkDialog);

    /// <summary>用指定的编辑器编辑收藏(收藏管理窗口用,理由同 <see cref="AddBookmarkAsync()" /> 的另一个重载)。</summary>
    /// <param name="item">要编辑的收藏。</param>
    /// <param name="editor">收藏编辑器,取消时返回 null;为 null 时什么也不做。</param>
    public async Task EditBookmarkAsync(
        DirectoryBookmarkViewModel? item, Func<RemoteDirectoryBookmark, Task<RemoteDirectoryBookmark?>>? editor)
    {
        if (item is null || editor is null)
        {
            return;
        }

        await _bookmarkGate.WaitAsync(_lifetime.Token);
        try
        {
            BookmarkErrorMessage = null;
            RemoteDirectoryBookmark? edited = await editor(item.Snapshot);
            if (edited is not null)
            {
                await AddOrUpdateBookmarkAsync(edited, item.Id);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _bookmarkGate.Release();
        }
    }

    /// <summary>确认后删除收藏;路径本身不会被删除。</summary>
    public Task DeleteBookmarkAsync(DirectoryBookmarkViewModel? item) => DeleteBookmarkAsync(item, ConfirmBookmarkDelete);

    /// <summary>用指定的确认框删除收藏(收藏管理窗口用,理由同 <see cref="AddBookmarkAsync()" /> 的另一个重载)。</summary>
    /// <param name="item">要删除的收藏。</param>
    /// <param name="confirm">删除确认;为 null 时什么也不做。</param>
    public async Task DeleteBookmarkAsync(DirectoryBookmarkViewModel? item, Func<string, Task<bool>>? confirm)
    {
        if (item is null || confirm is null)
        {
            return;
        }

        await _bookmarkGate.WaitAsync(_lifetime.Token);
        try
        {
            BookmarkErrorMessage = null;
            if (!await confirm(item.Name))
            {
                return;
            }

            Guid id = item.Id;
            await SaveBookmarksAsync(bookmarks =>
            {
                bookmarks.RemoveAll(bookmark => bookmark.Id == id);
                return null;
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _bookmarkGate.Release();
        }
    }

    private async Task AddOrUpdateBookmarkAsync(RemoteDirectoryBookmark input, Guid? existingId)
    {
        if (await NormalizeBookmarkAsync(input) is not { } normalized)
        {
            return;
        }
        if (existingId is { } id)
        {
            normalized.Id = id;
        }

        await SaveBookmarksAsync(bookmarks =>
        {
            // 查重与找原条目都对着库里最新的那份做:本文档的列表可能早就不是最新的了。
            if (bookmarks.Exists(bookmark => bookmark.Id != existingId
                    && PathsEqual(bookmark.LocalPath, normalized.LocalPath)
                    && string.Equals(bookmark.RemotePath, normalized.RemotePath, StringComparison.Ordinal)))
            {
                return "Bookmark_Duplicate";
            }
            if (existingId is null)
            {
                bookmarks.Add(normalized.Clone());
                return null;
            }
            int index = bookmarks.FindIndex(bookmark => bookmark.Id == existingId);
            if (index < 0)
            {
                return "Bookmark_NotFound";
            }
            bookmarks[index] = normalized.Clone();
            return null;
        });
    }

    /// <summary>规整一条收藏;不合法时写好 <see cref="BookmarkErrorMessage" /> 并返回 null。</summary>
    private async Task<RemoteDirectoryBookmark?> NormalizeBookmarkAsync(RemoteDirectoryBookmark input)
    {
        string name = input.Name.Trim();
        if (name.Length is 0 or > RemoteDirectoryBookmark.MaxNameLength)
        {
            BookmarkErrorMessage = Strings.Get("Bookmark_InvalidName");
            return null;
        }

        string? localPath;
        try
        {
            localPath = LocalFilePaneViewModel.ResolvePathInput(input.LocalPath, LocalFiles.CurrentPath);
            if (localPath is null)
            {
                BookmarkErrorMessage = Strings.Get("Bookmark_InvalidLocalPath");
                return null;
            }
            localPath = RemoteDirectoryBookmark.NormalizeLocalPath(localPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            BookmarkErrorMessage = Strings.Get("Bookmark_InvalidLocalPath");
            return null;
        }

        (string? remotePath, string? remoteError) = await ResolveRemoteBookmarkPathAsync(input.RemotePath);
        if (remotePath is null)
        {
            BookmarkErrorMessage = Strings.Get(remoteError ?? "Bookmark_InvalidRemotePath");
            return null;
        }

        if (localPath.Length > RemoteDirectoryBookmark.MaxPathLength
            || remotePath.Length > RemoteDirectoryBookmark.MaxPathLength)
        {
            BookmarkErrorMessage = Strings.Get("Bookmark_PathTooLong");
            return null;
        }

        return new RemoteDirectoryBookmark
        {
            Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id,
            Name = name,
            LocalPath = localPath,
            RemotePath = remotePath,
        };
    }

    /// <summary>
    /// 远程收藏路径:相对路径按远程栏当前目录解析,<c>~</c> 开头的按远程家目录展开。
    /// 不 Trim —— 首尾的空格可以是目录名的一部分,原样收藏当前目录时尤其不能改它。
    /// </summary>
    /// <returns>规整后的绝对路径;不合法时路径为 null,并给出错误文案的键。</returns>
    private async Task<(string? Path, string? Error)> ResolveRemoteBookmarkPathAsync(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return (null, "Bookmark_InvalidRemotePath");
        }

        string path = input;
        if (path[0] == '~')
        {
            // 收藏是要存下来的:展开不了就拒收。原样当相对路径拼上去,存下的是「当前目录/~/logs」,错得悄无声息。
            if (await RemoteFiles.ExpandHomeAsync(path) is not { } expanded)
            {
                return (null, "Bookmark_HomeUnavailable");
            }
            path = expanded;
        }
        else if (path[0] != '/')
        {
            path = RemoteFiles.CurrentPath.TrimEnd('/') + "/" + path;
        }
        return (RemoteDirectoryBookmark.NormalizeRemotePath(path), null);
    }

    /// <summary>
    /// 落盘一次收藏修改:在库里<b>最新</b>的那份收藏上重做这次修改,而不是把本文档手里的整条配置写回去 ——
    /// 那份配置可能早就过时了(编辑框改过端口、另一个文档加过收藏),整条写回会把别处的改动悄悄回滚。
    /// 改成了才换掉本文档的列表;落盘失败(不论哪种异常)时列表原样不动,界面上不会留下一条没存上的收藏。
    /// </summary>
    /// <param name="change">对收藏列表的修改,返回错误文案的键,null 表示改成了。可能在后台线程上执行,不许碰界面状态。</param>
    private async Task SaveBookmarksAsync(Func<List<RemoteDirectoryBookmark>, string?> change)
    {
        string? error = null;
        List<RemoteDirectoryBookmark>? saved;
        try
        {
            saved = _updateBookmarksAsync is null
                ? null
                : await _updateBookmarksAsync(Profile.Id, bookmarks => (error = change(bookmarks)) is null);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            BookmarkErrorMessage = Strings.Get("Bookmark_SaveFailed");
            return;
        }

        if (saved is null)
        {
            // 临时连接(「最近连接」重建的配置)没有持久化边界:只改本文档内的那份,本次文档里照样有效。
            List<RemoteDirectoryBookmark> local = SnapshotBookmarks();
            error = change(local);
            if (error is null)
            {
                Profile.DirectoryBookmarks = local;
            }
        }
        else
        {
            // 改没改成都换成库里的那份:别处加的收藏这时一并显示出来。
            Profile.DirectoryBookmarks = saved;
        }
        RebuildBookmarks();
        if (error is not null)
        {
            BookmarkErrorMessage = Strings.Get(error);
        }
    }

    /// <summary>
    /// 打开文档时换成库里最新的收藏。传进来的配置可能是资源管理器缓存或登录框交回的副本,
    /// 别的文档后来加的收藏它没有 —— 不刷新的话,明明存好了的收藏在这里看不见。
    /// </summary>
    private async Task RefreshBookmarksAsync()
    {
        if (_updateBookmarksAsync is null)
        {
            return;
        }

        await _bookmarkGate.WaitAsync(_lifetime.Token);
        try
        {
            if (await _updateBookmarksAsync(Profile.Id, static _ => false) is { } latest)
            {
                Profile.DirectoryBookmarks = latest;
                RebuildBookmarks();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 读不到就先用手里那份,别拖累后面的目录加载;下一次收藏操作落盘时会换成库里的。
        }
        finally
        {
            _bookmarkGate.Release();
        }
    }

    private List<RemoteDirectoryBookmark> SnapshotBookmarks() =>
        [.. (Profile.DirectoryBookmarks ?? []).Select(static bookmark => bookmark.Clone())];

    private void RebuildBookmarks()
    {
        Bookmarks.Clear();
        foreach (RemoteDirectoryBookmark bookmark in Profile.DirectoryBookmarks ?? [])
        {
            Bookmarks.Add(new DirectoryBookmarkViewModel(bookmark));
        }
        this.RaisePropertyChanged(nameof(HasBookmarks));
    }

    private static string DefaultBookmarkName(string remotePath)
    {
        string trimmed = remotePath.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return Strings.Get("Bookmark_RootName");
        }
        int slash = trimmed.LastIndexOf('/');
        return trimmed[(slash + 1)..];
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalFilePaneViewModel.CurrentPath) or nameof(FileBrowserViewModel.CurrentPath))
        {
            CompareSummary = null;
        }
    }

    /// <summary>用于建立连接的会话配置。</summary>
    public SessionProfile Profile { get; }
    /// <summary>当前活跃的 SSH 会话;FTP / S3 文档没有 SSH 会话,为 null。</summary>
    public SshSession? Session { get; }

    /// <summary>
    /// 标签页状态灯的连接状态。必须由本视图模型给出,不能让界面直接绑 <c>Session.Status</c>:
    /// FTP / S3 文档的 <see cref="Session" /> 是 null,那样每开一个这类标签都会刷一条
    /// <c>An error occurred binding 'Fill' to 'ViewModel.Session.Status': 'Value is null.'</c>,
    /// 而且那颗灯自始至终不上色。
    /// </summary>
    /// <remarks>
    /// 这两种协议都没有长驻的会话对象可读,而文档只在登录成功后才建出来,因此按「已连接」呈现;
    /// 掉线由文件面板的错误提示承担,侧边栏树上的状态圆点则由各自的
    /// <c>SessionStateChanged</c> 事件驱动(见 <c>MainWindowViewModel</c>)。
    /// </remarks>
    public SessionStatus Status => Session?.Status ?? SessionStatus.Connected;
    /// <summary>SSH 会话的唯一标识。</summary>
    public Guid SessionId { get; }

    /// <inheritdoc />
    public IReadOnlyList<Guid> SessionIds => [SessionId];

    /// <inheritdoc />
    public IReadOnlyList<FileBrowserViewModel> RemotePanes => [RemoteFiles];
    /// <summary>SFTP 文档标签页的显示标题。</summary>
    public string Title { get; }
    /// <summary>本地文件浏览器面板。</summary>
    public LocalFilePaneViewModel LocalFiles { get; }
    /// <summary>远程文件浏览器面板。</summary>
    public FileBrowserViewModel RemoteFiles { get; }
    /// <summary>将选中的本地文件上传到远程服务器的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> UploadSelectedCommand { get; }
    /// <summary>将选中的远程文件下载到本机的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DownloadSelectedCommand { get; }
    /// <summary>删除选中的本地文件的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteLocalSelectedCommand { get; }
    /// <summary>确认本地文件删除的回调。</summary>
    public Func<string, Task<bool>>? ConfirmLocalDelete { get; set; }

    internal Task InitialLoadTask { get; }

    /// <summary>将选中的本地文件上传到远程服务器。</summary>
    public async Task UploadSelectedAsync(CancellationToken cancellationToken = default)
    {
        string[] paths = [.. LocalFiles.SelectedEntries
            .Where(entry => !entry.IsParentEntry)
            .Select(entry => entry.FullPath)];
        await RemoteFiles.UploadLocalPathsAsync(paths, cancellationToken);
    }

    /// <summary>将选中的远程文件下载到本机。</summary>
    public async Task DownloadSelectedAsync(CancellationToken cancellationToken = default)
    {
        RemoteFileInfoViewModel[] entries = [.. RemoteFiles.SelectedFiles.Where(entry => !entry.IsParentEntry)];
        if (entries.Length == 0 || string.IsNullOrWhiteSpace(LocalFiles.CurrentPath))
        {
            return;
        }
        await RemoteFiles.DownloadRemoteEntriesAsync(entries, LocalFiles.CurrentPath, cancellationToken);
        if (RemoteFiles.ErrorMessage is null)
        {
            await LocalFiles.RefreshAsync(cancellationToken);
        }
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SshSession.Status) or null)
        {
            this.RaisePropertyChanged(nameof(Status));
        }
    }

    /// <summary>分离文件浏览器并取消生命周期令牌。</summary>
    public void Detach()
    {
        _lifetime.Cancel();
        // 同步窗口与「保持远端最新」的监视都挂在这条连接上:连接要走了,它们得先停。
        ActiveSync?.Dispose();
        Detached?.Invoke(this, EventArgs.Empty);
        LocalFiles.PropertyChanged -= OnPanePropertyChanged;
        RemoteFiles.PropertyChanged -= OnPanePropertyChanged;
        if (Session is { } session)
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
        }
        RemoteFiles.Detach();
        // Dispose 而不只是 Detach:本地面板还挂着一个 FileSystemWatcher(目录自动刷新),
        // 不释放就是一个跟着文档一起泄漏的系统句柄。
        LocalFiles.Dispose();
    }

    /// <summary>关闭 SFTP 会话并清理资源,确保仅执行一次。</summary>
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

    /// <summary>异步释放 SFTP 文档视图模型。</summary>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private async Task LoadAsync()
    {
        await RefreshBookmarksAsync();
        await LocalFiles.LoadInitialAsync(_lifetime.Token);
        await RemoteFiles.LoadInitialAsync(_lifetime.Token);
    }

    private async Task CloseCoreAsync()
    {
        Detach();

        // 本会话名下还开着的远程编辑会话先收尾:把没传完的传掉,再停止监视。
        // 顺序要紧 —— 得赶在 SFTP 连接被关掉之前,否则那几次补传注定失败,
        // 用户只会看到一串"上传失败",而改动其实本来传得上去。
        await Services.RemoteEditSessionManager.CloseScopeAsync(SessionId).ConfigureAwait(false);
        try
        {
            await _serializedSftp.CloseAsync().ConfigureAwait(false);

            // 等初始加载收尾,但**不能**把它的取消当成关闭失败上报:上面的 Detach() 刚刚
            // 取消了 _lifetime,若初始加载还在飞(开标签后立刻关就会这样),它必然以
            // OperationCanceledException 收场 —— 那是本方法自己造成的,不是错误。
            // 其它异常照常抛出。
            //
            // 实测栈(2026-07-22,SftpDocumentClose_WhenCallerWaitIsCancelled_... 偶发红):
            //   LocalFilePaneViewModel.RefreshRootsAsync → LoadInitialAsync → LoadAsync
            //   → CloseCoreAsync 原样抛出 TaskCanceledException。
            // 注意本地栏与远程栏在这点上不一致:远程栏内部吞掉了自身的取消,本地栏没有,
            // 所以只有本地栏还在飞时才会炸 —— 这也是它只在混跑时偶发的原因。
            // 无对应单测:要稳定复现须把 ILocalRootProvider 从 SftpDocumentViewModel 一路穿透
            // 注入进 LocalFilePaneViewModel(目前只有后者的双参构造有这个注入点),
            // 属于为可测性改生产 API,暂未做。
            try
            {
                await InitialLoadTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
        finally
        {
            await _disconnectAsync(SessionId, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
