using System.Globalization;
using Avalonia.Data.Converters;
using VelaShell.Core.Models;
using VelaShell.Services;

namespace VelaShell.Converters;

/// <summary>
/// 把会话自己挑的标签页图标 id(<see cref="SessionTabIcons" />)换成宿主图标集里的几何。
/// </summary>
/// <remarks>
/// 会话树节点在表现层,只持有 id 字符串,几何在视图层解析(与频道字母配色同一个分法)。
/// 认不出的 id 给 null:图标不画,而不是画出一个错的。
/// </remarks>
public sealed class TabIconIdToGeometryConverter : IValueConverter
{
    /// <summary>供在 XAML 中直接使用的共享单例。</summary>
    public static readonly TabIconIdToGeometryConverter Instance = new();

    /// <summary>返回 id 对应的几何;空、认不出时返回 null。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ConnectionIcon.GeometryFor(SessionTabIcons.ResourceKey(value as string));

    /// <summary>反向转换不受支持,始终抛出 <see cref="NotSupportedException" />。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
