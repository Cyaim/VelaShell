using VelaShell.Core.Models;

namespace VelaShell.Presentation.ViewModels;

/// <summary>
/// 资源管理器过滤框的匹配规则:按名称、主机、标签、备注(以及所在分组的名字)过滤连接。
/// </summary>
/// <remarks>
/// <para>
/// <b>按空白拆成几个词,每个词都要命中</b>(各词可以落在不同字段上):「prod web」= 标签或分组里有 prod、
/// 名字或主机里有 web。两百台设备的用户靠的正是这种「环境 + 角色」的组合,只认整串的话
/// 一个空格就什么都搜不到。每个词按不区分大小写的连续子串比:过滤是为了「把不相干的藏起来」,
/// 子序列那种松散匹配(命令面板用的)会把一大半留下,等于没过滤。
/// </para>
/// <para>
/// 分组名算作组内每条连接的一个字段:输入分组名就把整组摊出来,不必再记得组里每台叫什么。
/// 用户名不参与 —— 输一个 <c>root</c> 会把几乎所有连接都留下。
/// </para>
/// </remarks>
public static class SessionTreeFilter
{
    /// <summary>把过滤框里的文字拆成待匹配的词;只有空白时返回空数组(= 不过滤)。</summary>
    /// <param name="text">过滤框里的文字。</param>
    /// <returns>去重后的词。</returns>
    public static string[] Terms(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>一条连接是否命中全部词。</summary>
    /// <param name="profile">连接配置。</param>
    /// <param name="groupName">它所在分组的名字;未分组为 null。</param>
    /// <param name="terms">由 <see cref="Terms" /> 拆出来的词;空 = 恒命中。</param>
    /// <returns>每个词都在某个字段里出现过时为 true。</returns>
    public static bool Matches(SessionProfile profile, string? groupName, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(terms);
        foreach (string term in terms)
        {
            if (!Hit(profile.Name, term)
                && !Hit(profile.Host, term)
                && !(profile.Tags ?? []).Any(tag => Hit(tag, term))
                && !Hit(profile.Notes, term)
                && !Hit(groupName, term))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Hit(string? field, string term) =>
        field is { Length: > 0 } && field.Contains(term, StringComparison.OrdinalIgnoreCase);
}
