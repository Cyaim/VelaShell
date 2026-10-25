namespace VelaShell.Core.Layouts;

/// <summary>
/// 删掉的模板留下的墓碑:同步时靠它把「删了」传到别的设备 —— 不然对方会把「这边没有、那边有」当成
/// 「这边还没收到」,再给加回来。
/// </summary>
public sealed class LayoutTemplateTombstone
{
    /// <summary>被删的模板 Id。</summary>
    public Guid Id { get; set; }

    /// <summary>删除的时间(UTC)。</summary>
    public DateTimeOffset DeletedAt { get; set; }
}

/// <summary>一端(本机,或云端那份文件)的模板全集:活着的模板加上墓碑。</summary>
/// <param name="Templates">活着的模板。</param>
/// <param name="Tombstones">墓碑。</param>
public sealed record LayoutTemplateSyncState(
    IReadOnlyList<LayoutTemplate> Templates,
    IReadOnlyList<LayoutTemplateTombstone> Tombstones)
{
    /// <summary>什么都没有的一端(云端还没有这份文件)。</summary>
    public static LayoutTemplateSyncState Empty { get; } = new([], []);
}

/// <summary>一次合并的结果。</summary>
/// <param name="Merged">合并之后两端都该是的样子(写回云端的就是它)。</param>
/// <param name="LocalUpserts">本机要写入(新增或换成对方那份)的模板。</param>
/// <param name="LocalTombstones">本机要记下的墓碑(连带删掉同 Id 的模板)。</param>
/// <param name="LocalExpired">过了保留期、本机要连模板带墓碑一起清掉的 Id。</param>
/// <param name="RemoteChanged">合并结果与云端那份不同 —— 要推一次。</param>
public sealed record LayoutTemplateMergeResult(
    LayoutTemplateSyncState Merged,
    IReadOnlyList<LayoutTemplate> LocalUpserts,
    IReadOnlyList<LayoutTemplateTombstone> LocalTombstones,
    IReadOnlyList<Guid> LocalExpired,
    bool RemoteChanged)
{
    /// <summary>本机有没有要改的。</summary>
    public bool LocalChanged => LocalUpserts.Count > 0 || LocalTombstones.Count > 0 || LocalExpired.Count > 0;
}

/// <summary>
/// 布局模板的同步合并:按模板 Id 逐条比时间,较新的一方胜;删除靠墓碑传播。纯函数,不碰存储。
/// </summary>
/// <remarks>
/// <para>
/// <b>不跟着整份载荷的「较新者胜」走</b>:那是整份覆盖,两台设备各存了一个模板,后推的那台会把先推的那个抹掉。
/// 这里每个 Id 各比各的(活着的比 <see cref="LayoutTemplate.UpdatedAt" />,墓碑比 <see cref="LayoutTemplateTombstone.DeletedAt" />),
/// 结果与谁先谁后、合并几次都无关(交换、幂等),所以推之前合一次、拉之后再合一次都不会互相打架。
/// 时间取各设备自己的钟,设备钟快的那台在并发改同一个模板时占便宜 —— 与整份载荷的较新者胜同一个前提。
/// </para>
/// <para>
/// <b>同一时刻的平局</b>:墓碑胜过活着的(删除总是看过那个版本之后才做的);两边都活着而时间相同,留本机的、
/// 也不算云端要改 —— 改动一律刷新时间,同 Id 同时间只能是同一份,按内容比反而会让每次同步都推一遍。
/// </para>
/// <para>
/// <b>重名(不同 Id 同名)不合并、不改名,两条都留</b>:两台设备各自起了同一个名字,存的多半是两套不同的分屏,
/// 按名字合并就是悄悄扔掉其中一套;同步替用户改名又会在两台设备上各改出一个名字、来回打架。
/// 两条同名的模板列表里都看得见(摘要里的标签数、窗格数通常就分得开),用户自己改名或删掉一条即可。
/// </para>
/// <para>
/// <b>墓碑只留 <see cref="TombstoneRetention" /></b>:过了保留期,赢的墓碑连同它压着的模板一起清掉、不再推。
/// 代价是一台离线超过保留期的设备回来时,手里那份被删过的模板会重新出现(它的时间早于删除,但墓碑已经没了)——
/// 删掉的模板一般都很少,留半年足够覆盖「笔记本放了一个季度」这类情形。
/// </para>
/// </remarks>
public static class LayoutTemplateMerge
{
    /// <summary>墓碑的保留期。</summary>
    public static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(180);

