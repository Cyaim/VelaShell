using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("LocalFilePane")]
public sealed class LocalFilePaneViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) => _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LocalFilePaneViewUiTests).Assembly);

    [TestMethod]
    public async Task InaccessibleRootSelectionRestoresPreviousSelection()
    {
        await _session.RunOnUiAsync(async () =>
        {
            using var first = new TempDirectory();
            var inaccessible = new LocalRootEntry("Unavailable", Path.Combine(first.Path, "missing"), false, Path.Combine(first.Path, "missing"));
            var accessible = new LocalRootEntry("~", first.Path, true, first.Path);
            var roots = new TestRootProvider(accessible, inaccessible);
            // using,且声明在 TempDirectory 之后(反序释放):视图模型给临时目录挂了
            // FileSystemWatcher,不先停掉它,删目录会在 dispatch 结束后从线程池线程回调进来,
            // 重建进程级的 Dispatcher.UIThread —— 下一个测试建 Compositor 时 VerifyAccess 就炸。
            using var viewModel = new LocalFilePaneViewModel(
                new TransferOptions { LocalDownloadDirectory = first.Path },
                rootProvider: roots);
            await viewModel.LoadInitialAsync();

            var view = new LocalFilePaneView { DataContext = viewModel };
            var window = new Window { Width = 600, Height = 400, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                ComboBox combo = view.GetVisualDescendants().OfType<ComboBox>().Single();
                Assert.AreSame(accessible, combo.SelectedItem);

                combo.SelectedItem = inaccessible;
                Dispatcher.UIThread.RunJobs();

                Assert.AreSame(accessible, combo.SelectedItem);
                Assert.AreSame(accessible, viewModel.SelectedRoot);
                Assert.AreEqual(first.Path, viewModel.CurrentPath);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public async Task DraggingFromRowWhitespace_MarqueesAcrossLocalRows()
    {
        await _session.RunOnUiAsync(async () =>
        {
            using var root = new TempDirectory();
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(root.Path, $"f{i}.txt"), $"file {i}");
            }

            var accessible = new LocalRootEntry("~", root.Path, true, root.Path);
            // 同上:必须早于 TempDirectory 释放,否则删目录的监视回调会污染 Dispatcher.UIThread。
            using var viewModel = new LocalFilePaneViewModel(
                new TransferOptions { LocalDownloadDirectory = root.Path },
                rootProvider: new TestRootProvider(accessible));
            await viewModel.LoadInitialAsync();

            var view = new LocalFilePaneView { DataContext = viewModel };
            var window = new Window { Width = 700, Height = 450, Content = view };
            try
            {
                window.Show();
                Pump(window);

                ListBox list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "FileList");
                Assert.IsGreaterThanOrEqualTo(4, list.ItemCount);
                viewModel.SelectedEntries.Clear();

                const int firstRow = 1;
                const int lastRow = 3;
                Control first = list.ContainerFromIndex(firstRow)!;
                Control last = list.ContainerFromIndex(lastRow)!;
                Point start = first.TranslatePoint(FindWhitespacePoint(first), window)!.Value;
                Point end = last.TranslatePoint(FindWhitespacePoint(last), window)!.Value;
                Border overlay = view.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "MarqueeOverlay");

                window.MouseDown(start, MouseButton.Left);
                Pump(window);
                window.MouseMove(end);
                Pump(window);

                Assert.IsTrue(overlay.IsVisible, "本地栏行内空白拖动应显示框选矩形。");

                window.MouseUp(end, MouseButton.Left);
                Pump(window);

                Assert.AreSequenceEqual(
                    [.. viewModel.Entries.Skip(firstRow).Take(lastRow - firstRow + 1).Select(entry => entry.Name)], [.. viewModel.SelectedEntries.Select(entry => entry.Name)], SequenceOrder.InAnyOrder);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    /// <summary>
    /// 本地栏的列标题点了要排序(差距分析 A2):视图模型的排序早就写好了,表头却是三个纯 TextBlock,点了什么也不发生。
    /// 用真实的指针点击走一遍命中测试与绑定,而不是直接调命令。
    /// </summary>
    [TestMethod]
    public async Task ClickingAColumnHeader_SortsTheLocalPaneAndShowsTheDirectionGlyph()
    {
        await _session.RunOnUiAsync(async () =>
        {
            using var root = new TempDirectory();
            Directory.CreateDirectory(Path.Combine(root.Path, "z-dir"));
            File.WriteAllText(Path.Combine(root.Path, "a-big.txt"), new string('x', 3000));
            File.WriteAllText(Path.Combine(root.Path, "b-small.txt"), "x");
            var accessible = new LocalRootEntry("~", root.Path, true, root.Path);
            // 同上:必须早于 TempDirectory 释放,否则删目录的监视回调会污染 Dispatcher.UIThread。
            using var viewModel = new LocalFilePaneViewModel(
                new TransferOptions { LocalDownloadDirectory = root.Path },
                rootProvider: new TestRootProvider(accessible));
            await viewModel.LoadInitialAsync();

            var view = new LocalFilePaneView { DataContext = viewModel };
            var window = new Window { Width = 700, Height = 450, Content = view };
            try
            {
                window.Show();
                Pump(window);
                Button sizeHeader = view.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Classes.Contains("col-header") && Equals(button.CommandParameter, "size"));
                Point center = sizeHeader.TranslatePoint(new Point(sizeHeader.Bounds.Width / 2, sizeHeader.Bounds.Height / 2), window)!.Value;

                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                Pump(window);

                Assert.AreEqual("size", viewModel.SortColumn);
                Assert.IsFalse(viewModel.SortDescending);
                Assert.AreEqual(" ▲", viewModel.SizeSortGlyph);
                Assert.AreEqual(string.Empty, viewModel.NameSortGlyph);
                Assert.AreSequenceEqual(
                    ["z-dir", "b-small.txt", "a-big.txt"], [.. viewModel.Entries.Where(entry => !entry.IsParentEntry).Select(entry => entry.Name)], "目录始终在前,文件按大小升序。");

                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                Pump(window);

                Assert.IsTrue(viewModel.SortDescending, "再点同一列翻转方向。");
                Assert.AreEqual(" ▼", viewModel.SizeSortGlyph);
                Assert.AreSequenceEqual(
                    ["z-dir", "a-big.txt", "b-small.txt"], [.. viewModel.Entries.Where(entry => !entry.IsParentEntry).Select(entry => entry.Name)]);
                Assert.AreSequenceEqual(
                    ["name", "size", "modified"], view.GetVisualDescendants().OfType<Button>()
                        .Where(button => button.Classes.Contains("col-header"))
                        .Select(button => button.CommandParameter as string)
                        .ToArray(), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder, "三列都应可点。");
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    /// <summary>
    /// 从系统文件管理器拖文件进本地栏(差距分析 A12):不显示「可放下」,松手也不复制 ——
    /// 以前给的是 Copy,松手却只刷新一下,是假承诺(维护者 2026-10-25 定:先不显示可放下)。
    /// 远端栏拖过来的行照旧可放(下载);系统文件拖进远端栏照旧可放(上传)。
    /// </summary>
    [TestMethod]
    public async Task SystemFilesDraggedOntoTheLocalPane_ShowNoDropEffect_WhileDownloadsAndUploadsStillDo()
    {
        await _session.RunOnUiAsync(async () =>
        {
            using var root = new TempDirectory();
            using var elsewhere = new TempDirectory();
            File.WriteAllText(Path.Combine(root.Path, "keep.txt"), "x");
            string outside = Path.Combine(elsewhere.Path, "outside.txt");
            File.WriteAllText(outside, "from the system file manager");
            var accessible = new LocalRootEntry("~", root.Path, true, root.Path);
            // 同上:必须早于 TempDirectory 释放,否则删目录的监视回调会污染 Dispatcher.UIThread。
            using var viewModel = new LocalFilePaneViewModel(
                new TransferOptions { LocalDownloadDirectory = root.Path },
                rootProvider: new TestRootProvider(accessible));
            await viewModel.LoadInitialAsync();

            var local = new LocalFilePaneView { DataContext = viewModel };
            var remote = new FileBrowserView
            {
                DataContext = new FileBrowserViewModel(Substitute.For<ISftpService>(), Guid.NewGuid()) { IsVisible = true },
            };
            var grid = new Grid { ColumnDefinitions = [with("*,*")] };
            Grid.SetColumn(remote, 1);
            grid.Children.Add(local);
            grid.Children.Add(remote);
            var window = new Window { Width = 1400, Height = 450, Content = grid };
            // 视图在列表上把 DragOver 标成已处理;窗口上连已处理的一起收,读到的就是视图最后给的效果。
            var effect = new StrongBox<DragDropEffects?>();
            window.AddHandler(DragDrop.DragOverEvent, (_, e) => effect.Value = e.DragEffects, RoutingStrategies.Bubble, handledEventsToo: true);
            try
            {
                window.Show();
                Pump(window);
                Point overLocal = CenterOf(local.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "FileList"), window);
                Point overRemote = CenterOf(remote.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "FileList"), window);

                IStorageFile file = Substitute.For<IStorageFile>();
                file.Name.Returns("outside.txt");
                file.Path.Returns(new Uri(outside));
                DataTransfer systemFiles = Payload(item => item.SetFile(file));

                Assert.AreEqual(DragDropEffects.None, DragOver(window, overLocal, systemFiles, effect), "系统文件拖进本地栏:不显示可放下");
                window.DragDrop(overLocal, RawDragEventType.Drop, systemFiles, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
                Pump(window);
                Assert.AreSequenceEqual(
                    ["keep.txt"], Directory.GetFileSystemEntries(root.Path).Select(Path.GetFileName).ToArray(), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder, "松手也不复制");

                Assert.AreEqual(DragDropEffects.Copy, DragOver(window, overRemote, systemFiles, effect), "系统文件拖进远端栏照旧是上传");
                window.DragDrop(overRemote, RawDragEventType.DragLeave, systemFiles, DragDropEffects.Copy, RawInputModifiers.None);

                DataTransfer remoteRows = Payload(item => item.SetText(DragDropFormats.RemotePaths + "/srv/app.log"));
                Assert.AreEqual(DragDropEffects.Copy, DragOver(window, overLocal, remoteRows, effect), "远端栏拖过来的行照旧可放(下载)");
                window.DragDrop(overLocal, RawDragEventType.DragLeave, remoteRows, DragDropEffects.Copy, RawInputModifiers.None);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

        static DataTransfer Payload(Action<DataTransferItem> fill)
        {
            var data = new DataTransfer();
            var item = new DataTransferItem();
            fill(item);
            data.Add(item);
            return data;
        }

        static Point CenterOf(Control control, Window window) =>
            control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
    }

    /// <summary>拖着 <paramref name="data" /> 进到 <paramref name="at" /> 并移动一下,返回视图给出的放下效果。</summary>
    private static DragDropEffects? DragOver(Window window, Point at, DataTransfer data, StrongBox<DragDropEffects?> effect)
    {
        effect.Value = null;
        window.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.DragOver, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
        Pump(window);
        return effect.Value;
    }

    private static Point FindWhitespacePoint(Control row)
    {
        double y = row.Bounds.Height / 2;
        for (double x = 40; x < row.Bounds.Width - 10; x += 10)
        {
            var point = new Point(x, y);
            if (!MarqueeSelection.IsDndSurface(row, row, point))
            {
                return point;
            }
        }

        throw new InvalidOperationException("本地文件行中没有找到可用于框选的视觉空白区域。");
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private sealed class TestRootProvider(params LocalRootEntry[] roots) : ILocalRootProvider
    {
        public Task<IReadOnlyList<LocalRootEntry>> EnumerateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalRootEntry>>(roots);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"velashell-root-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
