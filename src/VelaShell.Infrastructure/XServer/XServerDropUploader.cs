using System.Diagnostics;
using System.Globalization;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.XServer;

/// <summary>
/// 本机的文件拖进经 SSH 转发来的 X 程序的窗口(F16):文件在本机,程序在远端,远端程序打不开本机路径 ——
/// 先经那个 SSH 会话的 SFTP 传到远端的一个临时目录,再把<b>远端路径</b>交给程序(XDND 的 <c>text/uri-list</c>)。
/// 这是同时有 SSH、SFTP 与 X 服务端才做得到的结合。
/// </summary>
public interface IXServerDropUploader
{
    /// <summary>
    /// 这个连接名(X 程序的 <see cref="VelaShell.XServer.XTopLevelSnapshot.ClientLabel" />,形如 <c>user@host:22</c>)对应的 SSH 会话还连着吗
    /// —— 连着才传得上去。
    /// </summary>
    /// <param name="clientLabel">连接名。</param>
    bool CanUpload(string clientLabel);

    /// <summary>
    /// 把本机的文件与目录(目录连同里面的文件)传到远端一个新建的临时目录,返回远端路径(与 <paramref name="localPaths" /> 同序;
    /// 本机已经不存在的跳过)。会话不在时为 <see langword="null" />。传输失败抛出底层的异常。
    /// 临时目录什么时候删见实现(<see cref="SftpXServerDropUploader" />)。
    /// </summary>
    /// <param name="clientLabel">连接名(见 <see cref="CanUpload" />)。</param>
    /// <param name="localPaths">本机的文件或目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<string>?> UploadAsync(string clientLabel, IReadOnlyList<string> localPaths, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IXServerDropUploader" /> 的实现:按连接名找连着的 SSH 会话(连接器给的名字就是 <c>用户名@主机:端口</c>),
/// 经 <see cref="ISftpService" /> 传到 <c>/tmp/velashell-drop-随机串/</c>(目录权限 0700,别的用户看不到)。
/// </summary>
/// <remarks>
/// <para>
/// <b>什么时候删</b>:拖放一结束就删太早 —— 程序可能稍后才读(先弹个对话框问怎么打开),编辑器还会往原处存;拖进终端的是一条路径,
/// 用户随后才拿它敲命令。经 SSH 转发来的 X 程序走的是那个会话的 X11 通道,会话一断它们也跟着断开,所以
/// <b>拖放那一刻连着这台主机(同一个连接名)的会话全都断了</b>,那一批文件就没有程序会用了。会话断了远端也就够不着了,
/// 于是在下一次够得着的时候删:<b>又连上同一个连接名</b>时,以及<b>下一次往那台主机拖放</b>时(在后台做,不耽误这一次)。
/// 传输失败、取消、一个文件都没传上的,程序根本没拿到路径,当场删。
/// </para>
/// <para>
/// 本进程不知道的(应用崩溃或被结束、上一次运行留下的、本功能之前就堆着的、别的电脑上的 VelaShell 留下的)在往那台主机拖放时一并清:
/// 只删 <c>/tmp</c> 下名字正好是 <c>velashell-drop-</c> 加 32 位小写十六进制的<b>真目录</b>(不是符号链接)、属主与这一次新建的目录相同、
/// 而且按远端自己的时钟一天以前就没再动过的(与拖出方向的本机临时目录同一口径,<see cref="SftpXServerDragDownloader.Retention" />)。
/// </para>
/// <para>
/// 删除一律经 <see cref="ISftpService.DeleteAsync" />(先试一条单引号包好路径的 <c>rm -rf --</c>,不行退回 SFTP 逐个删,
/// 符号链接只删链接本身),而且路径先过 <see cref="IsDropDirectory" />:不是自己建的那种名字一律不删 —— 变量空了也变不成 <c>/tmp/</c>。
/// 删不掉不报错(权限被改过、会话恰好又断了):本进程记着的那批留到下一次机会再删,三次都不行就不再记着,等一天以后按上面的口径清。
/// </para>
/// </remarks>
public sealed class SftpXServerDropUploader : IXServerDropUploader
{
    /// <summary>远端临时目录的前缀;后面是 32 位小写十六进制(<see cref="Guid.ToString(string)" /> 的 <c>N</c>)。</summary>
    internal const string DirectoryPrefix = "/tmp/velashell-drop-";

