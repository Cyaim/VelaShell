using System.Diagnostics;
using System.Globalization;
using VelaShell.Core.Data;

namespace VelaShell.Core.Layouts;

/// <summary>
/// 布局模板的持久化:SonnetDB 文档库里的 <c>layout_templates</c> 集合,一个模板一条文档(主键 = 模板 Id);
/// 删掉的模板在 <c>layout_template_tombstones</c> 里留一块墓碑(同步靠它传播删除);
/// 「退出时的布局」单独一条,放在 <c>layout_session</c> 集合里。
/// </summary>
/// <remarks>
/// <para>
/// <b>不放进 <c>AppSettings</c></b>:设置会跟着「导出配置」与 Gist 同步整份走,而模板里是一棵引用会话配置 Id 的树 ——
/// 把它塞进设置,旧客户端拉到之后不认识、再推一次就把它抹掉(快捷命令的 <c>HiddenBuiltInIds</c> 吃过这个亏)。
/// 同步走 Gist 里单独的一个文件、按 Id 合并(<see cref="LayoutTemplateMerge" />),导出导入走自己的文件格式
/// (<see cref="LayoutTemplateFile" />)。
/// </para>
/// <para>
/// 宿主里只有一个实例(依赖注入的单例):界面与云同步写的是同一份,<see cref="Changed" /> 与 <see cref="SyncApplied" />
/// 才对得上 —— 前者让自动同步推送,后者让界面重读。读改写在一把锁里做,同步合并与界面上的改名同时落下时不会互相盖掉。
/// </para>
/// <para>
/// 与收藏、命令面板的使用痕迹一样经 <see cref="IAppDataStore" />:集合在第一次用到时由文档库建出来,
/// 不用登记;没有文档库(无界面单测)时一切操作退化为空。
/// </para>
/// </remarks>
/// <param name="store">文档存储;为 null 时所有操作退化为空操作。</param>
/// <param name="time">时钟(墓碑的删除时间、过期判断);单测可注入,默认系统时钟。</param>
public sealed class LayoutTemplateStore(IAppDataStore? store, TimeProvider? time = null)
{
    /// <summary>命名模板的集合名。</summary>
    public const string Collection = "layout_templates";

    /// <summary>墓碑的集合名(一块墓碑一条文档,主键 = 被删模板的 Id)。</summary>
    public const string TombstoneCollection = "layout_template_tombstones";

    /// <summary>「退出时的布局」所在的集合名。</summary>
    public const string SessionCollection = "layout_session";

    /// <summary>「退出时的布局」那条文档的主键。</summary>
    public const string LastSessionId = "last";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>串起所有读改写:同步的合并要「读本机 → 比 → 写」一气呵成。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>本机这边的增删改(保存、覆盖、改名、删除、导入)落盘之后触发;自动同步据此推送。可能从后台线程触发。</summary>
    public event EventHandler? Changed;

    /// <summary>云同步把别的设备的改动写进来之后触发;界面据此重读。可能从后台线程触发。</summary>
    public event EventHandler? SyncApplied;

    /// <summary>读出全部命名模板,按名字的自然顺序(「布局 2」排在「布局 10」前面)。</summary>
    /// <returns>模板列表;读取失败时为空(并记一条诊断日志)。</returns>
    public async Task<IReadOnlyList<LayoutTemplate>> LoadAllAsync()
    {
        if (store is null)
        {
            return [];
        }
        try
        {
            return await LoadTemplatesAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.WriteLine($"[LayoutTemplates] 读取布局模板失败:{ex.Message}");
            return [];
        }
    }

