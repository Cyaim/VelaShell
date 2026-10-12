using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 上传用的本地路径选择器:文件与文件夹在同一个列表里,可混选、可多选。
/// <para>
/// 为什么不用系统对话框:Avalonia 的 <c>IStorageProvider</c> 只有
/// <c>OpenFilePickerAsync</c>(纯文件)与 <c>OpenFolderPickerAsync</c>(纯文件夹)两个入口,
/// Windows/Linux 的系统对话框本身也做不到一次同时选文件和文件夹 —— 上传因此长期被拆成
/// "上传文件""上传文件夹"两个菜单项。自绘一个就没这个限制,三个平台行为还一致。
/// </para>
/// </summary>
public partial class LocalPathPickerDialog : Window
{
    /// <summary>无参构造仅供 XAML 设计期使用。</summary>
    public LocalPathPickerDialog()
        : this(new()) { }

    /// <summary>用给定的传输设置(决定起始目录)创建选择器。</summary>
    public LocalPathPickerDialog(TransferOptions transferOptions)
        : this(new LocalFilePaneViewModel(transferOptions), loadInitial: true) { }

    /// <summary>用现成的面板视图模型创建选择器(测试用:可指定起始目录、跳过初始加载)。</summary>
    internal LocalPathPickerDialog(LocalFilePaneViewModel viewModel, bool loadInitial)
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.SelectedEntries.CollectionChanged += (_, _) => UpdateSelectionSummary();
        UpdateSelectionSummary();

        // 框选。这里的行不发起拖放,不存在"拖行 = 拖文件"的歧义,所以放开从行上起框 ——
        // 行是撑满整行宽的,只准从空白起框等于文件一多就用不上,正是最需要它的时候。
        _marquee = MarqueeSelection.Attach(
            FileList,
            Marquee,
            item => item is LocalFileEntry { IsParentEntry: false },
                _ => true
        );

        // 键入定位:在窗口里直接敲文件名开头几个字母就选中它,资源管理器同款。
        TypeAheadSelection.Attach(FileList, this, item => item is LocalFileEntry { IsParentEntry: false } entry ? entry.Name : null);

        // 列表里的回车。ListBoxItem 自己把回车标成已处理,默认的「上传」按钮因此收不到,
        // 所以这里连已处理的也收 —— 否则定位到文件之后还得伸手去点按钮。
        FileList.AddHandler(KeyDownEvent, OnFileListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        // 手动输入路径。Ctrl+L 走隧道,免得先被文件列表的键盘导航吃掉。
        ViewModel.PathEditActivated += OnPathEditActivated;
        AddHandler(KeyDownEvent, OnDialogKeyDownTunnel, RoutingStrategies.Tunnel);

        if (loadInitial)
        {
            _ = ViewModel.LoadInitialAsync();
        }
    }

    private LocalFilePaneViewModel ViewModel { get; }

    private readonly MarqueeSelection _marquee;

