using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 连接对话框的「选类型」这一步:卡片 → 下一步 → 表单;上一步 / 更换回到卡片。
/// <para>
/// 一个页签承载一族连接类型(数据库:MySQL / PostgreSQL / SQLite)时,新建连接先选类型再进表单,
/// 表单里那一行"类型"下拉不再出现,由表头「类型:X」替代。不出卡片的页签(Redis、S3 这类)
/// 全程停在表单上,与没有这一步时一样。
/// </para>
/// <para>纯视图模型用例:不起界面,只看状态机与它驱动的连接框形态(端口、主机标签、凭据)。</para>
/// </summary>
[TestClass]
public sealed class ConnectionProfileTypePickerTests
{
    private const string DbId = "test.db";
    private const string CacheId = "test.cache";
    private const string DialectKey = "dialect";
    private const string DatabaseKey = "database";

    /// <summary>卡片图标:第一期取工作台描述符自报的那一个。</summary>
    private static readonly PluginIcon DbIcon = PluginIcon.Stroked("M4 4h16v16H4z");

    private sealed class NoopWorkspace : IWorkspaceProvider
    {
        public Task<IWorkspaceDocument> OpenAsync(
            WorkspaceConnectRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// 数据库式的工作台:「类型」那一栏选 MySQL(3306)/ PostgreSQL(5432)/ SQLite(文件,无凭据无端点),
    /// 外加一个普通的「数据库名」字段 —— 用来看"其它已填值一律不动",以及变体字段那一行藏起来之后
    /// 「常规」页上照旧有东西可画。
    /// </summary>
    private static WorkspaceDescriptor DbDescriptor() => new()
    {
        Id = DbId,
        DisplayName = "数据库",
        DefaultPort = 3306,
        Icon = DbIcon,
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
            },
            new() { Key = DatabaseKey, Label = "数据库名", Kind = ProtocolSettingKind.Text }
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
                Features = WorkspaceFeatures.NoCredentials | WorkspaceFeatures.NoEndpoint
            }
        ]
    };

    /// <summary>数据库页签 + 一个不出卡片的缓存页签(Redis 形态:没有 VariantKey)。</summary>
    private static PluginProtocolRegistry Registry(out IDisposable handle)
    {
        var registry = new PluginProtocolRegistry();
        IDisposable db = registry.RegisterWorkspace(DbId, DbDescriptor(), new NoopWorkspace());
        IDisposable cache = registry.RegisterWorkspace(CacheId, new()
        {
            Id = CacheId,
            DisplayName = "缓存",
            DefaultPort = 6379
        }, new NoopWorkspace());
        handle = new CompositeHandle(db, cache);
        return registry;
    }

    private sealed class CompositeHandle(params IDisposable[] handles) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable handle in handles)
            {
                handle.Dispose();
            }
        }
    }

    private static PluginProtocolFieldViewModel Row(ConnectionProfileViewModel vm, string key) =>
        vm.PluginFields.Single(f => f.Key == key);

    private static async Task<ConnectionProfileViewModel> NewOnDbTabAsync(PluginProtocolRegistry registry)
    {
        var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
        await vm.SelectPluginProtocolCommand.Execute(DbId).FirstAsync();
        return vm;
    }

    /// <summary>
    /// <b>新建连接点进数据库页签:先出卡片,默认那一档已经选中,「下一步」立即可用</b>(决策 D8)。
    /// <para>卡片顺序、名字取自变体字段的候选项;图标第一期是工作台自报的那一个。</para>
    /// </summary>
    [TestMethod]
    public async Task TypePicker_NewProfile_ShowsCards_WithTheDefaultPreselected()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);

            Assert.AreEqual(ConnectionProfileStep.PickType, vm.Step, "新建、页签带类型选择:应先停在卡片上。");
            Assert.IsTrue(vm.IsPickingType);
            CollectionAssert.AreEqual(
                new[] { "mysql", "postgresql", "sqlite" },
                vm.TypeCards.Select(c => c.Value).ToArray(),
                "卡片顺序要与候选项一致。");
            CollectionAssert.AreEqual(
                new[] { "MySQL", "PostgreSQL", "SQLite" },
                vm.TypeCards.Select(c => c.Label).ToArray());
            Assert.IsTrue(vm.TypeCards.All(c => ReferenceEquals(c.Icon, DbIcon)), "第一期卡片图标 = 工作台图标。");
            Assert.AreEqual("mysql", vm.SelectedTypeValue, "默认值那一档要预选。");
            CollectionAssert.AreEqual(
                new[] { true, false, false },
                vm.TypeCards.Select(c => c.IsSelected).ToArray(),
                "只有预选的那张卡片是高亮的。");
            Assert.IsTrue(await vm.NextCommand.CanExecute.FirstAsync(), "预选了一档,「下一步」应立即可用。");
            Assert.IsFalse(vm.ShowTypeHeader, "还在卡片上,表头不出现。");
            Assert.IsFalse(vm.ShowBackButton, "卡片阶段没有「上一步」。");
            Assert.IsFalse(vm.ShowChangeTypeLink);
            Assert.IsFalse(vm.IsEditing);
        }
    }

    /// <summary>
    /// <b>单击卡片只选不走</b>(T2):高亮换过去,但还停在卡片上,变体字段与端口都没被碰。
    /// </summary>
    [TestMethod]
    public async Task TypePicker_SingleClick_SelectsWithoutLeavingTheCards()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);

            await vm.SelectTypeCardCommand.Execute("postgresql").FirstAsync();

            Assert.IsTrue(vm.IsPickingType, "单击不该离开卡片。");
            Assert.AreEqual("postgresql", vm.SelectedTypeValue);
            CollectionAssert.AreEqual(
                new[] { false, true, false },
                vm.TypeCards.Select(c => c.IsSelected).ToArray(),
                "高亮要跟着换过去,上一张要灭掉。");
            Assert.AreEqual("mysql", Row(vm, DialectKey).Text, "点「下一步」之前不该写进变体字段。");
            Assert.AreEqual(3306, vm.Port, "点「下一步」之前端口不该跟着变。");
        }
    }

    /// <summary>
    /// <b>「下一步」进表单,端口跟着类型走</b>(T3):选 PostgreSQL,端口是 5432;
    /// 表头出现「类型:PostgreSQL」,新建时页脚有「上一步」,落在「常规」页。
    /// </summary>
    [TestMethod]
    public async Task TypePicker_Next_GoesToTheForm_AndThePortFollowsTheType()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);
            await vm.SelectTypeCardCommand.Execute("postgresql").FirstAsync();

            await vm.NextCommand.Execute().FirstAsync();

            Assert.AreEqual(ConnectionProfileStep.Form, vm.Step);
            Assert.AreEqual("postgresql", Row(vm, DialectKey).Text, "选中的类型要写进变体字段。");
            Assert.AreEqual(5432, vm.Port, "选了 PostgreSQL,端口还停在 MySQL 的 3306 上。");
            Assert.AreEqual("PostgreSQL", vm.CurrentTypeLabel, "表头显示的是候选项的名字。");
            Assert.IsTrue(vm.ShowTypeHeader);
            Assert.IsTrue(vm.ShowBackButton, "新建:表单阶段有「上一步」。");
            Assert.IsFalse(vm.ShowChangeTypeLink, "新建不走「更换」。");
            Assert.AreEqual(ConnectionProfileSection.General, vm.SelectedSection);
            Assert.AreEqual("10.0.0.9", vm.Host, "其它已填的值不该被动。");
            Assert.IsFalse(await vm.NextCommand.CanExecute.FirstAsync(), "已经在表单上了,「下一步」不该可用。");
            Assert.IsTrue(await vm.BackCommand.CanExecute.FirstAsync());
        }
    }

    /// <summary>
    /// <b>「下一步」时主机标签与凭据也跟着类型走</b>(T3):SQLite 是个文件 ——
    /// 主机那一栏改标「数据库文件」,凭据两栏与端口栏收起,按钮不能因没填用户名而灰着。
    /// </summary>
    [TestMethod]
    public async Task TypePicker_Next_ToSqlite_ReshapesHostLabelAndCredentials()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);
            Assert.IsTrue(vm.ShowCredentialFields, "前置条件:默认那一档(MySQL)要凭据。");

            await vm.SelectTypeCardCommand.Execute("sqlite").FirstAsync();
            await vm.NextCommand.Execute().FirstAsync();

            Assert.AreEqual("数据库文件", vm.HostLabel);
            Assert.IsFalse(vm.ShowCredentialFields, "SQLite 没有用户名口令这回事。");
            Assert.IsFalse(vm.ShowPortField, "SQLite 没有端点。");
            Assert.IsTrue(vm.AllowsAnonymous, "NoCredentials 蕴含匿名,否则按钮永远灰着。");
            Assert.AreEqual("SQLite", vm.CurrentTypeLabel);
        }
    }

    /// <summary>
    /// <b>用户手填的端口不被「下一步」盖掉</b>:端口只在「还是某个协议的默认值」时跟随,
    /// 与切页签同一条判定。
    /// </summary>
    [TestMethod]
    public async Task TypePicker_Next_KeepsAHandTypedPort()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);
            vm.Port = 13306;

            await vm.SelectTypeCardCommand.Execute("postgresql").FirstAsync();
            await vm.NextCommand.Execute().FirstAsync();

            Assert.AreEqual(13306, vm.Port, "用户手填的端口被「下一步」盖掉了。");
            Assert.AreEqual("postgresql", Row(vm, DialectKey).Text);
        }
    }

    /// <summary>
    /// <b>「上一步」回到卡片,不清任何值、不碰端口</b>(T4)。
    /// <para>
    /// 预选的是变体字段当前那一档;回来看一眼再点「下一步」,表单原样还在 ——
    /// 包括手填的端口、主机、用户名与其它字段。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task TypePicker_Back_ReturnsToTheCards_WithoutClearingValuesOrPort()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);
            await vm.SelectTypeCardCommand.Execute("postgresql").FirstAsync();
            await vm.NextCommand.Execute().FirstAsync();
            Assert.AreEqual(5432, vm.Port, "前置条件:端口已经跟上了。");

            // 端口还是变体默认值时退回去:不该被拉回页签默认的 3306。
            await vm.BackCommand.Execute().FirstAsync();
            Assert.IsTrue(vm.IsPickingType, "「上一步」应回到卡片。");
            Assert.AreEqual(5432, vm.Port, "「上一步」不该碰端口。");
            Assert.AreEqual("postgresql", vm.SelectedTypeValue, "回到卡片时预选的是当前那一档。");
            Assert.IsTrue(vm.TypeCards.Single(c => c.Value == "postgresql").IsSelected);
            Assert.IsFalse(vm.ShowBackButton, "卡片阶段没有「上一步」。");
            Assert.IsFalse(vm.ShowTypeHeader);
            Assert.IsFalse(await vm.BackCommand.CanExecute.FirstAsync());

            // 再走一遍,这回带着手填的值退回去。
            await vm.NextCommand.Execute().FirstAsync();
            vm.Host = "db.example";
            vm.Username = "admin";
            vm.Port = 15432;
            Row(vm, DatabaseKey).Text = "app";

            await vm.BackCommand.Execute().FirstAsync();

            Assert.IsTrue(vm.IsPickingType);
            Assert.AreEqual("db.example", vm.Host);
            Assert.AreEqual("admin", vm.Username);
            Assert.AreEqual(15432, vm.Port);
            Assert.AreEqual("app", Row(vm, DatabaseKey).Text);
            Assert.AreEqual("postgresql", Row(vm, DialectKey).Text, "退回卡片不该改变体字段。");

            await vm.NextCommand.Execute().FirstAsync();
            Assert.AreEqual(ConnectionProfileStep.Form, vm.Step);
            Assert.AreEqual(15432, vm.Port, "退回再前进,手填的端口不该丢。");
            Assert.AreEqual("app", Row(vm, DatabaseKey).Text);
        }
    }

    /// <summary><b>双击卡片 = 选中 + 下一步</b>(T7)。</summary>
    [TestMethod]
    public async Task TypePicker_DoubleClick_SelectsAndConfirms()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);

            await vm.ConfirmTypeCardCommand.Execute("postgresql").FirstAsync();

            Assert.AreEqual(ConnectionProfileStep.Form, vm.Step, "双击应直接进表单。");
            Assert.AreEqual("postgresql", vm.SelectedTypeValue);
            Assert.AreEqual("postgresql", Row(vm, DialectKey).Text);
            Assert.AreEqual(5432, vm.Port);
            Assert.AreEqual("PostgreSQL", vm.CurrentTypeLabel);
        }
    }

    /// <summary>
    /// <b>换协议复位这一步</b>(T5):去了别的插件页签或内建协议,卡片清空、表头与「上一步」消失;
    /// 再回到数据库页签,类型要重新选(上一次的确认不带过去)。
    /// </summary>
    [TestMethod]
    public async Task TypePicker_SwitchingProtocol_ResetsTheStep()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);
            await vm.ConfirmTypeCardCommand.Execute("postgresql").FirstAsync();
            Assert.IsTrue(vm.ShowTypeHeader, "前置条件:已经确认过类型、停在表单上。");

            await vm.SelectPluginProtocolCommand.Execute(CacheId).FirstAsync();

            Assert.AreEqual(ConnectionProfileStep.Form, vm.Step);
            Assert.IsEmpty(vm.TypeCards, "上一个页签的卡片留到了缓存页签上。");
            Assert.IsNull(vm.SelectedTypeValue);
            Assert.IsFalse(vm.ShowTypeHeader, "缓存页签没有类型可选,表头不该出现。");
            Assert.IsFalse(vm.ShowBackButton);

            await vm.SelectPluginProtocolCommand.Execute(DbId).FirstAsync();
            Assert.IsTrue(vm.IsPickingType, "换过协议再回来,类型要重新选。");
            Assert.AreEqual(3, vm.TypeCards.Count);

            await vm.SelectConnectionTypeCommand.Execute(ConnectionType.SSH).FirstAsync();
            Assert.AreEqual(ConnectionProfileStep.Form, vm.Step, "内建协议没有卡片。");
            Assert.IsEmpty(vm.TypeCards);
            Assert.IsFalse(vm.ShowTypeHeader);
            Assert.IsFalse(vm.ShowBackButton);
        }
    }

    /// <summary>
    /// <b>还没确认类型就重复点同一个页签,回到(留在)卡片</b>(T1 的早退路径);
    /// 点去 SSH 再点回来,同样回到卡片。
    /// </summary>
    [TestMethod]
    public async Task TypePicker_ReselectingTheTabBeforeConfirming_ReturnsToTheCards()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);

            await vm.SelectPluginProtocolCommand.Execute(DbId).FirstAsync();
            Assert.IsTrue(vm.IsPickingType, "重复点同一页签、还没确认:应留在卡片上。");
            Assert.AreEqual(3, vm.TypeCards.Count);
            Assert.IsNotNull(vm.SelectedTypeValue, "卡片上要有预选,「下一步」才可用。");

            await vm.SelectConnectionTypeCommand.Execute(ConnectionType.SSH).FirstAsync();
            await vm.SelectPluginProtocolCommand.Execute(DbId).FirstAsync();
            Assert.IsTrue(vm.IsPickingType, "点去 SSH 又点回来、还没确认:应回到卡片。");
            Assert.AreEqual(3, vm.TypeCards.Count);
        }
    }

    /// <summary>
    /// <b>确认过类型之后重复点同一个页签,留在表单上</b>:那一下只是"刷新页签",
    /// 不该把用户踢回卡片。
    /// </summary>
    [TestMethod]
    public async Task TypePicker_ReselectingTheTabAfterConfirming_StaysOnTheForm()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);
            await vm.ConfirmTypeCardCommand.Execute("postgresql").FirstAsync();

            await vm.SelectPluginProtocolCommand.Execute(DbId).FirstAsync();

            Assert.AreEqual(ConnectionProfileStep.Form, vm.Step, "确认过的类型被重复点页签打回了卡片。");
            Assert.IsTrue(vm.ShowTypeHeader);
            Assert.AreEqual(5432, vm.Port);
        }
    }

    /// <summary>
    /// <b>编辑态恒在表单上:表头带「更换」,没有「上一步」</b>(决策 D9 / D14)。
    /// 卡片照样建好 —— 点「更换」要用到。
    /// </summary>
    [TestMethod]
    public async Task TypePicker_EditingProfile_StartsOnTheForm_WithChangeLink_AndNoBack()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(ExistingPostgres(5432), protocolRegistry: registry);
            await vm.PendingPluginLoad;

            Assert.IsTrue(vm.IsEditing);
            Assert.AreEqual(ConnectionProfileStep.Form, vm.Step, "编辑已有配置不该先出卡片。");
            Assert.IsTrue(vm.ShowTypeHeader);
            Assert.AreEqual("PostgreSQL", vm.CurrentTypeLabel);
            Assert.IsTrue(vm.ShowChangeTypeLink, "编辑态改类型走表头的「更换」。");
            Assert.IsFalse(vm.ShowBackButton, "编辑态没有「上一步」。");
            Assert.IsFalse(await vm.BackCommand.CanExecute.FirstAsync());
            Assert.IsTrue(await vm.ChangeTypeCommand.CanExecute.FirstAsync());
            Assert.AreEqual(3, vm.TypeCards.Count, "卡片要提前建好,「更换」才有东西可选。");
        }
    }

    /// <summary>
    /// <b>编辑态点「更换」→ 选另一档 → 下一步,类型就换过去了</b>(T4 → T3)。
    /// 端口按默认判定跟随(5432 是变体默认值 → 跟到 3306);卡片阶段同样没有「上一步」。
    /// </summary>
    [TestMethod]
    public async Task TypePicker_EditingProfile_ChangeTypeThenNext_SwitchesTheType()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(ExistingPostgres(5432), protocolRegistry: registry);
            await vm.PendingPluginLoad;

            await vm.ChangeTypeCommand.Execute().FirstAsync();
            Assert.IsTrue(vm.IsPickingType, "「更换」应回到卡片。");
            Assert.AreEqual("postgresql", vm.SelectedTypeValue, "预选的是当前类型。");
            Assert.IsFalse(vm.ShowBackButton, "编辑态的卡片阶段也没有「上一步」。");
            Assert.AreEqual(5432, vm.Port, "「更换」本身不碰端口。");

            await vm.SelectTypeCardCommand.Execute("mysql").FirstAsync();
            await vm.NextCommand.Execute().FirstAsync();

            Assert.AreEqual(ConnectionProfileStep.Form, vm.Step);
            Assert.AreEqual("mysql", Row(vm, DialectKey).Text, "换型没写进变体字段。");
            Assert.AreEqual(3306, vm.Port, "端口还是变体默认值时应跟随新类型。");
            Assert.AreEqual("MySQL", vm.CurrentTypeLabel);
            Assert.IsTrue(vm.ShowChangeTypeLink);
            Assert.AreEqual("db.internal", vm.Host, "换型不该动已填的值。");
            Assert.AreEqual("orders", Row(vm, DatabaseKey).Text);
        }
    }

    /// <summary>
    /// <b>只要出卡片,变体字段那一行在表单里永不显示</b>(决策 D11):卡片阶段、表单阶段、
    /// 编辑态都一样;同页的别的字段照常显示,「常规」页那一节也还在。
    /// </summary>
    [TestMethod]
    public async Task TypePicker_VariantFieldRow_IsNeverShown()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);
            Assert.IsFalse(Row(vm, DialectKey).IsRowVisible, "卡片阶段:变体字段那一行不该显示。");
            Assert.IsTrue(Row(vm, DatabaseKey).IsRowVisible, "别的字段照常显示。");

            await vm.NextCommand.Execute().FirstAsync();
            Assert.IsFalse(Row(vm, DialectKey).IsRowVisible, "表单阶段:变体字段那一行由表头替代。");
            Assert.IsTrue(Row(vm, DatabaseKey).IsRowVisible);
            Assert.IsTrue(vm.ShowPluginGeneralFields);

            var editing = new ConnectionProfileViewModel(ExistingPostgres(5432), protocolRegistry: registry);
            await editing.PendingPluginLoad;
            Assert.IsFalse(Row(editing, DialectKey).IsRowVisible, "编辑态:变体字段那一行同样不显示。");
            Assert.IsTrue(Row(editing, DatabaseKey).IsRowVisible);
        }
    }

    /// <summary>
    /// <b>陈旧续体不把卡片盖到别的协议上。</b>
    /// <para>
    /// 数据库页签的插件还在惰性激活时,用户点去了缓存页签;激活这才完成。那次装载已经作废,
    /// 不能再去定步骤 —— 否则缓存页签上会冒出一屏数据库类型的卡片。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task TypePicker_StaleLoad_DoesNotPutCardsOnAnotherProtocol()
    {
        var registry = new PluginProtocolRegistry();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? dbHandle = null;
        registry.DeclareWorkspaces("plugin.db", [new() { Id = DbId, DisplayName = "数据库", DefaultPort = 3306 }]);
        registry.ActivationRequested = async _ =>
        {
            await gate.Task.ConfigureAwait(false);
            dbHandle = registry.RegisterWorkspace(DbId, DbDescriptor(), new NoopWorkspace());
            return true;
        };
        using IDisposable cache = registry.RegisterWorkspace(CacheId, new()
        {
            Id = CacheId,
            DisplayName = "缓存",
            DefaultPort = 6379
        }, new NoopWorkspace());
        try
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };

            Task loadingDb = vm.SelectPluginProtocolAsync(DbId);
            Assert.IsFalse(loadingDb.IsCompleted, "前置条件:数据库页签的装载要卡在激活上。");
            await vm.SelectPluginProtocolCommand.Execute(CacheId).FirstAsync();

            gate.SetResult();
            await loadingDb;

            Assert.AreEqual(CacheId, vm.PluginProtocolId);
            Assert.AreEqual(ConnectionProfileStep.Form, vm.Step, "作废的装载把步骤改到了卡片上。");
            Assert.IsEmpty(vm.TypeCards, "数据库的卡片被盖到了缓存页签上。");
            Assert.IsFalse(vm.ShowTypeHeader);
            Assert.IsFalse(vm.ShowBackButton);
        }
        finally
        {
            dbHandle?.Dispose();
        }
    }

    /// <summary>不出卡片的几种形态(Redis / S3 这类,或变体字段画不成卡片)。</summary>
    public enum NoPickerShape
    {
        /// <summary>没有 VariantKey(Redis / S3 的样子)。</summary>
        NoVariantKey,

        /// <summary>变体字段是动态下拉:候选是现取的。</summary>
        DynamicChoice,

        /// <summary>变体字段可手输:值不在表里正是它的意义。</summary>
        CustomValue,

        /// <summary>变体字段只有一个候选:没得选。</summary>
        SingleChoice,

        /// <summary>变体字段是隐藏的。</summary>
        Hidden
    }

    /// <summary>
    /// <b>没有类型可选的工作台:全程在表单上,没有表头、没有「上一步」</b> ——
    /// 与没有这一步时完全一样。那一行下拉(若有)照常显示在表单里。
    /// </summary>
    [TestMethod]
    [DataRow(NoPickerShape.NoVariantKey)]
    [DataRow(NoPickerShape.DynamicChoice)]
    [DataRow(NoPickerShape.CustomValue)]
    [DataRow(NoPickerShape.SingleChoice)]
    [DataRow(NoPickerShape.Hidden)]
    public async Task TypePicker_NewProfile_WithoutVariants_StartsOnForm_NoHeader_NoBack(NoPickerShape shape)
    {
        var registry = new PluginProtocolRegistry();
        using IDisposable handle = registry.RegisterWorkspace(CacheId, new()
        {
            Id = CacheId,
            DisplayName = "缓存",
            DefaultPort = 6379,
            Fields =
            [
                new()
                {
                    Key = DialectKey,
                    Label = "部署形态",
                    Kind = shape == NoPickerShape.DynamicChoice ? ProtocolSettingKind.DynamicChoice : ProtocolSettingKind.Choice,
                    DefaultValue = "standalone",
                    AllowsCustomValue = shape == NoPickerShape.CustomValue,
                    IsHidden = shape == NoPickerShape.Hidden,
                    Choices = shape == NoPickerShape.SingleChoice
                        ? [new("standalone", "独立")]
                        : [new("standalone", "独立"), new("sentinel", "哨兵")]
                }
            ],
            VariantKey = shape == NoPickerShape.NoVariantKey ? null : DialectKey
        }, new NoopWorkspace());

        var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
        await vm.SelectPluginProtocolCommand.Execute(CacheId).FirstAsync();

        Assert.AreEqual(ConnectionProfileStep.Form, vm.Step, $"{shape}:不该出卡片。");
        Assert.IsFalse(vm.IsPickingType);
        Assert.IsEmpty(vm.TypeCards, $"{shape}:不该建卡片。");
        Assert.IsFalse(vm.ShowTypeHeader, $"{shape}:没有类型可选,表头不该出现。");
        Assert.IsFalse(vm.ShowBackButton, $"{shape}:没有卡片,就没有「上一步」。");
        Assert.IsFalse(vm.ShowChangeTypeLink);
        Assert.IsFalse(await vm.NextCommand.CanExecute.FirstAsync());
        Assert.IsFalse(await vm.BackCommand.CanExecute.FirstAsync());
        if (vm.PluginFields.SingleOrDefault(f => f.Key == DialectKey) is { } row)
        {
            Assert.IsTrue(row.IsRowVisible, $"{shape}:不出卡片时那一行下拉要照常显示。");
        }
    }

    /// <summary>
    /// <b>选 PostgreSQL → 切到 SSH → 点回数据库 → 下一步:端口是 5432</b>。
    /// <para>
    /// 点回数据库时变体信息已经清掉了,端口按页签默认值回到 3306,而下拉照旧是 PostgreSQL。
    /// 「下一步」写进去的值与原值相同,不会经由字段变化触发变体刷新 ——
    /// 所以「下一步」必须无论值变没变都显式按变体刷新一次。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task TypePicker_PostgresThenSshThenBack_Next_RestoresTheVariantPort()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);
            await vm.SelectTypeCardCommand.Execute("postgresql").FirstAsync();
            await vm.NextCommand.Execute().FirstAsync();
            Assert.AreEqual(5432, vm.Port, "前置条件:端口已经跟到 5432。");

            await vm.SelectConnectionTypeCommand.Execute(ConnectionType.SSH).FirstAsync();
            Assert.AreEqual(22, vm.Port, "前置条件:切到 SSH 端口还原成 22。");

            await vm.SelectPluginProtocolCommand.Execute(DbId).FirstAsync();
            Assert.IsTrue(vm.IsPickingType, "点回来、类型未确认:回到卡片。");
            Assert.AreEqual("postgresql", vm.SelectedTypeValue, "预选的是之前填过的那一档。");
            Assert.AreEqual("postgresql", Row(vm, DialectKey).Text);
            Assert.AreEqual(3306, vm.Port, "前置条件(要钉住的那个窗口):下拉是 PostgreSQL,端口却是 3306。");

            await vm.NextCommand.Execute().FirstAsync();

            Assert.AreEqual(5432, vm.Port, "值没变的「下一步」没有按变体刷新端口。");
            Assert.AreEqual("PostgreSQL", vm.CurrentTypeLabel);
        }
    }

    /// <summary>
    /// <b>编辑态点「更换」看一眼、类型没换就「下一步」:已存的端口不能被改</b>(R-b)。
    /// <para>
    /// 已存的是 PostgreSQL@3306(3306 恰好是页签默认端口,会被当成"还是默认值")。
    /// 编辑态回表单的唯一路就是「下一步」(D10),要是这一下也按变体刷新端口,
    /// 用户只是点开「更换」看了一眼,3306 就静默变成了 5432,保存即落盘。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task TypePicker_EditingProfile_ChangeTypeThenNext_SameType_KeepsTheSavedPort()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(ExistingPostgres(3306), protocolRegistry: registry);
            await vm.PendingPluginLoad;
            Assert.AreEqual(3306, vm.Port, "前置条件:装载不改已存的端口。");

            await vm.ChangeTypeCommand.Execute().FirstAsync();
            await vm.NextCommand.Execute().FirstAsync();

            Assert.AreEqual(ConnectionProfileStep.Form, vm.Step);
            Assert.AreEqual("postgresql", Row(vm, DialectKey).Text);
            Assert.AreEqual(3306, vm.Port, "类型没换,「更换 → 下一步」却把已存的端口改了。");
        }
    }

    /// <summary>
    /// <b>新建时手填了 3306,「上一步 → 下一步」类型没换:手填的端口不能被改</b>(R-b)。
    /// <para>
    /// 3306 是页签默认端口,判定上算"还是默认值";类型已经确认过,这一下不该再按变体刷新。
    /// 用例 6 用的是 15432(不是任何默认值),测不出这一条。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task TypePicker_BackThenNext_SameType_KeepsAHandTypedDefaultPort()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);
            await vm.SelectTypeCardCommand.Execute("postgresql").FirstAsync();
            await vm.NextCommand.Execute().FirstAsync();
            Assert.AreEqual(5432, vm.Port, "前置条件:端口已经跟到 5432。");

            vm.Port = 3306;
            await vm.BackCommand.Execute().FirstAsync();
            await vm.NextCommand.Execute().FirstAsync();

            Assert.AreEqual(ConnectionProfileStep.Form, vm.Step);
            Assert.AreEqual(3306, vm.Port, "类型没换,「上一步 → 下一步」却把手填的 3306 改成了变体端口。");
        }
    }

    /// <summary>
    /// <b>卡片阶段的「取消」= 关对话框</b>(T6 / D10):新建停在卡片上、编辑态点了「更换」之后,
    /// 取消都可用、都返回 null(视图收到 null 就关);不写任何值、不退回表单。
    /// </summary>
    [TestMethod]
    public async Task TypePicker_Cancel_OnTheCards_ClosesTheDialog()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);
            await vm.SelectTypeCardCommand.Execute("postgresql").FirstAsync();

            Assert.IsTrue(await vm.CancelCommand.CanExecute.FirstAsync(), "新建的卡片阶段,「取消」必须可用。");
            Assert.IsNull(await vm.CancelCommand.Execute().FirstAsync(), "取消要返回 null,对话框据此关闭。");
            Assert.AreEqual(ConnectionProfileStep.PickType, vm.Step, "取消不是「回表单」(D10)。");
            Assert.AreEqual("mysql", Row(vm, DialectKey).Text, "取消不该把卡片上的选择写进变体字段。");
            Assert.AreEqual(3306, vm.Port, "取消不该碰端口。");

            var editing = new ConnectionProfileViewModel(ExistingPostgres(5432), protocolRegistry: registry);
            await editing.PendingPluginLoad;
            await editing.ChangeTypeCommand.Execute().FirstAsync();
            await editing.SelectTypeCardCommand.Execute("mysql").FirstAsync();
            Assert.IsTrue(editing.IsPickingType, "前置条件:编辑态点了「更换」停在卡片上。");

            Assert.IsTrue(await editing.CancelCommand.CanExecute.FirstAsync(), "编辑态「更换」之后,「取消」必须可用。");
            Assert.IsNull(await editing.CancelCommand.Execute().FirstAsync(), "编辑态点了更换再取消,也是关闭(D10)。");
            Assert.AreEqual(ConnectionProfileStep.PickType, editing.Step, "取消不是「回表单」(D10)。");
            Assert.AreEqual("postgresql", Row(editing, DialectKey).Text, "取消不该改已存的类型。");
            Assert.AreEqual(5432, editing.Port, "取消不该碰端口。");
        }
    }

    /// <summary>
    /// <b>变体字段是「常规」页唯一的插件字段时,那一节整节收起</b>(⚠️3):
    /// 那一行已由表头替代,剩下的只会是一个有标题、没内容的空节。
    /// 同理,标了「高级」的变体字段不计入「高级」页签上的字段数。
    /// </summary>
    [TestMethod]
    public async Task TypePicker_VariantFieldIsTheOnlyGeneralField_CollapsesThePluginSection()
    {
        static WorkspaceDescriptor OnlyDialect(bool advanced) => new()
        {
            Id = DbId,
            DisplayName = "数据库",
            DefaultPort = 3306,
            Fields =
            [
                new()
                {
                    Key = DialectKey,
                    Label = "数据库类型",
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "mysql",
                    IsAdvanced = advanced,
                    Choices = [new("mysql", "MySQL"), new("postgresql", "PostgreSQL")]
                }
            ],
            VariantKey = DialectKey
        };

        var registry = new PluginProtocolRegistry();
        using (registry.RegisterWorkspace(DbId, OnlyDialect(advanced: false), new NoopWorkspace()))
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);
            Assert.IsTrue(vm.IsPickingType, "前置条件:这一页签出卡片。");
            Assert.IsFalse(vm.ShowPluginGeneralFields, "卡片阶段:「常规」页的插件那一节只剩标题。");

            await vm.NextCommand.Execute().FirstAsync();
            Assert.IsTrue(vm.ShowTypeHeader, "前置条件:已进表单、表头替代了那一行。");
            Assert.IsFalse(vm.ShowPluginGeneralFields, "表单阶段:「常规」页的插件那一节只剩标题。");
        }

        using (registry.RegisterWorkspace(DbId, OnlyDialect(advanced: true), new NoopWorkspace()))
        {
            ConnectionProfileViewModel vm = await NewOnDbTabAsync(registry);
            Assert.IsTrue(vm.IsPickingType, "前置条件:这一页签出卡片。");
            Assert.IsFalse(vm.HasAdvancedBadge, "被卡片替代的那一行不该计入「高级」页签的字段数。");
        }
    }

    /// <summary>编辑用的已存配置:PostgreSQL,主机 db.internal,库 orders。</summary>
    private static SessionProfile ExistingPostgres(int port) => new()
    {
        Name = "订单库",
        ConnectionType = ConnectionType.Plugin,
        PluginProtocolId = DbId,
        Host = "db.internal",
        Port = port,
        Username = "app",
        PluginSettings = new(StringComparer.Ordinal) { [DialectKey] = "postgresql", [DatabaseKey] = "orders" }
    };
}
