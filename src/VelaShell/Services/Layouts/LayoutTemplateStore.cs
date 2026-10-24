using System.Diagnostics;
using System.Globalization;
using VelaShell.Core.Data;
using VelaShell.Core.Layouts;

namespace VelaShell.Services.Layouts;

/// <summary>
/// 布局模板的持久化:SonnetDB 文档库里的 <c>layout_templates</c> 集合,一个模板一条文档(主键 = 模板 Id);
/// 「退出时的布局」单独一条,放在 <c>layout_session</c> 集合里。
/// </summary>
/// <remarks>
/// <para>
/// <b>不放进 <c>AppSettings</c></b>:设置会跟着「导出配置」与 Gist 同步整份走,而模板里是一棵引用会话配置 Id 的树 ——
/// 把它塞进设置,旧客户端拉到之后不认识、再推一次就把它抹掉(快捷命令的 <c>HiddenBuiltInIds</c> 吃过这个亏);
/// 要同步也该像快捷命令那样单独成一段、带自己的版本号,而那需要先定好删除与冲突怎么合并。眼下只存本机。
/// </para>
/// <para>
/// 与收藏、命令面板的使用痕迹一样经 <see cref="IAppDataStore" />:集合在第一次用到时由文档库建出来,
/// 不用登记;没有文档库(无界面单测)时一切操作退化为空。
/// </para>
/// </remarks>
/// <param name="store">文档存储;为 null 时所有操作退化为空操作。</param>
public sealed class LayoutTemplateStore(IAppDataStore? store)
{
    /// <summary>命名模板的集合名。</summary>
    internal const string Collection = "layout_templates";

    /// <summary>「退出时的布局」所在的集合名。</summary>
    internal const string SessionCollection = "layout_session";

    /// <summary>「退出时的布局」那条文档的主键。</summary>
    internal const string LastSessionId = "last";

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
            List<LayoutTemplate> all = await store.GetAllAsync<LayoutTemplate>(Collection).ConfigureAwait(false);
            return Sort(all.Where(template => template.Id != Guid.Empty));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.WriteLine($"[LayoutTemplates] 读取布局模板失败:{ex.Message}");
            return [];
        }
    }

    /// <summary>新增或覆盖一个模板。失败照常抛出 —— 调用方要告诉用户「没存上」。</summary>
    /// <param name="template">要保存的模板。</param>
    public Task SaveAsync(LayoutTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return store is null
                   ? Task.CompletedTask
                   : store.UpsertAsync(Collection, Key(template.Id), template);
    }

    /// <summary>删除一个模板。失败照常抛出。</summary>
    /// <param name="id">模板 Id。</param>
    public Task DeleteAsync(Guid id) =>
        store is null ? Task.CompletedTask : store.DeleteAsync(Collection, Key(id));

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

    /// <summary>记下(或在 <paramref name="layout" /> 为 null 时清掉)「退出时的布局」。</summary>
    /// <param name="layout">要记下的布局;null = 清掉旧的,免得下次启动还原一份过期的。</param>
    public Task SaveLastSessionAsync(LayoutTemplate? layout) =>
        store is null
            ? Task.CompletedTask
            : layout is null
                ? store.DeleteAsync(SessionCollection, LastSessionId)
                : store.UpsertAsync(SessionCollection, LastSessionId, layout);

    /// <summary>按名字的自然顺序排(不变全球化模式下退回按区域规则比)。</summary>
    internal static IReadOnlyList<LayoutTemplate> Sort(IEnumerable<LayoutTemplate> templates)
    {
        StringComparer comparer = NaturalNameComparer();
        return [.. templates.OrderBy(template => template.Name, comparer).ThenBy(template => template.CreatedAt)];
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