    /// <summary>把云端那份合并进本机这份。</summary>
    /// <param name="local">本机的模板与墓碑。</param>
    /// <param name="remote">云端那份;null = 云端还没有(当作空)。</param>
    /// <param name="now">现在(UTC),判墓碑过期用。</param>
    /// <returns>合并结果。</returns>
    public static LayoutTemplateMergeResult Merge(
        LayoutTemplateSyncState local,
        LayoutTemplateSyncState? remote,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(local);
        Dictionary<Guid, Entry> mine = Index(local);
        Dictionary<Guid, Entry> theirs = Index(remote ?? LayoutTemplateSyncState.Empty);
        DateTimeOffset expiry = now - TombstoneRetention;

        var templates = new List<LayoutTemplate>();
        var tombstones = new List<LayoutTemplateTombstone>();
        var upserts = new List<LayoutTemplate>();
        var localTombstones = new List<LayoutTemplateTombstone>();
        var expired = new List<Guid>();
        bool remoteChanged = false;

        foreach (Guid id in mine.Keys.Union(theirs.Keys))
        {
            Entry? l = mine.TryGetValue(id, out Entry a) ? a : null;
            Entry? r = theirs.TryGetValue(id, out Entry b) ? b : null;
            // 赢的是云端那份(本机没有,或云端的更晚)才要动本机;平局留本机的。
            bool fromRemote = r is { } incoming && (l is not { } current || Wins(incoming, current));
            Entry winner = fromRemote ? r!.Value : l!.Value;

            if (winner.IsTombstone && winner.Stamp < expiry)
            {
                // 过期:赢的是墓碑,压着的模板该删的删掉,墓碑本身也不再留、不再推。
                if (l is not null)
                {
                    expired.Add(id);
                }
                remoteChanged |= r is not null;
                continue;
            }
            if (winner.IsTombstone)
            {
                tombstones.Add(new() { Id = id, DeletedAt = winner.Stamp });
                if (fromRemote)
                {
                    localTombstones.Add(new() { Id = id, DeletedAt = winner.Stamp });
                }
            }
            else
            {
                templates.Add(winner.Template!);
                if (fromRemote)
                {
                    upserts.Add(winner.Template!);
                }
            }
            remoteChanged |= r is not { } theirsEntry || !Same(winner, theirsEntry);
        }

        var merged = new LayoutTemplateSyncState(
            LayoutTemplateStore.Sort(templates),
            [.. tombstones.OrderBy(t => t.DeletedAt).ThenBy(t => t.Id)]);
        return new(merged, upserts, localTombstones, expired, remoteChanged);
    }

    /// <summary>一端里一个 Id 的状态:活着(带模板)或墓碑,外加用来比先后的时间。</summary>
    private readonly record struct Entry(LayoutTemplate? Template, DateTimeOffset Stamp)
    {
        public bool IsTombstone => Template is null;
    }

    private static Dictionary<Guid, Entry> Index(LayoutTemplateSyncState state)
    {
        var index = new Dictionary<Guid, Entry>();
        // 反序列化来的集合与元素都可能是 null(手改过的文件、更新的版本写出的形状),一律跳过。
        foreach (LayoutTemplate? template in state.Templates ?? [])
        {
            if (template is null || template.Id == Guid.Empty)
            {
                continue;
            }
            var entry = new Entry(template, template.UpdatedAt);
            if (!index.TryGetValue(template.Id, out Entry existing) || Wins(entry, existing))
            {
                index[template.Id] = entry;
            }
        }
        foreach (LayoutTemplateTombstone? tombstone in state.Tombstones ?? [])
        {
            if (tombstone is null || tombstone.Id == Guid.Empty)
            {
                continue;
            }
            var entry = new Entry(null, tombstone.DeletedAt);
            if (!index.TryGetValue(tombstone.Id, out Entry existing) || Wins(entry, existing))
            {
                index[tombstone.Id] = entry;
            }
        }
        return index;
    }

    /// <summary><paramref name="challenger" /> 是否胜过 <paramref name="incumbent" />:时间更晚,或同一时刻的墓碑对活着的。</summary>
    private static bool Wins(Entry challenger, Entry incumbent) =>
        challenger.Stamp > incumbent.Stamp
        || challenger.Stamp == incumbent.Stamp && challenger.IsTombstone && !incumbent.IsTombstone;

    /// <summary>同一种状态、同一个时间就算一样(不比内容,理由见类注释)。</summary>
    private static bool Same(Entry a, Entry b) => a.IsTombstone == b.IsTombstone && a.Stamp == b.Stamp;
}
