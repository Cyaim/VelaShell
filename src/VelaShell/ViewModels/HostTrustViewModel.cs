using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.HostKeys;

namespace VelaShell.ViewModels;

/// <summary>设置 → 安全审计「受信主机 CA」表格里的一行。</summary>
/// <param name="Marker">信任库里的那条记录。</param>
public sealed record KnownHostMarkerRow(KnownHostMarker Marker)
{
    /// <summary>是不是吊销记录(行上换成告警色的标签)。</summary>
    public bool IsRevoked => Marker.Kind == KnownHostMarkerKind.Revoked;

    /// <summary>种类标签:受信 CA / 已吊销。</summary>
    public string KindText => IsRevoked ? Strings.Get("HostCa_KindRevoked") : Strings.Get("HostCa_KindAuthority");

    /// <summary>主机名模式(原文)。</summary>
    public string Patterns => Marker.HostPatterns;

    /// <summary>公钥类型。</summary>
    public string KeyType => Marker.KeyType;

    /// <summary>公钥指纹;本版认不得的类型没有指纹时写类型本身。</summary>
    public string Fingerprint => Marker.Fingerprint.Length > 0 ? Marker.Fingerprint : Marker.KeyType;

    /// <summary>注释。</summary>
    public string Comment => Marker.Comment;

    /// <summary>有没有注释(没有就不占那一行)。</summary>
    public bool HasComment => Marker.Comment.Length > 0;
}

/// <summary>
/// 设置 → 安全审计里与 OpenSSH 互通的那几件事:受信主机 CA(<c>@cert-authority</c>)与吊销记录的增删,
/// 以及 <c>known_hosts</c> 的导入 / 导出。
/// </summary>
/// <remarks>
/// <para>
/// 与「已信任主机」列表同一种做法:操作当场生效,不随设置窗口的「保存」走 —— 这是信任数据,不是偏好设置。
/// 导入会往「已信任主机」里加记录,所以导入之后回调 <c>knownHostsChanged</c> 让那张表重读。
/// </para>
/// <para>
/// 文件选择在视图里做(要 <c>TopLevel</c>),这里只收路径;读写文件的失败如实写进 <see cref="StatusText" />。
/// </para>
/// </remarks>
public sealed class HostTrustViewModel : ReactiveObject
{
    /// <summary>
    /// 导入文件的大小上限。几千台主机的 known_hosts 也就几 MB;再大多半是选错了文件,整份读进内存解析纯属白费。
    /// </summary>
    internal const long MaxImportBytes = 16L * 1024 * 1024;

    private readonly IHostKeyService? _hostKeys;
    private readonly ISessionRepository? _sessions;
    private readonly Func<Task>? _knownHostsChanged;

    /// <summary>创建视图模型;信任库缺席(设计期、无 UI 单测)时整块只显示空表、按钮不可用。</summary>
    /// <param name="hostKeys">信任库。</param>
    /// <param name="sessions">连接仓储:导入时拿已保存连接的 host:port 去对散列过的主机名。</param>
    /// <param name="knownHostsChanged">导入改动了「已信任主机」之后调它,让那张表重读。</param>
    public HostTrustViewModel(IHostKeyService? hostKeys, ISessionRepository? sessions, Func<Task>? knownHostsChanged)
    {
        _hostKeys = hostKeys;
        _sessions = sessions;
        _knownHostsChanged = knownHostsChanged;
        IObservable<bool> available = this.WhenAnyValue(x => x.IsBusy, busy => !busy && hostKeys is not null);
        AddMarkerCommand = ReactiveCommand.CreateFromTask(AddMarkerAsync, available);
        RemoveMarkerCommand = ReactiveCommand.CreateFromTask<KnownHostMarkerRow>(RemoveMarkerAsync);
    }

    /// <summary>信任库在(按钮据此可用)。</summary>
    public bool IsAvailable => _hostKeys is not null;

    /// <summary>导入文件选择框默认打开的位置:<c>~/.ssh/known_hosts</c>。</summary>
    public static string DefaultKnownHostsPath => KnownHostsFile.DefaultPath;

    /// <summary>受信主机 CA 与吊销记录。</summary>
    public ObservableCollection<KnownHostMarkerRow> Markers { get; } = [];