    /// <summary>新增或覆盖一个模板(同 Id 若有墓碑一并撤掉)。失败照常抛出 —— 调用方要告诉用户「没存上」。</summary>
    /// <param name="template">要保存的模板。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task SaveAsync(LayoutTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (store is null)
        {
            return;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await store.UpsertAsync(Collection, Key(template.Id), template).ConfigureAwait(false);
            // 导入、恢复历史版本会让删过的 Id 回来:墓碑不撤,下一次同步它又会把模板删掉。
            await store.DeleteAsync(TombstoneCollection, Key(template.Id)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>删除一个模板,并留下墓碑(删除时间 = 现在)。失败照常抛出。</summary>
    /// <param name="id">模板 Id。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task DeleteAsync(Guid id)
    {
        if (store is null)
        {
            return;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 先立墓碑再删模板:中间失败最多留下「模板还在、也有墓碑」,下次合并时墓碑照样胜出;
            // 反过来就是模板没了、墓碑也没立,别的设备再推一次就把它加回来。
            await store.UpsertAsync(TombstoneCollection, Key(id),
                new LayoutTemplateTombstone { Id = id, DeletedAt = _time.GetUtcNow() }).ConfigureAwait(false);
            await store.DeleteAsync(Collection, Key(id)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>读出本机的模板与墓碑(同步合并用)。读不出来照常抛出:拿一份空的去合并,会把云端那份当成本机没有的全收下来。</summary>
    /// <returns>本机的全集。</returns>
    public async Task<LayoutTemplateSyncState> LoadSyncStateAsync()
    {
        if (store is null)
        {
            return LayoutTemplateSyncState.Empty;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await LoadSyncStateCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 把云端那份合并进本机(<see cref="LayoutTemplateMerge.Merge" />)并把本机要改的写下去;本机确有改动时触发 <see cref="SyncApplied" />。
    /// </summary>
    /// <param name="remote">云端那份;null = 云端还没有。</param>
    /// <returns>合并结果(其中的 <see cref="LayoutTemplateMergeResult.Merged" /> 就是要推上去的那份)。</returns>
    public async Task<LayoutTemplateMergeResult> MergeAsync(LayoutTemplateSyncState? remote)
    {
        if (store is null)
        {
            return LayoutTemplateMerge.Merge(LayoutTemplateSyncState.Empty, LayoutTemplateSyncState.Empty, _time.GetUtcNow());
        }
        LayoutTemplateMergeResult result;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            LayoutTemplateSyncState local = await LoadSyncStateCoreAsync().ConfigureAwait(false);
            result = LayoutTemplateMerge.Merge(local, remote, _time.GetUtcNow());
            foreach (LayoutTemplate template in result.LocalUpserts)
            {
                await store.UpsertAsync(Collection, Key(template.Id), template).ConfigureAwait(false);
                await store.DeleteAsync(TombstoneCollection, Key(template.Id)).ConfigureAwait(false);
            }
            foreach (LayoutTemplateTombstone tombstone in result.LocalTombstones)
            {
                await store.UpsertAsync(TombstoneCollection, Key(tombstone.Id), tombstone).ConfigureAwait(false);
                await store.DeleteAsync(Collection, Key(tombstone.Id)).ConfigureAwait(false);
            }
            foreach (Guid id in result.LocalExpired)
            {
                await store.DeleteAsync(Collection, Key(id)).ConfigureAwait(false);
                await store.DeleteAsync(TombstoneCollection, Key(id)).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
        if (result.LocalChanged)
        {
            SyncApplied?.Invoke(this, EventArgs.Empty);
        }
        return result;
    }

    /// <summary>
    /// 恢复云同步的一个历史版本:那个版本里的模板全部回来(哪怕之后删过、改过),时间刷成现在,
    /// 好让随后那次推送里它们胜过云端现在的那份。本机多出来的模板不动(与连接配置「恢复不删本机独有的」同一口径)。
    /// </summary>
    /// <param name="templates">历史版本里的模板。</param>
    /// <returns>写回了几个。</returns>
    public async Task<int> RestoreAsync(IEnumerable<LayoutTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (store is null)
        {
            return 0;
        }
        int restored = 0;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            foreach (LayoutTemplate template in templates.Where(t => t is not null && t.Id != Guid.Empty))
            {
                template.UpdatedAt = now;
                await store.UpsertAsync(Collection, Key(template.Id), template).ConfigureAwait(false);
                await store.DeleteAsync(TombstoneCollection, Key(template.Id)).ConfigureAwait(false);
                restored++;
            }
        }
        finally
        {
            _gate.Release();
        }
        if (restored > 0)
        {
            SyncApplied?.Invoke(this, EventArgs.Empty);
        }
        return restored;
    }

    /// <summary>读出「退出时的布局」;没有或读不出来时为 null。</summary>
    /// <returns>上次退出时记下的布局。</returns>
    public async Task<LayoutTemplate?> LoadLastSessionAsync()
    {
        if (store is null)
        {
            return null;
        }
        try
        {
            return await store.GetAsync<LayoutTemplate>(SessionCollection, LastSessionId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.WriteLine($"[LayoutTemplates] 读取上次的布局失败:{ex.Message}");
            return null;
        }
    }

    /// <summary>记下(或在 <paramref name="layout" /> 为 null 时清掉)「退出时的布局」。它不参与同步,也不触发 <see cref="Changed" />。</summary>
    /// <param name="layout">要记下的布局;null = 清掉旧的,免得下次启动还原一份过期的。</param>
    /// <returns>表示异步操作的任务。</returns>
    public Task SaveLastSessionAsync(LayoutTemplate? layout) =>
        store is null
            ? Task.CompletedTask
            : layout is null
                ? store.DeleteAsync(SessionCollection, LastSessionId)
                : store.UpsertAsync(SessionCollection, LastSessionId, layout);

    /// <summary>按名字的自然顺序排(不变全球化模式下退回按区域规则比)。</summary>
    /// <param name="templates">模板。</param>
    /// <returns>排好的列表。</returns>
    public static IReadOnlyList<LayoutTemplate> Sort(IEnumerable<LayoutTemplate> templates)
    {
        StringComparer comparer = NaturalNameComparer();
        return [.. templates.OrderBy(template => template.Name, comparer).ThenBy(template => template.CreatedAt)];
    }

    private async Task<IReadOnlyList<LayoutTemplate>> LoadTemplatesAsync()
    {
        List<LayoutTemplate> all = await store!.GetAllAsync<LayoutTemplate>(Collection).ConfigureAwait(false);
        return Sort(all.Where(template => template.Id != Guid.Empty));
    }

    private async Task<LayoutTemplateSyncState> LoadSyncStateCoreAsync()
    {
        IReadOnlyList<LayoutTemplate> templates = await LoadTemplatesAsync().ConfigureAwait(false);
        List<LayoutTemplateTombstone> tombstones =
            await store!.GetAllAsync<LayoutTemplateTombstone>(TombstoneCollection).ConfigureAwait(false);
        return new(templates, [.. tombstones.Where(tombstone => tombstone.Id != Guid.Empty)]);
    }

    private static string Key(Guid id) => id.ToString("D");

    private static StringComparer NaturalNameComparer()
    {
        try
        {
            var natural = StringComparer.Create(CultureInfo.CurrentCulture,
                CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
            // 有的平台创建时不报、比较时才报:先比一次,别让排序做到一半抛出来。
            _ = natural.Compare("2", "10");
            return natural;
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            return StringComparer.CurrentCultureIgnoreCase;
        }
    }
}
