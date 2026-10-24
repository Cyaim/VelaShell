using System.Diagnostics;
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
