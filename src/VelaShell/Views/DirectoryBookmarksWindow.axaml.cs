using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FireAndForget = VelaShell.Services.FireAndForget;
using VelaShell.Core.Models;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>连接级本地 ↔ 远程目录收藏管理窗口。</summary>
/// <remarks>
/// 新增、编辑、删除的对话框以本窗口为属主自己弹,不借文档视图挂在视图模型上的那几个回调:
/// 文档不是当前标签时那个视图已经离开窗口树,借它弹框什么也弹不出来,操作悄无声息地落空。
/// </remarks>
public partial class DirectoryBookmarksWindow : Window
{
    private SftpDocumentViewModel? _viewModel;
    private bool _closed;

    /// <summary>初始化窗口。</summary>
    public DirectoryBookmarksWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Tool);
        DataContextChanged += (_, _) => Bind(DataContext as SftpDocumentViewModel);
        Closed += (_, _) =>
        {
            _closed = true;
            Bind(null);
        };
    }

    private void Bind(SftpDocumentViewModel? viewModel)
    {
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel?.Detached -= OnViewModelDetached;
        _viewModel = viewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel?.Detached += OnViewModelDetached;
        UpdateEmptyState();
    }

    /// <summary>文档关了:窗口跟着关,不留一个对着已关文档、什么也存不进去的窗口(它还攥着视图模型不放)。</summary>
    private void OnViewModelDetached(object? sender, EventArgs e)
    {
        if (!_closed)
        {
            this.PostClose();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SftpDocumentViewModel.HasBookmarks) or null)
        {
            UpdateEmptyState();
        }
    }

    private void UpdateEmptyState() => EmptyText.IsVisible = _viewModel is { HasBookmarks: false };

    private void OnAddClick(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (_viewModel is not null)
        {
            await _viewModel.AddBookmarkAsync(EditAsync);
        }
    });

    private void OnApplyClick(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (_viewModel is not null && sender is Control { DataContext: DirectoryBookmarkViewModel item })
        {
            await _viewModel.ApplyBookmarkAsync(item);
        }
    });

    private void OnEditClick(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (_viewModel is not null && sender is Control { DataContext: DirectoryBookmarkViewModel item })
        {
            await _viewModel.EditBookmarkAsync(item, EditAsync);
        }
    });

    private void OnDeleteClick(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (_viewModel is not null && sender is Control { DataContext: DirectoryBookmarkViewModel item })
        {
            await _viewModel.DeleteBookmarkAsync(item, ConfirmDeleteAsync);
        }
    });

    private Task<RemoteDirectoryBookmark?> EditAsync(RemoteDirectoryBookmark initial) =>
        DirectoryBookmarkDialogs.EditAsync(this, initial);

    private Task<bool> ConfirmDeleteAsync(string name) => DirectoryBookmarkDialogs.ConfirmDeleteAsync(this, name);

    private void OnBookmarkDoubleTapped(object? sender, TappedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (_viewModel is not null && BookmarkList.SelectedItem is DirectoryBookmarkViewModel item)
        {
            await _viewModel.ApplyBookmarkAsync(item);
        }
    });

    private void OnCloseClick(object? sender, RoutedEventArgs e) => this.PostClose();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