    /// <summary>关窗时务必收起框选,否则自动滚动的计时器会拽着已关闭的窗口继续跑。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _marquee.End();
        ViewModel.PathEditActivated -= OnPathEditActivated;
        base.OnClosed(e);
    }

    /// <summary>
    /// 打开选择器,返回用户选中的本地路径(文件与文件夹混合);取消则返回空列表。
    /// </summary>
    public static async Task<IReadOnlyList<string>> PickAsync(Window owner, TransferOptions transferOptions)
    {
        var dialog = new LocalPathPickerDialog(transferOptions);
        return await dialog.ShowDialog<IReadOnlyList<string>?>(owner) ?? [];
    }

    /// <summary>当前选中的真实条目(排除合成的 ".." 行)。</summary>
    private List<LocalFileEntry> PickedEntries() =>
        [.. ViewModel.SelectedEntries.Where(entry => !entry.IsParentEntry)];

    private void UpdateSelectionSummary()
    {
        if (SelectionSummary is null)
        {
            return;
        }
        List<LocalFileEntry> picked = PickedEntries();
        int folders = picked.Count(entry => entry.IsDirectory);
        SelectionSummary.Text = picked.Count == 0
            ? Strings.Get("Sftp_PickUploadHint")
            : Strings.Format("Sftp_PickUploadSelected", picked.Count - folders, folders);

        // 没选东西时不给按,免得开一个空批次。
        ConfirmButton?.IsEnabled = picked.Count > 0;
    }

    /// <summary>双击:目录(含 "..")进去,文件直接当作选定并确认 —— 单选是最常见的用法。</summary>
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: LocalFileEntry entry })
        {
            return;
        }
        if (entry.IsDirectory)
        {
            ViewModel.ActivateCommand.Execute(entry).Subscribe();
            return;
        }
        this.PostClose(new[] { entry.FullPath });
    }

    /// <summary>
    /// 列表里的回车与双击同一套规矩:只选着一个目录(含 "..")时进去,其余情况与「上传」按钮一样确认选中项。
    /// 进目录后焦点给新列表的第一行,接着能按方向键、敲字定位。
    /// </summary>
    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        e.Handled = true;
        if (ViewModel.SelectedEntries is [{ IsDirectory: true } directory])
        {
            ViewModel.ActivateCommand.Execute(directory).Subscribe(_ => FocusList(), _ => { });
            return;
        }
        Confirm();
    }

    private void OnRootSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox
            && comboBox.SelectedItem is LocalRootEntry root
            && !ReferenceEquals(root, ViewModel.SelectedRoot))
        {
            if (!root.IsAccessible)
            {
                comboBox.SelectedItem = ViewModel.SelectedRoot;
                return;
            }
            ViewModel.SwitchRootCommand.Execute(root).Subscribe();
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Confirm();

    // 推迟关闭:同步 Close 会让本轮点击/按键的后续路由打到已销毁的窗口刷
    // "PlatformImpl is null" 警告(见 WindowCloseExtensions)。
    private void Confirm()
    {
        List<LocalFileEntry> picked = PickedEntries();
        if (picked.Count > 0)
        {
            this.PostClose(picked.Select(entry => entry.FullPath).ToArray());
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => this.PostClose(null);

    // ---- 手动输入路径 ----

    private void OnDialogKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ViewModel.BeginPathEdit();
            e.Handled = true;
        }
    }

    /// <summary>点面包屑后面的空白 = 切成输入框(资源管理器地址栏同款)。</summary>
    private void OnPathAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ViewModel.BeginPathEdit();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 进入手动输入态后聚焦输入框并全选:十有八九是整条换掉,而不是在现有路径里插字。
    /// 延到 Loaded 优先级是因为输入框此刻刚由 IsVisible 显现,尚未参与布局,立即 Focus 会落空。
    /// </summary>
    private void OnPathEditActivated(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (PathEditBox is not { IsVisible: true } box)
                {
                    return;
                }
                box.Focus();
                box.SelectAll();
            },
            DispatcherPriority.Loaded
        );

    /// <summary>
    /// 回车跳转、Esc 收起。两个键都必须就地标成已处理:回车冒到窗口上会触发默认的「上传」按钮,
    /// 把列表里选着的东西直接传走;Esc 冒上去则把整个选择器关掉。
    /// </summary>
    private void OnPathEditKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ViewModel.CommitPathEditCommand.Execute().Subscribe(_ => FocusList(), _ => { });
                return;
            case Key.Escape:
                e.Handled = true;
                ViewModel.CancelPathEdit();
                FocusList();
                return;
        }
    }

    /// <summary>点到别处即放弃输入,回到面包屑 —— 不留一个半开的输入框。</summary>
    private void OnPathEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPathEditing)
        {
            ViewModel.CancelPathEdit();
        }
    }

    /// <summary>
    /// 跳转之后把焦点放回列表,接着就能按方向键、回车:有选中项(路径指向文件时它已被选中)就滚到它并给它,
    /// 否则给第一行。路径跳转失败时输入框还开着,焦点留在原地让人改。
    /// </summary>
    /// <remarks>
    /// 焦点只能给行容器:ListBox 本身不可聚焦,<c>FileList.Focus()</c> 什么也不做。
    /// 代码里 Focus 一行不会选中它,所以没有选中项时给第一行,选中集照旧是空的。
    /// </remarks>
    private void FocusList()
    {
        if (ViewModel.IsPathEditing)
        {
            return;
        }
        int index = Math.Max(FileList.SelectedIndex, 0);
        if (index < FileList.ItemCount)
        {
            FileList.ScrollIntoView(index);
            FileList.ContainerFromIndex(index)?.Focus();
        }
    }

    /// <summary>Esc 取消。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose(null);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }
}
