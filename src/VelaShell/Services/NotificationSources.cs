using System.Diagnostics;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Notifications;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Diagnostics;

namespace VelaShell.Services;

/// <summary>
/// 后台下载启动检查选定的那个更新并校验(设置 → 常规 → 自动下载更新),返回是否已有可重启换版的包。
/// 实现是 <c>SettingsViewModel.DownloadUpdateInBackgroundAsync</c>:与关于页共用一条下载链路和忙状态。
/// </summary>
public delegate Task<bool> BackgroundUpdateDownload();

/// <summary>
/// 攒出这一轮该投进消息中心的条目:有新版本了、订阅源的公告、上次运行留下的崩溃记录。
/// </summary>
/// <remarks>
/// <para>
/// 从 <c>MainWindowViewModel</c> 拆出来的一簇(Q-01)。拆的是<b>决策</b>那一半 ——
/// 投不投、投什么、id 怎么起;而"接铃铛角标、起定时器、开面板"那一半是界面接线,
/// 留在视图模型里。混在一起时,想验一条"关掉推广之后促销条目不该进来"
/// 得先把三十个构造参数的主窗口造出来。
/// </para>
/// <para>
/// 整段对失败极度宽容:消息中心是锦上添花的东西,离线、源不可达、格式变了 ——
/// 任何一样都不该拦住应用启动,更不该让铃铛变得不可用。
/// </para>
/// </remarks>
public static class NotificationSources
{
    /// <summary>
    /// 收集这一轮要投的条目。
    /// </summary>
    /// <param name="settings">当前设置(决定投不投更新、要不要收促销)。</param>
    /// <param name="feed">订阅资讯源;null = 不取。</param>
    /// <param name="updateService">更新服务;null = 不查更新。</param>
    /// <returns>要投的条目;没有则为空列表。</returns>
    public static async Task<IReadOnlyList<NotificationItem>> CollectAsync(
        AppSettings settings,
        IAnnouncementFeed? feed,
        IUpdateService? updateService)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<NotificationItem> incoming = [];
        if (updateService is not null
            && await CollectUpdateAsync(settings, updateService, backgroundDownload: null).ConfigureAwait(true) is { } update)
        {
            incoming.Add(update);
        }
        if (feed is not null)
        {
            IReadOnlyList<NotificationItem> fetched = await feed.FetchAsync().ConfigureAwait(true);
            incoming.AddRange(settings.Notifications.AllowPromotions
                                  ? fetched
                                  : fetched.Where(item => item.Kind != NotificationKind.Promotion));
        }
        if (BuildCrashNotification() is { } crash)
        {
            incoming.Add(crash);
        }
        return incoming;
    }

    /// <summary>
    /// 启动时的更新这一轮:查一次,该自动下载就在后台下完、校验完,再决定投哪一条。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「启动时检查更新」是联网的总闸,关掉时一个请求都不发。开着时,查了之后总得有人要这个结果 ——
    /// 要么投消息(「提醒可用更新」),要么后台下载(「自动下载更新」);两样都不要就不查。
    /// </para>
    /// <para>
    /// 自动下载只在能原地换版的安装上发生(<see cref="IUpdateService.CanSelfUpdate" />:Linux、商店版、
    /// 只读目录都不算)。下好了投「已下载好,重启即可更新」,不重启;没下成不打扰,退回普通的「有新版本」,
    /// 下一次启动检查再试。下载要几十秒到几分钟,所以调用方要把它和资讯源分开投 —— 别让一条公告等着它。
    /// </para>
    /// </remarks>
    /// <param name="settings">当前设置。</param>
    /// <param name="updateService">更新服务。</param>
    /// <param name="backgroundDownload">后台下载入口(关于页那条下载链路);null = 只检查不下载。</param>
    /// <returns>要投的那一条;不查、没有新版本、或这次的结果不需要投消息时为 null。</returns>
    public static async Task<NotificationItem?> CollectUpdateAsync(
        AppSettings settings,
        IUpdateService updateService,
        BackgroundUpdateDownload? backgroundDownload)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(updateService);
        bool notify = settings.Notifications.NotifyUpdates;
        bool autoDownload = backgroundDownload is not null && settings.General.AutoDownloadUpdates;
        if (!settings.General.CheckUpdatesOnStartup || !(notify || autoDownload))
        {
            return null;
        }
        if (await BuildUpdateNotificationAsync(updateService).ConfigureAwait(true) is not { } available)
        {
            return null;
        }
        if (autoDownload && updateService.CanSelfUpdate)
        {
            bool ready;
            try
            {
                ready = await backgroundDownload!().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // 下载入口自己会兜住失败;这里再兜一层,免得一个意外的异常吞掉下面那条退回的消息。
                Trace.WriteLine($"[VelaShell] Background update download failed: {ex.Message}");
                ready = false;
            }
            if (ready && updateService.ReadyUpdate is { } snapshot)
            {
                return notify ? BuildUpdateReadyNotification(snapshot.Version, updateService.CurrentVersion) : null;
            }
        }
        return notify ? available : null;
    }

    /// <summary>后台下载好、校验过的更新:点它落到关于页,那里有「重启并更新」。</summary>
    /// <param name="readyVersion">就绪包的版本号。</param>
    /// <param name="currentVersion">当前运行的版本号。</param>
    /// <returns>消息条目。</returns>
    public static NotificationItem BuildUpdateReadyNotification(string readyVersion, string? currentVersion) =>
        new()
        {
            // 与「有新版本」的 update:<版本> 分开:同一个版本先投过「有新版本」(当时没开自动下载),
            // 下好之后这一条要重新亮起未读,而不是被当成同一条去重掉。
            Id = $"update-ready:{readyVersion}",
            Kind = NotificationKind.Update,
            Title = Strings.Format("Notify_UpdateReadyTitle", readyVersion),
            Body = Strings.Format("Notify_UpdateReadyBody", currentVersion ?? "?"),
            PublishedAt = DateTime.UtcNow,
            Link = new()
            {
                Label = Strings.Get("Notify_UpdateReadyAction"),
                CommandId = "app.settings.about"
            }
        };

    /// <summary>
    /// 上次运行留下了未提示过的崩溃记录时,攒一条消息中心条目(带"打开日志目录"动作)。
    /// </summary>
    /// <remarks>
    /// 走消息中心而不是启动弹窗:用户刚打开应用是想干活,不是想读崩溃报告。
    /// <c>TryTakeUnseenCrash</c> 取走即标记已看,同一份崩溃不会每次启动都再提示一遍。
    /// </remarks>
    /// <returns>崩溃条目;没有未提示过的崩溃时为 null。</returns>
    public static NotificationItem? BuildCrashNotification()
    {
        if (!DiagnosticLog.TryTakeUnseenCrash(out string path))
        {
            return null;
        }
        string fileName = Path.GetFileName(path);
        return new()
        {
            Id = "crash:" + fileName,
            Kind = NotificationKind.System,
            Severity = NotificationSeverity.Warning,
            Title = Strings.Get("Notify_CrashTitle"),
            Body = Strings.Format("Notify_CrashBody", fileName),
            PublishedAt = DateTime.UtcNow,
            Link = new()
            {
                Label = Strings.Get("Notify_CrashAction"),
                CommandId = "app.logs.open"
            }
        };
    }

    /// <summary>外部编辑草稿那几条消息的 id 前缀,后面接草稿子目录的名字。</summary>
    public const string RemoteEditDraftIdPrefix = "remote-edit-draft:";

    /// <summary>「打开外部编辑草稿文件夹」命令。</summary>
    public const string OpenRemoteEditDraftsCommandId = "files.remoteEditDrafts.open";

    /// <summary>
    /// 每份外部编辑草稿(上次运行留下、带着或可能带着没传回服务器的改动)一条消息,带「打开所在文件夹」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// id 用草稿子目录的名字:草稿还在,每次启动重投都按 id 去重、保住已读状态;草稿没了(用户处理完删掉了),
    /// 调用方按 <see cref="StaleRemoteEditDraftIds" /> 把消息撤掉。发布时间取第一次发现它的时刻(之后重投按 id 去重,保住这个时刻):列表按发布时间倒序、满 200 条丢最旧的,
    /// 拿草稿最后被改的时刻会让几个月前的草稿一投进来就沉底甚至被挤掉。
    /// </para>
    /// <para>
    /// 只能打开文件夹,不能从这里直接重传:消息中心的动作只有一条不带参数的命令,而重传要先连上那台服务器
    /// (会话随上次运行一起没了)、再按这一份草稿传。标记里记着连接配置,留给以后。
    /// </para>
    /// </remarks>
    /// <param name="drafts">启动时找到的草稿(<see cref="RemoteEditDrafts.ListDrafts" />)。</param>
    /// <returns>消息条目,一份草稿一条。</returns>
    public static IReadOnlyList<NotificationItem> BuildRemoteEditDraftNotifications(IEnumerable<RemoteEditDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        return [.. drafts.Select(static draft => new NotificationItem
        {
            Id = RemoteEditDraftIdPrefix + Path.GetFileName(draft.Directory),
            Kind = NotificationKind.System,
            Severity = NotificationSeverity.Warning,
            Title = Strings.Get("Notify_RemoteEditDraftTitle"),
            Body = draft.RemotePath is { Length: > 0 } remotePath
                ? Strings.Format("Notify_RemoteEditDraftBody", draft.FileName,
                    draft.Server is { Length: > 0 } server ? $"{server}:{remotePath}" : remotePath, draft.LocalPath)
                : Strings.Format("Notify_RemoteEditDraftBodyUnknown", draft.FileName, draft.LocalPath),
            PublishedAt = DateTime.UtcNow,
            Link = new()
            {
                Label = Strings.Get("Notify_RemoteEditDraftAction"),
                CommandId = OpenRemoteEditDraftsCommandId
            }
        })];
    }

    /// <summary>消息中心里还挂着、草稿却已经不在了的那几条(用户处理完删掉了草稿):该撤掉。</summary>
    /// <param name="existing">消息中心此刻的条目。</param>
    /// <param name="drafts">此刻还在的草稿。</param>
    /// <returns>要撤掉的消息 id。</returns>
    public static IReadOnlyList<string> StaleRemoteEditDraftIds(IEnumerable<NotificationItem> existing, IEnumerable<RemoteEditDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(drafts);
        var current = drafts.Select(static draft => RemoteEditDraftIdPrefix + Path.GetFileName(draft.Directory)).ToHashSet(StringComparer.Ordinal);
        return [.. existing.Select(static item => item.Id)
            .Where(id => id.StartsWith(RemoteEditDraftIdPrefix, StringComparison.Ordinal) && !current.Contains(id))];
    }

    /// <summary>
    /// 本机加密密钥的解析结果里有要用户知道的事时,攒成消息中心条目;一切照常(包括这次顺利迁进了密钥链)时为空。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 走消息中心而不是启动弹窗,理由同崩溃提示;但这几条关系到「已存的密码为什么解不开」,
    /// 所以要留存可回看 —— 用户多半是连接时被要求重新输入密码,才回头来找原因。
    /// </para>
    /// <para>
    /// id 决定提示几次:密钥文件被换过、某把密钥在密钥链里找不到,都是一次性的事,按文件名 / 编号去重,只提示一次;
    /// 密钥链暂时用不了可能反复发生,按天去重;回落到文件是常态(没装 keyring 的机器),只提示一次。
    /// </para>
    /// </remarks>
    /// <param name="status">解析结果。</param>
    /// <param name="nowUtc">此刻(UTC)。</param>
    /// <returns>要投的条目;没有则为空列表。</returns>
    public static IReadOnlyList<NotificationItem> BuildMasterKeyNotifications(MasterKeyStatus status, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(status);
        List<NotificationItem> notices = [];
        if (status.ReplacedKeyFile is { } replaced)
        {
            string fileName = Path.GetFileName(replaced);
            notices.Add(Notice($"master-key:replaced:{fileName}", NotificationSeverity.Warning,
                "Notify_MasterKeyReplacedTitle", Strings.Format("Notify_MasterKeyReplacedBody", fileName), nowUtc));
        }
        if (status.MissingKeyIds.Count > 0)
        {
            notices.Add(Notice($"master-key:missing:{string.Join('+', status.MissingKeyIds)}", NotificationSeverity.Warning,
                "Notify_MasterKeyMissingTitle", Strings.Get("Notify_MasterKeyMissingBody"), nowUtc));
        }
        if (status.UnavailableKeyIds.Count > 0)
        {
            notices.Add(Notice($"master-key:unavailable:{nowUtc:yyyyMMdd}", NotificationSeverity.Warning,
                "Notify_MasterKeyUnavailableTitle",
                Strings.Format("Notify_MasterKeyUnavailableBody", status.KeychainName ?? "", status.KeychainError ?? ""), nowUtc));
        }
        else if (status is { Protection: MasterKeyProtection.PlainFile, KeychainName: { } keychain, KeychainError: { } error })
        {
            notices.Add(Notice("master-key:file-fallback", NotificationSeverity.Info,
                "Notify_MasterKeyFileFallbackTitle", Strings.Format("Notify_MasterKeyFileFallbackBody", keychain, error), nowUtc));
        }
        return notices;

        static NotificationItem Notice(string id, NotificationSeverity severity, string titleKey, string body, DateTime publishedAt) =>
            new()
            {
                Id = id,
                Kind = NotificationKind.System,
                Severity = severity,
                Title = Strings.Get(titleKey),
                Body = body,
                PublishedAt = publishedAt
            };
    }

    /// <summary>
    /// 检查更新,有新版本就攒一条消息。
    /// </summary>
    /// <remarks>
    /// 商店版直接跳过:安装目录只读、更新由 Microsoft Store 接管,推一条"去关于页更新"
    /// 只会把用户送到一个什么也做不了的页面。
    /// </remarks>
    /// <param name="updateService">更新服务。</param>
    /// <returns>更新条目;无更新、离线或商店版时为 null。</returns>
    public static async Task<NotificationItem?> BuildUpdateNotificationAsync(IUpdateService updateService)
    {
        ArgumentNullException.ThrowIfNull(updateService);
        if (updateService.IsStoreManaged)
        {
            return null;
        }
        bool hasUpdate;
        try
        {
            hasUpdate = await updateService.CheckForUpdateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 离线或更新源不可达是常态。但要留一行:这条请求是开箱默认发的(设置 → 通用 →
            // 启动时检查更新),失败时调试输出里只浮出一串裸的 TLS/套接字异常,看到的人
            // 无从判断是自己的网络不通、还是更新检查本身坏了 —— 而这两件事的处置完全不同。
            Trace.WriteLine($"[VelaShell] Update check failed: {ex.Message}");
            return null;
        }
        if (!hasUpdate || updateService.AvailableVersion is not { Length: > 0 } available)
        {
            return null;
        }
        return new()
        {
            // id 里带上版本号:同一个版本每次启动都会重投,靠它去重并保住已读状态;
            // 真出了新版本则是一条新 id,会重新亮起未读。
            Id = $"update:{available}",
            Kind = NotificationKind.Update,
            Title = Strings.Format("Notify_UpdateTitle", available),
            Body = Strings.Format("Notify_UpdateBody", updateService.CurrentVersion ?? "?"),
            PublishedAt = DateTime.UtcNow,
            Link = new()
            {
                Label = Strings.Get("Notify_UpdateAction"),
                CommandId = "app.settings.about"
            }
        };
    }
}