    /// <summary>本进程不知道的遗留目录,(按远端时钟)这么久没再动过才清。</summary>
    internal static readonly TimeSpan LeftoverRetention = TimeSpan.FromDays(1);

    /// <summary>一次删除至多等这么久(会话半死不活时别让后台清扫一直挂着)。</summary>
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(30);

    /// <summary>本进程记着的一批删了这么多次都没删掉,就不再记着(交给遗留目录的口径)。</summary>
    private const int MaxDeleteAttempts = 3;

    private readonly ISshConnectionService _connections;
    private readonly ISftpService _sftp;
    private readonly TimeProvider _time;

    /// <summary>保护 <see cref="_batches" /> 与 <see cref="_sweeping" />。</summary>
    private readonly Lock _gate = new();

    /// <summary>本进程传上去、还没删的批次。</summary>
    private readonly List<DropBatch> _batches = [];

    /// <summary>正在后台清扫的连接名(同一个连接名同一时刻只扫一遍)。</summary>
    private readonly HashSet<string> _sweeping = [with(StringComparer.Ordinal)];

    /// <summary>
    /// 一批拖进去的文件:传到哪个连接名的哪个目录、什么时候(本机 UTC);<see cref="Abandoned" /> = 程序没拿到路径,随时可删;
    /// <see cref="Failures" /> = 已经删过几次没删掉。
    /// </summary>
    private sealed record DropBatch(string Label, string Directory, DateTime CreatedAt, bool Abandoned, int Failures = 0);

