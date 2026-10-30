using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 打开工作台会话前的两处判定(要不要弹登录框、要不要起 SSH 隧道)读的是 <see cref="WorkspaceVariants" />。
/// <para>
/// 这两处只看描述符本身的话,连接框按变体收起了的东西(SQLite 的凭据、跳板会话),
/// 连的时候又会被要回来;取值不回落到字段默认值的话,一条从没动过"类型"那一栏的配置
/// 会解析不到变体 —— 而连接框里显示的、启动器交给插件的都是默认那一档。
/// </para>
/// </summary>
[TestClass]
public sealed class WorkspaceVariantsTests
{
    private const string DialectKey = "dialect";

    /// <summary>
    /// 基础描述符要凭据、能走隧道;字段默认值指向 SQLite 那一档(没有凭据、没有端点、不走隧道)。
    /// 默认值特意不指向"与描述符同形"的那一档,否则回落不回落测不出差别。
    /// </summary>
    private static WorkspaceDescriptor Descriptor() => new()
    {
        Id = "test.db",
        DisplayName = "数据库",
        DefaultPort = 3306,
        Features = WorkspaceFeatures.CertificateTrust | WorkspaceFeatures.SshTunnel,
        Fields =
        [
            new()
            {
                Key = DialectKey,
                Label = "数据库类型",
                Kind = ProtocolSettingKind.Choice,
                DefaultValue = "sqlite",
                Choices = [new("mysql", "MySQL"), new("sqlite", "SQLite")]
            }
        ],
        VariantKey = DialectKey,
        Variants =
        [
            new()
            {
                Value = "mysql",
                DefaultPort = 3306,
                Features = WorkspaceFeatures.CertificateTrust | WorkspaceFeatures.SshTunnel
            },
            new()
            {
                Value = "sqlite",
                DefaultPort = 1,
                Features = WorkspaceFeatures.NoCredentials | WorkspaceFeatures.NoEndpoint
            }
        ]
    };

    /// <summary>
    /// <b>配置里没存"类型"这个键时,按字段默认值解析变体。</b>
    /// <para>
    /// 与连接配置页、启动器同一条回落链:先看配置里存的,没有再退到字段默认值。
    /// 只看配置的话这里会落回描述符(要凭据、走隧道),与连接框里显示的那一档不一致。
    /// </para>
    /// </summary>
    [TestMethod]
    public void EffectiveFeatures_UsesTheFieldDefault_WhenTheProfileLacksTheKey()
    {
        WorkspaceDescriptor descriptor = Descriptor();
        const WorkspaceFeatures Sqlite = WorkspaceFeatures.NoCredentials | WorkspaceFeatures.NoEndpoint;

        Assert.AreEqual(Sqlite, WorkspaceVariants.EffectiveFeatures(
            descriptor, new Dictionary<string, string>(StringComparer.Ordinal) { ["other"] = "x" }),
            "配置里没有这个键,应当按字段默认值(sqlite)解析。");
        Assert.AreEqual(Sqlite, WorkspaceVariants.EffectiveFeatures(descriptor, null),
            "配置一个设置都没存,同样按字段默认值解析。");

        // 反面:存了就以存的为准,默认值不能盖过它。
        Assert.AreEqual(
            WorkspaceFeatures.CertificateTrust | WorkspaceFeatures.SshTunnel,
            WorkspaceVariants.EffectiveFeatures(
                descriptor, new Dictionary<string, string>(StringComparer.Ordinal) { [DialectKey] = "mysql" }),
            "配置里存了 mysql,不该被字段默认值盖掉。");
    }

    /// <summary>
    /// <b>一个变体都对不上时,沿用描述符自身的能力位。</b>
    /// <para>插件新版删了某一档、或者配置是手改过的:不能因此把能力位清零(那等于"什么都不支持")。</para>
    /// </summary>
    [TestMethod]
    public void EffectiveFeatures_FallsBackToDescriptorFeatures_WhenNoVariantMatches()
    {
        WorkspaceDescriptor descriptor = Descriptor();

        WorkspaceFeatures features = WorkspaceVariants.EffectiveFeatures(
            descriptor, new Dictionary<string, string>(StringComparer.Ordinal) { [DialectKey] = "oracle" });

        Assert.AreEqual(descriptor.Features, features);
    }

    /// <summary>
    /// <b>没有凭据的那一档算匿名</b>:打开 SQLite 会话前不该被判成"缺凭据"而弹一个填了也没用的登录框。
    /// <para>反面:要凭据的那一档不算匿名 —— 没有这一条,"一律匿名"也能让上面那句通过。</para>
    /// </summary>
    [TestMethod]
    public void EffectiveFeatures_NoCredentialsVariant_CountsAsAnonymous()
    {
        WorkspaceDescriptor descriptor = Descriptor();

        Assert.IsTrue(WorkspaceVariants.AllowsAnonymous(WorkspaceVariants.EffectiveFeatures(
                descriptor, new Dictionary<string, string>(StringComparer.Ordinal) { [DialectKey] = "sqlite" })),
            "NoCredentials 蕴含匿名。");
        Assert.IsFalse(WorkspaceVariants.AllowsAnonymous(WorkspaceVariants.EffectiveFeatures(
                descriptor, new Dictionary<string, string>(StringComparer.Ordinal) { [DialectKey] = "mysql" })),
            "MySQL 那一档要凭据,不算匿名。");
        Assert.IsTrue(WorkspaceVariants.AllowsAnonymous(WorkspaceFeatures.AnonymousAccess),
            "显式声明匿名的照旧算匿名。");
    }
}
