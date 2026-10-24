using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using VelaShell.Core.Data;
using VelaShell.Services;
using VelaShell.Tests.TestSupport;

namespace VelaShell.Tests.Services;

/// <summary>
/// 非模态工具窗口(任务管理器、资源监视、链路追踪、插件管理、审计日志、回放中心)的尺寸与位置记忆。
/// </summary>
/// <remarks>
/// 位置的「还在不在某块屏上」核实复用主窗口那一套(<see cref="MainWindowPlacement.TryRestorePosition" />),
/// 这里钉的是工具窗口这一层:记下了什么、什么时候沿用旧值、摆不摆回去、老记录(只有尺寸)照旧能用。
/// </remarks>
[TestClass]
[TestCategory("WindowPlacement")]
public sealed class WindowLayoutStoreTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(WindowLayoutStoreTests).Assembly);

    [TestMethod]
    public void Capture_NormalState_RecordsSizeAndPosition()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 900, Height = 600 };
            window.Position = new(-1500, 120);   // 左边副屏:负坐标是正当的位置

            WindowLayout layout = WindowLayoutStore.Capture(window, view: null, previous: null);

            Assert.AreEqual(900, layout.Width);
            Assert.AreEqual(600, layout.Height);
            Assert.AreEqual(-1500, layout.X);
            Assert.AreEqual(120, layout.Y);
            Assert.IsFalse(layout.Maximized);
        });
    }

    /// <summary>最大化时关掉:那时的位置是最大化后的左上角、尺寸是整屏,都沿用上次普通态记下的。</summary>
    [TestMethod]
    public void Capture_Maximized_KeepsThePreviousNormalBounds()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 900, Height = 600 };
            window.Position = new(0, 0);
            window.WindowState = WindowState.Maximized;
            WindowLayout previous = new() { Width = 800, Height = 500, X = 300, Y = 200 };

            WindowLayout layout = WindowLayoutStore.Capture(window, view: null, previous);

            Assert.IsTrue(layout.Maximized);
            Assert.AreEqual(800, layout.Width);
            Assert.AreEqual(500, layout.Height);
            Assert.AreEqual(300, layout.X);
            Assert.AreEqual(200, layout.Y);
        });
    }

    [TestMethod]
    public void Apply_RememberedPositionOnAScreen_PlacesTheWindowThere()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 700, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            PixelRect area = window.Screens.All[0].WorkingArea;
            WindowLayout layout = new() { Width = 800, Height = 520, X = area.X + 60, Y = area.Y + 40 };

            WindowLayoutStore.Apply(window, layout, restorePosition: true);

            Assert.AreEqual(WindowStartupLocation.Manual, window.WindowStartupLocation);
            Assert.AreEqual(new PixelPoint(area.X + 60, area.Y + 40), window.Position);
            Assert.AreEqual(800, window.Width);
        });
    }

    /// <summary>上次摆在副屏上,这次副屏拔了:不摆回去,照窗口自己的启动位置(跟着属主居中)打开。</summary>
    [TestMethod]
    public void Apply_RememberedPositionOffEveryScreen_FallsBackToCentering()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 700, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            PixelPoint before = window.Position;
            PixelRect all = window.Screens.All.Select(s => s.WorkingArea).Aggregate((a, b) => a.Union(b));
            WindowLayout layout = new() { Width = 800, Height = 520, X = all.Right + 200, Y = all.Y + 40 };

            WindowLayoutStore.Apply(window, layout, restorePosition: true);

            Assert.AreEqual(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
            Assert.AreEqual(before, window.Position);
            Assert.AreEqual(800, window.Width, "尺寸照样套用");
        });
    }

    /// <summary>模态框(上传选择框)只记尺寸:位置跟着属主走,开在别处反而找不着。</summary>
    [TestMethod]
    public void Apply_WithoutRestorePosition_LeavesThePositionAlone()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 700, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            PixelRect area = window.Screens.All[0].WorkingArea;

            WindowLayoutStore.Apply(window, new() { Width = 800, Height = 520, X = area.X + 60, Y = area.Y + 40 });

            Assert.AreEqual(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
        });
    }

    /// <summary>这次改动之前存下的记录只有尺寸:照旧套尺寸,位置交给启动位置。</summary>
    [TestMethod]
    public void Apply_LegacyRecordWithoutPosition_OnlySizes()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 700, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            WindowLayout legacy = JsonSerializer.Deserialize<WindowLayout>("""{"Width":820,"Height":540,"Maximized":false}""")!;

            WindowLayoutStore.Apply(window, legacy, restorePosition: true);

            Assert.IsNull(legacy.X);
            Assert.AreEqual(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
            Assert.AreEqual(820, window.Width);
        });
    }

    /// <summary>挂上记忆的窗口关掉时写回尺寸与位置;下一扇同类窗口打开时摆回原处。</summary>
    [TestMethod]
    public Task Remember_ClosingSavesAndTheNextWindowOpensWhereTheLastOneWas() => _session.RunOnUiAsync(async () =>
    {
        MemoryStore store = new();
        WindowLayoutStore layouts = new(store);
        var first = new Window { Width = 700, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        await layouts.RememberAsync("auditLog", first);
        first.Show();
        PixelRect area = first.Screens.All[0].WorkingArea;
        first.Position = new(area.X + 80, area.Y + 50);
        first.Width = 760;
        first.Close();
        await store.WaitForWriteAsync("auditLog");

        var second = new Window { Width = 700, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        await layouts.RememberAsync("auditLog", second);

        Assert.AreEqual(WindowStartupLocation.Manual, second.WindowStartupLocation);
        Assert.AreEqual(new PixelPoint(area.X + 80, area.Y + 50), second.Position);
        Assert.AreEqual(760, second.Width);
    });

    private static void OnUi(Action action) =>
        _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>按 JSON 往返存取的内存文档存储,与 SonnetDB 一样每次读出新对象。</summary>
    private sealed class MemoryStore : IAppDataStore
    {
        private readonly ConcurrentDictionary<string, string> _rows = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _written = new();

        public Task WaitForWriteAsync(string id) =>
            _written.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task
                    .WaitAsync(TimeSpan.FromSeconds(5));

        public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_rows.TryGetValue($"{collection}/{id}", out string? json) ? JsonSerializer.Deserialize<T>(json) : null);

        public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_rows.Where(r => r.Key.StartsWith(collection + "/", StringComparison.Ordinal))
                                 .Select(r => JsonSerializer.Deserialize<T>(r.Value)!).ToList());

        public Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
        {
            _rows[$"{collection}/{id}"] = JsonSerializer.Serialize(value);
            _written.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            _rows.TryRemove($"{collection}/{id}", out _);
            return Task.CompletedTask;
        }
    }
}
