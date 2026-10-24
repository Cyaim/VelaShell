using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>搜索结果里的一行。</summary>
/// <param name="hit">搜到的那一条。</param>
public sealed class RemoteSearchResultViewModel(RemoteSearchHit hit)
{
    /// <summary>远端绝对路径。</summary>
    public string Path { get; } = hit.Path;

    /// <summary>最后一段名字。</summary>
    public string Name { get; } = hit.Path == "/" ? "/" : hit.Path[(hit.Path.LastIndexOf('/') + 1)..];

    /// <summary>所在目录。</summary>
    public string Directory { get; } = RemotePath.Parent(hit.Path);

    /// <summary>是不是目录。</summary>
    public bool IsDirectory { get; } = hit.IsDirectory;
}

/// <summary>
/// 文件面板的「搜索」窗口:按名字(<c>find</c>)或内容(<c>find … -exec grep</c>)在远端找,结果点了在面板里定位。
/// </summary>
/// <remarks>
/// <para>
/// 一个面板一个窗口、一个视图模型;同一时刻只跑一次搜索,再点「搜索」先把上一次停掉。
/// 结果逐条进列表(远端每吐一行就回调一次),所以慢的搜索也立刻看得到东西,停下时已经找到的照样留着。
/// </para>
/// <para>
/// 条数与时间的上限在服务那一层(<see cref="RemoteSearch.DefaultMaxResults" /> / <see cref="RemoteSearchService.DefaultTimeLimit" />),
/// 状态行如实说出收场的原因 —— 到上限、超时、远端不是 POSIX shell、缺工具,各说各的,不混成「没找到」。
/// </para>
/// </remarks>
public sealed class RemoteSearchViewModel : ReactiveObject, IDisposable
{
    private readonly IRemoteSearchService _service;
    private readonly FileBrowserViewModel _pane;
    private readonly Func<Action, Task> _onUi;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>为一个文件面板建搜索视图模型。</summary>
    /// <param name="service">远端搜索服务。</param>
    /// <param name="pane">结果在哪个面板里定位;起始的搜索位置是它当前的目录。</param>
    /// <param name="onUi">把一段改界面状态的动作送回 UI 线程;null = Avalonia 的 UI 线程(单测里同步执行)。</param>
    public RemoteSearchViewModel(IRemoteSearchService service, FileBrowserViewModel pane, Func<Action, Task>? onUi = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _onUi = onUi ?? (action => Dispatcher.UIThread.InvokeAsync(action).GetTask());
        Root = pane.CurrentPath;
        StatusText = Strings.Format("Sftp_SearchHint", RemoteSearch.DefaultMaxResults, (int)RemoteSearchService.DefaultTimeLimit.TotalSeconds);
        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync);
        StopCommand = ReactiveCommand.Create(Stop, this.WhenAnyValue(x => x.IsSearching));
        RevealCommand = ReactiveCommand.CreateFromTask<RemoteSearchResultViewModel?>(RevealAsync);
    }

    /// <summary>结果在哪个面板里定位(视图据此认出「这个面板已经开着一扇搜索窗口」)。</summary>
    internal FileBrowserViewModel Pane => _pane;

    /// <summary>服务器名(窗口标题里点明是哪一台)。</summary>
    public string ServerName => _pane.ServerDisplayName;

    /// <summary>从哪个目录找起(远端绝对路径)。</summary>
    public string Root
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "/";

    /// <summary>文件名模式;不带通配符时按「名字里含有」找。</summary>
    public string? NamePattern
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>文件内容里要有的文字(按字面);空 = 只按名字找。</summary>
    public string? ContentText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>名字与内容都不区分大小写(默认开:找文件时很少有人记得住大小写)。</summary>
    public bool IgnoreCase
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>正在搜。</summary>
    public bool IsSearching
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>状态行。</summary>
    public string? StatusText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>状态行说的是一个问题(不支持、出错、结果不全),界面用警示色。</summary>
    public bool IsStatusWarning
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>结果。</summary>
    public ObservableCollection<RemoteSearchResultViewModel> Results { get; } = [];

    /// <summary>开始搜(正在搜时先停掉上一次)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SearchCommand { get; }

    /// <summary>停下当前这次搜索;已经找到的留着。</summary>
    public ReactiveCommand<RxVoid, RxVoid> StopCommand { get; }

    /// <summary>在面板里定位到一条结果。</summary>
    public ReactiveCommand<RemoteSearchResultViewModel?, RxVoid> RevealCommand { get; }

    /// <summary>搜一次。</summary>
    public async Task SearchAsync()
    {
        if (_disposed)
        {
            return;
        }
        string root = Root?.Trim() ?? string.Empty;
        if (!root.StartsWith('/'))
        {
            SetStatus(Strings.Get("Sftp_SearchRootNotAbsolute"), warning: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(NamePattern) && string.IsNullOrEmpty(ContentText))
        {
            SetStatus(Strings.Get("Sftp_SearchNeedCriteria"), warning: true);
            return;
        }
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        Results.Clear();
        IsSearching = true;
        SetStatus(Strings.Format("Sftp_SearchRunning", 0), warning: false);
        var query = new RemoteSearchQuery(root, NamePattern, ContentText, IgnoreCase);
        RemoteSearchOutcome outcome;
        try
        {
            outcome = await _service.SearchAsync(
                _pane.SessionId,
                query,
                hit => new ValueTask(_onUi(() =>
                {
                    // 被下一次搜索顶掉之后还在路上的那几条不能混进新结果里。
                    if (ReferenceEquals(_cts, cts))
                    {
                        Results.Add(new(hit));
                        StatusText = Strings.Format("Sftp_SearchRunning", Results.Count);
                    }
                })),
                cts.Token);
        }
        catch (Exception ex)
        {
            outcome = new(RemoteSearchStatus.Failed, Results.Count, ex.Message);
        }
        if (!ReferenceEquals(_cts, cts))
        {
            // 已经有新的一次搜索接手了状态行。
            cts.Dispose();
            return;
        }
        _cts = null;
        cts.Dispose();
        IsSearching = false;
        ShowOutcome(outcome);
    }

    /// <summary>停下当前这次搜索。</summary>
    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经收尾了。
        }
    }

    /// <summary>在面板里定位到一条结果。</summary>
    /// <param name="result">那一条;null 时什么也不做。</param>
    public async Task RevealAsync(RemoteSearchResultViewModel? result)
    {
        if (result is null || _disposed)
        {
            return;
        }
        await _pane.RevealAsync(result.Path, result.IsDirectory);
    }

    /// <summary>窗口关了:停下还在跑的搜索。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
    }

    private void ShowOutcome(RemoteSearchOutcome outcome)
    {
        int seconds = (int)RemoteSearchService.DefaultTimeLimit.TotalSeconds;
        (string text, bool warning) = outcome.Status switch
        {
            RemoteSearchStatus.Completed => (Strings.Format("Sftp_SearchDone", outcome.Count), false),
            RemoteSearchStatus.Truncated => (Strings.Format("Sftp_SearchTruncated", outcome.Count), true),
            RemoteSearchStatus.TimedOut => (Strings.Format("Sftp_SearchTimedOut", seconds, outcome.Count), true),
            RemoteSearchStatus.Cancelled => (Strings.Format("Sftp_SearchCancelled", outcome.Count), false),
            RemoteSearchStatus.NoSession => (Strings.Get("Sftp_SearchNoSession"), true),
            RemoteSearchStatus.NotPosix => (Strings.Get("Sftp_SearchNotPosix"), true),
            RemoteSearchStatus.ShellUnknown => (Strings.Get("Sftp_SearchShellUnknown"), true),
            RemoteSearchStatus.ToolMissing => (Strings.Get("Sftp_SearchToolMissing"), true),
            _ => (Strings.Format("Sftp_SearchFailed", outcome.Detail ?? string.Empty), true),
        };
        SetStatus(text, warning);
    }

    private void SetStatus(string text, bool warning)
    {
        StatusText = text;
        IsStatusWarning = warning;
    }
}
