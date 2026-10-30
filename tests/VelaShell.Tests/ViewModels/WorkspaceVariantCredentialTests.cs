using VelaShell.Core.Models;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 变体决定"要不要凭据"这件事,必须在**打开表单的那一刻**就生效,而不是等用户碰一下字段。
/// <para>
/// 数据库插件把几种方言收成一个「数据库」页签之后,SQLite 那一档声明了
/// <see cref="WorkspaceFeatures.NoCredentials" />(它就是个本地文件,没有用户名口令这回事)。
/// 变体若只在**字段值变化**时才套用,"新选一次 SQLite"看着是对的(两栏收起来了),
/// 而"打开一条已存的 SQLite 配置"却停在基础描述符那一档:
/// <b>用户名口令两栏冒出来,还被当成必填,表现就是"一个本地文件却非要我登录"。</b>
/// </para>
/// </summary>
[TestClass]
public sealed class WorkspaceVariantCredentialTests
{
    private const string WorkspaceId = "test.db";
    private const string DialectKey = "dialect";

    private sealed class NoopWorkspace : IWorkspaceProvider
    {
        public Task<IWorkspaceDocument> OpenAsync(
            WorkspaceConnectRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static PluginProtocolRegistry Registry(out IDisposable handle)
    {
        var registry = new PluginProtocolRegistry();
        handle = registry.RegisterWorkspace(WorkspaceId, new()
        {
            Id = WorkspaceId,
            DisplayName = "数据库",
            DefaultPort = 3306,
            // 基础描述符是"要凭据"的那一档 —— 正是这一点让漏套变体的后果显形。
            Features = WorkspaceFeatures.CertificateTrust,
            Fields =
            [
                new()
                {
                    Key = DialectKey,
                    Label = "数据库类型",
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "mysql",
                    Choices = [new("mysql", "MySQL"), new("postgresql", "PostgreSQL"), new("sqlite", "SQLite")]
                }
            ],
            VariantKey = DialectKey,
            Variants =
            [
                new() { Value = "mysql", DefaultPort = 3306 },
                new() { Value = "postgresql", DefaultPort = 5432 },
                new()
                {
                    Value = "sqlite",
                    DefaultPort = 1,
                    HostLabel = "数据库文件",
                    Features = WorkspaceFeatures.NoCredentials
                }
            ]
        }, new NoopWorkspace());
        return registry;
    }

    /// <summary>
    /// <b>打开一条已存的 SQLite 配置:凭据两栏一开始就该是收起的。</b>
    /// <para>
    /// 关键在于**全程不碰任何字段** —— 一碰就会触发 setter、把变体套上,
    /// 那样测的就不是"装载时有没有套"这件事了。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task ExistingProfile_WithNoCredentialVariant_HidesCredentialsOnLoad()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            var existing = new SessionProfile
            {
                Name = "本地库",
                ConnectionType = ConnectionType.Plugin,
                PluginProtocolId = WorkspaceId,
                Host = @"D:\data\app.db",
                Port = 1,
                PluginSettings = new(StringComparer.Ordinal) { [DialectKey] = "sqlite" }
            };

            var vm = new ConnectionProfileViewModel(existing, protocolRegistry: registry);
            await LoadedAsync(vm);

            Assert.IsFalse(vm.ShowCredentialFields, "SQLite 是个文件,用户名口令两栏不该出现。");
            Assert.IsTrue(vm.AllowsAnonymous, "不该拿「用户名没填」把连接按钮堵死。");
            Assert.AreEqual("数据库文件", vm.HostLabel, "「主机」那一栏装的是文件路径,标签要按变体改写。");
        }
    }

    /// <summary>
    /// 反面:同一个连接类型下要凭据的那一档,装载后照旧显示两栏。
    /// <para>没有这一条,"一律收起"也能让上面那条通过。</para>
    /// </summary>
    [TestMethod]
    public async Task ExistingProfile_WithCredentialVariant_KeepsCredentialsOnLoad()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            var existing = new SessionProfile
            {
                Name = "远端库",
                ConnectionType = ConnectionType.Plugin,
                PluginProtocolId = WorkspaceId,
                Host = "127.0.0.1",
                Port = 3306,
                Username = "root",
                PluginSettings = new(StringComparer.Ordinal) { [DialectKey] = "mysql" }
            };

            var vm = new ConnectionProfileViewModel(existing, protocolRegistry: registry);
            await LoadedAsync(vm);

            Assert.IsTrue(vm.ShowCredentialFields, "MySQL 是要凭据的。");
            Assert.AreNotEqual("数据库文件", vm.HostLabel, "上一档的标签不该粘到这一档上。");
        }
    }

    /// <summary>
    /// <b>装载时套变体不能把已存的端口盖掉。</b>
    /// <para>
    /// 用户把端口改成 13306(容器映射)存下来,重新打开时必须还是 13306 ——
    /// 装载那一路要用 followPort: false,否则每打开一次就被变体的默认值抹一次。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task ExistingProfile_LoadingTheVariant_DoesNotOverwriteTheSavedPort()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            var existing = new SessionProfile
            {
                Name = "映射端口",
                ConnectionType = ConnectionType.Plugin,
                PluginProtocolId = WorkspaceId,
                Host = "127.0.0.1",
                Port = 13306,
                PluginSettings = new(StringComparer.Ordinal) { [DialectKey] = "mysql" }
            };

            var vm = new ConnectionProfileViewModel(existing, protocolRegistry: registry);
            await LoadedAsync(vm);

            Assert.AreEqual(13306, vm.Port, "已存的端口被变体的默认值盖掉了。");
        }
    }

    /// <summary>
    /// <b>已存的端口恰好是另一档的默认端口时,装载照样不能跟随。</b>
    /// <para>
    /// 13306 不是任何默认端口,"用户手填过"那条判定本来就会护住它,测不出装载这一路跟没跟随。
    /// 3306 不同:它是 MySQL 那一档的默认端口,判定会把它当成"没手填过"。装载时若让端口跟随,
    /// 一条存好的 PostgreSQL@3306 配置每打开一次就被改成 5432。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task ExistingProfile_LoadingTheVariant_KeepsASavedPortThatIsAnotherVariantsDefault()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            var existing = new SessionProfile
            {
                Name = "PG 跑在 3306",
                ConnectionType = ConnectionType.Plugin,
                PluginProtocolId = WorkspaceId,
                Host = "127.0.0.1",
                Port = 3306,
                Username = "postgres",
                PluginSettings = new(StringComparer.Ordinal) { [DialectKey] = "postgresql" }
            };

            var vm = new ConnectionProfileViewModel(existing, protocolRegistry: registry);
            await LoadedAsync(vm);

            Assert.AreEqual(3306, vm.Port, "装载时端口跟随了变体默认值(5432),已存的 3306 被盖掉了。");
        }
    }

    /// <summary>
    /// 等构造函数里发起的那次表单装载完成(解析连接类型可能触发插件惰性激活,是异步的)。
    /// 等的是 <see cref="ConnectionProfileViewModel.PendingPluginLoad" />,不轮询。
    /// </summary>
    private static async Task LoadedAsync(ConnectionProfileViewModel vm)
    {
        await vm.PendingPluginLoad;
        Assert.IsNotEmpty(vm.PluginFields, "插件表单没装载出来。");
    }
}
