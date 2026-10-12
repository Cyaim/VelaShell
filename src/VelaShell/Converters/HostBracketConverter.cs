using System.Globalization;
using Avalonia.Data.Converters;
using VelaShell.Core.Net;

namespace VelaShell.Converters;

/// <summary>
/// 主机拼在 <c>:端口</c> 前面显示时用:IPv6 字面量加方括号(<c>[2001:db8::1]</c>),否则 <c>2001:db8::1:22</c> 读不出哪段是端口。
/// 主机名与 IPv4 原样。
/// </summary>
public sealed class HostBracketConverter : IValueConverter
{
    /// <summary>可在 XAML 中直接引用的共享单例。</summary>
    public static readonly HostBracketConverter Instance = new();

    /// <summary>字符串按 <see cref="HostAddress.Bracket" /> 处理;别的原样返回。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string host ? HostAddress.Bracket(host) : value;

    /// <summary>不支持反向转换,调用即抛出 <see cref="NotSupportedException" />。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
