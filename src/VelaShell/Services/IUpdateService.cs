namespace VelaShell.Services;

/// <summary>应用自动更新服务:检查、下载并应用新版本。</summary>
public interface IUpdateService
{
    /// <summary>当前正在运行的应用版本;无法确定时为 null。</summary>
    string? CurrentVersion { get; }

    /// <summary>检查后发现的可用新版本;无可用更新时为 null。</summary>
    string? AvailableVersion { get; }

    /// <summary>
    /// 检查选定的那份清单(版本、Release 标签、各平台产物);无可用更新时为 null。
    /// Linux 据它的标签拼出对应安装包的下载地址 —— 检查与打开浏览器之间若发了新版,也不会指错版本。
    /// </summary>
    Update.UpdateManifest? AvailableUpdate { get; }

    /// <summary>
    /// 能否原地自更新:应用目录可写、平台受支持且非商店版时为 true。装在 Program Files 等
    /// 只读位置时为 false;Linux 始终为 false,发现新版本后打开发布页面供用户手动下载。
    /// </summary>
    bool CanSelfUpdate { get; }

    /// <summary>
    /// 更新是否由 Microsoft Store 接管(即本进程以 MSIX 包身份运行)。为 true 时应用内的
    /// 检查/下载/换版一律不做:安装目录只读,且商店政策要求包应用只能经商店更新。
    /// 界面应据此把更新相关操作换成一句"更新由 Microsoft Store 管理"。
    /// </summary>
    bool IsStoreManaged { get; }

    /// <summary>检查是否有可用更新;返回 true 表示存在比当前版本更新的版本。</summary>
    Task<bool> CheckForUpdateAsync();

    /// <summary>
    /// 上一次换版失败的原因;没有失败记录时为 null。换版由独立进程在本应用退出后执行,
    /// 它的报错没法当场弹给用户,故落盘留到下次启动后展示。
    /// </summary>
    string? LastUpdateError { get; }

    /// <summary>清掉 <see cref="LastUpdateError" /> 的失败记录(用户重新尝试更新时调用)。</summary>
    void ClearUpdateError();

    /// <summary>
    /// 下载已检测到的更新包并校验完整性,可通过 <paramref name="progress"/> 汇报已下载字节、
    /// 整包字节与当前速率(见 <see cref="Update.UpdateDownloadProgress" />)。
    /// 连接静默断开(长时间收不到任何数据)时自行断开重连、按断点续传,重连次数有上限,
    /// 用尽即抛出 <see cref="TimeoutException" />,不会无限挂起。
    /// 没有检查到的目标(包括被并发的另一次检查清掉)时抛出 <see cref="InvalidOperationException" />。
    /// </summary>
    Task DownloadUpdateAsync(
        IProgress<Update.UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 已下载且通过完整性校验的更新快照;尚未就绪时为 null,检查更新不会清除它。
    /// 换版前发现包已丢失或损坏时清除,需重新下载。
    /// </summary>
    Update.UpdateManifest? ReadyUpdate { get; }

    /// <summary>
    /// 下载指定清单的更新,不再查询最新版本。目标必须比当前版本新,且本安装支持自更新。
    /// 正在下载、换版或修复时立即拒绝;已有不同目标的就绪包时,须先明确修复重置再切换。
    /// </summary>
    Task DownloadUpdateAsync(
        Update.UpdateManifest manifest,
        IProgress<Update.UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 应用已下载的更新并重启应用。包已丢失或损坏时清除 <see cref="ReadyUpdate" /> 并抛出;
    /// 其余失败保留它,可直接重试。
    /// </summary>
    void ApplyUpdateAndRestart();

    /// <summary>
    /// 强制重置更新状态:回滚没换完的换版,清掉暂存目录(含已下载的包,下次重新下载)、
    /// 更新器临时目录与历史遗留文件。返回 false 表示仍有文件被占用,关掉占用者后可再试。
    /// 用于把因意外中断而卡住的更新流程恢复到干净起点。
    /// </summary>
    bool RepairUpdateState();
}
