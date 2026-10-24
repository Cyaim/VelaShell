using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>
/// 插件日志窗口(插件管理页每行的「查看日志」):只读显示这个插件自己那份日志的尾部,能刷新、能打开所在文件夹。
/// </summary>
public partial class PluginLogWindow : Window
{
    /// <summary>初始化窗口,按平台装外框(与插件管理窗口同规格)。打开时读一次尾部。</summary>
    public PluginLogWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Tool, ResizeGrips);
        Opened += (_, _) => FireAndForget.Run(RefreshAsync);
    }

    private PluginLogViewModel? ViewModel => DataContext as PluginLogViewModel;

    /// <summary>重读尾部,并把视图滚到最后一行 —— 最近发生的事在最下面。</summary>
    internal async Task RefreshAsync()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        await vm.RefreshAsync();
        // 等文本排版完再挪光标,否则滚动范围还是旧的。
        Dispatcher.UIThread.Post(() => LogText.CaretIndex = LogText.Text?.Length ?? 0, DispatcherPriority.Background);
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(RefreshAsync);

    private void OpenFolder_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ViewModel?.Directory is not { } directory)
        {
            return;
        }
        // 插件还没写过日志时目录可能还不存在:建出来再打开,免得系统文件管理器报「找不到」。
        System.IO.Directory.CreateDirectory(directory);
        await Launcher.LaunchDirectoryInfoAsync(new(directory));
    });

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        this.BeginWindowMoveDrag(e);
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

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
