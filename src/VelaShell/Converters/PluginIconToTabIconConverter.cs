using System.Globalization;
using Avalonia.Data.Converters;
using VelaShell.PluginSdk;
using VelaShell.Services;

namespace VelaShell.Converters;

/// <summary>
/// 把插件自报的图标(<see cref="PluginIcon" />)换成可绘制的 <see cref="TabIcon" />;没给、或路径解析不了时
/// 退回协议栏同款的通用插头(<see cref="ConnectionIcon.PluginKey" />)。
/// </summary>
/// <remarks>
/// 连接对话框「选类型」那一屏的磁贴用它:卡片视图模型只持有插件交出来的原值(可空),
/// 几何与兜底在视图层解析(与 <see cref="TabIconIdToGeometryConverter" /> 同一个分法)。
/// 规则与插件面板标签页一致,所以直接走 <see cref="ConnectionIcon.ForPanel" />。
/// </remarks>
public sealed class PluginIconToTabIconConverter : IValueConverter
{
    /// <summary>供在 XAML 中直接使用的共享单例。</summary>
    public static readonly PluginIconToTabIconConverter Instance = new();

    /// <summary>返回要画的图标;连通用插头都取不到(无资源字典)时为 null。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ConnectionIcon.ForPanel(value as PluginIcon);

    /// <summary>反向转换不受支持,始终抛出 <see cref="NotSupportedException" />。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
