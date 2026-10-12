using System.Text;

namespace VelaShell.Views;

/// <summary>
/// 资源管理器式「键入定位」的纯逻辑部分:把短时间内连着敲的字符攒成一个前缀,
/// 再在名字列表里找该选中哪一项。
/// <para>
/// 与 UI 无关,可单测;接线(收键入、选中、滚动、移焦点)在 <see cref="TypeAheadSelection" />。
/// </para>
/// </summary>
/// <param name="timeProvider">计时来源;测试里换成手动拨的钟。</param>
internal sealed class TypeAheadSearch(TimeProvider? timeProvider = null)
{
    /// <summary>两次键入间隔超过这么久,就当作重新开始敲另一个名字。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly StringBuilder _term = new();
    private long _lastInput;

    /// <summary>
    /// 收一段键入的文本,返回追加之后的前缀。文本里没有可当作名字的字符时返回 null ——
    /// 这一下不是定位输入,事件应当留给别人处理。
    /// </summary>
    /// <param name="text">一次 TextInput 带来的文本(输入法上屏时可能是一整个词)。</param>
    public string? Append(string? text)
    {
        long now = _time.GetTimestamp();
        if (_term.Length > 0 && _time.GetElapsedTime(_lastInput, now) > Timeout)
        {
            _term.Clear();
        }

        string typed = new([.. (text ?? string.Empty).Where(c => !char.IsControl(c))]);

        // 开头的空格不算:空格在列表里另有用处,也没人会去找以空格开头的名字。
        // 敲到一半的空格("my f")照收,那是名字的一部分。
        if (typed.Length == 0 || (_term.Length == 0 && string.IsNullOrWhiteSpace(typed)))
        {
            return null;
        }

        _term.Append(typed);
        _lastInput = now;
        return _term.ToString();
    }

    /// <summary>丢掉攒下的前缀,下一次键入从头开始(换了目录时用:接着敲的是新目录里的名字)。</summary>
    public void Reset() => _term.Clear();

    /// <summary>
    /// 在 <paramref name="names" /> 里为前缀 <paramref name="term" /> 找该选中的下标,没有匹配时返回 -1。
    /// 名字为 null 的项(如合成的 ".." 行)永远不参与匹配;比较不分大小写。
    /// </summary>
    /// <remarks>
    /// 规则照资源管理器:
    /// <list type="bullet">
    /// <item>只敲了一个字符:从当前项的<b>下一项</b>起往后找、到底绕回 —— 所以慢慢地连按 f,
    /// 会在 f 开头的项之间一个个轮转。</item>
    /// <item>攒了多个字符:从当前项<b>本身</b>起找 —— 敲 f、fx、fxy 时,只要当前项还对得上就留在原地。</item>
    /// <item>快速连按同一个字符(ff、fff)又没有以它开头的项时,退化成按首字符轮转到下一项。</item>
    /// </list>
    /// </remarks>
    /// <param name="names">各项的名字,下标与列表一致。</param>
    /// <param name="current">当前项(焦点或选中所在)的下标;没有时传 -1。</param>
    /// <param name="term">攒下的前缀。</param>
    public static int FindMatch(IReadOnlyList<string?> names, int current, string term)
    {
        if (names.Count == 0 || string.IsNullOrEmpty(term))
        {
            return -1;
        }

        int hit = Scan(names, term.Length == 1 ? current + 1 : current, term);
        if (hit >= 0 || term.Length == 1 || !IsOneRepeatedChar(term))
        {
            return hit;
        }
        return Scan(names, current + 1, term[..1]);
    }

    /// <summary>从 <paramref name="start" /> 起往后找第一个以 <paramref name="prefix" /> 开头的项,到底绕回开头。</summary>
    private static int Scan(IReadOnlyList<string?> names, int start, string prefix)
    {
        int count = names.Count;
        int first = start < 0 || start >= count ? 0 : start;
        for (int offset = 0; offset < count; offset++)
        {
            int index = (first + offset) % count;
            if (names[index]?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
            {
                return index;
            }
        }
        return -1;
    }

    private static bool IsOneRepeatedChar(string term) =>
        term.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(term[0]));
}