    /// <summary>创建上传器;订阅会话连上的事件,在重新连上同一个连接名时清掉上次会话留下的目录。</summary>
    /// <param name="connections">SSH 连接服务。</param>
    /// <param name="sftp">远程文件服务。</param>
    /// <param name="time">本机时钟(与 <see cref="SshSession.ConnectedAt" /> 比先后);默认系统时钟。</param>
    public SftpXServerDropUploader(ISshConnectionService connections, ISftpService sftp, TimeProvider? time = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));
        _time = time ?? TimeProvider.System;
        _connections.SessionConnected += OnSessionConnected;
    }

    /// <summary>本机此刻(UTC,与 <see cref="SshSession.ConnectedAt" /> 同一个钟)。</summary>
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>最近一次启动的后台清扫(用例等它做完);没有时是已完成的任务。</summary>
    internal Task Sweeping { get; private set; } = Task.CompletedTask;

    /// <summary>连接器给连接起的名字(与 SSH 包装器的 <c>target</c> 同一个写法:<see cref="HostAddress.FormatTarget" />,IPv6 带方括号)。</summary>
    internal static string LabelOf(ConnectionInfo info) => HostAddress.FormatTarget(info.Username, info.Host, info.Port);

    /// <summary>
    /// 这个路径是不是本上传器建的那种临时目录:<c>/tmp/velashell-drop-</c> 加正好 32 位小写十六进制,别的什么都没有
    /// (没有结尾的斜杠、没有下一级、没有 <c>..</c>)。删除之前一律先过这一关。
    /// </summary>
    /// <param name="path">远端路径。</param>
    internal static bool IsDropDirectory(string? path)
    {
        if (path is null || path.Length != DirectoryPrefix.Length + 32 || !path.StartsWith(DirectoryPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        foreach (char c in path.AsSpan(DirectoryPrefix.Length))
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }
        return true;
    }

    private SshSession? SessionFor(string clientLabel) =>
        _connections.Sessions.FirstOrDefault(s => s.Status == SessionStatus.Connected && LabelOf(s.ConnectionInfo) == clientLabel);

    /// <inheritdoc />
    public bool CanUpload(string clientLabel) => SessionFor(clientLabel) is not null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> UploadAsync(string clientLabel, IReadOnlyList<string> localPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localPaths);
        if (SessionFor(clientLabel) is not { } session)
        {
            return null;
        }
        Guid id = session.SessionId;
        string directory = $"{DirectoryPrefix}{Guid.NewGuid():N}";
        await _sftp.CreateDirectoryAsync(id, directory, cancellationToken).ConfigureAwait(false);
        List<string> remote = [];
        try
        {
            await _sftp.SetPermissionsAsync(id, directory, 700, cancellationToken).ConfigureAwait(false);   // chmod 记法(见 ISftpService)
            HashSet<string> names = [with(StringComparer.Ordinal)];
            foreach (string local in localPaths)
            {
                string name = UniqueName(names, Path.GetFileName(local.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                string target = $"{directory}/{name}";
                if (File.Exists(local))
                {
                    await _sftp.UploadFileAsync(id, local, target, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                else if (Directory.Exists(local))
                {
                    await UploadDirectoryAsync(id, local, target, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    continue;   // 拖着的时候还在,放下时已经没了
                }
                remote.Add(target);
            }
        }
        catch
        {
            // 传了一半(或取消了):程序拿不到路径,这个目录没人用 —— 在后台删,不耽误把原来的异常报给用户。
            Track(new DropBatch(clientLabel, directory, Now, Abandoned: true));
            StartSweep(id, clientLabel, fresh: null);
            throw;
        }
        // 一个都没传上(全在放下之前没了)同样当场不要了;否则记下来,等拖放时连着的会话都断了再删。
        Track(new DropBatch(clientLabel, directory, Now, Abandoned: remote.Count == 0));
        StartSweep(id, clientLabel, fresh: remote.Count > 0 ? directory : null);
        return remote;
    }

    private void Track(DropBatch batch)
    {
        lock (_gate)
        {
            _batches.Add(batch);
        }
    }

    /// <summary>
    /// 一个会话连上了:这个连接名下有本进程记着、已经没人用的目录(上次会话断了),借这个新会话删掉。
    /// 在建连的后台线程上触发,只看一眼内存里的表;真有要删的才到线程池上去做。
    /// </summary>
    private void OnSessionConnected(SshSession session)
    {
        string label = LabelOf(session.ConnectionInfo);
        if (DueBatches(label).Length > 0)
        {
            StartSweep(session.SessionId, label, fresh: null);
        }
    }

    /// <summary>
    /// 这个连接名下可以删的批次:程序没拿到路径的,或者拖放那一刻连着这个连接名的会话全都断了的 ——
    /// 还连着的会话里只要有一个在那一刻之前(含)就连上了,它上面的 X 程序就可能是拿到路径的那个,留着。
    /// 连接时刻不明的会话按「早就连着」算。
    /// </summary>
    private DropBatch[] DueBatches(string label)
    {
        DateTime?[] live = [.. _connections.Sessions
            .Where(s => s.Status == SessionStatus.Connected && LabelOf(s.ConnectionInfo) == label)
            .Select(s => s.ConnectedAt)];
        lock (_gate)
        {
            return [.. _batches.Where(b => b.Label == label && (b.Abandoned || !live.Any(at => at is not { } connected || connected <= b.CreatedAt)))];
        }
    }

    /// <summary>在线程池上清扫这个连接名(已经在扫的不再起一遍:没赶上的下一次再删)。</summary>
    /// <param name="sessionId">借来删除的那个会话。</param>
    /// <param name="label">连接名。</param>
    /// <param name="fresh">刚建好、正在用的目录:有它时顺带清本进程不知道的遗留目录(属主与远端的「现在」都从它取)。</param>
    private void StartSweep(Guid sessionId, string label, string? fresh)
    {
        lock (_gate)
        {
            if (!_sweeping.Add(label))
            {
                return;
            }
        }
        Sweeping = Task.Run(() => SweepAsync(sessionId, label, fresh));
    }

    private async Task SweepAsync(Guid sessionId, string label, string? fresh)
    {
        try
        {
            foreach (DropBatch batch in DueBatches(label))
            {
                // 没删掉的(会话恰好又断了、权限被改过)留到下一次,几次都不行就不再记着,一天以后按遗留目录的口径清。
                bool removed = await TryDeleteAsync(sessionId, batch.Directory).ConfigureAwait(false);
                lock (_gate)
                {
                    int at = _batches.IndexOf(batch);
                    if (at < 0)
                    {
                        continue;
                    }
                    if (removed || batch.Failures + 1 >= MaxDeleteAttempts)
                    {
                        _batches.RemoveAt(at);
                    }
                    else
                    {
                        _batches[at] = batch with { Failures = batch.Failures + 1 };
                    }
                }
            }
            if (fresh is not null)
            {
                await SweepLeftoversAsync(sessionId, fresh).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[XServer] cleaning up dropped files on {label} failed (ignored): {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _sweeping.Remove(label);
            }
        }
    }

    /// <summary>
    /// 清本进程不知道的遗留目录(见类的说明):名字对、是真目录、属主与 <paramref name="fresh" /> 相同、比它早一天以上没再动过,
    /// 而且不是本进程还记着的(那些归 <see cref="DueBatches" /> 管)。时间都用远端的:本机与远端的时钟可能对不上。
    /// </summary>
    private async Task SweepLeftoversAsync(Guid sessionId, string fresh)
    {
        using CancellationTokenSource timeout = new(DeleteTimeout);
        RemoteFileInfo mine = await _sftp.GetFileInfoAsync(sessionId, fresh, timeout.Token).ConfigureAwait(false);
        DateTime cutoff = mine.LastModified - LeftoverRetention;
        List<RemoteFileInfo> entries = await _sftp.ListDirectoryAsync(sessionId, "/tmp", timeout.Token).ConfigureAwait(false);
        HashSet<string> tracked;
        lock (_gate)
        {
            tracked = [.. _batches.Select(b => b.Directory)];
        }
        foreach (RemoteFileInfo entry in entries)
        {
            string path = "/tmp/" + entry.Name;
            if (IsDropDirectory(path) && entry.IsDirectory && !entry.IsSymbolicLink && entry.Owner == mine.Owner
                && entry.LastModified < cutoff && path != fresh && !tracked.Contains(path))
            {
                await TryDeleteAsync(sessionId, path).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 删一个临时目录(连同里面的东西);路径不是自己建的那种名字时什么都不做。已经不在也算删掉了;别的失败只留痕、不上抛。
    /// </summary>
    /// <returns>删掉了(或本来就不在)。</returns>
    private async Task<bool> TryDeleteAsync(Guid sessionId, string directory)
    {
        if (!IsDropDirectory(directory))
        {
            return false;
        }
        try
        {
            using CancellationTokenSource timeout = new(DeleteTimeout);
            await _sftp.DeleteAsync(sessionId, directory, cancellationToken: timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;   // 已经没了(远端重启过、/tmp 被系统清过)
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[XServer] could not remove {directory} (ignored): {ex.Message}");
            return false;
        }
    }

    private async Task UploadDirectoryAsync(Guid id, string local, string remote, CancellationToken cancellationToken)
    {
        await _sftp.EnsureDirectoryAsync(id, remote, cancellationToken).ConfigureAwait(false);
        foreach (string file in Directory.EnumerateFiles(local))
        {
            await _sftp.UploadFileAsync(id, file, $"{remote}/{Path.GetFileName(file)}", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        foreach (string sub in Directory.EnumerateDirectories(local))
        {
            await UploadDirectoryAsync(id, sub, $"{remote}/{Path.GetFileName(sub)}", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>同一次拖放里同名的(不同目录下的 a.txt)加序号区分:<c>a.txt</c>、<c>a (2).txt</c>……</summary>
    private static string UniqueName(HashSet<string> names, string name)
    {
        if (name.Length == 0)
        {
            name = "dropped";
        }
        string candidate = name;
        for (int i = 2; !names.Add(candidate); i++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(name)} ({i.ToString(CultureInfo.InvariantCulture)}){Path.GetExtension(name)}";
        }
        return candidate;
    }
}
