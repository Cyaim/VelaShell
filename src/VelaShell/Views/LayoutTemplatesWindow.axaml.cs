using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>「管理布局模板」窗口:打开、用当前布局覆盖、重命名、删除,以及把当前布局存成新模板。</summary>
/// <remarks>
/// 弹框一律以本窗口为属主(<see cref="LayoutTemplateDialogs" />):本窗口是非模态的,
/// 借主窗口弹的话,弹框会被本窗口挡在后面,看起来像点了没反应。
/// </remarks>
public partial class LayoutTemplatesWindow : Window
{
    private readonly LayoutTemplateDialogs _dialogs;

    /// <summary>初始化窗口。</summary>
    public LayoutTemplatesWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Tool);
        _dialogs = new(this);
    }

    private LayoutTemplatesViewModel? ViewModel => DataContext as LayoutTemplatesViewModel;

    private void OnSaveClick(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ViewModel is { } viewModel)
        {
            await viewModel.SaveCurrentAsync(_dialogs);
        }
    });

    private void OnOpenClick(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ViewModel is { } viewModel && sender is Control { DataContext: LayoutTemplateItemViewModel item })
        {
            await viewModel.OpenAsync(item, _dialogs);
        }
    });

    private void OnOverwriteClick(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ViewModel is { } viewModel && sender is Control { DataContext: LayoutTemplateItemViewModel item })
        {
            await viewModel.OverwriteAsync(item, _dialogs);
        }
    });

    private void OnRenameClick(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ViewModel is { } viewModel && sender is Control { DataContext: LayoutTemplateItemViewModel item })
        {
            await viewModel.RenameAsync(item, _dialogs);
        }
    });

    private void OnDeleteClick(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ViewModel is { } viewModel && sender is Control { DataContext: LayoutTemplateItemViewModel item })
        {
            await viewModel.DeleteAsync(item, _dialogs);
        }
    });

    private void OnTemplateDoubleTapped(object? sender, TappedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ViewModel is { } viewModel && TemplateList.SelectedItem is LayoutTemplateItemViewModel item)
        {
            await viewModel.OpenAsync(item, _dialogs);
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
