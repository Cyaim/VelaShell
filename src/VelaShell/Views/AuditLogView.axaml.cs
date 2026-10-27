using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using VelaShell.Core.Import;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>
/// 审计日志窗口(设置 → 安全审计 → 查看审计日志),非模态;数据与筛选全在 <see cref="AuditLogViewModel" />。
/// </summary>
public partial class AuditLogView : Window
{
    /// <summary>构造窗口并按平台装上工具窗外框。</summary>
    public AuditLogView()
    {
        InitializeComponent();
        // 按平台装外框;最大化时卡片铺满、右下角手柄让位也由它管(见 WindowChrome)。
        WindowChrome.Apply(this, WindowChromeKind.Tool, ResizeGrip);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    /// <summary>双击标题栏空白处切换最大化;落在按钮上的双击不算(理由同回放中心)。</summary>
    private void Header_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Avalonia.Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }
        ToggleMaximize();
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>把眼前筛出来的那些导出成 CSV(UTF-8 带 BOM,Excel 直接打开不乱码)。</summary>
    private void ExportCsv_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not AuditLogViewModel { IsEmpty: false } viewModel)
        {
            return;
        }
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Strings.Get("AuditLog_ExportCsv"),
            SuggestedFileName = $"velashell-audit-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            SuggestedStartLocation = await StorageDefaults.DownloadsAsync(this),
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType(Strings.Get("SessFile_CsvType")) { Patterns = ["*.csv"] }]
        });
        if (file?.TryGetLocalPath() is not { Length: > 0 } path)
        {
            return; // 取消了。
        }
        try
        {
            await File.WriteAllBytesAsync(path, SessionFileText.EncodeCsv(viewModel.BuildCsv()));
            viewModel.ReportExport(Strings.Format("AuditLog_ExportDone", viewModel.Rows.Count, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            viewModel.ReportExport(Strings.Format("AuditLog_ExportFailed", ex.Message));
        }
    });

    /// <summary>打开这一行对应的连接配置(编辑框以本窗口为主人弹出)。</summary>
    private void OpenProfile_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (sender is Control { DataContext: AuditLogRow row } && DataContext is AuditLogViewModel viewModel)
        {
            await viewModel.OpenProfileAsync(row);
        }
    });

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

    /// <summary>Esc 关闭窗口 —— 但搜索框里有字时先让 Esc 清掉关键字。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is AuditLogViewModel { SearchText.Length: > 0 } vm)
            {
                vm.SearchText = string.Empty;
            }
            else
            {
                this.PostClose();
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
