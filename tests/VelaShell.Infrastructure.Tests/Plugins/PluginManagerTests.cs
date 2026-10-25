using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk;
using VelaShell.TestPlugin;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
[TestCategory("Plugins")]
public class PluginManagerTests
{
    private string _root = null!;
    private string _dataRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "plugins");
        _dataRoot = Path.Combine(baseDir, "plugin-data");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch
        {
            // ALC 卸载是异步的,Windows 上偶尔还锁着 dll:留给临时目录清理。
        }
    }

    private PluginManagerOptions Options(params string[] roots) => new()
    {
        PluginRoots = roots.Length > 0 ? roots : [_root],
        DataRootDirectory = _dataRoot,
        HostVersion = "1.0.0"
    };

    private string AddPlugin(string dirName, string manifestJson)
    {
        string dir = Path.Combine(_root, dirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifestJson);
        return dir;
    }

    [TestMethod]
    public void Discover_ClassifiesEveryFailureModeWithReason()
    {
        AddPlugin("ok", """{ "id": "a.ok", "version": "1.0.0", "displayName": "Ok", "entry": "Ok.dll" }""");
        AddPlugin("bad-json", "{ this is not json");
        AddPlugin("disabled", """{ "id": "a.disabled", "version": "1.0.0", "displayName": "D", "entry": "D.dll" }""");
        File.WriteAllText(Path.Combine(_root, "disabled", ".disabled"), "");
        AddPlugin("future", """{ "id": "a.future", "version": "1.0.0", "displayName": "F", "entry": "F.dll", "apiLevel": 99 }""");
        AddPlugin("needs-newer-host", """{ "id": "a.newer", "version": "1.0.0", "displayName": "N", "entry": "N.dll", "minHostVersion": "9.9.9" }""");
        // apiLevel 只在**破坏性**变更时才动,所以"我用到了 SDK 1.1 新增的接口方法"没法用它表达。
        // 不拦的话插件会装上、激活,然后在第一次调用新方法时抛 MissingMethodException ——
        // 正是 apiLevel 当初要消灭的那种异常。
        AddPlugin("needs-newer-sdk", """{ "id": "a.newsdk", "version": "1.0.0", "displayName": "S", "entry": "S.dll", "minSdkVersion": "9.9.9" }""");
        AddPlugin("sdk-satisfied", $$"""{ "id": "a.sdkok", "version": "1.0.0", "displayName": "S2", "entry": "S2.dll", "minSdkVersion": "{{VelaPluginApi.SdkVersion}}" }""");
        Directory.CreateDirectory(Path.Combine(_root, "no-manifest")); // 无 plugin.json:直接忽略

        var manager = new PluginManager(Options());
        manager.Discover();
        var byId = manager.Plugins.ToDictionary(p => p.Id);

        Assert.HasCount(7, byId);
        Assert.AreEqual(PluginState.Discovered, byId["a.ok"].State);
        Assert.AreEqual(PluginState.Invalid, byId["bad-json"].State);
        Assert.IsNotNull(byId["bad-json"].Error);
        Assert.AreEqual(PluginState.Disabled, byId["a.disabled"].State);
        Assert.AreEqual(PluginState.Incompatible, byId["a.future"].State);
        Assert.Contains("apiLevel", byId["a.future"].Error);
        Assert.AreEqual(PluginState.Incompatible, byId["a.newer"].State);
        Assert.Contains("9.9.9", byId["a.newer"].Error);
        Assert.AreEqual(PluginState.Incompatible, byId["a.newsdk"].State);
        Assert.Contains("SDK", byId["a.newsdk"].Error);
        // 要求的正是本宿主带的这一版 —— 必须放行,不能把"等于"判成"更老"。
        Assert.AreEqual(PluginState.Discovered, byId["a.sdkok"].State);
    }

    /// <summary>
    /// minHostVersion 闸与 CLI、商店同一份口径(SDK 的 SemanticVersion.CompareCoreTo):只比数字段。
    /// 预发布宿主装得上为同号正式版写的插件;<c>1.0.0.0</c> 与 <c>1.0.0</c> 相等(原先按 <see cref="Version" />
    /// 比,第四段缺省是 -1,三段的宿主被判成比 <c>1.0.0.0</c> 老);带构建元数据的宿主版本照样参与比较(原先解析不出、一律放行)。
    /// </summary>
    [TestMethod]
    [DataRow("1.1.0-beta.2", "1.1.0", PluginState.Discovered)]
    [DataRow("1.0.0", "1.0.0.0", PluginState.Discovered)]
    [DataRow("1.0.0", "1.0.0.1", PluginState.Incompatible)]
    [DataRow("1.0.0+build.7", "1.0.1", PluginState.Incompatible)]
    [DataRow("1.0.0+build.7", "1.0.0", PluginState.Discovered)]
    [DataRow("not-a-version", "9.9.9", PluginState.Discovered)]
    public void Discover_MinHostVersionComparesNumericSegmentsOnly(string hostVersion, string minHost, PluginState expected)
    {
        AddPlugin("gated", $$"""{ "id": "a.gated", "version": "1.0.0", "displayName": "G", "entry": "G.dll", "minHostVersion": "{{minHost}}" }""");

        var manager = new PluginManager(new()
        {
            PluginRoots = [_root],
            DataRootDirectory = _dataRoot,
            HostVersion = hostVersion
        });
        manager.Discover();

        Assert.AreEqual(expected, manager.Plugins.Single().State, manager.Plugins.Single().Error);
    }

    /// <summary>
    /// 清单的版本字段与宿主 / CLI / 商店共用的 SemanticVersion 同一口径(维护者 2026-10-25 定,第二轮第 28 条):
    /// SDK 正则放过、SemanticVersion 判不合法的写法(预发布纯数字标识符带前导零、空标识符)发现期判 Invalid,
    /// 原因写明怎么改;清单留在描述上 —— 管理页照常显示插件名,id 也还是它自己的(卸载清扫按它认人)。
    /// </summary>
    [TestMethod]
    [DataRow("\"version\": \"1.0.0-beta.01\"", "'1.0.0-beta.1'")]
    [DataRow("\"version\": \"1.0.0-a..b\"", "must not be empty")]
    [DataRow("\"version\": \"1.0.0-rc.\"", "must not be empty")]
    [DataRow("\"version\": \"1.0.0\", \"minSdkVersion\": \"2.0.0-rc.007\"", "minSdkVersion")]
    [DataRow("\"version\": \"1.0.0\", \"minHostVersion\": \"1.0.0-00\"", "minHostVersion")]
    [DataRow("\"version\": \"99999999999.0.0\"", "32 bits")]
    public void Discover_RejectsManifestVersionsSemanticVersionCannotOrder(string versionFields, string expectedInError)
    {
        AddPlugin("loose", $$"""{ "id": "a.loose", {{versionFields}}, "displayName": "Loose", "entry": "L.dll" }""");

        var manager = new PluginManager(Options());
        manager.Discover();

        PluginDescriptor plugin = manager.Plugins.Single();
        Assert.AreEqual(PluginState.Invalid, plugin.State);
        Assert.Contains(expectedInError, plugin.Error);
        Assert.AreEqual("a.loose", plugin.Id);
        Assert.AreEqual("Loose", plugin.Manifest?.DisplayName);
    }

    /// <summary>收紧只动 SemanticVersion 判不合法的那部分:合法的预发布写法(含纯数字 0 与字母混排的前导零)照常放行。</summary>
    [TestMethod]
    [DataRow("1.0.0-beta.1")]
    [DataRow("1.0.0-0")]
    [DataRow("1.0.0-0a.01b")]
    [DataRow("1.2.3.4-rc.10")]
    public void Discover_AcceptsSemVerPreReleaseVersions(string version)
    {
        AddPlugin("strict", $$"""{ "id": "a.strict", "version": "{{version}}", "displayName": "S", "entry": "S.dll" }""");

        var manager = new PluginManager(Options());
        manager.Discover();

        Assert.AreEqual(PluginState.Discovered, manager.Plugins.Single().State, manager.Plugins.Single().Error);
    }

    [TestMethod]
    public void Discover_DirectoryNameNeedNotMatchId()
    {
        // 随包分发的插件目录名把 id 里的点换成了短横(velashell.ai → velashell-ai),
        // 否则 macOS 的 codesign 会把 .app 内带点号的目录当嵌套 bundle 而签名失败
        // (见 plugins/Directory.Build.targets)。id 只认 plugin.json,目录名不参与任何逻辑 ——
        // 这条测试就是钉住这个前提,免得日后有人把发现逻辑改成按目录名取 id。
        AddPlugin("velashell-ai", """{ "id": "velashell.ai", "version": "1.0.0", "displayName": "AI", "entry": "Ai.dll" }""");

        var manager = new PluginManager(Options());
        manager.Discover();

        PluginDescriptor plugin = manager.Plugins.Single();
        Assert.AreEqual("velashell.ai", plugin.Id);
        Assert.AreEqual(PluginState.Discovered, plugin.State);
    }

    [TestMethod]
    public void Discover_DuplicateId_FirstRootWins()
    {
        string secondRoot = Path.Combine(Path.GetDirectoryName(_root)!, "plugins2");
        Directory.CreateDirectory(secondRoot);
        AddPlugin("dup", """{ "id": "a.dup", "version": "1.0.0", "displayName": "One", "entry": "One.dll" }""");
        string dir2 = Path.Combine(secondRoot, "dup");
        Directory.CreateDirectory(dir2);
        File.WriteAllText(Path.Combine(dir2, "plugin.json"),
            """{ "id": "a.dup", "version": "2.0.0", "displayName": "Two", "entry": "Two.dll" }""");

        var manager = new PluginManager(Options(_root, secondRoot));
        manager.Discover();

        List<PluginDescriptor> dups = [.. manager.Plugins.Where(p => p.Id == "a.dup")];
        Assert.HasCount(2, dups);
        Assert.ContainsSingle(d => d.State == PluginState.Discovered, dups);
        PluginDescriptor rejected = dups.Single(d => d.State == PluginState.Invalid);
        Assert.Contains("Duplicate", rejected.Error);
    }

    [TestMethod]
    public async Task StartAsync_ActivatesRealPluginEndToEnd_AndDeactivatesOnDispose()
    {
        // 用真实的夹具插件程序集端到端验证:可收集 ALC 装载、入口发现、
        // 激活(能力缺席时退化实现)、存储落盘、停用。
        string pluginDir = Path.Combine(_root, "hello");
        Directory.CreateDirectory(pluginDir);
        string source = typeof(TestFixturePlugin).Assembly.Location;
        File.Copy(source, Path.Combine(pluginDir, "VelaShell.TestPlugin.dll"));
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), """
            { "id": "velashell.test-fixture", "version": "0.1.0", "displayName": "Test Fixture",
              "entry": "VelaShell.TestPlugin.dll" }
            """);

        var manager = new PluginManager(Options());
        await manager.StartAsync();
        PluginDescriptor descriptor = manager.Plugins.Single();
        Assert.AreEqual(PluginState.Active, descriptor.State, descriptor.Error);
        Assert.IsTrue(File.Exists(Path.Combine(_dataRoot, "velashell.test-fixture", "storage.json")),
            "激活应把激活计数写入插件存储");

        await manager.DisposeAsync();
        Assert.AreEqual(PluginState.Deactivated, manager.Plugins.Single().State);
    }

    [TestMethod]
    public async Task StartAsync_EntryAssemblyMissing_MarksFailedWithoutThrowing()
    {
        AddPlugin("ghost", """{ "id": "a.ghost", "version": "1.0.0", "displayName": "G", "entry": "Ghost.dll" }""");
        var manager = new PluginManager(Options());
        await manager.StartAsync();
        PluginDescriptor descriptor = manager.Plugins.Single();
        Assert.AreEqual(PluginState.Failed, descriptor.State);
        Assert.Contains("Ghost.dll", descriptor.Error);
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task StartAsync_IsIdempotent()
    {
        AddPlugin("ok", """{ "id": "a.ok", "version": "1.0.0", "displayName": "Ok", "entry": "Ok.dll" }""");
        var manager = new PluginManager(Options());
        await manager.StartAsync();
        await manager.StartAsync();
        Assert.ContainsSingle(p => p.Id == "a.ok", manager.Plugins);
        await manager.DisposeAsync();
    }
}
