using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 会话健康巡检的结果窗口:非模态、全程至多一扇;数据与巡检逻辑全在 <see cref="SessionHealthCheckViewModel" />。
/// </summary>
/// <remarks>
/// 行上的「连接诊断」「编辑配置」由主窗口接(弹框以本窗口为属主:本窗口是非模态的,借主窗口弹的话框会被它挡在后面)。
/// 关窗即停下还没查完的那几台。
/// </remarks>
public partial class SessionHealthCheckWindow : Window
{
    /// <summary>构造窗口并按平台装上工具窗外框。</summary>
    public SessionHealthCheckWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Tool, ResizeGrip);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SessionHealthCheckViewModel vm)
            {
                vm.CopyToClipboard = CopyAsync;
            }
        };
        Closed += (_, _) => (DataContext as SessionHealthCheckViewModel)?.Close();
    }

    private async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    /// <summary>双击标题栏空白处切换最大化;落在按钮上的双击不算。</summary>
    private void Header_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Avalonia.Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }
        ToggleMaximize();
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>右下角缩放手柄:无边框窗口经此拖拽调整大小。</summary>
    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.SouthEast, e);
        }
    }

    // 推迟关闭:同步 Close 会让本轮点击 / 按键的后续路由打到已销毁的窗口(见 WindowCloseExtensions)。
    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <summary>Esc 关窗,与右上角关闭按钮同路径。</summary>
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
