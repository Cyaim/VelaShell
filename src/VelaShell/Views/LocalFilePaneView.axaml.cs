using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>本地文件面板视图,含面包屑、上下文菜单与跨面板拖放。</summary>
public partial class LocalFilePaneView : UserControl
{
    // 拖拽状态
    private bool _isDragging;
    private Point _dragOrigin;
    private LocalFileEntry? _dragRow;
    private PointerPressedEventArgs? _dragPointerArgs;
    private readonly List<LocalFileEntry> _selectionAtPress = [];
    private bool _selectionRestoredForPendingDrag;
    private const double DragThreshold = 5;

    /// <summary>初始化本地文件面板视图,并接线 VM 委托。</summary>
    public LocalFilePaneView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LocalFilePaneViewModel vm)
            {
                vm.ConfirmDelete = ConfirmAsync;
                vm.PromptForText = PromptForTextAsync;
                vm.OpenLocalFile = OpenLocalFileAsync;
            }
        };

        // 框选与跨栏拖放使用不相交的按下表面:只有 XAML 标记的内容表面发起拖放。
        if (this.FindControl<ListBox>("FileList") is { } list
            && this.FindControl<Border>("MarqueeOverlay") is { } overlay)
        {
            MarqueeSelection.Attach(
                list,
                overlay,
                item => item is LocalFileEntry { IsParentEntry: false },
                e => !MarqueeSelection.IsDndSurface(e.Source, FileList, e.GetPosition(FileList))
            );
        }

        // 拖放:接收远端文件拖入 + 发起本地文件拖拽。
        if (this.FindControl<ListBox>("FileList") is { } fileList)
        {
            DragDrop.SetAllowDrop(fileList, true);
            fileList.AddHandler(DragDrop.DragOverEvent, OnLocalDropDragOver);
            fileList.AddHandler(DragDrop.DropEvent, OnLocalDrop);
            fileList.AddHandler(PointerPressedEvent, OnLocalDragPointerPressedTunnel, RoutingStrategies.Tunnel, true);
            fileList.AddHandler(PointerPressedEvent, OnLocalDragPointerPressedBubble, RoutingStrategies.Bubble, true);
            fileList.AddHandler(PointerMovedEvent, OnLocalDragPointerMoved);
            fileList.AddHandler(PointerReleasedEvent, OnLocalDragPointerReleased, RoutingStrategies.Bubble, true);
        }

        // 键入定位:焦点在本地栏里时直接敲文件名开头几个字母就选中它,资源管理器同款。
        // 挂在本地栏自己身上,不碰远端栏与终端的键入。
        TypeAheadSelection.Attach(FileList, this, item => item is LocalFileEntry { IsParentEntry: false } entry ? entry.Name : null);
        FileList.KeepFocusAcrossReloads();

        // 回车进文件夹。ListBoxItem 自己把回车标成已处理,所以连已处理的也收。
        FileList.AddHandler(KeyDownEvent, OnFileListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        // 手动输入路径(路径栏见 LocalPathBar)。Ctrl+L 走隧道,免得先被文件列表的键盘导航吃掉;
        // 登记在本地栏上,焦点在远端栏时由远端栏自己的 Ctrl+L 接。输入框收起后焦点回到列表。
        // F5 / F2 / Delete 同一个处理器(见 FilePaneKeys)。
        AddHandler(KeyDownEvent, OnLocalPaneKeyDownTunnel, RoutingStrategies.Tunnel);
        PathBar.EditFinished += (_, _) => FileList.FocusSelectedOrFirstRow();
    }

    private void OnLocalPaneKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (DataContext is not LocalFilePaneViewModel vm)
        {
            return;
        }
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.BeginPathEdit();
            e.Handled = true;
            return;
        }

        // F5 / F2 / Delete(资源管理器口径,见 FilePaneKeys)。走隧道:F2 / Delete 要赶在列表自己的键盘处理之前。
        switch (FilePaneKeys.Classify(e, FileList))
        {
            case FilePaneKeyAction.Refresh:
                vm.RefreshCommand.Execute().Subscribe(_ => { }, _ => { });
                e.Handled = true;
                return;
            case FilePaneKeyAction.Rename when vm.SelectedEntries is [{ IsParentEntry: false } entry]:
                vm.RenameCommand.Execute(entry).Subscribe(_ => { }, _ => { });
                e.Handled = true;
                return;
            case FilePaneKeyAction.Delete:
                // 删除命令自己会先弹确认,只选着 ".." 时什么也不做。
                vm.DeleteSelectedCommand.Execute().Subscribe(_ => { }, _ => { });
                e.Handled = true;
                return;
            case FilePaneKeyAction.Back:
                // 与按钮同一个命令、同样先问能不能执行:没有历史、或上一步还没走完时什么也不做。
                FilePaneKeys.Run(vm.GoBackCommand);
                e.Handled = true;
                return;
            case FilePaneKeyAction.Forward:
                FilePaneKeys.Run(vm.GoForwardCommand);
                e.Handled = true;
                return;
        }
    }

    /// <summary>用户点开过滤框就把焦点给它;程序改开关不抢焦点(与远端栏同一处理,见 FilePaneFilter)。</summary>
    private void OnFilterToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: true })
        {
            FilePaneFilter.FocusWhenShown(FilterBox);
        }
    }

    /// <summary>过滤框里:Esc 清空并收起,回车把焦点交回列表。</summary>
    private void OnFilterBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is LocalFilePaneViewModel vm && FilePaneFilter.HandleKey(e, FileList, () => vm.IsFilterOpen = false))
        {
            e.Handled = true;
        }
    }

    private void OnRootSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is LocalFilePaneViewModel viewModel
            && sender is ComboBox comboBox
            && comboBox.SelectedItem is LocalRootEntry root
            && !ReferenceEquals(root, viewModel.SelectedRoot))
        {
            if (!root.IsAccessible)
            {
                comboBox.SelectedItem = viewModel.SelectedRoot;
                return;
            }
            viewModel.SwitchRootCommand.Execute(root).Subscribe();
        }
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }
        return await MessageDialog.ConfirmAsync(
            owner,
            Strings.ConfirmDeleteTitle,
            message,
            Strings.Delete,
            kind: MessageDialogKind.Warning,
            danger: true);
    }

    /// <summary>
    /// 双击:目录(含 "..")进去;文件用系统默认程序打开,与远端栏双击文件对称(上传留给拖放、按钮与右键)。
    /// </summary>
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not LocalFilePaneViewModel vm || sender is not ListBox { SelectedItem: LocalFileEntry entry })
        {
            return;
        }
        if (entry.IsDirectory)
        {
            vm.ActivateCommand.Execute(entry).Subscribe();
            return;
        }
        vm.OpenItemCommand.Execute(entry).Subscribe(_ => { }, _ => { });
    }

    /// <summary>
    /// 列表里的回车:只选着一个文件夹(含 "..")时进去,与双击一样;进去之后焦点留在列表里,接着就能敲字定位。
    /// 文件上的回车不做事。
    /// </summary>
    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || e.KeyModifiers != KeyModifiers.None
            || DataContext is not LocalFilePaneViewModel vm
            || (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
        {
            return;
        }
        if (vm.SelectedEntries is [{ IsDirectory: true } folder])
        {
            e.Handled = true;
            vm.ActivateCommand.Execute(folder).Subscribe();
        }
    }

    private void OnFileRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            // 非父行上按左键:为拖往远端面板做准备。
            if (sender is Border { DataContext: LocalFileEntry entry }
                && !entry.IsParentEntry
                && MarqueeSelection.IsDndSurface(e.Source, (Visual)sender, e.GetPosition((Visual)sender)))
            {
                _dragOrigin = e.GetPosition(this.FindControl<ListBox>("FileList"));
                _dragRow = entry;
                _dragPointerArgs = e;
                _isDragging = false;
            }
            return;
        }
        // 右键:为上下文菜单选中该行。
        if (sender is not Border { DataContext: LocalFileEntry entry2 }
            || entry2.IsParentEntry
            || DataContext is not LocalFilePaneViewModel
            || FileList is not { } listBox)
        {
            return;
        }
        if (listBox.SelectedItems is not { } selection)
        {
            return;
        }
        if (!selection.Contains(entry2))
        {
            selection.Clear();
            selection.Add(entry2);
        }
    }

    private void OnLocalDragPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        _selectionAtPress.Clear();
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed
            || FileList.SelectedItems is not { } selected)
        {
            return;
        }

        foreach (object? item in selected)
        {
            if (item is LocalFileEntry entry && !entry.IsParentEntry)
            {
                _selectionAtPress.Add(entry);
            }
        }
    }

    private void OnLocalDragPointerPressedBubble(object? sender, PointerPressedEventArgs e)
    {
        if (_dragRow is not { } source
            || _selectionAtPress.Count <= 1
            || !_selectionAtPress.Contains(source)
            || e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            || DataContext is not LocalFilePaneViewModel vm)
        {
            return;
        }

        DragSelectionResolver.SynchronizeSelection(vm.SelectedEntries, _selectionAtPress);
        _selectionRestoredForPendingDrag = true;
    }

    private async Task<string?> PromptForTextAsync(string title, string initialValue)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }
        return await MessageDialog.PromptAsync(owner, title, initialValue);
    }

    private async Task OpenLocalFileAsync(string localPath)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }
        await top.Launcher.LaunchFileInfoAsync(new(localPath));
    }

    // ── 拖拽发起(本地 → 远端) ────────────────────────────────

    private void OnLocalDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging || _dragRow is null || _dragPointerArgs is null)
        {
            return;
        }
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed)
        {
            ResetLocalDragGesture();
            return;
        }
        Point current = e.GetPosition(this.FindControl<ListBox>("FileList"));
        if (Math.Abs(current.X - _dragOrigin.X) < DragThreshold && Math.Abs(current.Y - _dragOrigin.Y) < DragThreshold)
        {
            return;
        }

        _isDragging = true;
        _ = StartLocalDragAsync(_dragRow, _dragPointerArgs);
    }

    private void OnLocalDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging
            && _selectionRestoredForPendingDrag
            && _dragRow is { } source
            && DataContext is LocalFilePaneViewModel vm)
        {
            DragSelectionResolver.SynchronizeSelection(vm.SelectedEntries, [source]);
        }
        ResetLocalDragGesture();
    }

    private async Task StartLocalDragAsync(LocalFileEntry source, PointerPressedEventArgs pointerArgs)
    {
        if (DataContext is LocalFilePaneViewModel vm)
        {
            IReadOnlyList<LocalFileEntry> entries = DragSelectionResolver.ResolveAtDragStart(
                _selectionAtPress,
                vm.SelectedEntries,
                source,
                item => item.IsParentEntry,
                usePressSnapshot: !pointerArgs.KeyModifiers.HasFlag(KeyModifiers.Control)
                    && !pointerArgs.KeyModifiers.HasFlag(KeyModifiers.Shift));
            DragSelectionResolver.SynchronizeSelection(vm.SelectedEntries, entries);
            string[] paths = [.. entries.Select(item => item.FullPath)];
            await StartDragAsync(paths, pointerArgs, DragDropFormats.LocalPaths);
            return;
        }
        await StartDragAsync([source.FullPath], pointerArgs, DragDropFormats.LocalPaths);
    }

    private async Task StartDragAsync(
        IReadOnlyList<string> paths,
        PointerPressedEventArgs pointerArgs,
        string format)
    {
        var data = new DataTransfer();
        var dragItem = new DataTransferItem();
        dragItem.SetText(format + string.Join("\n", paths));
        data.Add(dragItem);

        try
        {
            await DragDrop.DoDragDropAsync(pointerArgs, data, DragDropEffects.Copy);
        }
        finally
        {
            ResetLocalDragGesture();
        }
    }

    private void ResetLocalDragGesture()
    {
        _isDragging = false;
        _dragRow = null;
        _dragPointerArgs = null;
        _selectionAtPress.Clear();
        _selectionRestoredForPendingDrag = false;
    }

    // ── 放置处理(远端 → 本地) ──────────────────────────────────

    /// <summary>
    /// 只接远端栏拖过来的行(下载)。从系统文件管理器拖进来的文件给 None:光标不显示「可放下」,松手也什么都不做。
    /// </summary>
    /// <remarks>
    /// 以前这里对系统文件也给 Copy,松手却只刷新一下 —— 显示能放、放了又没复制,这个假承诺比不接更糟。
    /// 本机文件之间的复制不是这个窗口的本职(系统文件管理器做得更好);真要做,得沿用冲突策略、
    /// 给目录复制进度与取消,另立一项(维护者 2026-10-25 定:先不显示可放下)。拖进远端栏照旧是上传。
    /// </remarks>
    private void OnLocalDropDragOver(object? sender, DragEventArgs e)
    {
        bool isRemoteDrag = !string.IsNullOrEmpty(e.DataTransfer.TryGetText())
            && e.DataTransfer.TryGetText()!.StartsWith(DragDropFormats.RemotePaths);
        e.DragEffects = isRemoteDrag ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnLocalDrop(object? sender, DragEventArgs e) => FireAndForget.Run(async () =>
    {
        // 先检查跨面板的远端文件拖拽(VFTP 标记)。
        string? text = e.DataTransfer.TryGetText();
        if (!string.IsNullOrEmpty(text) && text.StartsWith(DragDropFormats.RemotePaths))
        {
            string[] remotePaths = text[DragDropFormats.RemotePaths.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (remotePaths.Length == 0) return;

            // 用可视化树祖先查找,代替脆弱的 Parent.Parent 链式访问。
            if (this.FindAncestorOfType<SftpDocumentView>()?.DataContext is not SftpDocumentViewModel sftpVm) return;

            // 由路径解析远端条目,且不修改远端的选中集合。
            RemoteFileInfoViewModel[] entries = remotePaths
                .Select(p => sftpVm.RemoteFiles.Files.FirstOrDefault(f => f.FullPath == p))
                .Where(e => e is not null && !e.IsParentEntry)
                .ToArray()!;

            if (entries.Length > 0)
            {
                try
                {
                    await sftpVm.RemoteFiles.DownloadRemoteEntriesAsync(entries, sftpVm.LocalFiles.CurrentPath);
                    await sftpVm.LocalFiles.RefreshAsync();
                }
                catch (FileNotFoundException)
                {
                    // 文件可能已在列举与下载之间被删除;刷新远端列表。
                    sftpVm.RemoteFiles.RefreshCommand.Execute().Subscribe();
                }
            }
            e.Handled = true;
        }
        // 系统文件拖进来不接(见 OnLocalDropDragOver):DragOver 已经给了 None,这里也不做任何事。
    });
}