    /// <summary>有没有记录(控制空状态与表格的显示)。</summary>
    public bool HasMarkers
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>添加 CA:主机名模式。</summary>
    public string NewPatterns
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>添加 CA:公钥行(CA 的 <c>.pub</c>),或从 <c>known_hosts</c> 整行复制来的 <c>@cert-authority …</c>。</summary>
    public string NewPublicKey
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>添加 CA:说明(可空)。</summary>
    public string NewComment
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>添加不成立时的原因;成立时为空。</summary>
    public string AddError
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasAddError));
        }
    } = string.Empty;

    /// <summary>有没有添加错误要显示。</summary>
    public bool HasAddError => AddError.Length > 0;

    /// <summary>导出时把主机名散列成 <c>|1|…</c>(OpenSSH 的 <c>HashKnownHosts yes</c>)。默认关:散列之后人就读不出是哪台了。</summary>
    public bool HashHostNamesOnExport
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>最近一次导入 / 导出 / 添加的结果说明;没有时为空。</summary>
    public string StatusText
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasStatus));
        }
    } = string.Empty;

    /// <summary>有没有结果说明要显示。</summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>导入 / 导出进行中(按钮灰掉,防连点导入两遍)。</summary>
    public bool IsBusy
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>按输入框添加一条受信 CA(或整行复制来的吊销记录)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> AddMarkerCommand { get; }

    /// <summary>删掉一条受信 CA / 吊销记录,当场生效。</summary>
    public ReactiveCommand<KnownHostMarkerRow, RxVoid> RemoveMarkerCommand { get; }

    /// <summary>重读受信 CA 与吊销记录。读失败不阻塞设置页其余功能。</summary>
    public async Task RefreshAsync()
    {
        if (_hostKeys is null)
        {
            return;
        }
        try
        {
            IReadOnlyList<KnownHostMarker> markers = await _hostKeys.GetMarkersAsync();
            Markers.Clear();
            // 先置可见再入列:与「已信任主机」列表同一个坑(IsVisible=false 期间灌进去的行占住高度却画不出来)。
            HasMarkers = markers.Count > 0;
            foreach (KnownHostMarker marker in markers
                         .OrderBy(static m => m.Kind == KnownHostMarkerKind.Revoked)
                         .ThenBy(static m => m.HostPatterns, StringComparer.OrdinalIgnoreCase))
            {
                Markers.Add(new KnownHostMarkerRow(marker));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] host CA list load failed: {ex}");
        }
        HasMarkers = Markers.Count > 0;
    }

    /// <summary>导入一份 <c>known_hosts</c> 文件:普通行进「已信任主机」,<c>@cert-authority</c> / <c>@revoked</c> 进这张表。</summary>
    /// <param name="path">文件路径。</param>
    public async Task ImportFromFileAsync(string path)
    {
        if (_hostKeys is null || IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            string content;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    StatusText = Strings.Format("KnownHosts_ImportFailed", path);
                    return;
                }
                if (info.Length > MaxImportBytes)
                {
                    StatusText = Strings.Get("KnownHosts_ImportTooLarge");
                    return;
                }
                content = await File.ReadAllTextAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                StatusText = Strings.Format("KnownHosts_ImportFailed", ex.Message);
                return;
            }

            KnownHostsImportResult result;
            try
            {
                result = await KnownHostsInterop.ImportAsync(_hostKeys, content, await CandidatesAsync());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusText = Strings.Format("KnownHosts_ImportFailed", ex.Message);
                return;
            }
            StatusText = DescribeImport(result);
            await RefreshAsync();
            if (_knownHostsChanged is not null)
            {
                await _knownHostsChanged();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把信任库导出成 OpenSSH 格式的 <c>known_hosts</c> 文件。</summary>
    /// <param name="path">目标文件(由保存框选定,已确认覆盖)。</param>
    public async Task ExportToFileAsync(string path)
    {
        if (_hostKeys is null || IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            KnownHostsExport export = await KnownHostsInterop.ExportAsync(_hostKeys, HashHostNamesOnExport);
            await File.WriteAllTextAsync(path, export.Content);
            StatusText = DescribeExport(export, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = Strings.Format("KnownHosts_ExportFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddMarkerAsync()
    {
        if (_hostKeys is null)
        {
            return;
        }
        if (!KnownHostMarkers.TryCreate(NewPatterns, NewPublicKey, NewComment, out KnownHostMarker? marker, out string? error))
        {
            AddError = error;
            return;
        }
        try
        {
            if (!await _hostKeys.AddMarkerAsync(marker))
            {
                AddError = Strings.Get("HostCa_ErrDuplicate");
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddError = ex.Message;
            return;
        }
        AddError = string.Empty;
        NewPatterns = string.Empty;
        NewPublicKey = string.Empty;
        NewComment = string.Empty;
        StatusText = Strings.Format(
            marker.Kind == KnownHostMarkerKind.CertificateAuthority ? "HostCa_Added" : "HostCa_RevokedAdded",
            marker.HostPatterns);
        await RefreshAsync();
    }

    private async Task RemoveMarkerAsync(KnownHostMarkerRow row)
    {
        if (_hostKeys is null)
        {
            return;
        }
        try
        {
            await _hostKeys.RemoveMarkerAsync(row.Marker.Id);
            Markers.Remove(row);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] host CA removal failed: {ex}");
        }
        HasMarkers = Markers.Count > 0;
    }

    /// <summary>用来对散列主机名的 host:port:已保存的 SSH / SFTP 连接,加上已信任的主机。读不到的那一路当没有。</summary>
    private async Task<List<(string Host, int Port)>> CandidatesAsync()
    {
        List<(string Host, int Port)> candidates = [];
        if (_sessions is not null)
        {
            try
            {
                candidates.AddRange((await _sessions.GetAllSessionsAsync())
                    .Where(static s => s.ConnectionType is ConnectionType.SSH or ConnectionType.SFTP && s.Host.Length > 0)
                    .Select(static s => (s.Host, s.Port)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Trace.WriteLine($"[VelaShell] session list load failed: {ex}");
            }
        }
        if (_hostKeys is not null)
        {
            candidates.AddRange((await _hostKeys.GetKnownHostsAsync()).Select(static h => (h.Host, h.Port)));
        }
        return candidates;
    }

    /// <summary>导入结果的一句话:做了什么在前,没做的逐项说明原因(只列不为零的)。</summary>
    internal static string DescribeImport(KnownHostsImportResult result)
    {
        List<string> parts =
        [
            Strings.Format("KnownHosts_ImportDone", result.Imported, result.Authorities, result.Revocations),
        ];
        if (result.AlreadyTrusted + result.MarkersAlreadyPresent > 0)
        {
            parts.Add(Strings.Format("KnownHosts_ImportAlready", result.AlreadyTrusted + result.MarkersAlreadyPresent));
        }
        if (result.Conflicts > 0)
        {
            parts.Add(Strings.Format("KnownHosts_ImportConflicts", result.Conflicts));
        }
        if (result.RevokedSkipped > 0)
        {
            parts.Add(Strings.Format("KnownHosts_ImportRevokedSkipped", result.RevokedSkipped));
        }
        if (result.HashedUnmatched > 0)
        {
            parts.Add(Strings.Format("KnownHosts_ImportHashedUnmatched", result.HashedUnmatched));
        }
        if (result.Wildcards > 0)
        {
            parts.Add(Strings.Format("KnownHosts_ImportWildcards", result.Wildcards));
        }
        if (result.Unsupported > 0)
        {
            parts.Add(Strings.Format("KnownHosts_ImportUnsupported", result.Unsupported));
        }
        return string.Join(Environment.NewLine, parts);
    }

    /// <summary>导出结果的一句话;只记了指纹、导不出来的单独说一声(下次连上那台主机会补上)。</summary>
    internal static string DescribeExport(KnownHostsExport export, string path)
    {
        string done = Strings.Format("KnownHosts_ExportDone", export.HostKeys, export.Markers, path);
        int skipped = export.MissingPublicKey + export.Unrecordable;
        return skipped == 0 ? done : done + Environment.NewLine + Strings.Format("KnownHosts_ExportSkipped", skipped);
    }
}
