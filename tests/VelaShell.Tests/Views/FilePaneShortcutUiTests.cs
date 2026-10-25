using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 文件栏的视图内按键(维护者 2026-10-25 定,资源管理器口径):F5 刷新、F2 重命名、Delete 删除(先确认),
/// 本地栏、远端栏都有。按键都从窗口按下去(真实的隧道 / 冒泡路由),不直接调命令 ——
/// 这一层盯的是接线:焦点在列表里才删、在输入框里不接、确认框说不的时候什么也不删。
/// </summary>
[TestClass]
[TestCategory("PaneKeyboardUI")]
public sealed class FilePaneShortcutUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FilePaneShortcutUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    // ---- 本地栏 ----

    [TestMethod]
    public void LocalPane_F5_RefreshesTheListing()
    {
        RunWithLocalPane((window, view, list, vm, root) =>
        {
            FocusRow(window, list, 1);
            File.WriteAllText(Path.Combine(root, "late.txt"), "x");
            Assert.IsFalse(vm.Entries.Any(entry => entry.Name == "late.txt"), "前提:监视关掉了,不按 F5 就不会出现。");

            Press(window, Key.F5);
            PumpWhile(window, () => !vm.Entries.Any(entry => entry.Name == "late.txt"));

            Assert.IsTrue(vm.Entries.Any(entry => entry.Name == "late.txt"));
        });
    }

    [TestMethod]
    public void LocalPane_F2_RenamesTheSelectedEntry()
    {
        RunWithLocalPane((window, view, list, vm, root) =>
        {
            string? asked = null;
            vm.PromptForText = (_, initial) =>
            {
                asked = initial;
                return Task.FromResult<string?>("renamed.txt");
            };
            SelectAndFocus(window, list, vm.Entries.Single(entry => entry.Name == "b.txt"));

            Press(window, Key.F2);
            PumpWhile(window, () => !File.Exists(Path.Combine(root, "renamed.txt")));

            Assert.AreEqual("b.txt", asked, "改名框预填原名。");
            Assert.IsTrue(File.Exists(Path.Combine(root, "renamed.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "b.txt")));
        });
    }

    [TestMethod]
    public void LocalPane_Delete_AsksFirst_AndHonoursTheAnswer()
    {
        RunWithLocalPane((window, view, list, vm, root) =>
        {
            List<string> asked = [];
            bool answer = false;
            vm.ConfirmDelete = message =>
            {
                asked.Add(message);
                return Task.FromResult(answer);
            };
            SelectAndFocus(window, list, vm.Entries.Single(entry => entry.Name == "b.txt"));

            Press(window, Key.Delete);
            PumpWhile(window, () => asked.Count == 0);
            Assert.HasCount(1, asked, "删之前先问。");
            Assert.IsTrue(File.Exists(Path.Combine(root, "b.txt")), "确认框说不,就什么也不删。");

            answer = true;
            SelectAndFocus(window, list, vm.Entries.Single(entry => entry.Name == "b.txt"));
            Press(window, Key.Delete);
            PumpWhile(window, () => File.Exists(Path.Combine(root, "b.txt")));

            Assert.HasCount(2, asked);
            Assert.IsFalse(File.Exists(Path.Combine(root, "b.txt")));
        });
    }

    [TestMethod]
    public void LocalPane_DeleteInThePathBox_EditsText_InsteadOfDeletingFiles()
    {
        RunWithLocalPane((window, view, list, vm, root) =>
        {
            int asked = 0;
            vm.ConfirmDelete = _ =>
            {
                asked++;
                return Task.FromResult(true);
            };
            SelectAndFocus(window, list, vm.Entries.Single(entry => entry.Name == "b.txt"));
            Press(window, Key.L, RawInputModifiers.Control);
            Assert.IsTrue(vm.IsPathEditing);

            Press(window, Key.Delete);
            Press(window, Key.F2);

            Assert.AreEqual(0, asked, "焦点在输入框里时 Delete 是删字。");
            Assert.IsTrue(File.Exists(Path.Combine(root, "b.txt")));
        });
    }

    [TestMethod]
    public void LocalPane_DeleteWithFocusOnTheToolbar_DoesNothing()
    {
        RunWithLocalPane((window, view, list, vm, root) =>
        {
            int asked = 0;
            vm.ConfirmDelete = _ =>
            {
                asked++;
                return Task.FromResult(true);
            };
            vm.SelectedEntries.Add(vm.Entries.Single(entry => entry.Name == "b.txt"));
            view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LocalRefreshButton").Focus();
            Pump(window);

            Press(window, Key.Delete);

            Assert.AreEqual(0, asked, "焦点不在列表里时按 Delete,不该删掉一批选中着的文件。");
        });
    }

    // ---- 远端栏 ----

    [TestMethod]
    public void RemotePane_F5_ListsTheDirectoryAgain()
    {
        RunWithRemotePane((window, list, vm, sftp, sessionId) =>
        {
            FocusRow(window, list, 1);
            sftp.ClearReceivedCalls();

            Press(window, Key.F5);
            PumpWhile(window, () => sftp.ReceivedCalls().All(call => call.GetMethodInfo().Name != nameof(ISftpService.ListDirectoryAsync)));

            sftp.Received().ListDirectoryAsync(sessionId, "/home/user", Arg.Any<CancellationToken>());
        });
    }

    [TestMethod]
    public void RemotePane_F2_RenamesTheSelectedEntry()
    {
        RunWithRemotePane((window, list, vm, sftp, sessionId) =>
        {
            vm.PromptForText = (_, _) => Task.FromResult<string?>("renamed.txt");
            SelectAndFocus(window, list, vm.Files.Single(file => file.Name == "b.txt"));

            Press(window, Key.F2);
            PumpWhile(window, () => sftp.ReceivedCalls().All(call => call.GetMethodInfo().Name != nameof(ISftpService.RenameAsync)));

            sftp.Received().RenameAsync(sessionId, "/home/user/b.txt", "/home/user/renamed.txt", Arg.Any<CancellationToken>());
        });
    }

    [TestMethod]
    public void RemotePane_Delete_AsksFirst_ThenDeletesTheSelection()
    {
        RunWithRemotePane((window, list, vm, sftp, sessionId) =>
        {
            string? asked = null;
            vm.ConfirmDelete = message =>
            {
                asked = message;
                return Task.FromResult(true);
            };
            SelectAndFocus(window, list, vm.Files.Single(file => file.Name == "b.txt"));

            Press(window, Key.Delete);
            PumpWhile(window, () => sftp.ReceivedCalls().All(call => call.GetMethodInfo().Name != nameof(ISftpService.DeleteAsync)));

            Assert.IsNotNull(asked, "删之前先问。");
            StringAssert.Contains(asked, "b.txt");
            sftp.Received().DeleteAsync(sessionId, "/home/user/b.txt", Arg.Any<IProgress<SftpDeleteProgress>?>(), Arg.Any<CancellationToken>());
        });
    }

    [TestMethod]
    public void RemotePane_F2WithSeveralSelected_DoesNotRename()
    {
        RunWithRemotePane((window, list, vm, sftp, _) =>
        {
            int prompted = 0;
            vm.PromptForText = (_, _) =>
            {
                prompted++;
                return Task.FromResult<string?>(null);
            };
            SelectAndFocus(window, list, vm.Files.Single(file => file.Name == "b.txt"));
            vm.SelectedFiles.Add(vm.Files.Single(file => file.Name == "c.txt"));
            Pump(window);

            Press(window, Key.F2);

            Assert.AreEqual(0, prompted, "选着多项时 F2 不知道改哪一个,不动。");
        });
    }

    // ---- 过滤框 ----

    [TestMethod]
    public void RemotePane_FilterToggle_OpensAFocusedBox_TypingFilters_EscClearsAndReturnsToTheList()
    {
        RunWithRemotePane((window, list, vm, _, _) =>
        {
            Control view = list.FindAncestorOfType<FileBrowserView>()!;
            TextBox box = Named<TextBox>(view, "FilterBox");
            Assert.IsFalse(box.IsEffectivelyVisible, "过滤框默认收着。");

            Click(window, Named<ToggleButton>(view, "FilterToggle"));
            PumpWhile(window, () => !box.IsFocused);
            Assert.IsTrue(vm.IsFilterOpen);
            Assert.IsTrue(box.IsFocused, "点开就能直接打字。");

            window.KeyTextInput("B.T");
            Pump(window);
            Assert.AreSequenceEqual(["..", "b.txt"], [.. vm.Files.Select(f => f.Name)], "名字含有、不分大小写。");
            Assert.IsEmpty(vm.SelectedFiles, "在过滤框里打的字不是键入定位。");

            Press(window, Key.Escape);
            PumpWhile(window, () => FocusedItem(window) is null);

            Assert.IsFalse(vm.IsFilterOpen);
            Assert.AreEqual(string.Empty, vm.FilterText);
            Assert.AreEqual(1 + 4, list.ItemCount);
            Assert.IsNotNull(FocusedItem(window), "收起之后焦点回到列表。");
        });
    }

    [TestMethod]
    public void LocalPane_FilterBox_EnterKeepsTheFilter_AndHandsFocusToTheList()
    {
        RunWithLocalPane((window, view, list, vm, _) =>
        {
            TextBox box = Named<TextBox>(view, "FilterBox");
            Click(window, Named<ToggleButton>(view, "FilterToggle"));
            PumpWhile(window, () => !box.IsFocused);

            window.KeyTextInput("a.");
            Pump(window);
            Press(window, Key.Delete);   // 在输入框里是删字,不是删文件
            Press(window, Key.Enter);
            PumpWhile(window, () => FocusedItem(window) is null);

            Assert.AreEqual("a.", vm.FilterText);
            Assert.AreSequenceEqual(["a.txt"], [.. vm.Entries.Where(e => !e.IsParentEntry).Select(e => e.Name)]);
            Assert.IsTrue(vm.IsFilterOpen, "回车只是把焦点交回列表,过滤留着。");
            Assert.IsNotNull(FocusedItem(window));
        });
    }

    // ---- 本地栏双击 ----

    [TestMethod]
    public void LocalPane_DoubleClickingAFile_OpensItWithTheSystemProgram_AndAFolderIsEntered()
    {
        RunWithLocalPane((window, view, list, vm, root) =>
        {
            List<string> opened = [];
            vm.OpenLocalFile = path =>
            {
                opened.Add(path);
                return Task.CompletedTask;
            };

            DoubleClick(window, list, vm.Entries.Single(entry => entry.Name == "b.txt"));
            PumpWhile(window, () => opened.Count == 0);
            Assert.AreSequenceEqual([Path.Combine(root, "b.txt")], opened, "与远端栏双击文件对称:用系统程序打开,不是上传。");
            Assert.AreEqual(root, vm.CurrentPath);

            DoubleClick(window, list, vm.Entries.Single(entry => entry.Name == "sub"));
            PumpWhile(window, () => vm.CurrentPath != Path.Combine(root, "sub"));
            Assert.AreEqual(Path.Combine(root, "sub"), vm.CurrentPath, "目录照旧是进去。");
            Assert.HasCount(1, opened);
        });
    }

    private static void DoubleClick(Window window, ListBox list, object item)
    {
        list.ScrollIntoView(item);
        Pump(window);
        Control row = list.ContainerFromItem(item)!;
        Point at = row.TranslatePoint(new(20, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Pump(window);
    }

    // ---- 后退 / 前进 ----

    [TestMethod]
    public void RemotePane_AltLeftAndAltRight_WalkTheHistory_AndTheButtonsFollow()
    {
        RunWithRemotePane((window, list, vm, _, _) =>
        {
            Control view = list.FindAncestorOfType<FileBrowserView>()!;
            Button back = Named<Button>(view, "RemoteBackButton");
            Button forward = Named<Button>(view, "RemoteForwardButton");
            Assert.IsTrue(back.Classes.Contains("unavailable"), "还没有历史:后退钮淡着。");

            PumpUntil(vm.NavigateToAsync("/home/user/sub"));
            Pump(window);
            Assert.IsFalse(back.Classes.Contains("unavailable"));
            FocusRow(window, list, 0);

            Press(window, Key.Left, RawInputModifiers.Alt);
            PumpWhile(window, () => vm.CurrentPath != "/home/user");
            Assert.AreEqual("/home/user", vm.CurrentPath);
            Assert.IsFalse(forward.Classes.Contains("unavailable"));

            Click(window, forward);
            PumpWhile(window, () => vm.CurrentPath != "/home/user/sub");
            Assert.AreEqual("/home/user/sub", vm.CurrentPath, "前进钮与 Alt+→ 是同一个命令。");
        });
    }

    [TestMethod]
    public void LocalPane_AltLeft_GoesBack_ButNotFromThePathBox()
    {
        RunWithLocalPane((window, view, list, vm, root) =>
        {
            PumpUntil(vm.NavigateToAsync(Path.Combine(root, "sub")));
            Pump(window);
            FocusRow(window, list, 0);
            Press(window, Key.L, RawInputModifiers.Control);
            Assert.IsTrue(vm.IsPathEditing);

            Press(window, Key.Left, RawInputModifiers.Alt);
            Assert.AreEqual(Path.Combine(root, "sub"), vm.CurrentPath, "焦点在输入框里时 Alt+← 归输入框。");

            Press(window, Key.Escape);
            PumpWhile(window, () => FocusedItem(window) is null);
            Press(window, Key.Left, RawInputModifiers.Alt);
            PumpWhile(window, () => vm.CurrentPath != root);

            Assert.AreEqual(root, vm.CurrentPath);
        });
    }

    /// <summary>
    /// 主窗口的 Alt+方向移焦走隧道、先于文件栏:焦点在文件栏里时它得让出左右两个方向,
    /// 不然分屏时文件栏的后退 / 前进一按就变成了换窗格。上下两个方向、输入框里、文件栏以外照旧归移焦。
    /// </summary>
    [TestMethod]
    public void PaneNavigation_YieldsAltLeftRight_OnlyWhileFocusIsInAFilePane()
    {
        RunWithRemotePane((window, list, vm, _, _) =>
        {
            Control view = list.FindAncestorOfType<FileBrowserView>()!;
            Control row = list.ContainerFromIndex(1)!;
            TextBox filter = Named<TextBox>(view, "FilterBox");

            Assert.IsTrue(FilePaneKeys.OwnsPaneNavigationKey(Key(Avalonia.Input.Key.Left, row)));
            Assert.IsTrue(FilePaneKeys.OwnsPaneNavigationKey(Key(Avalonia.Input.Key.Right, row)));
            Assert.IsFalse(FilePaneKeys.OwnsPaneNavigationKey(Key(Avalonia.Input.Key.Up, row)), "上下照旧移焦。");
            Assert.IsFalse(FilePaneKeys.OwnsPaneNavigationKey(Key(Avalonia.Input.Key.Left, filter)), "输入框里不接。");
            Assert.IsFalse(FilePaneKeys.OwnsPaneNavigationKey(Key(Avalonia.Input.Key.Left, new Button())), "文件栏以外不接。");
        });

        static KeyEventArgs Key(Key key, object source) => new()
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.Alt,
            Source = source,
        };
    }

    // ---- 工具 ----

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static object? FocusedItem(Window window) =>
        (window.FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext;

    private static void Click(Window window, Control target)
    {
        Point center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Pump(window);
    }

    private static void FocusRow(Window window, ListBox list, int index)
    {
        list.ContainerFromIndex(index)!.Focus();
        Pump(window);
    }

    private static void SelectAndFocus(Window window, ListBox list, object item)
    {
        list.SelectedItems!.Clear();
        list.SelectedItems.Add(item);
        Pump(window);
        list.ContainerFromItem(item)!.Focus();
        Pump(window);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Pump(window);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpWhile(Window window, Func<bool> condition)
    {
        for (int i = 0; i < 2000 && condition(); i++)
        {
            Pump(window);
            Thread.Sleep(1);
        }
        Pump(window);
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

    /// <summary>
    /// 在临时目录(a.txt、b.txt、c.txt、sub/)上开一个本地栏。目录监视关掉:F5 的用例要看得出列表是它刷出来的,
    /// 而不是监视器自己刷的。
    /// </summary>
    private static void RunWithLocalPane(Action<Window, LocalFilePaneView, ListBox, LocalFilePaneViewModel, string> body) =>
        OnUi(() =>
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"vela-keys-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            foreach (string name in (string[])["a.txt", "b.txt", "c.txt"])
            {
                File.WriteAllText(Path.Combine(root, name), "x");
            }

            var vm = new LocalFilePaneViewModel(new TransferOptions(), new UnwatchedFileSystem(), null);
            PumpUntil(vm.NavigateToAsync(root));
            var view = new LocalFilePaneView { DataContext = vm };
            var window = new Window { Width = 700, Height = 400, Content = view };
            try
            {
                window.Show();
                Pump(window);
                ListBox list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "FileList");
                body(window, view, list, vm, root);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
                vm.Dispose();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        });

    /// <summary>开一个远端栏:/home/user 下 sub/、a.txt、b.txt、c.txt。</summary>
    private static void RunWithRemotePane(Action<Window, ListBox, FileBrowserViewModel, ISftpService, Guid> body) =>
        OnUi(() =>
        {
            ISftpService sftp = Substitute.For<ISftpService>();
            var sessionId = Guid.NewGuid();
            static RemoteFileInfo Remote(string name, bool isDirectory = false) => new()
            {
                Name = name,
                FullPath = $"/home/user/{name}",
                Size = 1,
                Permissions = isDirectory ? "drwxr-xr-x" : "-rw-r--r--",
                IsDirectory = isDirectory,
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user",
            };
            sftp.ListDirectoryAsync(sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<List<RemoteFileInfo>>([Remote("sub", isDirectory: true), Remote("a.txt"), Remote("b.txt"), Remote("c.txt")]));

            var vm = new FileBrowserViewModel(sftp, sessionId)
            {
                IsVisible = true,
                TransferOptions = new(),
                CurrentPath = "/home/user",
            };
            PumpUntil(vm.NavigateToAsync("/home/user"));

            var view = new FileBrowserView { DataContext = vm };
            var window = new Window { Width = 900, Height = 400, Content = view };
            try
            {
                window.Show();
                Pump(window);
                ListBox list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "FileList");
                Assert.AreEqual(1 + 4, list.ItemCount, "应为 \"..\"、sub 与三个文件。");
                body(window, list, vm, sftp, sessionId);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>真实的本机文件系统,只是不挂目录监视。</summary>
    private sealed class UnwatchedFileSystem : ILocalFileSystem
    {
        private readonly PhysicalLocalFileSystem _inner = new();

        public Task<IReadOnlyList<LocalFileSystemEntry>> EnumerateAsync(string path, CancellationToken cancellationToken) =>
            _inner.EnumerateAsync(path, cancellationToken);

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => _inner.DeleteFileAsync(path, cancellationToken);

        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) =>
            _inner.DeleteDirectoryAsync(path, cancellationToken);

        public Task MoveAsync(string sourcePath, string destPath, CancellationToken cancellationToken) =>
            _inner.MoveAsync(sourcePath, destPath, cancellationToken);

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
            _inner.CreateDirectoryAsync(path, cancellationToken);

        public Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken) =>
            _inner.GetFileSizeAsync(path, cancellationToken);

        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) => _inner.OpenReadAsync(path, cancellationToken);

        public Task<Stream> OpenWriteAsync(string path, FileMode mode, CancellationToken cancellationToken) =>
            _inner.OpenWriteAsync(path, mode, cancellationToken);

        public IDisposable? Watch(string directory, Action onChanged) => null;
    }
}
