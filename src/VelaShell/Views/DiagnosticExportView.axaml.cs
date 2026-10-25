using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 导出诊断包之前的勾选框(关于页)。<c>ShowDialog</c> 的结果是勾上的那几类日志(一类都不勾就是空表),取消为 null。
/// </summary>
public partial class DiagnosticExportView : Window
{
    /// <summary>初始化对话框。</summary>
    public DiagnosticExportView()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
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
        if (DataContext is DiagnosticExportViewModel viewModel)
        {
            this.PostClose(viewModel.SelectedOptions);
        }
    }
}
