using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Localization;
using VelaShell.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 文件栏的列宽与分栏比例跨重启记忆(维护者 2026-10-25 定):全局一份,存文档存储的 windowLayout 集合,
/// 与上传选择框同一做法。「重启」用一个新的 <see cref="WindowLayoutStore" /> 读同一个存储来模拟。
/// </summary>
[TestClass]
[TestCategory("PaneKeyboardUI")]
public sealed class FilePaneLayoutMemoryUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FilePaneLayoutMemoryUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public async Task Store_RoundTripsTheColumnWidthsAndTheSplitRatio_AcrossInstances()
    {
        var data = new MemoryStore();
        var first = new WindowLayoutStore(data);
        await first.SaveFilePanesAsync(layout => layout.ColumnWidths = new() { ["name"] = 420, ["size"] = 130 });
        await first.SaveFilePanesAsync(layout => layout.SplitRatio = 0.3);

        var restarted = new WindowLayoutStore(data);
        Assert.IsNull(restarted.FilePanes, "新实例还没读库。");
        FilePaneLayout loaded = await restarted.LoadFilePanesAsync();

        Assert.AreEqual(420, loaded.ColumnWidths!["name"]);
        Assert.AreEqual(130, loaded.ColumnWidths["size"]);
        Assert.AreEqual(0.3, loaded.SplitRatio);
        Assert.AreSame(loaded, restarted.FilePanes, "读过一次之后就用内存里那份。");
    }

    [TestMethod]
    public void RemotePane_TakesTheRememberedWidths_AndWritesBackAfterADrag()
    {
        var data = new MemoryStore();
        data.Seed("filePanes", new FilePaneLayout { ColumnWidths = new() { ["name"] = 360, ["owner"] = 120 } });
        WithStore(new WindowLayoutStore(data), store => OnUi(() =>
        {
            PumpUntil(store.LoadFilePanesAsync());
            FileBrowserViewModel vm = Pane();
            var view = new FileBrowserView { DataContext = vm };
            var window = new Window { Width = 1100, Height = 400, Content = view };
            try
            {
                window.Show();
                Pump(window);
                Assert.AreEqual(360, vm.NameColumnWidth.Value, "打开时套上记下的宽度。");
                Assert.AreEqual(120, vm.OwnerColumnWidth.Value);
                Assert.AreEqual(100, vm.SizeColumnWidth.Value, "没记过的列按出厂宽度。");

                // 拖「文件名」右侧的拖拽条往右 40。
                Border splitter = view.GetVisualDescendants().OfType<Border>().First(b => b.Tag as string == "name" && b.Cursor is not null);
                Point start = splitter.TranslatePoint(new(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(start + new Point(40, 0));
                window.MouseUp(start + new Point(40, 0), MouseButton.Left);
                Pump(window);
                PumpUntil(data.WaitForWriteAsync("filePanes"));

                Assert.IsGreaterThan(360, vm.NameColumnWidth.Value, "前提:确实拖宽了(上限按面板宽度钳)。");
                Assert.AreEqual(vm.NameColumnWidth.Value, data.Read("filePanes")!.ColumnWidths!["name"], "拖完写回拖成的宽度。");
                Assert.AreEqual(120, data.Read("filePanes")!.ColumnWidths!["owner"], "其余列照当前宽度一并写回。");
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }));
    }

    [TestMethod]
    public void DualPaneDocument_TakesTheRememberedRatio_AndWritesBackAfterDraggingTheSplitter()
    {
        var data = new MemoryStore();
        data.Seed("filePanes", new FilePaneLayout { SplitRatio = 0.3 });
        WithStore(new WindowLayoutStore(data), store => OnUi(() =>
        {
            PumpUntil(store.LoadFilePanesAsync());
            var view = new DualSftpDocumentView();
            var window = new Window { Width = 1000, Height = 400, Content = view };
            try
            {
                window.Show();
                Pump(window);
                Grid grid = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "PaneGrid");
                double left = grid.ColumnDefinitions[0].ActualWidth;
                double right = grid.ColumnDefinitions[2].ActualWidth;
                Assert.AreEqual(0.3, left / (left + right), 0.01, "打开时按记下的比例分栏。");

                GridSplitter splitter = view.GetVisualDescendants().OfType<GridSplitter>().Single(s => s.Name == "PaneSplitter");
                Point start = splitter.TranslatePoint(new(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(start + new Point(200, 0));
                window.MouseUp(start + new Point(200, 0), MouseButton.Left);
                Pump(window);
                PumpUntil(data.WaitForWriteAsync("filePanes"));

                double saved = data.Read("filePanes")!.SplitRatio!.Value;
                left = grid.ColumnDefinitions[0].ActualWidth;
                right = grid.ColumnDefinitions[2].ActualWidth;
                Assert.IsGreaterThan(0.4, saved, "往右拖了 200,左栏变宽。");
                Assert.AreEqual(left / (left + right), saved, 0.01, "写回的就是拖完之后的比例。");
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }));
    }

    // ---- 工具 ----

    private static FileBrowserViewModel Pane()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var session = Guid.NewGuid();
        sftp.ListDirectoryAsync(session, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        return new FileBrowserViewModel(sftp, session) { IsVisible = true, TransferOptions = new() };
    }

    private static void WithStore(WindowLayoutStore store, Action<WindowLayoutStore> body)
    {
        Func<WindowLayoutStore?> previous = FilePaneLayoutMemory.StoreResolver;
        FilePaneLayoutMemory.StoreResolver = () => store;
        try
        {
            body(store);
        }
        finally
        {
            FilePaneLayoutMemory.StoreResolver = previous;
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpUntil(Task task)
    {
        for (int i = 0; i < 2000 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>按 JSON 往返存取的内存文档存储(与 SonnetDB 一样每次读出新对象),能等某一条被写入。</summary>
    private sealed class MemoryStore : IAppDataStore
    {
        private readonly ConcurrentDictionary<string, string> _rows = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _written = new();

        public void Seed<T>(string id, T value) => _rows[$"windowLayout/{id}"] = JsonSerializer.Serialize(value);

        public FilePaneLayout? Read(string id) =>
            _rows.TryGetValue($"windowLayout/{id}", out string? json) ? JsonSerializer.Deserialize<FilePaneLayout>(json) : null;

        public Task WaitForWriteAsync(string id) =>
            _written.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(5));

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
