using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Headless;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Layouts;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Services.Layouts;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 命名布局模板的增删改与打开,以及「恢复会话」顺带的那份退出时的布局(含主窗口退出时的接线)。
/// </summary>
[TestClass]
[TestCategory("Docking")]
public sealed class LayoutTemplatesViewModelTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LayoutTemplatesViewModelTests).Assembly);

    /// <summary>内存里的文档库:与 SonnetDB 一样每次存取都过一遍 JSON,改了对象不会悄悄改到库里。</summary>
    private sealed class MemoryStore : IAppDataStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public Dictionary<(string Collection, string Id), string> Rows { get; } = [];

        public bool FailWrites { get; set; }

        public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(Rows.TryGetValue((collection, id), out string? json) ? JsonSerializer.Deserialize<T>(json, Json) : null);

        public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(Rows.Where(row => row.Key.Collection == collection)
                                .Select(row => JsonSerializer.Deserialize<T>(row.Value, Json)!)
                                .ToList());

        public Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
        {
            if (FailWrites)
            {
                throw new IOException("disk full");
            }
            Rows[(collection, id)] = JsonSerializer.Serialize(value, Json);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            Rows.Remove((collection, id));
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedDialogs : ILayoutTemplateDialogs
    {
        public Queue<string?> Names { get; } = new();

        public bool ConfirmAnswer { get; set; } = true;

        public List<string> Asked { get; } = [];

        public Task<string?> PromptNameAsync(string title, string initialValue, string? message)
        {
            Asked.Add($"prompt:{initialValue}");
            return Task.FromResult(Names.Count > 0 ? Names.Dequeue() : initialValue);
        }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger)
        {
            Asked.Add(danger ? $"danger:{message}" : $"confirm:{message}");
            return Task.FromResult(ConfirmAnswer);
        }
    }

    private sealed class RestoredDocument(LayoutOpenRequest request) : DockDocument
    {
        public LayoutOpenRequest Request { get; } = request;
    }

    private sealed class FakeOpener(DockWorkspace workspace) : ILayoutDocumentOpener
    {
        public Dictionary<Guid, SessionProfile> Profiles { get; } = [];

        public Task<SessionProfile?> FindProfileAsync(Guid profileId) => Task.FromResult(Profiles.GetValueOrDefault(profileId));

        public LocalShellInfo? FindLocalShell(string shellId) => null;

        public Task OpenAsync(LayoutOpenRequest request)
        {
            workspace.AddDocument(new RestoredDocument(request) { Title = request.Profile?.Name ?? "?" });
            return Task.CompletedTask;
        }
    }

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed record Scene(
        LayoutTemplatesViewModel ViewModel,
        DockWorkspace Workspace,
        MemoryStore Store,
        FakeOpener Opener,
        ScriptedDialogs Dialogs,
        ToastHostViewModel Toasts);

    private static Scene NewScene()
    {
        var workspace = new DockWorkspace();
        var store = new MemoryStore();
        var opener = new FakeOpener(workspace);
        var dialogs = new ScriptedDialogs();
        var toasts = new ToastHostViewModel((_, _) => new NoTimer());
        var vm = new LayoutTemplatesViewModel(
            new LayoutTemplateStore(store),
            workspace,
            opener,
            document => LayoutDocumentDescriber.Describe(document, _ => false),
            id => Task.FromResult(opener.Profiles.ContainsKey(id)),
            toasts,
            () => new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero))
        {
            Dialogs = dialogs,
        };
        return new(vm, workspace, store, opener, dialogs, toasts);
    }

    private static TerminalDocument OpenTerminal(Scene scene, string name, bool saved = true, bool connected = true)
    {
        var profile = new SessionProfile { Name = name, Host = $"{name}.example" };
        if (saved)
        {
            scene.Opener.Profiles[profile.Id] = profile;
        }
        var document = new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Title = name,
            Profile = profile,
            ConnectionStatus = connected ? SessionStatus.Connected : SessionStatus.Disconnected,
        });
        scene.Workspace.AddDocument(document);
        return document;
    }

    private static string LastToast(Scene scene) => scene.Toasts.Toasts.First().Message;

    [TestMethod]
    public async Task SaveCurrent_StoresTheLayout_SkippingOneOffConnectionsAndSayingSo()
    {
        Scene scene = NewScene();
        TerminalDocument web = OpenTerminal(scene, "web");
        OpenTerminal(scene, "quick", saved: false); // 快速连接:配置不在库里
        scene.Workspace.SetPinned(web, pinned: true);
        scene.Dialogs.Names.Enqueue("  prod  ");

        Assert.IsTrue(await scene.ViewModel.SaveCurrentAsync());

        LayoutTemplateItemViewModel item = scene.ViewModel.Templates.Single();
        Assert.AreEqual("prod", item.Name, "名字去掉首尾空白");
        Assert.AreEqual(1, item.DocumentCount);
        LayoutDocument saved = LayoutTree.Documents(item.Template.Root).Single();
        Assert.AreEqual(web.Terminal.Profile!.Id, saved.ProfileId);
        Assert.IsTrue(saved.IsPinned, "固定状态跟着存");
        Assert.AreEqual("prompt:" + VelaShell.Core.Resources.Strings.Format("LayoutTemplate_DefaultName", 1), scene.Dialogs.Asked[0]);
        Assert.IsTrue(scene.Store.Rows.ContainsKey((LayoutTemplateStore.Collection, item.Template.Id.ToString("D"))));
        StringAssert.Contains(LastToast(scene), "quick", "没存进去的那个点名说出来");

        // 另起一个视图模型重新读一遍:列表来自库,不只是内存。
        var reloaded = new LayoutTemplatesViewModel(
            new LayoutTemplateStore(scene.Store), new DockWorkspace(), scene.Opener,
            document => LayoutDocumentDescriber.Describe(document, _ => false),
            _ => Task.FromResult(true), scene.Toasts);
        await reloaded.LoadAsync();
        Assert.AreEqual("prod", reloaded.Templates.Single().Name);
        Assert.IsTrue(LayoutTree.Documents(reloaded.Templates.Single().Template.Root).Single().IsPinned);
    }

    [TestMethod]
    public async Task SaveCurrent_WithNothingReopenable_WarnsWithoutAsking()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "quick", saved: false);

        Assert.IsFalse(await scene.ViewModel.SaveCurrentAsync());

        Assert.IsEmpty(scene.Dialogs.Asked);
        Assert.IsEmpty(scene.Store.Rows);
        Assert.AreEqual(VelaShell.Core.Resources.Strings.Get("LayoutTemplate_NothingToSave"), LastToast(scene));
    }

    [TestMethod]
    public async Task SaveCurrent_UnderAnExistingName_AsksAndReplacesInPlace()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "web");
        scene.Dialogs.Names.Enqueue("prod");
        await scene.ViewModel.SaveCurrentAsync();
        Guid id = scene.ViewModel.Templates.Single().Template.Id;
        OpenTerminal(scene, "db");

        scene.Dialogs.ConfirmAnswer = false;
        scene.Dialogs.Names.Enqueue("PROD");
        Assert.IsFalse(await scene.ViewModel.SaveCurrentAsync(), "不替换就什么都不存");
        Assert.AreEqual(1, scene.ViewModel.Templates.Single().DocumentCount);

        scene.Dialogs.ConfirmAnswer = true;
        scene.Dialogs.Names.Enqueue("PROD");
        Assert.IsTrue(await scene.ViewModel.SaveCurrentAsync());

        LayoutTemplateItemViewModel item = scene.ViewModel.Templates.Single();
        Assert.AreEqual(id, item.Template.Id, "替换的是原来那一条,不是又多出一条");
        Assert.AreEqual(2, item.DocumentCount);
    }

    [TestMethod]
    public async Task Rename_RefusesANameThatBelongsToAnotherTemplate()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "web");
        scene.Dialogs.Names.Enqueue("alpha");
        await scene.ViewModel.SaveCurrentAsync();
        scene.Dialogs.Names.Enqueue("beta");
        await scene.ViewModel.SaveCurrentAsync();
        LayoutTemplateItemViewModel beta = scene.ViewModel.Templates.Single(t => t.Name == "beta");

        scene.Dialogs.Names.Enqueue("Alpha");
        Assert.IsFalse(await scene.ViewModel.RenameAsync(beta));
        StringAssert.Contains(LastToast(scene), "alpha");

        scene.Dialogs.Names.Enqueue("gamma");
        Assert.IsTrue(await scene.ViewModel.RenameAsync(beta));
        Assert.AreSequenceEqual(["alpha", "gamma"], scene.ViewModel.Templates.Select(t => t.Name).ToArray());
        Assert.AreEqual(beta.Template.Id, scene.ViewModel.Templates.Single(t => t.Name == "gamma").Template.Id);
    }

    [TestMethod]
    public async Task Overwrite_KeepsNameAndId_AndTakesTheCurrentLayout()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "web");
        scene.Dialogs.Names.Enqueue("prod");
        await scene.ViewModel.SaveCurrentAsync();
        LayoutTemplateItemViewModel original = scene.ViewModel.Templates.Single();
        TerminalDocument db = OpenTerminal(scene, "db");
        scene.Workspace.SplitDocument(db, DockOrientation.Horizontal);

        Assert.IsTrue(await scene.ViewModel.OverwriteAsync(original));

        LayoutTemplateItemViewModel item = scene.ViewModel.Templates.Single();
        Assert.AreEqual((original.Template.Id, "prod"), (item.Template.Id, item.Name));
        Assert.AreEqual(2, LayoutTree.GroupCount(item.Template.Root));
        Assert.AreEqual(original.Template.CreatedAt, item.Template.CreatedAt);
    }

    [TestMethod]
    public async Task Delete_AsksAsADangerousAction_ThenRemovesOnlyTheTemplate()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "web");
        scene.Dialogs.Names.Enqueue("prod");
        await scene.ViewModel.SaveCurrentAsync();
        LayoutTemplateItemViewModel item = scene.ViewModel.Templates.Single();

        Assert.IsTrue(await scene.ViewModel.DeleteAsync(item));

        Assert.IsTrue(scene.Dialogs.Asked.Last().StartsWith("danger:", StringComparison.Ordinal));
        Assert.IsFalse(scene.ViewModel.HasTemplates);
        Assert.IsEmpty(scene.Store.Rows);
        Assert.HasCount(1, scene.Workspace.AllDocuments().ToList(), "删模板不碰开着的标签");
    }

    [TestMethod]
    public async Task SaveFailure_IsReportedAndTheListStaysAsItWas()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "web");
        scene.Store.FailWrites = true;
        scene.Dialogs.Names.Enqueue("prod");

        Assert.IsFalse(await scene.ViewModel.SaveCurrentAsync());

        Assert.IsFalse(scene.ViewModel.HasTemplates);
        Assert.IsTrue(scene.Toasts.Toasts.First().IsError);
        StringAssert.Contains(LastToast(scene), "disk full");
    }

    [TestMethod]
    public async Task Open_ReportsDeletedConnections_AndOpensTheRest()
    {
        Scene scene = NewScene();
        TerminalDocument web = OpenTerminal(scene, "web");
        TerminalDocument db = OpenTerminal(scene, "db");
        scene.Dialogs.Names.Enqueue("prod");
        await scene.ViewModel.SaveCurrentAsync();
        scene.Workspace.RemoveDocument(web);
        scene.Workspace.RemoveDocument(db);
        scene.Opener.Profiles.Remove(db.Terminal.Profile!.Id); // db 那条配置后来被删了

        LayoutOpenResult result = (await scene.ViewModel.OpenAsync(scene.ViewModel.Templates.Single()))!;

        Assert.AreEqual(1, result.Opened);
        Assert.AreEqual("web", scene.Workspace.AllDocuments().Single().Title);
        string toast = LastToast(scene);
        StringAssert.Contains(toast, VelaShell.Core.Resources.Strings.Format("LayoutTemplate_MissingProfile", "db"));
    }

    [TestMethod]
    public async Task Open_ManyTabs_AsksFirst()
    {
        Scene scene = NewScene();
        for (int i = 0; i <= LayoutTemplatesViewModel.OpenManyConfirmThreshold; i++)
        {
            OpenTerminal(scene, $"host{i}");
        }
        scene.Dialogs.Names.Enqueue("fleet");
        await scene.ViewModel.SaveCurrentAsync();
        scene.Dialogs.ConfirmAnswer = false;

        Assert.IsNull(await scene.ViewModel.OpenAsync(scene.ViewModel.Templates.Single()));

        Assert.IsFalse(scene.Workspace.AllDocuments().OfType<RestoredDocument>().Any());
    }

    // ---- 退出时的布局 ----

    [TestMethod]
    public async Task SessionLayout_RecordsOnlyLiveTabs_AndRestoresThem()
    {
        Scene scene = NewScene();
        OpenTerminal(scene, "up");
        OpenTerminal(scene, "down", connected: false);

        LayoutTemplate layout = scene.ViewModel.CaptureSessionLayout()!;
        Assert.AreEqual("up", LayoutTree.Documents(layout.Root).Single().Title, "断开着的不记:下次启动不替用户去连");
        await scene.ViewModel.SaveSessionLayoutAsync(layout);

        Scene next = NewScene(); // 下一次启动:新的工作区,同一个库
        var restoring = new LayoutTemplatesViewModel(
            new LayoutTemplateStore(scene.Store), next.Workspace, next.Opener,
            document => LayoutDocumentDescriber.Describe(document, _ => false),
            _ => Task.FromResult(true), next.Toasts);
        foreach (SessionProfile profile in scene.Opener.Profiles.Values)
        {
            next.Opener.Profiles[profile.Id] = profile;
        }

        Assert.IsTrue(await restoring.RestoreSessionLayoutAsync());
        Assert.AreEqual("up", next.Workspace.AllDocuments().Single().Title);

        await restoring.SaveSessionLayoutAsync(null);
        Assert.IsFalse(await restoring.RestoreSessionLayoutAsync(), "没有记下的布局:交给调用方逐条重连");
    }

    [TestMethod]
    public async Task MainWindowClose_WritesTheSessionLayoutOnlyWhenTheOptionIsOn() => await _session.RunOnUiAsync(() =>
    {
        var store = new MemoryStore();
        var vm = new MainWindowViewModel(appDataStore: store);
        vm.Layout.AddDocument(new TerminalDocument(new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Title = "web",
            Profile = new SessionProfile { Name = "web", Host = "web.example" },
            ConnectionStatus = SessionStatus.Connected,
        }));
        var window = new MainWindow { DataContext = vm };
        typeof(MainWindow)
            .GetField("_settingsService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, Substitute.For<ISettingsService>());
        MethodInfo persist = typeof(MainWindow).GetMethod("PersistWindowBounds", BindingFlags.Instance | BindingFlags.NonPublic)!;
        (string, string) key = (LayoutTemplateStore.SessionCollection, LayoutTemplateStore.LastSessionId);

        var settings = new AppSettings();
        settings.General.RestoreSessionsOnStartup = true;
        persist.Invoke(window, [settings]);
        Assert.IsFalse(store.Rows.ContainsKey(key), "默认关:不记布局");

        settings.General.RestoreLayoutOnStartup = true;
        persist.Invoke(window, [settings]);
        Assert.IsTrue(store.Rows.ContainsKey(key));
        StringAssert.Contains(store.Rows[key], "\"title\":\"web\"");

        settings.General.RestoreLayoutOnStartup = false;
        persist.Invoke(window, [settings]);
        Assert.IsFalse(store.Rows.ContainsKey(key), "关掉之后清掉旧的,免得哪天重新打开时还原出一份过期的");
        return Task.CompletedTask;
    });

    [TestMethod]
    public async Task MainWindowViewModel_ListsTemplatesInThePaletteAndRegistersTheCommands()
    {
        var store = new MemoryStore();
        await new LayoutTemplateStore(store).SaveAsync(new LayoutTemplate
        {
            Name = "prod",
            Root = new() { Documents = [new() { Kind = LayoutDocumentKind.Terminal, ProfileId = Guid.NewGuid(), Title = "web" }] },
        });
        var vm = new MainWindowViewModel(appDataStore: store);
        await vm.LayoutTemplates.LoadAsync();

        var items = (List<CommandPaletteItem>)typeof(MainWindowViewModel)
            .GetMethod("BuildPaletteItems", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null)!;

        CommandPaletteItem template = items.Single(item => item.Id.StartsWith("layout:", StringComparison.Ordinal));
        Assert.AreEqual("prod", template.Title);
        Assert.AreEqual(VelaShell.Core.Resources.Strings.Get("LayoutTemplate_Title"), template.Category);
        string[] ids = [.. vm.Commands.All.Select(command => command.Id)];
        CollectionAssert.IsSubsetOf(new[] { "layout.save", "layout.manage" }, ids);
    }
}
