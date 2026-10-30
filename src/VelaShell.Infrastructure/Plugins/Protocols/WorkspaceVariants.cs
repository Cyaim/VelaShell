using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Infrastructure.Plugins.Protocols;

/// <summary>
/// 按一条已存配置的设置,解析工作台连接类型**这一次**实际生效的变体能力位。
/// <para>
/// 一个连接类型可以按某个设置字段切换形态(数据库插件的「数据库」页签下有好几种方言),
/// 而"要不要凭据""能不能走 SSH 隧道"正是随之变的:SQLite 是一个本地文件,
/// 既没有用户名口令,也不该去连跳板机。打开会话前的判定只看描述符本身的话,
/// 连接框按变体收起了的东西,连的时候又会被要回来。
/// </para>
/// <para>
/// <b>取值的回落链与连接配置页、启动器一致</b>:先看配置里存的值,存的里没有这个键
/// 再退到字段的 <see cref="ProtocolSettingField.DefaultValue" />。只看配置的话,
/// 一条从没动过"类型"那一栏的配置(值从没落过盘)会解析不到变体,
/// 而连接框里显示的、启动器交给插件的都是默认那一档。
/// </para>
/// </summary>
public static class WorkspaceVariants
{
    /// <summary>
    /// 按键取设置值:配置里存的优先,没有这个键时退到字段声明的默认值。
    /// </summary>
    /// <param name="descriptor">连接类型描述。</param>
    /// <param name="settings">配置里存的非机密设置;可为 <see langword="null" />。</param>
    /// <param name="key">字段键。</param>
    /// <returns>取到的值;配置里没有、字段也没声明默认值(或没有这个字段)时为 <see langword="null" />。</returns>
    public static string? SettingOrDefault(
        WorkspaceDescriptor descriptor,
        IReadOnlyDictionary<string, string>? settings,
        string key)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return settings?.GetValueOrDefault(key)
               ?? descriptor.Fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal))
                   ?.DefaultValue;
    }

    /// <summary>
    /// 这条配置实际生效的能力位:对上了变体且变体声明了能力位就用变体的(整体替换,不是按位合并),
    /// 否则用描述符自身的。
    /// </summary>
    /// <param name="descriptor">连接类型描述。</param>
    /// <param name="settings">配置里存的非机密设置;可为 <see langword="null" />。</param>
    /// <returns>生效的能力位。</returns>
    public static WorkspaceFeatures EffectiveFeatures(
        WorkspaceDescriptor descriptor,
        IReadOnlyDictionary<string, string>? settings)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.ResolveVariant(key => SettingOrDefault(descriptor, settings, key))?.Features
               ?? descriptor.Features;
    }

    /// <summary>
    /// 这组能力位是否允许不填凭据直接连。<see cref="WorkspaceFeatures.NoCredentials" /> 蕴含匿名:
    /// 没有凭据这回事(SQLite 就是个文件),就不能拿"用户名没填"把连接拦下来。
    /// <para>连接配置页与打开会话前的判定共用这一条,两处判据才不会不一致。</para>
    /// </summary>
    /// <param name="features">生效的能力位(通常来自 <see cref="EffectiveFeatures" />)。</param>
    /// <returns>允许匿名时为 <see langword="true" />。</returns>
    public static bool AllowsAnonymous(WorkspaceFeatures features) =>
        features.HasFlag(WorkspaceFeatures.AnonymousAccess) || features.HasFlag(WorkspaceFeatures.NoCredentials);
}
