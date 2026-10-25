namespace VelaShell.Core.Data;

/// <summary>
/// 已存机密在内存里的样子:仓储载入时经 <see cref="ISecretProtector.Unprotect" /> 解成明文;
/// 解不开的(本机加密密钥换过、系统密钥链这次没打开、数据从别的设备拷来)按历史契约原样交回 —— 仍是密文。
/// </summary>
/// <remarks>
/// <para>
/// 所以「载入之后的值还带着密文前缀」就等于「这一份在这台设备上解不开」,判断它不必再找保护器要一次:
/// 连接前的几处「要不要先弹登录框」都是静态判定(<c>MainWindowViewModel.RequiresCredentials</c> 一类),
/// 工作流与凭据解析也各自用得上。原样交回是为了存回去时密文不丢,不是为了拿它去登录 ——
/// 把密文当口令发给服务器只会白白浪费一次认证,还可能撞上 fail2ban 一类的封禁。
/// </para>
/// <para>
/// 只对载入过的值成立。用户真把密码设成以这个前缀开头的字符串,保护器本来就把它当成「已加密」不再加密(幂等),
/// 载入时同样解不开 —— 那种密码在这里也会被认成解不开,极少见,代价是每次连接都问一遍。
/// </para>
/// </remarks>
public static class StoredSecret
{
    /// <summary>密文前缀(<c>AesSecretProtector</c> 写出的格式,两边共用这一个常量)。</summary>
    public const string ProtectedPrefix = "enc1:";

    /// <summary>载入之后仍是密文原样 —— 在这台设备上解不开的那一份。</summary>
    /// <param name="value">载入之后的值(密码、私钥口令)。</param>
    /// <returns>解不开时为 true;明文、空值为 false。</returns>
    public static bool IsUnreadable(string? value) =>
        value is not null && value.StartsWith(ProtectedPrefix, StringComparison.Ordinal);
}
