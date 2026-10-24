namespace VelaShell.Core.Models;

/// <summary>连接对话框里可挑的一个标签页图标:存进配置的 id 与界面上的名字。</summary>
/// <param name="Id">存进 <see cref="TerminalOverrides.TabIcon" /> 的 id,即 lucide 字形名(资源键是 <c>Icon.&lt;id&gt;</c>)。</param>
/// <param name="NameKey">图标名字的资源键(悬停提示与无障碍名称)。</param>
public sealed record SessionTabIconChoice(string Id, string NameKey);

/// <summary>
/// 按会话自定义标签页图标的那张清单:「像挑颜色一样挑图标」(Xshell / Termius 都有)。
/// </summary>
/// <remarks>
/// <para>
/// 与按协议的默认图标(<c>Services/ConnectionIcon</c>,§68)是两件事:那个答「哪一种连接」,
/// 这里让用户自己答「这台是干什么的」—— 数据库、跳板、生产、家里那台 NAS。挑了就压过协议图标与插件自报的图标;
/// 不挑(null)就照旧跟协议走。
/// </para>
/// <para>
/// <b>存的是 id,不是几何。</b>认不出的 id(更新的版本加的、配置文件手改的)一律当作没挑,退回协议图标 ——
/// 一个写错的字符串不该让标签上的图标消失。清单只收宿主 <c>Icons.axaml</c> 里有的 lucide 字形,
/// <c>SessionTabIconCatalogTests</c> 在真实资源字典里逐个解析过。
/// </para>
/// <para>
/// 名字描述的是<b>画的是什么</b>(服务器、火焰、房子),不替用户规定含义:同一枚火焰,有人拿来标生产,有人标压测。
/// </para>
/// </remarks>
public static class SessionTabIcons
{
    /// <summary>全部可挑的图标,按对话框里排列的顺序。</summary>
    public static IReadOnlyList<SessionTabIconChoice> All { get; } =
    [
        new("server", "TabIcon_Server"),
        new("database", "TabIcon_Database"),
        new("cloud", "TabIcon_Cloud"),
        new("globe", "TabIcon_Globe"),
        new("box", "TabIcon_Box"),
        new("layers", "TabIcon_Layers"),
        new("network", "TabIcon_Network"),
        new("ethernet-port", "TabIcon_EthernetPort"),
        new("wifi", "TabIcon_Wifi"),
        new("shield", "TabIcon_Shield"),
        new("key-round", "TabIcon_Key"),
        new("cpu", "TabIcon_Cpu"),
        new("map-pinned", "TabIcon_Location"),
        new("hard-drive", "TabIcon_HardDrive"),
        new("square-terminal", "TabIcon_Terminal"),
        new("bug", "TabIcon_Bug"),
        new("house", "TabIcon_House"),
        new("flame", "TabIcon_Flame"),
        new("star", "TabIcon_Star"),
        new("zap", "TabIcon_Zap"),
        new("gauge", "TabIcon_Gauge"),
        new("activity", "TabIcon_Activity"),
        new("user", "TabIcon_User")
    ];

    private static readonly Dictionary<string, SessionTabIconChoice> ById =
        All.ToDictionary(static choice => choice.Id, StringComparer.Ordinal);

    /// <summary>把配置里存的 id 规整成清单里的那个;空、认不出时返回 null(= 跟协议)。</summary>
    /// <param name="id">配置里存的 id。</param>
    /// <returns>清单里的 id,或 null。</returns>
    public static string? Normalize(string? id) =>
        id is null ? null : ById.TryGetValue(id.Trim(), out SessionTabIconChoice? choice) ? choice.Id : null;

    /// <summary>按 id 找清单里的那一项;认不出时返回 null。</summary>
    /// <param name="id">图标 id。</param>
    /// <returns>清单项,或 null。</returns>
    public static SessionTabIconChoice? Find(string? id) => Normalize(id) is { } known ? ById[known] : null;

    /// <summary>图标 id 对应的资源键(<c>Icon.&lt;id&gt;</c>);认不出时返回 null。</summary>
    /// <param name="id">图标 id。</param>
    /// <returns>资源键,或 null。</returns>
    public static string? ResourceKey(string? id) => Normalize(id) is { } known ? $"Icon.{known}" : null;

    /// <summary>这条配置自己挑的图标 id;没挑或认不出时返回 null(跟协议)。</summary>
    /// <param name="profile">连接配置。</param>
    /// <returns>图标 id,或 null。</returns>
    public static string? For(SessionProfile? profile) => Normalize(profile?.Terminal?.TabIcon);
}
