using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;
using WindowLayout = VelaShell.Services.WindowLayout;
using WindowLayoutStore = VelaShell.Services.WindowLayoutStore;

namespace VelaShell.Views;

/// <summary>上传选择框文件列表的视图形态。</summary>
public enum LocalPickerView
{
    /// <summary>列表(详细信息):一行一项,带大小与修改时间两列。</summary>
    List,

    /// <summary>图标(资源管理器「中等图标」):格子从左到右排,大图标在上、名字在下。</summary>
    Icons
}

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
        WindowChrome.Apply(this, WindowChromeKind.Dialog, ResizeGrips);
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.SelectedEntries.CollectionChanged += (_, _) => UpdateSelectionSummary();
        UpdateSelectionSummary();

        // 框选。这里的行不发起拖放,不存在"拖行 = 拖文件"的歧义,所以放开从行上起框 ——
        // 行是撑满整行宽的,只准从空白起框等于文件一多就用不上,正是最需要它的时候。
        // 图标视图同理从格子上也能起框;框中哪些格子由格子面板按二维几何算(见 MarqueeSelection.SweptIndices)。
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

        // 手动输入路径(路径栏见 LocalPathBar)。Ctrl+L 走隧道,免得先被文件列表的键盘导航吃掉;
        // 输入框收起后焦点回到列表。
        AddHandler(KeyDownEvent, OnDialogKeyDownTunnel, RoutingStrategies.Tunnel);
        PathBar.EditFinished += (_, _) => FocusList();

        if (loadInitial)
        {
            _ = ViewModel.LoadInitialAsync();
        }
    }

    /// <summary>尺寸与视图记在 windowLayout 里的键(见 <see cref="WindowLayoutStore" />)。</summary>
    private const string LayoutKey = "uploadPicker";

    private LocalFilePaneViewModel ViewModel { get; }

    private readonly MarqueeSelection _marquee;

    /// <summary>
    /// 文件列表的视图形态。两种视图共用一个格子面板(<see cref="VirtualizingTilePanel" />),
    /// 切换只换项模板与列表上的 <c>tiles</c> 类(格子尺寸由样式给);选中集在视图模型里,切换前后不变。
    /// </summary>
    internal LocalPickerView View
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            field = value;
            bool icons = value == LocalPickerView.Icons;
            FileList.Classes.Set("tiles", icons);
            FileList.ItemTemplate = (IDataTemplate)Resources[icons ? "TileTemplate" : "RowTemplate"]!;
            ColumnHeader.IsVisible = !icons;
            ListViewButton.IsChecked = !icons;
            IconViewButton.IsChecked = icons;
        }
    } = LocalPickerView.List;

    /// <summary>关窗时务必收起框选,否则自动滚动的计时器会拽着已关闭的窗口继续跑。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _marquee.End();
        base.OnClosed(e);
    }

    /// <summary>
    /// 默认尺寸是按 1080p 定的;小屏(1366×768 的笔记本、高缩放)放不下,打开时按工作区夹一次,并摆回中央。
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitIntoWorkArea();
    }

    /// <summary>
    /// 打开选择器,返回用户选中的本地路径(文件与文件夹混合);取消则返回空列表。
    /// 上次调过的尺寸与选过的视图照旧(所有会话共用一条记录)。
    /// </summary>
    public static async Task<IReadOnlyList<string>> PickAsync(Window owner, TransferOptions transferOptions)
    {
        var dialog = new LocalPathPickerDialog(transferOptions);
        if ((Application.Current as App)?.Services?.GetService<WindowLayoutStore>() is { } layoutStore)
        {
            WindowLayout? layout = await layoutStore.LoadAsync(LayoutKey);
            WindowLayoutStore.Apply(dialog, layout);
            if (Enum.TryParse(layout?.View, out LocalPickerView view))
            {
                dialog.View = view;
            }
            dialog.Closing += (_, _) => _ = layoutStore.SaveAsync(LayoutKey, dialog, dialog.View.ToString());
        }
        return await dialog.ShowDialog<IReadOnlyList<string>?>(owner) ?? [];
    }

    /// <summary>
    /// 视图切换器(右上角的「列表 | 图标」分段)。两段是一组 RadioButton,点中的那段触发这里;
    /// 被挤掉的那段也会来一次(IsChecked 变 false),不理它。
    /// </summary>
    private void OnViewSwitchChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } button)
        {
            return;
        }
        LocalPickerView view = button == IconViewButton ? LocalPickerView.Icons : LocalPickerView.List;
        if (view == View)
        {
            return;
        }
        View = view;

        // 焦点回到列表(容器整批重建完再给):点完切换器接着按方向键、敲字定位,走的是文件而不是切换器。
        Dispatcher.UIThread.Post(FocusList, DispatcherPriority.Loaded);
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

    /// <summary>
    /// 跳转之后把焦点放回列表,接着就能按方向键、回车:有选中项(路径指向文件时它已被选中)就滚到它并给它,否则给第一行。
    /// </summary>
    private void FocusList() => FileList.FocusSelectedOrFirstRow();

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

    /// <summary>缩放抓取区。只认左键:系统 sizing 模态循环只在左键弹起时退出(#116)。</summary>
    private void ResizeEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (WindowState == WindowState.Normal
            && sender is Border { Tag: string tag }
            && Enum.TryParse(tag, out WindowEdge edge))
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void FitIntoWorkArea()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
        {
            return;
        }
        // WorkingArea 是物理像素,而窗口尺寸按 DIP 计 —— 高 DPI 下不换算就会算出一个"看起来放得下"的假结论。
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        // Wayland 的阴影与描边画在 Width/Height 之外,要一起算进去;其它平台窗口尺寸就是全部。
        double frame = WindowChrome.OuterFrameSize(this);
        double availableWidth = (screen.WorkingArea.Width / scaling) - frame;
        double availableHeight = (screen.WorkingArea.Height / scaling) - frame;

        // 留一点余量,免得正好顶到工作区边缘(某些桌面环境的自动隐藏面板会占掉几像素)。
        const double margin = 16;
        double width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, availableWidth - margin));
        double height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, availableHeight - margin));
        if (Math.Abs(width - Width) < 0.5 && Math.Abs(height - Height) < 0.5)
        {
            return; // 放得下,保持 CenterOwner 定好的位置。
        }
        Width = width;
        Height = height;
        // 尺寸变了,CenterOwner 算出来的位置就不再居中(还可能把标题栏顶出屏幕外)。
        Position = new PixelPoint(
            screen.WorkingArea.X + (int)Math.Max(0, (screen.WorkingArea.Width - (width * scaling)) / 2),
            screen.WorkingArea.Y + (int)Math.Max(0, (screen.WorkingArea.Height - (height * scaling)) / 2));
    }
}
