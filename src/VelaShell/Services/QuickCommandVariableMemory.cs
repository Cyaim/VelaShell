using VelaShell.Core.Models;

namespace VelaShell.Services;

/// <summary>
/// 快捷命令变量询问框的「上一次填的值」:按快捷命令 + 占位名记,下次弹框时预填。
/// </summary>
/// <remarks>
/// <para>
/// <c>kubectl logs -f {{pod}}</c> 这类命令往往连着跑好几遍,每次都是同一个 pod;没有记忆就得每次重敲。
/// </para>
/// <para>
/// <b>只记在进程里,不落盘、不进同步载荷。</b>占位里填的东西没有任何约束:可能是 pod 名,也可能是
/// <c>{{token}}</c>、<c>{{password}}</c>。落盘就得给它们定级、加密、写进导出与同步的口径;
/// 而这项便利要解决的正是「连着重复用」,进程内足够了 —— 重启之后从默认值重新来,代价只是敲一遍。
/// </para>
/// <para>
/// 名字看起来是凭据的占位(含 pass / pwd / secret / token / otp / 密码 / 口令 / 令牌 …)<b>连进程内也不记</b>:
/// 询问框是明文输入框,预填进去就等于把口令摆在屏幕上给身后的人看。宁可误伤一个叫 <c>bypass</c> 的变量。
/// </para>
/// <para>
/// 填空的那一格不记(并忘掉旧值):留空多半是想要默认值,下一次再把旧值塞回来反而要多删一次。
/// 键优先用快捷命令的 id(改了命令正文记忆照旧跟着它);没有 id 的按命令正文记。
/// </para>
/// </remarks>
public sealed class QuickCommandVariableMemory
{
    /// <summary>最多记多少条命令的值;再多就丢掉最早记下的那条。只是防一个跑飞的脚本把内存撑大。</summary>
    public const int MaxCommands = 200;

    /// <summary>名字里出现这些片段(不分大小写)就当作凭据,不记。</summary>
    private static readonly string[] SensitiveFragments =
    [
        "pass", "pwd", "secret", "token", "otp", "credential", "apikey", "api_key", "api-key", "privatekey", "private_key",
        "密码", "口令", "密钥", "令牌", "验证码", "密碼", "金鑰", "權杖", "驗證碼", "パスワード", "トークン", "비밀번호", "토큰"
    ];

    private readonly Dictionary<string, Dictionary<string, string>> _values = [with(StringComparer.Ordinal)];
    private readonly LinkedList<string> _order = [];

    /// <summary>这个变量名是不是看起来像凭据(这类的值不记)。</summary>
    /// <param name="variableName">占位名。</param>
    /// <returns>像凭据时为 true。</returns>
    public static bool IsSensitive(string variableName) =>
        SensitiveFragments.Any(fragment => variableName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>弹框时每一格的初值:记过的用记下的值,没记过的用占位自己的默认值。</summary>
    /// <param name="commandId">快捷命令 id;没有时为 null。</param>
    /// <param name="commandText">命令正文(没有 id 时作键)。</param>
    /// <param name="template">解析好的模板。</param>
    /// <returns>「变量名 → 初值」,覆盖模板里的每一个变量。</returns>
    public IReadOnlyDictionary<string, string> Prefill(Guid? commandId, string commandText, QuickCommandTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        _values.TryGetValue(KeyFor(commandId, commandText), out Dictionary<string, string>? remembered);
        var initial = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (QuickCommandVariable variable in template.Variables)
        {
            initial[variable.Name] = remembered is not null && remembered.TryGetValue(variable.Name, out string? value)
                ? value
                : variable.DefaultValue;
        }
        return initial;
    }

    /// <summary>用户点了发送:记下这一次填的值(凭据类与空着的格子除外)。</summary>
    /// <param name="commandId">快捷命令 id;没有时为 null。</param>
    /// <param name="commandText">命令正文(没有 id 时作键)。</param>
    /// <param name="template">解析好的模板。</param>
    /// <param name="values">询问框交回的「变量名 → 值」。</param>
    public void Remember(
        Guid? commandId,
        string commandText,
        QuickCommandTemplate template,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        string key = KeyFor(commandId, commandText);
        if (!_values.TryGetValue(key, out Dictionary<string, string>? remembered))
        {
            remembered = new(StringComparer.Ordinal);
        }
        foreach (QuickCommandVariable variable in template.Variables)
        {
            if (IsSensitive(variable.Name)
                || !values.TryGetValue(variable.Name, out string? value)
                || string.IsNullOrWhiteSpace(value))
            {
                remembered.Remove(variable.Name);
                continue;
            }
            remembered[variable.Name] = value;
        }
        _order.Remove(key);
        if (remembered.Count == 0)
        {
            _values.Remove(key);
            return;
        }
        _values[key] = remembered;
        _order.AddLast(key);
        while (_order.Count > MaxCommands && _order.First is { } oldest)
        {
            _values.Remove(oldest.Value);
            _order.RemoveFirst();
        }
    }

    private static string KeyFor(Guid? commandId, string commandText) =>
        commandId is { } id && id != Guid.Empty ? $"id:{id:N}" : $"text:{commandText}";
}
