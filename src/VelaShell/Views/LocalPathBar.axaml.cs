using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ReactiveUI.Primitives;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 本地路径栏:面包屑与手动输入路径的输入框。上传选择框与 SFTP / FTP 文档的本地栏共用。
/// <para>
/// <c>Ctrl+L</c> 不在这里接:它的作用范围是整个宿主(选择框是整个窗口,文档是本地栏),由宿主在自己身上以隧道登记,
/// 调视图模型的 <see cref="LocalFilePaneViewModel.BeginPathEdit" />。
/// </para>
/// </summary>
public partial class LocalPathBar : UserControl
{
    private LocalFilePaneViewModel? _viewModel;

    /// <summary>初始化路径栏。</summary>
    public LocalPathBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            _viewModel?.PathEditActivated -= OnPathEditActivated;
            _viewModel = DataContext as LocalFilePaneViewModel;
            _viewModel?.PathEditActivated += OnPathEditActivated;
        };
    }

    /// <summary>
    /// 输入框收起了:跳转成功,或按 Esc 放弃。宿主据此把焦点放回列表,接着就能按方向键、敲字定位。
    /// 跳转失败时输入框还开着,不触发。
    /// </summary>
    public event EventHandler? EditFinished;

    /// <summary>点面包屑后面的空白 = 切成输入框(资源管理器地址栏同款)。</summary>
    private void OnPathAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is { } vm && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            vm.BeginPathEdit();
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
    /// 回车跳转、Esc 收起。两个键都必须就地标成已处理:冒上去的回车会触发选择框默认的「上传」按钮,
    /// 把列表里选着的东西直接传走;Esc 则会把选择框整个关掉。
    /// </summary>
    private void OnPathEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                vm.CommitPathEditCommand.Execute().Subscribe(
                    _ =>
                    {
                        if (!vm.IsPathEditing)
                        {
                            EditFinished?.Invoke(this, EventArgs.Empty);
                        }
                    },
                    _ => { });
                return;
            case Key.Escape:
                e.Handled = true;
                vm.CancelPathEdit();
                EditFinished?.Invoke(this, EventArgs.Empty);
                return;
        }
    }

    /// <summary>点到别处即放弃输入,回到面包屑 —— 不留一个半开的输入框。</summary>
    private void OnPathEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is { IsPathEditing: true } vm)
        {
            vm.CancelPathEdit();
        }
    }
}
