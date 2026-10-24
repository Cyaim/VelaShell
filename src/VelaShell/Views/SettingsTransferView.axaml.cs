using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 设置导出 / 导入的分类勾选框(常规页)。<c>ShowDialog</c> 的结果是勾上的分类标识,取消为 null。
/// </summary>
public partial class SettingsTransferView : Window
{
    /// <summary>初始化对话框。</summary>
    public SettingsTransferView()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
    }

    /// <summary>按模式换上导出 / 导入的图标(与常规页、导出连接对话框同一对图标)。</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SettingsTransferViewModel viewModel
            && this.TryFindResource(viewModel.IsExport ? "Icon.file-down" : "Icon.file-up", out object? icon)
            && icon is Geometry geometry)
        {
            TitleIcon.Data = geometry;
            ConfirmIcon.Data = geometry;
        }
    }

    /// <summary>Esc 等价于取消。</summary>
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

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => this.PostClose(null);

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsTransferViewModel { CanConfirm: true } viewModel)
        {
            this.PostClose(viewModel.SelectedIds);
        }
    }
}
