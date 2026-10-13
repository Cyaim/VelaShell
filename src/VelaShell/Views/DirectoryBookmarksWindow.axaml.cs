using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>连接级本地 ↔ 远程目录收藏管理窗口。</summary>
public partial class DirectoryBookmarksWindow : Window
{
    private SftpDocumentViewModel? _viewModel;

    /// <summary>初始化窗口。</summary>
    public DirectoryBookmarksWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Tool);
        DataContextChanged += (_, _) => Bind(DataContext as SftpDocumentViewModel);
        Closed += (_, _) => Bind(null);
    }

    private void Bind(SftpDocumentViewModel? viewModel)
    {
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = viewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        UpdateEmptyState();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SftpDocumentViewModel.HasBookmarks) or null)
        {
            UpdateEmptyState();
        }
    }

    private void UpdateEmptyState() => EmptyText.IsVisible = _viewModel is { HasBookmarks: false };

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.AddBookmarkAsync();
        }
    }

    private async void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Control { DataContext: DirectoryBookmarkViewModel item })
        {
            await _viewModel.ApplyBookmarkAsync(item);
        }
    }

    private async void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Control { DataContext: DirectoryBookmarkViewModel item })
        {
            await _viewModel.EditBookmarkAsync(item);
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Control { DataContext: DirectoryBookmarkViewModel item })
        {
            await _viewModel.DeleteBookmarkAsync(item);
        }
    }

    private async void OnBookmarkDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is not null && BookmarkList.SelectedItem is DirectoryBookmarkViewModel item)
        {
            await _viewModel.ApplyBookmarkAsync(item);
        }
    }

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
